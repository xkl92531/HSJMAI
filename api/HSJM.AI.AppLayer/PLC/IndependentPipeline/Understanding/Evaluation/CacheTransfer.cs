#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

/// <summary>离线评测的文件缓存 → 发布器用的数据库缓存（键相同的前提是指纹统一，见 UnderstandingModelSetup）。</summary>
public static class CacheTransfer
{
    /// <summary>directory/*.json 为 FB 契约条目、directory/networks/*.json 为普通网络条目；只写 Status=Ok 且有回复原文的条目，
    /// 其余（非 Ok、无回复、无法解析）计入 Skipped。目录不存在时什么都不写。</summary>
    public static async Task<(int Contracts, int Networks, int Skipped)> PushAsync(string directory, IFbContractCache contracts,
        INetworkAiCache networks, CancellationToken ct)
    {
        int c = 0, n = 0, skipped = 0;
        foreach (var (dir, key) in Files(directory))
        {
            var e = await JsonFileCache.GetAsync<FbContractCacheEntry>(dir, key, ct);
            if (e is null || string.IsNullOrEmpty(e.Key) || e.Status != FbContractStatus.Ok || string.IsNullOrEmpty(e.ContractJson)) { skipped++; continue; }
            await contracts.SaveAsync(e, ct);
            c++;
        }
        foreach (var (dir, key) in Files(Path.Combine(directory, "networks")))
        {
            var e = await JsonFileCache.GetAsync<NetworkAiCacheEntry>(dir, key, ct);
            if (e is null || string.IsNullOrEmpty(e.Key) || e.Status != NetworkAiStatus.Ok || string.IsNullOrEmpty(e.ReplyJson)) { skipped++; continue; }
            await networks.SaveAsync(e, ct);
            n++;
        }
        return (c, n, skipped);
    }

    static IEnumerable<(string Directory, string Key)> Files(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal)
                .Select(p => (directory, Path.GetFileNameWithoutExtension(p))).ToArray()
            : [];
}

/// <summary>R2：计划 5 之前离线工具用另一套指纹写的文件缓存。按旧指纹算出同一份 system+user 的旧键，命中即返回回复原文。
/// FB 契约与普通网络的缓存键公式不同（普通网络多一个核对器版本 Check），分别与 FbContractReader / NetworkAiReader 保持一致。</summary>
public static class LegacyCacheMigration
{
    /// <summary>与 FbContractReader 的缓存键（整块与分段都是这个公式）一致。</summary>
    public static string ContractKeyOf(string system, string user, string modelFingerprint) =>
        PipelineHash.Of(JsonSerializer.Serialize(new
        {
            System = system, User = user, Model = modelFingerprint, Prompt = FbContractPrompt.Version
        }));

    /// <summary>与 NetworkAiReader 的缓存键一致（含 Check = NetworkConstraintValidator.Version）。</summary>
    public static string NetworkKeyOf(string system, string user, string modelFingerprint) =>
        PipelineHash.Of(JsonSerializer.Serialize(new
        {
            System = system, User = user, Model = modelFingerprint, Prompt = NetworkAiPrompt.Version,
            Check = NetworkConstraintValidator.Version
        }));

    /// <summary>按提示词判断是 FB 契约还是普通网络，取旧指纹下的缓存条目；只返回 Status=Ok 的回复原文，其它（不存在、损坏、非 Ok）返回 null。</summary>
    public static string? TryRead(string directory, string legacyFingerprint, string system, string user)
    {
        if (system == FbContractPrompt.System)
        {
            var e = JsonFileCache.GetAsync<FbContractCacheEntry>(directory, ContractKeyOf(system, user, legacyFingerprint), CancellationToken.None)
                .GetAwaiter().GetResult();
            return e?.Status == FbContractStatus.Ok && !string.IsNullOrEmpty(e.ContractJson) ? e.ContractJson : null;
        }
        if (system == NetworkAiPrompt.System)
        {
            var e = JsonFileCache.GetAsync<NetworkAiCacheEntry>(Path.Combine(directory, "networks"),
                NetworkKeyOf(system, user, legacyFingerprint), CancellationToken.None).GetAwaiter().GetResult();
            return e?.Status == NetworkAiStatus.Ok && !string.IsNullOrEmpty(e.ReplyJson) ? e.ReplyJson : null;
        }
        return null;
    }
}
