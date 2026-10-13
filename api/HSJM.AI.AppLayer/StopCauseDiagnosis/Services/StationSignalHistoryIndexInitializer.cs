using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.IDbService;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>
    /// 工位信号历史表 ai_plc_station_signal_history 的查询索引（2026-09-23）。
    /// <para>
    /// 现场一键排查：走 GRAPH 兜底路径（GraphDirectEval+ExecBitsFallback）的工位 GraphActiveCheck 要 34~44 秒，
    /// 其余环节都在 2 秒内。这条路径两次按「项目 + 工位 + 变量名列表，按 changed_at 倒序取前 N 条」查这张表，
    /// 而这张表是工位影子监控持续写入的变化流水，此前仓库里没有任何索引定义——每次都是全表扫描。
    /// </para>
    /// <para>
    /// 启动时后台检查，缺哪个建哪个；用 CREATE INDEX CONCURRENTLY，建索引期间不阻塞监控写入。
    /// 失败只记日志、不阻断启动。等价离线脚本：tools/mig_20260923_station_signal_history_index.sql。
    /// 若发现"存在但无效"的索引（上次并发建索引中途失败留下的），只告警不自动删——
    /// 另一台实例可能正在建同名索引，此时它也显示为无效，自动删会把别人的构建打断。
    /// </para>
    /// </summary>
    public class StationSignalHistoryIndexInitializer : IHostedService
    {
        public const string TableName = "ai_plc_station_signal_history";

        /// <summary>索引名 → 建索引语句（与 tools/mig_20260923_station_signal_history_index.sql 一致）。</summary>
        public static readonly IReadOnlyList<KeyValuePair<string, string>> Indexes = new[]
        {
            // 诊断：project_id + station_code + variable_name IN (...) ORDER BY changed_at DESC LIMIT N
            new KeyValuePair<string, string>(
                "ix_station_signal_history_station_var_time",
                "CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_station_signal_history_station_var_time " +
                "ON ai_plc_station_signal_history (project_id, station_code, variable_name, changed_at DESC)"),
            // 历史分页 / 样本库 / 过期清理：project_id + station_code 按 changed_at 排序或按范围删除
            new KeyValuePair<string, string>(
                "ix_station_signal_history_station_time",
                "CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_station_signal_history_station_time " +
                "ON ai_plc_station_signal_history (project_id, station_code, changed_at DESC)"),
        };

        /// <summary>大表上建索引可能要几分钟；SqlSugar 默认命令超时会把它掐断并留下无效索引。</summary>
        private const int BuildTimeoutSeconds = 3600;

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<StationSignalHistoryIndexInitializer> _logger;

        public StationSignalHistoryIndexInitializer(
            IServiceScopeFactory scopeFactory,
            ILogger<StationSignalHistoryIndexInitializer> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // 不 await：库不可达或索引构建耗时都不能拖慢宿主启动
            _ = EnsureIndexesAsync();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <summary>索引当前状态 → 要做什么。</summary>
        public enum IndexAction
        {
            Create,
            Skip,
            WarnInvalid,
        }

        /// <param name="isValid">pg_index.indisvalid；索引不存在为 null。</param>
        public static IndexAction Decide(bool? isValid) => isValid switch
        {
            null => IndexAction.Create,
            true => IndexAction.Skip,
            false => IndexAction.WarnInvalid,
        };

        private async Task EnsureIndexesAsync()
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<IPLC_StationSignalHistoryService>().GetDb().Context;

                var tableExists = await db.Ado.GetIntAsync(
                    "SELECT COUNT(*) FROM pg_class WHERE relname = @name AND relkind IN ('r', 'p')",
                    new { name = TableName }).ConfigureAwait(false);
                if (tableExists == 0)
                {
                    _logger.LogInformation("[信号历史索引] 表 {Table} 不存在，跳过", TableName);
                    return;
                }

                foreach (var (name, ddl) in Indexes)
                {
                    try
                    {
                        var rows = await db.Ado.SqlQueryAsync<bool>(
                            "SELECT i.indisvalid FROM pg_class c JOIN pg_index i ON i.indexrelid = c.oid WHERE c.relname = @name",
                            new { name }).ConfigureAwait(false);
                        bool? isValid = rows.Count > 0 ? rows[0] : null;

                        switch (Decide(isValid))
                        {
                            case IndexAction.Skip:
                                continue;
                            case IndexAction.WarnInvalid:
                                _logger.LogWarning(
                                    "[信号历史索引] 索引 {Index} 存在但无效（上次并发建索引中途失败，或另一实例正在构建）。" +
                                    "确认没有实例在建后执行：DROP INDEX CONCURRENTLY {Index}; 再重启后端重建",
                                    name, name);
                                continue;
                        }

                        var previousTimeout = db.Ado.CommandTimeOut;
                        db.Ado.CommandTimeOut = BuildTimeoutSeconds;
                        var sw = Stopwatch.StartNew();
                        try
                        {
                            _logger.LogInformation("[信号历史索引] 开始建索引 {Index}（CONCURRENTLY，不阻塞写入）", name);
                            await db.Ado.ExecuteCommandAsync(ddl).ConfigureAwait(false);
                            _logger.LogInformation("[信号历史索引] 索引 {Index} 建成，耗时 {Elapsed} ms", name, sw.ElapsedMilliseconds);
                        }
                        finally
                        {
                            db.Ado.CommandTimeOut = previousTimeout;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[信号历史索引] 建索引 {Index} 失败（不影响运行，只是诊断查询慢）", name);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[信号历史索引] 启动检查异常（不阻断启动）");
            }
        }
    }
}
