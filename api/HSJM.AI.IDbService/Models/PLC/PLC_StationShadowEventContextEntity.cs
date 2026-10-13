using SqlSugar;
using HSJM.AI.Core;
using System;

namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 工位监控事件现场快照固化表（T4，阶段1 确定性现场保全）。
    /// Silent/Resume 事件创建成功后，把 Redis 中的事件现场（snapshotModel 全信号快照 +
    /// eventBundle 静默前最后变化束）序列化固化到本表，防止 SnapshotTtlSeconds（默认 3 天）
    /// 过期后现场不可追溯。一行对应一个事件（event_id → ai_plc_station_shadow_event.id）。
    /// </summary>
    [SugarTable("ai_plc_station_shadow_event_context", tableDescription: "工位监控事件现场快照固化表")]
    public class PLC_StationShadowEventContextEntity
    {
        public PLC_StationShadowEventContextEntity()
        {
            Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        }

        [SugarColumn(ColumnName = "id", IsPrimaryKey = true)]
        public long Id { get; set; }

        /// <summary>关联事件 id（ai_plc_station_shadow_event.id），建索引用于按事件拉现场。</summary>
        [SugarColumn(ColumnName = "event_id", IsNullable = true)]
        public long? EventId { get; set; }

        /// <summary>冗余项目 id，便于 BI 不 join 直接按项目统计。</summary>
        [SugarColumn(ColumnName = "project_id", IsNullable = true)]
        public long? ProjectId { get; set; }

        /// <summary>冗余工位编码。</summary>
        [SugarColumn(ColumnName = "station_code", IsNullable = true, Length = 50)]
        public string StationCode { get; set; }

        /// <summary>冗余事件类型（Silent/Resume），便于按类型过滤。</summary>
        [SugarColumn(ColumnName = "event_type", IsNullable = true, Length = 32)]
        public string EventType { get; set; }

        /// <summary>快照 id（与事件表 snapshot_id 同源）。</summary>
        [SugarColumn(ColumnName = "snapshot_id", IsNullable = true, Length = 100)]
        public string SnapshotId { get; set; }

        /// <summary>事件现场全量快照 JSON（StationShadowSnapshotCacheModel 序列化）。
        /// 2026-09-18：nvarchar(max) 是 SQLServer 方言，PG 上 CodeFirst InitTables 报
        /// 42704 type "nvarchar" does not exist → EnsureSchemaAsync 每次失败 → 现场固化
        /// 从未写入（表 0 行）。改 text 与 PG 实际表结构一致。</summary>
        [SugarColumn(ColumnName = "context_json", IsNullable = true, ColumnDataType = "text")]
        public string ContextJson { get; set; }

        /// <summary>静默前最后变化束 JSON（StationShadowBundleCacheModel 序列化，可能为 null）。</summary>
        [SugarColumn(ColumnName = "bundle_json", IsNullable = true, ColumnDataType = "text")]
        public string BundleJson { get; set; }

        [SugarColumn(ColumnName = "isdeleted")]
        public bool IsDeleted { get; set; }

        [SugarColumn(ColumnName = "createrid", IsNullable = true)]
        public long? CreaterId { get; set; } = 0;

        [SugarColumn(ColumnName = "creatername", IsNullable = true)]
        public string CreaterName { get; set; }

        [SugarColumn(ColumnName = "createtime", IsNullable = true)]
        public DateTime? CreateTime { get; set; }

        [SugarColumn(ColumnName = "modifierid", IsNullable = true)]
        public long? ModifierId { get; set; } = 0;

        [SugarColumn(ColumnName = "modifytime", IsNullable = true)]
        public DateTime? ModifyTime { get; set; }

        [SugarColumn(ColumnName = "modifiername", IsNullable = true)]
        public string ModifierName { get; set; }
    }
}
