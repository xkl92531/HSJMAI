namespace HSJM.AI.IDbService
{
    /// <summary>
    /// IGW 注册项目级状态表 ai_plc_igwregistrationstate 的 DDL（2026-09-20 hardening，唯一权威来源）。
    /// 每个项目一行：最近一次成功写入 IGW 的注册身份（I-3）、GRAPH 注册写台账前失败的时间/原因（N-6）、
    /// 待补跑注册范围与所属本机队列（M-1）。原生 SQL 读写（PLC_IgwRegistrationStateService），不走实体/CodeFirst，
    /// 不继承 BaseField（避免 nvarchar 方言坑）。表/列全小写不带引号。
    /// 与 tools/mig_20260920_igw_registration_hardening.sql 中同名语句逐字节一致（IgwRegistrationStateTests 断言）。
    /// </summary>
    public static class PLC_IgwRegistrationStateSchemaDdl
    {
        public const string CreateTableSql = @"CREATE TABLE IF NOT EXISTS ai_plc_igwregistrationstate
(
    projectid              bigint        NOT NULL,
    registerediotbaseurl   varchar(500)  NULL,
    registeredplcip        varchar(64)   NULL,
    registeredchannel      varchar(128)  NULL,
    registeredat           timestamp     NULL,
    graphfailedat          timestamp     NULL,
    graphfailure           varchar(1000) NULL,
    pendingargsjson        text          NULL,
    pendingqueue           varchar(128)  NULL,
    pendingat              timestamp     NULL,
    updatedat              timestamp     NULL,
    CONSTRAINT pk_ai_plc_igwregistrationstate PRIMARY KEY (projectid)
);";

        /// <summary>启动恢复按本机队列查有待补跑的项目</summary>
        public const string CreatePendingIndexSql = @"CREATE INDEX IF NOT EXISTS idx_ai_plc_igwregistrationstate_pending
    ON ai_plc_igwregistrationstate (pendingqueue)
    WHERE pendingargsjson IS NOT NULL;";
    }
}
