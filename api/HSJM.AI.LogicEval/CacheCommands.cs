using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

namespace HSJM.AI.LogicEval;

/// <summary>cache-push：文件缓存里 Ok 的条目写入测试库 plc_independent.fb_contract_cache / network_ai_cache（同键覆盖），
/// 供发布器复用（离线与发布器指纹统一后键相同，见 UnderstandingModelSetup）。只写测试库 hsjm_ai_new_test。
/// --dry-run：只数会写入多少条，不连数据库。</summary>
static class CachePush
{
    public const string UsageLine = "cache-push --cache <目录> [--dry-run]   （把文件缓存里 Ok 的条目写入测试库 plc_independent 缓存表，供发布器复用；--dry-run 只计数不连库）";

    sealed class CountingContracts : IFbContractCache
    {
        public Task<FbContractCacheEntry?> GetAsync(string key, CancellationToken ct) => Task.FromResult<FbContractCacheEntry?>(null);
        public Task SaveAsync(FbContractCacheEntry entry, CancellationToken ct) => Task.CompletedTask;
    }

    sealed class CountingNetworks : INetworkAiCache
    {
        public Task<NetworkAiCacheEntry?> GetAsync(string key, CancellationToken ct) => Task.FromResult<NetworkAiCacheEntry?>(null);
        public Task SaveAsync(NetworkAiCacheEntry entry, CancellationToken ct) => Task.CompletedTask;
    }

    public static async Task<int> RunAsync(string[] args)
    {
        if (CliArgs.Flag(args, "--help") || CliArgs.Flag(args, "-h"))
        {
            Console.WriteLine("用法：\n  " + UsageLine);
            return 0;
        }
        var directory = CliArgs.Opt(args, "--cache");
        if (string.IsNullOrWhiteSpace(directory) || directory.StartsWith("--", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("缺少参数 --cache <目录>\n用法：\n  " + UsageLine);
            return 2;
        }
        if (!Directory.Exists(directory))
        {
            Console.Error.WriteLine($"缓存目录不存在：{directory}");
            return 2;
        }
        if (CliArgs.Flag(args, "--dry-run"))
        {
            var (c, n, s) = await CacheTransfer.PushAsync(directory, new CountingContracts(), new CountingNetworks(), CancellationToken.None);
            Console.WriteLine($"（--dry-run，未连库）将写入测试库缓存：FB 契约 {c} 条，普通网络 {n} 条；跳过（非 Ok / 无法解析）{s} 条");
            return 0;
        }
        var connection = EvalDb.ConnectionString("hsjm_ai_new_test");
        var (contracts, networks, skipped) = await CacheTransfer.PushAsync(directory,
            new PostgresFbContractCache(connection), new PostgresNetworkAiCache(connection), CancellationToken.None);
        Console.WriteLine($"写入测试库缓存：FB 契约 {contracts} 条，普通网络 {networks} 条；跳过（非 Ok / 无法解析）{skipped} 条");
        return 0;
    }
}
