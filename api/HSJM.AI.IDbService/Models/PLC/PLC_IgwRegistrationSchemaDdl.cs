namespace HSJM.AI.IDbService
{
    /// <summary>
    /// AI_PLC_IgwRegistration 台账表的建表 DDL（唯一权威来源，task-5 fix round 2）。
    /// </summary>
    /// <remarks>
    /// 不用 <c>db.CodeFirst.InitTables&lt;PLC_IgwRegistrationEntity&gt;()</c>——<c>HSJM.AI.Core.BaseField</c> 的
    /// CreaterName/ModifierName 两列带 <c>ColumnDataType = "nvarchar"</c>（SqlServer 方言，PG 无此类型）,
    /// CodeFirst 在 PG 上会对这两列生成 <c>nvarchar</c> 类型定义直接报 42704 <c>type "nvarchar" does not exist</c>
    /// （与 <c>PLC_StationShadowEventContextEntity</c> 注释记录的同一类历史 bug；该实体因此故意不继承
    /// BaseField、改手写不带 nvarchar 的列定义）。本表改为在 <c>PLC_IgwRegistrationService.EnsureLedgerSchemaAsync</c>
    /// 里对 <see cref="CreateTableSql"/> / <see cref="CreateIndexSql"/> 直接 <c>db.Ado.ExecuteCommandAsync</c>，
    /// 彻底绕开 CodeFirst 的方言翻译。
    /// <para>
    /// 这两个常量与 <c>tools/mig_20260919_igw_registration.sql</c> 里的同名语句要求逐字节一致——
    /// <c>IgwRegistrationSchemaDdlTests</c> 会读取该 .sql 文件断言其原文包含这两段常量文本，防止两处漂移。
    /// 改动任一处（改列/改索引）都必须同步改另一处，并重跑该测试确认仍然一致。
    /// </para>
    /// </remarks>
    public static class PLC_IgwRegistrationSchemaDdl
    {
        /// <summary>
        /// 建表语句。列的可空性/默认值对齐 BaseField 实际写入行为：
        /// isdeleted 非空默认 false（C# bool 默认值）；createrid/modifierid 可空默认 0
        /// （BaseField 属性 <c>long? = 0</c>）；creatername/createtime/modifierid/modifiername/modifytime
        /// 均可空（AOP DataExecuting 在 Insert/Update 时才填充，建表阶段不能假设非空）。
        /// </summary>
        public const string CreateTableSql = @"CREATE TABLE IF NOT EXISTS ai_plc_igwregistration
(
    id             bigint        NOT NULL,
    projectid      bigint        NULL,
    rootkind       varchar(20)   NOT NULL,
    rootname       varchar(300)  NOT NULL,
    rootgroup      varchar(128)  NULL,
    devicekind     varchar(20)   NULL,
    tagcount       integer       NULL,
    skippedcount   integer       NULL,
    skipsummary    varchar(500)  NULL,
    renamesjson    text          NULL,
    status         varchar(20)   NULL,
    error          varchar(1000) NULL,
    registeredat   timestamp     NULL,
    isdeleted      boolean       NOT NULL DEFAULT false,
    createrid      bigint        NULL DEFAULT 0,
    creatername    varchar(50)   NULL,
    createtime     timestamp     NULL,
    modifierid     bigint        NULL DEFAULT 0,
    modifiername   varchar(50)   NULL,
    modifytime     timestamp     NULL,
    CONSTRAINT pk_ai_plc_igwregistration PRIMARY KEY (id)
);";

        /// <summary>
        /// 台账替换（ReplaceForProjectAsync）与读链加载（GetLedgerAsync）都按
        /// (projectid, rootkind, rootname, isdeleted) 查询/更新，建组合索引；只覆盖未软删行以减小索引体积。
        /// </summary>
        public const string CreateIndexSql = @"CREATE INDEX IF NOT EXISTS idx_ai_plc_igwregistration_project_root
    ON ai_plc_igwregistration (projectid, rootkind, rootname)
    WHERE isdeleted = false;";

        /// <summary>
        /// 硬删历史软删行（M-5，2026-09-20）：台账是「最近一次注册结果」，没有审计价值；替换已改为硬删，
        /// 本语句只回收旧版本（替换软删）与删项目级联软删留下的行。启动初始化器每次启动执行一次，幂等。
        /// 与 tools/mig_20260920_igw_registration_hardening.sql 中同名语句逐字节一致（Task 5 的测试断言）。
        /// </summary>
        public const string PurgeSoftDeletedSql = "DELETE FROM ai_plc_igwregistration WHERE isdeleted = true;";

        /// <summary>
        /// 给存量表补 fingerprint 列（2026-09-22 只追加注册）。CreateTableSql 是 IF NOT EXISTS，
        /// 对已经建好的表不生效，所以补列要单独来一条。幂等，可重复执行。
        ///
        /// 补出来的列全是 NULL，而 NULL 语义是「没记过 ⇒ 必须重发」——所以上线后的第一次注册
        /// 会把所有根重发一遍，之后才进入稳态。这不是缺陷，是必须的：
        /// 反过来把 NULL 当成"一致"的话，跳过机制从第一天起就是死的且不报错。
        /// </summary>
        public const string AddFingerprintColumnSql =
            "ALTER TABLE ai_plc_igwregistration ADD COLUMN IF NOT EXISTS fingerprint varchar(64) NULL;";
    }
}
