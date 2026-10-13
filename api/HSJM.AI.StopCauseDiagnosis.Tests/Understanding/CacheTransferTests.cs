#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding;

public class CacheTransferTests
{
    sealed class ContractMemory : IFbContractCache
    {
        public readonly Dictionary<string, FbContractCacheEntry> Items = new();
        public Task<FbContractCacheEntry?> GetAsync(string key, CancellationToken ct) => Task.FromResult(Items.TryGetValue(key, out var e) ? e : null);
        public Task SaveAsync(FbContractCacheEntry entry, CancellationToken ct) { Items[entry.Key] = entry; return Task.CompletedTask; }
    }

    sealed class NetworkMemory : INetworkAiCache
    {
        public readonly Dictionary<string, NetworkAiCacheEntry> Items = new();
        public Task<NetworkAiCacheEntry?> GetAsync(string key, CancellationToken ct)
        {
            lock (Items) return Task.FromResult(Items.TryGetValue(key, out var e) ? e : null);
        }
        public Task SaveAsync(NetworkAiCacheEntry entry, CancellationToken ct) { lock (Items) Items[entry.Key] = entry; return Task.CompletedTask; }
    }

    static string TempDir() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "plan5-cache-" + Guid.NewGuid().ToString("N"))).FullName;

    // FB_Low：单网络小 FB；FB_Big：三网络，按预算分两段读
    static PipelineSnapshot ContractProject()
    {
        var units = Enumerable.Range(1, 3).Select(k =>
        {
            var b = new FlgNetBuilder();
            b.Series("Coil", $"O{k}", ($"I{k}", false));
            return new PipelineUnit(10, "FB_Big", 100 + k, "LAD", b.Build(), "", [], k,
                FlgNetBuilder.Interface(["I1", "I2", "I3"], ["O1", "O2", "O3"]), "FB");
        }).ToList();
        var low = new FlgNetBuilder();
        low.Series("Coil", "O", ("I", false));
        units.Add(new PipelineUnit(20, "FB_Low", 200, "LAD", low.Build(), "", [], 1, FlgNetBuilder.Interface(["I"], ["O"]), "FB"));
        return new PipelineSnapshot(1, units.ToArray(), []);
    }

    static int SegmentBudget(PipelineSnapshot s)
    {
        var len = IrBlock.Load(s, "FB_Big").Units.Select(u => IrText.Unit(u).Length).ToArray();
        return len[0] + len[1];
    }

    static string ContractReply(string fb, string input, string output, string evidence) =>
        $$"""{"fb":"{{fb}}","relations":[{"kind":"gate","input":"{{input}}","outputs":["{{output}}"],"evidence":["{{evidence}}"]}]}""";

    static Task<string> OldContractModel(string system, string user, CancellationToken ct) =>
        Task.FromResult(user.Contains("这是第 1 段") ? ContractReply("FB_Big", "I1", "O1", "FB_Big/1/1")
            : user.Contains("这是第 2 段") ? ContractReply("FB_Big", "I3", "O3", "FB_Big/3/1")
            : ContractReply("FB_Low", "I", "O", "FB_Low/1/1"));

    static readonly PipelineTag[] NetworkTags = [new("Q1", "%Q0.0", "Bool"), new("I1", "%I0.0", "Bool"), new("I2", "%I0.1", "Bool")];
    const string NetworkReply = """{"constraints":[{"operator":"SAME_STATE","signals":[{"name":"Q1","role":"Member"},{"name":"I1","role":"Member"}],"evidence":["OB1/1/1"]}]}""";

    static PipelineSnapshot NetworkProject()
    {
        var io = new FlgNetBuilder(); io.Series("Coil", "Q1", ("I1", false), ("I2", true));
        return new PipelineSnapshot(1, [new PipelineUnit(1, "OB1", 101, "LAD", io.Build(), "", [], 1, "", "OB")], NetworkTags);
    }

    [Fact]
    public async Task PushCopiesOnlyOkEntriesWithReplies()
    {
        var dir = TempDir();
        var files = new FileFbContractCache(dir);
        await files.SaveAsync(new("k1", "FB_A", FbContractStatus.Ok, "{\"fb\":\"FB_A\"}", null, "m", "v"), CancellationToken.None);
        await files.SaveAsync(new("k2", "FB_B", FbContractStatus.NoBody, null, null, "m", "v"), CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(dir, "broken.json"), "{");
        await new FileNetworkAiCache(Path.Combine(dir, "networks"))
            .SaveAsync(new("n1", "OB1", 1, NetworkAiStatus.Ok, "{\"constraints\":[]}", null, "m", "v"), CancellationToken.None);

        var contracts = new ContractMemory();
        var networks = new NetworkMemory();
        var (c, n, skipped) = await CacheTransfer.PushAsync(dir, contracts, networks, CancellationToken.None);

        Assert.Equal((1, 1, 2), (c, n, skipped));
        Assert.Equal("{\"fb\":\"FB_A\"}", contracts.Items["k1"].ContractJson);
        Assert.Equal("{\"constraints\":[]}", networks.Items["n1"].ReplyJson);
    }

    [Fact]
    public async Task MissingDirectoryPushesNothing()
    {
        var result = await CacheTransfer.PushAsync(Path.Combine(Path.GetTempPath(), "plan5-none-" + Guid.NewGuid().ToString("N")),
            new ContractMemory(), new NetworkMemory(), CancellationToken.None);
        Assert.Equal((0, 0, 0), result);
    }

    [Fact]
    public async Task ContractKeyFormulaMatchesTheContractReader()
    {
        var low = new FlgNetBuilder();
        low.Series("Coil", "O", ("I", false));
        var snapshot = new PipelineSnapshot(1,
            [new PipelineUnit(10, "FB_Low", 100, "LAD", low.Build(), "", [], 1, FlgNetBuilder.Interface(["I"], ["O"]), "FB")], []);
        string? system = null, user = null;
        var reader = new FbContractReader(new ContractMemory(), (s, u, ct) =>
        {
            system = s; user = u;
            return Task.FromResult("""{"fb":"FB_Low","relations":[]}""");
        }, "old-fp");
        var outcome = (await reader.ReadAsync(snapshot, ["FB_Low"], CancellationToken.None))["FB_Low"];
        Assert.Equal(LegacyCacheMigration.ContractKeyOf(system!, user!, "old-fp"), outcome.CacheKey);
    }

    [Fact]
    public async Task NetworkKeyFormulaMatchesTheNetworkReaderAndDiffersFromTheContractFormula()
    {
        string? system = null, user = null;
        var reader = new NetworkAiReader(new NetworkMemory(), (s, u, ct) => { system = s; user = u; return Task.FromResult(NetworkReply); }, "old-fp");
        var outcome = Assert.Single((await reader.ReadAsync(NetworkProject(), new Dictionary<string, FbContractOutcome>(), CancellationToken.None)).Values);
        Assert.Equal(NetworkAiStatus.Ok, outcome.Status);
        Assert.Equal(LegacyCacheMigration.NetworkKeyOf(system!, user!, "old-fp"), outcome.CacheKey);
        // 普通网络键多一个 Check 字段，用契约公式算一定对不上（计划原稿的缺陷）
        Assert.NotEqual(LegacyCacheMigration.ContractKeyOf(system!, user!, "old-fp"), outcome.CacheKey);
    }

    [Fact]
    public async Task LegacyContractCacheIncludingSegmentsIsMigratedWithoutCallingTheModel()
    {
        var source = ContractProject();
        var budget = SegmentBudget(source);
        var legacy = TempDir();
        // 旧指纹下真实写出的文件缓存：FB_Low 一条、FB_Big 两段各一条
        var first = await new FbContractReader(new FileFbContractCache(legacy), OldContractModel, "old", maxBodyChars: budget)
            .ReadAsync(source, ["FB_Big", "FB_Low"], CancellationToken.None);
        Assert.All(first.Values, o => Assert.Equal(FbContractStatus.Ok, o.Status));
        Assert.Equal(3, Directory.GetFiles(legacy, "*.json").Length);

        // 新指纹、空缓存：模型只能经旧缓存迁移得到回复
        var fresh = new ContractMemory();
        var hits = 0;
        var migrated = await new FbContractReader(fresh, (s, u, ct) =>
        {
            var old = LegacyCacheMigration.TryRead(legacy, "old", s, u) ?? throw new InvalidOperationException("旧缓存未命中");
            hits++;
            return Task.FromResult(old);
        }, "new", maxBodyChars: budget).ReadAsync(source, ["FB_Big", "FB_Low"], CancellationToken.None);

        Assert.Equal(3, hits);
        Assert.All(migrated.Values, o => Assert.Equal(FbContractStatus.Ok, o.Status));
        Assert.Null(migrated["FB_Big"].Error);
        Assert.Equal(["I1", "I3"], migrated["FB_Big"].Contract!.Accepted.Select(r => r.Relation.Inputs[0]).Order().ToArray());
        Assert.Equal(["FB_Big#1/2", "FB_Big#2/2", "FB_Low"], fresh.Items.Values.Select(e => e.FbName).Order().ToArray());
        Assert.NotEqual(first["FB_Low"].CacheKey, migrated["FB_Low"].CacheKey);
        Assert.Null(LegacyCacheMigration.TryRead(legacy, "other-fp", FbContractPrompt.System, "u"));
    }

    [Fact]
    public async Task LegacyNetworkCacheIsMigratedWithoutCallingTheModel()
    {
        var legacy = TempDir();
        var networksDir = Path.Combine(legacy, "networks");
        var first = await new NetworkAiReader(new FileNetworkAiCache(networksDir), (_, _, _) => Task.FromResult(NetworkReply), "old")
            .ReadAsync(NetworkProject(), new Dictionary<string, FbContractOutcome>(), CancellationToken.None);
        Assert.Equal(NetworkAiStatus.Ok, Assert.Single(first.Values).Status);
        Assert.Single(Directory.GetFiles(networksDir, "*.json"));

        var fresh = new NetworkMemory();
        var hits = 0;
        var migrated = await new NetworkAiReader(fresh, (s, u, ct) =>
        {
            var old = LegacyCacheMigration.TryRead(legacy, "old", s, u) ?? throw new InvalidOperationException("旧缓存未命中");
            Interlocked.Increment(ref hits);
            return Task.FromResult(old);
        }, "new").ReadAsync(NetworkProject(), new Dictionary<string, FbContractOutcome>(), CancellationToken.None);

        Assert.Equal(1, hits);
        var outcome = Assert.Single(migrated.Values);
        Assert.Equal(NetworkAiStatus.Ok, outcome.Status);
        Assert.Equal(NetworkReply, Assert.Single(fresh.Items.Values).ReplyJson);
        Assert.Null(LegacyCacheMigration.TryRead(legacy, "other-fp", NetworkAiPrompt.System, "u"));
        Assert.Null(LegacyCacheMigration.TryRead(legacy, "old", "别的提示词", "u"));
    }
}
