using HSJM.AI.IDbService;
using HSJM.AI.Core;
using HSJM.AI.Utility;
using SqlSugar;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HSJM.AI.DbService
{
    /// <summary>
    /// IGW 自动注册台账服务实现（AI_PLC_IgwRegistration / 物理表 ai_plc_igwregistration，spec §4.5）。
    /// </summary>
    /// <remarks>
    /// 建表/补列策略（task-5 fix round 2 controller 决策，取代 round 1 的 CodeFirst 方案）：
    /// - 台账表由 <see cref="EnsureLedgerSchemaAsync"/> 幂等执行 <see cref="PLC_IgwRegistrationSchemaDdl"/>
    ///   里的显式 DDL 建表补索引（**不**走 <c>db.CodeFirst.InitTables&lt;PLC_IgwRegistrationEntity&gt;()</c>——
    ///   继承的 BaseField.CreaterName/ModifierName 带 <c>ColumnDataType="nvarchar"</c>，CodeFirst 在 PG 上必然
    ///   报 42704 type "nvarchar" does not exist，见该常量类注释），主入口是启动阶段的宿主初始化器
    ///   （AppLayer 的 IgwLedgerSchemaInitializer，Program.cs 里 AddHostedService 注册，后台执行不阻塞启动）；
    ///   <see cref="ReplaceForProjectAsync"/> / <see cref="GetLedgerAsync"/> 也会懒加载调用一次本方法兜底
    ///   （应对初始化器尚未跑完的启动竞态、或初始化器本身失败的情况）——失败不标记为"已完成"，
    ///   但为避免每次调用都重试 DDL，失败后 5 分钟内的后续调用直接跳过。
    /// - 项目表 <c>ai_plc_project_new</c> 的 <c>igwchannelname</c> 列同样由启动初始化器调用
    ///   <see cref="EnsureProjectChannelColumnAsync"/> 幂等 <c>ALTER TABLE ... ADD COLUMN IF NOT EXISTS</c> 补出；
    ///   该列**不**在 <see cref="PLC_ProjectEntity"/> 上映射（见该实体注释），<see cref="GetChannelNameAsync"/> /
    ///   <see cref="SetChannelNameAsync"/> 走裸表/裸列参数化原生 SQL，列缺失时捕获 42703（
    ///   <see cref="IgwSqlErrorClassifier.IsUndefinedColumn"/>）优雅降级，不依赖 EnsureProjectChannelColumnAsync
    ///   一定先被调用过。
    /// </remarks>
    public class PLC_IgwRegistrationService : AbstractBaseService<PLC_IgwRegistrationEntity>, IPLC_IgwRegistrationService, IAutoInject
    {
        private const string ProjectTable = "ai_plc_project_new";
        private const string ProjectChannelColumn = "igwchannelname";

        // 进程内建表/补列状态（仿 PLC_StationShadowEventContextService.EnsureSchemaAsync）：
        // 静态字段 + 信号量双检，避免并发调用都跑 DDL。失败不置 _schemaEnsured=true，改记失败时间，5 分钟内不重试
        // （task-5 fix round 1 新增：原实现失败也不重试，只在进程重启后才会再试一次，DB 短暂不可用时会永久卡住）。
        private static volatile bool _schemaEnsured;
        private static DateTime _lastSchemaFailureUtc = DateTime.MinValue;
        private static readonly SemaphoreSlim _schemaEnsureLock = new SemaphoreSlim(1, 1);
        private static readonly TimeSpan SchemaRetryCooldown = TimeSpan.FromMinutes(5);

        /// <inheritdoc />
        public async Task EnsureLedgerSchemaAsync()
        {
            if (_schemaEnsured)
            {
                return;
            }

            if (_lastSchemaFailureUtc != DateTime.MinValue && DateTime.UtcNow - _lastSchemaFailureUtc < SchemaRetryCooldown)
            {
                return;
            }

            await _schemaEnsureLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_schemaEnsured)
                {
                    return;
                }

                if (_lastSchemaFailureUtc != DateTime.MinValue && DateTime.UtcNow - _lastSchemaFailureUtc < SchemaRetryCooldown)
                {
                    return;
                }

                try
                {
                    var db = GetDb().Context;
                    // 不用 db.CodeFirst.InitTables<PLC_IgwRegistrationEntity>()：BaseField 的
                    // CreaterName/ModifierName 带 ColumnDataType="nvarchar"（SqlServer 方言），CodeFirst 在 PG 上
                    // 会报 42704 type "nvarchar" does not exist（与 PLC_StationShadowEventContextEntity 注释
                    // 记录的同一类历史 bug；task-5 fix round 2 改正）。改为直接执行显式 DDL，与
                    // tools/mig_20260919_igw_registration.sql 逐字节同源（PLC_IgwRegistrationSchemaDdl）。
                    await db.Ado.ExecuteCommandAsync(PLC_IgwRegistrationSchemaDdl.CreateTableSql).ConfigureAwait(false);
                    await db.Ado.ExecuteCommandAsync(PLC_IgwRegistrationSchemaDdl.CreateIndexSql).ConfigureAwait(false);
                    // 存量表补 fingerprint 列：CreateTableSql 是 IF NOT EXISTS，对已建好的表不生效
                    await db.Ado.ExecuteCommandAsync(PLC_IgwRegistrationSchemaDdl.AddFingerprintColumnSql).ConfigureAwait(false);
                    _schemaEnsured = true;
                    _lastSchemaFailureUtc = DateTime.MinValue;
                }
                catch (Exception ex)
                {
                    _lastSchemaFailureUtc = DateTime.UtcNow;
                    await WriteLogHelper.WriteLogsAsync(
                        $"[IGW台账] 台账表建表/补列失败（{SchemaRetryCooldown.TotalMinutes:0} 分钟后允许重试）：{ex.Message}",
                        "IGW注册台账").ConfigureAwait(false);
                }
            }
            finally
            {
                _schemaEnsureLock.Release();
            }
        }

        /// <inheritdoc />
        public async Task EnsureProjectChannelColumnAsync()
        {
            try
            {
                var db = GetDb().Context;
                if (db.CurrentConnectionConfig.DbType != SqlSugar.DbType.PostgreSQL)
                {
                    return;
                }

                // 项目表不受 CodeFirst 管理（全仓搜索确认没有任何地方对 PLC_ProjectEntity 调用过 InitTables），
                // 补列走显式 DDL；IF NOT EXISTS 保证幂等可重复执行。物理表/列全小写不带引号
                // （ai_plc_project_new 历史上从未按带引号大小写建过，见 CLAUDE.md §三）。
                await db.Ado.ExecuteCommandAsync(
                    $"ALTER TABLE {ProjectTable} ADD COLUMN IF NOT EXISTS {ProjectChannelColumn} varchar(128)")
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WriteLogHelper.WriteLogsAsync(
                    $"[IGW台账] 项目表补列 {ProjectChannelColumn} 失败（GetChannelNameAsync/SetChannelNameAsync 会在列缺失时"
                    + $"优雅降级，不阻断启动）：{ex.Message}",
                    "IGW注册台账").ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        public async Task<string?> GetChannelNameAsync(long projectId)
        {
            try
            {
                var db = GetDb().Context;
                return await db.Ado.SqlQuerySingleAsync<string>(
                    $"SELECT {ProjectChannelColumn} FROM {ProjectTable} WHERE id = @Id",
                    new SugarParameter("@Id", projectId)).ConfigureAwait(false);
            }
            catch (Exception ex) when (IgwSqlErrorClassifier.IsUndefinedColumn(ex))
            {
                // 列缺失（迁移脚本/启动补列尚未执行）：优雅降级，调用方按"无固定通道名"现算通道名
                return null;
            }
        }

        /// <inheritdoc />
        public async Task SetChannelNameAsync(long projectId, string channelName)
        {
            try
            {
                var db = GetDb().Context;
                await db.Ado.ExecuteCommandAsync(
                    $"UPDATE {ProjectTable} SET {ProjectChannelColumn} = @Name WHERE id = @Id",
                    new SugarParameter("@Name", (object?)channelName ?? DBNull.Value),
                    new SugarParameter("@Id", projectId)).ConfigureAwait(false);
            }
            catch (Exception ex) when (IgwSqlErrorClassifier.IsUndefinedColumn(ex))
            {
                await WriteLogHelper.WriteLogsAsync(
                    $"[IGW台账] 项目 {projectId} 写入通道名跳过（{ProjectChannelColumn} 列不存在，"
                    + "尚未执行迁移脚本/启动补列）", "IGW注册台账").ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        public async Task ReplaceForProjectAsync(
            long projectId,
            IEnumerable<PLC_IgwRegistrationEntity> entities,
            IEnumerable<(string RootKind, string RootName)> removedRoots)
        {
            await EnsureLedgerSchemaAsync().ConfigureAwait(false);

            var list = (entities ?? Enumerable.Empty<PLC_IgwRegistrationEntity>()).ToList();

            // 待删的 (RootKind, RootName) 键集合：本批插入的根 ∪ 调用方额外声明的已移除根（如整块被删除）。
            var keys = new HashSet<(string RootKind, string RootName)>();
            foreach (var row in list)
            {
                keys.Add((row.RootKind ?? string.Empty, row.RootName ?? string.Empty));
            }
            foreach (var removed in removedRoots ?? Enumerable.Empty<(string RootKind, string RootName)>())
            {
                keys.Add((removed.RootKind ?? string.Empty, removed.RootName ?? string.Empty));
            }

            if (keys.Count == 0 && list.Count == 0)
            {
                return;
            }

            // 删旧行 + 插入新行原子化：中途失败不留下"旧行已删、新行未插"的半截状态
            // （照抄 LineUpGraphNodeBomRepository.ReplaceBatchRowsAsync 的 BeginTran/CommitTran/RollbackTran 写法）。
            var db = GetDb().Context;
            db.Ado.BeginTran();
            try
            {
                if (keys.Count > 0)
                {
                    // 按 RootKind 分组批量硬删（IN 查询；M-5 2026-09-20：原软删会让每次注册都留下一批死行）。
                    // 不带 IsDeleted 条件：同键的历史软删行一并回收。
                    foreach (var group in keys.GroupBy(k => k.RootKind))
                    {
                        var rootKind = group.Key;
                        var rootNames = group.Select(k => k.RootName).ToList();
                        await db.Deleteable<PLC_IgwRegistrationEntity>()
                            .Where(x => x.ProjectId == projectId && x.RootKind == rootKind && rootNames.Contains(x.RootName))
                            .ExecuteCommandAsync()
                            .ConfigureAwait(false);
                    }
                }

                if (list.Count > 0)
                {
                    foreach (var row in list)
                    {
                        row.ProjectId = projectId;
                        row.IsDeleted = false;
                    }

                    await InsertRangeAsync(list).ConfigureAwait(false);
                }

                db.Ado.CommitTran();
            }
            catch
            {
                db.Ado.RollbackTran();
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<IgwLedgerSnapshot> GetLedgerAsync(long projectId)
        {
            await EnsureLedgerSchemaAsync().ConfigureAwait(false);
            return await LoadLedgerAsync(projectId).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task<IgwLedgerSnapshot> GetLedgerForReadAsync(long projectId) => LoadLedgerAsync(projectId);

        /// <inheritdoc />
        public async Task<int> PurgeSoftDeletedAsync()
        {
            await EnsureLedgerSchemaAsync().ConfigureAwait(false);
            var db = GetDb().Context;
            return await db.Ado.ExecuteCommandAsync(PLC_IgwRegistrationSchemaDdl.PurgeSoftDeletedSql).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task DeleteProjectLedgerAsync(long projectId)
        {
            await EnsureLedgerSchemaAsync().ConfigureAwait(false);
            var db = GetDb().Context;
            await db.Deleteable<PLC_IgwRegistrationEntity>()
                .Where(x => x.ProjectId == projectId)
                .ExecuteCommandAsync()
                .ConfigureAwait(false);
        }

        private async Task<IgwLedgerSnapshot> LoadLedgerAsync(long projectId)
        {
            var rows = await GetListAsync(x => x.ProjectId == projectId && x.IsDeleted == false).ConfigureAwait(false);
            if (rows.Count == 0)
            {
                return IgwLedgerSnapshot.Empty;
            }

            var snapshot = IgwLedgerSnapshotBuilder.Build(rows, out var malformedCount);
            if (malformedCount > 0)
            {
                await WriteLogHelper.WriteLogsAsync(
                    $"[IGW台账] 项目 {projectId} 台账加载：{malformedCount} 行 RenamesJson 解析失败已跳过",
                    "IGW注册台账").ConfigureAwait(false);
            }

            return snapshot;
        }
    }
}
