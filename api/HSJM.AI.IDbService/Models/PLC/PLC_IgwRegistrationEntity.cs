using HSJM.AI.Core;
using SqlSugar;
using System;

namespace HSJM.AI.IDbService
{
    /// <summary>
    /// IGW 自动注册台账表（sub-project B task-5 / spec §4.5）。物理表 <c>ai_plc_igwregistration</c>（全小写、不带引号）——
    /// 本表是全新建表，RegisterDbService 的 CasePreservedTableCache 只给"连接时库里已存在、大小写与全小写不同"的旧表
    /// 加引号访问（如 Basic_*/AI_LineUp_*），本表不在该集合里，常规 CRUD（InsertRangeAsync/GetListAsync/Updateable 等）
    /// 会把 <c>[SugarTable]</c> 特性的 <c>"AI_PLC_IgwRegistration"</c> 整体 ToLowerInvariant() 当无引号标识符
    /// 访问。因此建表 DDL（<see cref="PLC_IgwRegistrationSchemaDdl"/>，见其注释——不用 CodeFirst，那会因
    /// BaseField.CreaterName/ModifierName 的 <c>ColumnDataType="nvarchar"</c> 在 PG 上报 42704）与
    /// <c>tools/mig_20260919_igw_registration.sql</c> 都刻意建成全小写不带引号，与常规 CRUD 的访问方式对齐
    /// （task-5 fix round 1 修正过一次"以为迁移脚本先跑就能让表带引号"的错误理解，round 2 又发现 CodeFirst 本身
    /// 在这张继承 BaseField 的表上跑不通，两轮加起来最终落定为纯手写 DDL + 全小写）。
    /// 每行记录一个"根"（DB 块 / IO / GRAPH 工站+DB）在某台 IGW 设备上的最近一次注册结果：
    /// 落在哪台设备（DeviceKind）、实际使用的根组名（RootGroup，规划器可能因重名/冲突改名，见 IgwRegistrationPlanner.BlockRootRenames）、
    /// 本根内的重名映射（RenamesJson）。读链（PLC_VariableAccessService.ReadExactViaIgwAsync 等，见 spec §4.6）
    /// 依赖本表按块查 DeviceKind/RootGroup/重名映射来拼读别名，因此本表必须与最近一次实际注册结果保持一致
    /// （每次注册用 <see cref="IgwLedgerRootKind"/> 对应的 (ProjectId, RootKind, RootName) 键先<b>硬删</b>旧行再插新行——
    /// M-5 起不再软删：台账只是"最近一次注册结果"，没有审计价值，软删行只会无限堆积）。
    /// </summary>
    [SugarTable("AI_PLC_IgwRegistration", tableDescription: "IGW自动注册台账表")]
    public class PLC_IgwRegistrationEntity : BaseField
    {
        public PLC_IgwRegistrationEntity()
        {
            Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        }

        [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
        public long Id { get; set; }

        /// <summary>
        /// 关联的项目ID（项目隔离；台账查询/替换始终带此条件）。
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "关联的项目ID")]
        public long? ProjectId { get; set; }

        /// <summary>
        /// 根类型：DbBlock（DB 块）/ Io（IO 变量表整体）/ Graph（GRAPH 工站下的一个实例 DB）。
        /// 取值见 <see cref="IgwLedgerRootKind"/>。
        /// </summary>
        [SugarColumn(IsNullable = false, Length = 20, ColumnDescription = "根类型（DbBlock/Io/Graph）")]
        public string RootKind { get; set; } = string.Empty;

        /// <summary>
        /// 根名称：RootKind=DbBlock 时为原始块名（如 DB490_OP30Manual）；
        /// RootKind=Io 时固定为常量 "IO"；
        /// RootKind=Graph 时为 "{工站代码}{U+0001}{DB名}"（分隔符 IgwLedgerSnapshotBuilder.GraphRootNameSeparator = IgwTagNaming.KeySeparator，
        /// 工站为空用 IgwTagNaming.UngroupedStation，与读别名规则一致；旧版 "{工站}|{DB名}" 形式读取时兼容）。
        /// </summary>
        [SugarColumn(IsNullable = false, Length = 300, ColumnDescription = "根名称（块名 / IO / 工站|DB名）")]
        public string RootName { get; set; } = string.Empty;

