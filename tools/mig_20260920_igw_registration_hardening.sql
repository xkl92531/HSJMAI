-- ============================================================================
-- IGW 自动注册 hardening（2026-09-20）：项目级注册状态表 + 回收软删台账行
--
-- 目标库：与 tools/mig_20260919_igw_registration.sql 相同（WebConfig:DBList 中 ConfigId="DefultDataBase" 的业务库）。
-- 幂等：全部语句可重复执行；应用启动时 IgwLedgerSchemaInitializer 执行同样的语句
-- （PLC_IgwRegistrationStateSchemaDdl / PLC_IgwRegistrationSchemaDdl.PurgeSoftDeletedSql，逐字节同源，
--  IgwRegistrationStateTests 断言），谁先跑结果都一样。
-- 表/列全小写不带引号；不继承 BaseField、不走 CodeFirst（避免 nvarchar 方言坑）。
-- ============================================================================

-- 1. 项目级注册状态表：注册身份（I-3）/ GRAPH 写台账前失败（N-6）/ 待补跑范围（M-1）
CREATE TABLE IF NOT EXISTS ai_plc_igwregistrationstate
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
);

CREATE INDEX IF NOT EXISTS idx_ai_plc_igwregistrationstate_pending
    ON ai_plc_igwregistrationstate (pendingqueue)
    WHERE pendingargsjson IS NOT NULL;

COMMENT ON TABLE ai_plc_igwregistrationstate IS 'IGW注册项目级状态（注册身份/GRAPH失败/待补跑）';

-- 2. 回收历史软删台账行（台账替换自 2026-09-20 起改为硬删；需先执行 mig_20260919_igw_registration.sql）
DELETE FROM ai_plc_igwregistration WHERE isdeleted = true;
