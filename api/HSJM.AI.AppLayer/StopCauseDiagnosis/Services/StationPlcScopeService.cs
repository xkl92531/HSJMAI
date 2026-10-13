using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using SqlSugar;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>
    /// 项目工位白名单服务（2026-08-26 建立；2026-09-08 回归 Basic_*）：
    /// 工位→PLC 归属映射直接读工位基础信息表 "Basic_SubStation"（PlcName 绑定 PLC1/PLC2），
    /// 项目 ai_plc_project_new.plccode 标识本项目属于哪台 PLC，
    /// 由此得出「本项目允许归属的工位集合」（如 PLC1 → OP010~OP070）。
    /// 工站推断/绑定时按该白名单过滤，防止跨 PLC 通信变量（DB182_PLC1&lt;-&gt;PLC2 等）把块染到别的 PLC 的工位
    /// （例：PLC1 的 FB361_HomeGraph 被误绑到 OP090 —— OP080~OP100 属于 PLC2）。
    /// 项目未配置 plccode 或查不到映射数据时返回 null（不过滤，保持旧行为）。
    /// </summary>
    public class StationPlcScopeService : ISelfScopedAutoInject
    {
        private readonly ISqlSugarClient _db;

        public StationPlcScopeService(ISqlSugarClient db)
        {
            _db = db;
        }

        /// <summary>
        /// 取项目允许的工位代码集合（大小写不敏感）。
        /// 返回 null = 未配置 plccode 或无子工位绑定数据，调用方应跳过过滤（兼容旧项目）。
        /// </summary>
        public async Task<HashSet<string>?> GetAllowedStationsAsync(long projectId)
        {
            // null 防御（2026-08-27 修复）：项目不存在/已软删时 FirstAsync 返回 null，
            // 原实现直接 .Trim() 抛 NullReferenceException（GRAPH 注册等调用链被炸断）。
            // 语义对齐注释约定：查不到项目 → 返回 null（不过滤，兼容旧行为）。
            var plcCode = (await _db.Queryable<PLC_ProjectEntity>()
                .Where(p => p.Id == projectId && p.IsDeleted == false)
                .Select(p => p.PlcCode)
                .FirstAsync())?.Trim();
            if (string.IsNullOrEmpty(plcCode))
            {
                return null;
            }

            // 工位基础信息：Basic_SubStation（2026-09-08 回归直读原表；
            // 2026-09-04 曾切到快照表 AI_PLC_StationPlcMap，快照方案已整体废弃）
            var rows = await _db.Ado.SqlQueryAsync<string>(
                "SELECT DISTINCT \"WorkStationCode\" FROM \"Basic_SubStation\" " +
                "WHERE \"IsDeleted\" = FALSE AND \"PlcName\" = @plcCode AND \"WorkStationCode\" IS NOT NULL AND \"WorkStationCode\" <> ''",
                new SugarParameter("@plcCode", plcCode));
            var allowed = rows
                .Select(c => c.Trim())
                .Where(c => c.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return allowed.Count > 0 ? allowed : null;
        }

        /// <summary>工位是否在白名单内（白名单为 null 时恒真，兼容未配置项目）。</summary>
        public static bool IsAllowed(string? stationCode, HashSet<string>? allowed)
        {
            if (allowed == null) return true;
            var code = stationCode?.Trim();
            return !string.IsNullOrWhiteSpace(code) && allowed.Contains(code);
        }
    }
}
