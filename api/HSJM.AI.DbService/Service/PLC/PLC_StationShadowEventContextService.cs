using System;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.Utility;

namespace HSJM.AI.DbService
{
    /// <summary>
    /// 工位监控事件现场快照固化服务（T4）。注册方式照抄 PLC_StationShadowEventService：
    /// AbstractBaseService + 接口 + IAutoInject（DbService 层既有惯例，按接口可解析）。
    /// </summary>
    public class PLC_StationShadowEventContextService : AbstractBaseService<PLC_StationShadowEventContextEntity>, IPLC_StationShadowEventContextService, IAutoInject
    {
        // 进程内一次性建表标记（仿 MemoryManagerService.EnsureMemoryEnhancementSchemaAsync）：
        // 静态字段 + 信号量双检，避免每个事件都跑 DDL。
        private static volatile bool _schemaEnsured;
        private static readonly SemaphoreSlim _schemaEnsureLock = new SemaphoreSlim(1, 1);

        public async Task EnsureSchemaAsync()
        {
            if (_schemaEnsured)
            {
                return;
            }

            await _schemaEnsureLock.WaitAsync();
            try
            {
                if (_schemaEnsured)
                {
                    return;
                }

                var db = GetDb().Context;
                // CodeFirst 幂等建表（存在则跳过，缺列则补列）
                db.CodeFirst.InitTables<PLC_StationShadowEventContextEntity>();
                if (db.CurrentConnectionConfig.DbType == SqlSugar.DbType.PostgreSQL)
                {
                    // CodeFirst 不建二级索引，按事件拉现场的查询路径补 event_id 索引（幂等）
                    db.Ado.ExecuteCommand(
                        "CREATE INDEX IF NOT EXISTS idx_station_shadow_event_context_event_id ON ai_plc_station_shadow_event_context(event_id)");
                }

                _schemaEnsured = true;
            }
            finally
            {
                _schemaEnsureLock.Release();
            }
        }
    }
}