        /// <summary>
        /// 规划器实际分配的 IGW 根组名（可能与 RootName 清洗后不同——重名/与 IO 根组冲突时规划器会改名，
        /// 见 IgwRegistrationPlanner.PlanResult.BlockRootRenames）。持久化后续规划可保持根组稳定，不随运行顺序漂移。
        /// </summary>
        [SugarColumn(IsNullable = true, Length = 128, ColumnDescription = "实际IGW根组名")]
        public string? RootGroup { get; set; }

        /// <summary>
        /// 本根落在哪台 IGW 设备：S7（经典地址）/ S7Plus（符号式）/ Graph（GRAPH 工步容器）。
        /// 取值对应 <see cref="IgwDeviceKind"/> 的字符串形式（Enum.ToString()/Enum.Parse）。
        /// </summary>
        [SugarColumn(IsNullable = true, Length = 20, ColumnDescription = "IGW设备类型（S7/S7Plus/Graph）")]
        public string? DeviceKind { get; set; }

        /// <summary>
        /// 本根成功注册的点位数。
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "成功注册的点位数")]
        public int? TagCount { get; set; }

        /// <summary>
        /// 本根跳过的行数（类型不可映射/地址非法/名称清洗为空等，见 IgwRegistrationPlanner.ReasonXxx）。
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "跳过的行数")]
        public int? SkippedCount { get; set; }

        /// <summary>
        /// 跳过原因汇总（Top N 原因及计数，供运维排查；≤500 字符，超出截断）。
        /// </summary>
        [SugarColumn(IsNullable = true, Length = 500, ColumnDescription = "跳过原因汇总")]
        public string? SkipSummary { get; set; }

        /// <summary>
        /// 本根的重名映射 JSON（读链据此把原始名映射为规划器实际使用的最终点名）：
        /// RootKind=DbBlock 行：{"变量路径": "最终点名"}；
        /// RootKind=Io 行：{"表名点名": "最终点名"}（ 分隔符见 IgwLedgerSnapshotBuilder.IoRenameKeySeparator，
        /// 与 IgwRegistrationPlanner 内部拼接组路径+点名键使用的同一约定字符一致，避免与合法名称字符冲突）；
        /// RootKind=Graph 行：未使用，恒为 null。
        /// 无重名时为空/null。
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDataType = "text", ColumnDescription = "重名映射JSON")]
        public string? RenamesJson { get; set; }

        /// <summary>
        /// 本根注册结果：Registered / Failed（瞬态，自动重试）/ Rejected（IGW 按内容永久拒绝，块变更或全量时才重发）/
        /// PendingClear（内容已写入，只剩另一台设备上的旧组待清除）/ FailedPendingClear（内容未全部写入且有旧组待清除，自动重做）。
        /// 取值见 <see cref="IgwLedgerStatus"/>。
        /// </summary>
        [SugarColumn(IsNullable = true, Length = 20, ColumnDescription = "注册结果（Registered/Failed/Rejected/PendingClear/FailedPendingClear）")]
        public string? Status { get; set; }

        /// <summary>
        /// 失败原因（IGW 批量接口错误原文等；≤1000 字符，超出截断）。
        /// </summary>
        [SugarColumn(IsNullable = true, Length = 1000, ColumnDescription = "失败原因")]
        public string? Error { get; set; }

        /// <summary>
        /// 本次注册时间。
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "本次注册时间")]
        public DateTime? RegisteredAt { get; set; }

        /// <summary>
        /// 本根组的内容指纹（2026-09-22 只追加注册）。下一轮注册若算出同一指纹且设备没变，
        /// **整根跳过**——不发点位也不发 replaceGroups。
        ///
        /// ⚠️ null/空 = 没记过 ⇒ **必须重发**。反过来当成"一致"的话，跳过机制从第一天起就是死的
        /// 且不报错；也正因如此，本列上线后的第一次注册天然是一次全量重发。
        /// </summary>
        [SugarColumn(IsNullable = true, Length = 64, ColumnDescription = "根组内容指纹")]
        public string? Fingerprint { get; set; }
    }
}
