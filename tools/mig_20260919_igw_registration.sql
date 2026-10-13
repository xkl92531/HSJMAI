-- ============================================================================
-- IGW 自动注册台账表 + 项目通道名列（sub-project B task-5 / spec §4.5，fix round 2 修正版）
--
-- 目标库：本表由 PLC_IgwRegistrationService（继承 AbstractBaseService<T>）读写，与本项目其它所有
-- AbstractBaseService<T> 仓储（PLC_ProjectApp/PLC_IOConstraintApp/PLC_GraphStepTagService 等）共用同一个
-- ISqlSugarClient——该客户端在 api/HSJM.AI.IDbService/RegisterDbService.cs 的 AddScoped<ISqlSugarClient> 里
-- 固定选用 appsettings.json WebConfig:DBList 中 ConfigId="DefultDataBase" 的连接（RegisterDbService.cs:23,31；
-- fix round 1 曾误写成 "DefultDataBase1"，这里改正）。即当前环境该 ConfigId 配置串指向的实际业务库——
-- 具体是哪个库名以当次部署的 appsettings.json 为准，本脚本不假定固定库名，请对照该配置连接后执行。
--
-- 幂等：全部语句可重复执行，且与应用自身的建表/补列路径完全等价（见下）：启动阶段的 HostedService
--       IgwLedgerSchemaInitializer 后台调用 PLC_IgwRegistrationService.EnsureLedgerSchemaAsync /
--       EnsureProjectChannelColumnAsync，前者直接执行本文件里的同一段 CREATE TABLE/INDEX 语句
--       （PLC_IgwRegistrationSchemaDdl.CreateTableSql / CreateIndexSql，逐字节同源，
--       IgwRegistrationSchemaDdlTests 断言两处一致，避免漂移）。谁先跑（本脚本 or 应用启动）结果都一样，
--       可用于离线预建表，也可以什么都不做直接依赖应用自愈。
--
-- 为什么不用 db.CodeFirst.InitTables<PLC_IgwRegistrationEntity>()（fix round 2 修正 round 1 的方案）：
--   该实体继承 BaseField，其 CreaterName/ModifierName 两列带 ColumnDataType="nvarchar"（SqlServer 方言，
--   PG 无此类型），CodeFirst 在 PG 上会对这两列生成 nvarchar 类型定义直接报 42704
--   type "nvarchar" does not exist——与 PLC_StationShadowEventContextEntity.cs:44-47 注释记录的历史 bug
--   同一类（那张表因此故意不继承 BaseField，手写不带 nvarchar 的列定义）。本表改为完全手写 DDL，
--   彻底绕开 CodeFirst 的方言翻译，不依赖 CodeFirst 对 PG 的兼容性。
--
-- 大小写说明：RegisterDbService 的 CasePreservedTableCache 只给"连接时库里已存在、且表名大小写与全小写不同"
--   的旧表（如历史上以带引号 SQL 建出的 Basic_*/AI_LineUp_*/AI_PLC_GraphStepTag 等）在 EntityNameService 里
--   加引号访问；本表不在该集合里，常规 CRUD（InsertRangeAsync/GetListAsync/Updateable 等）会把实体
--   [SugarTable("AI_PLC_IgwRegistration")] 整体 ToLowerInvariant() 当无引号标识符访问，因此本脚本建成全小写、
--   不带引号，与常规 CRUD 的访问方式对齐。
-- ============================================================================

-- ----------------------------------------------------------------------------
-- 1. 台账主表：ai_plc_igwregistration（物理表全小写、不带引号）
--    列名 = api/HSJM.AI.IDbService/Models/PLC/PLC_IgwRegistrationEntity.cs 的属性名整体 ToLowerInvariant()
--    （含 BaseField 继承列）。以下语句与
--    api/HSJM.AI.IDbService/Models/PLC/PLC_IgwRegistrationSchemaDdl.cs 的 CreateTableSql 常量逐字节一致
--    （由 IgwRegistrationSchemaDdlTests 断言），修改任一处务必同步另一处。
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS ai_plc_igwregistration
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
);

COMMENT ON TABLE ai_plc_igwregistration IS 'IGW自动注册台账表';

-- 台账替换（PLC_IgwRegistrationService.ReplaceForProjectAsync）与读链加载（GetLedgerAsync）
-- 都按 (projectid, rootkind, rootname, isdeleted) 查询/更新，建组合索引；只覆盖未软删行以减小索引体积。
-- 与 PLC_IgwRegistrationSchemaDdl.CreateIndexSql 逐字节一致。
CREATE INDEX IF NOT EXISTS idx_ai_plc_igwregistration_project_root
    ON ai_plc_igwregistration (projectid, rootkind, rootname)
    WHERE isdeleted = false;

-- ----------------------------------------------------------------------------
-- 2. 项目表补列：igwchannelname
--    ai_plc_project_new 物理表名全小写、不带引号（历史遗留：实体 SugarTable 属性写的是驼峰
--    "AI_PLC_Project_new"，但库里从未以该大小写建过表——见 CLAUDE.md §三"注意：所有表名小写（PG 默认
--    行为）；Entity 的 SugarTable attribute 用驼峰"）。该表不受 CodeFirst 管理，补列必须走本语句；
--    列名同理全小写、不带引号。
--    IgwChannelName **不**在 PLC_ProjectEntity 上做 SqlSugar 映射（task-5 fix round 1 决策：避免项目表的
--    整体 Update 把本列覆盖成 NULL），读写走 PLC_IgwRegistrationService.GetChannelNameAsync /
--    SetChannelNameAsync 的裸表/裸列参数化原生 SQL。
-- ----------------------------------------------------------------------------
ALTER TABLE ai_plc_project_new
    ADD COLUMN IF NOT EXISTS igwchannelname varchar(128);

COMMENT ON COLUMN ai_plc_project_new.igwchannelname IS 'IGW注册通道名（首次注册后固定，见IgwTagNaming.ChannelName）';
