using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.Utility;
using SqlSugar;

namespace HSJM.AI.DbService
{
    /// <summary>
    /// IGW 注册项目级状态表读写（原生 SQL；表由 <see cref="PLC_IgwRegistrationStateSchemaDdl"/> 幂等建出）。
    /// 写入一律 INSERT ... ON CONFLICT (projectid) DO UPDATE 只改本方法负责的列，互不覆盖。
    /// </summary>
    public sealed class PLC_IgwRegistrationStateService : IPLC_IgwRegistrationStateService, IAutoInject
    {
        private const string Table = "ai_plc_igwregistrationstate";

        private const string SelectColumns =
            "projectid AS \"ProjectId\", registerediotbaseurl AS \"RegisteredIotBaseUrl\", registeredplcip AS \"RegisteredPlcIp\", "
            + "registeredchannel AS \"RegisteredChannel\", registeredat AS \"RegisteredAt\", graphfailedat AS \"GraphFailedAt\", "
            + "graphfailure AS \"GraphFailure\", pendingargsjson AS \"PendingArgsJson\", pendingqueue AS \"PendingQueue\", "
            + "pendingat AS \"PendingAt\"";

        private const int MaxGraphFailureLength = 1000;

        private static volatile bool _schemaEnsured;
        private static DateTime _lastSchemaFailureUtc = DateTime.MinValue;
        private static readonly SemaphoreSlim SchemaLock = new(1, 1);
        private static readonly TimeSpan SchemaRetryCooldown = TimeSpan.FromMinutes(5);

        private readonly ISqlSugarClient _db;

        public PLC_IgwRegistrationStateService(ISqlSugarClient db)
        {
            _db = db;
        }

        public async Task EnsureSchemaAsync()
        {
            if (_schemaEnsured || InCooldown())
            {
                return;
            }
            await SchemaLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_schemaEnsured || InCooldown())
                {
                    return;
                }
                try
                {
                    await _db.Ado.ExecuteCommandAsync(PLC_IgwRegistrationStateSchemaDdl.CreateTableSql).ConfigureAwait(false);
                    await _db.Ado.ExecuteCommandAsync(PLC_IgwRegistrationStateSchemaDdl.CreatePendingIndexSql).ConfigureAwait(false);
                    _schemaEnsured = true;
                    _lastSchemaFailureUtc = DateTime.MinValue;
                }
                catch (Exception ex)
                {
                    _lastSchemaFailureUtc = DateTime.UtcNow;
                    await WriteLogHelper.WriteLogsAsync(
                        $"[IGW注册状态] 建表失败（{SchemaRetryCooldown.TotalMinutes:0} 分钟后允许重试）：{ex.Message}", "IGW注册台账").ConfigureAwait(false);
                }
            }
            finally
            {
                SchemaLock.Release();
            }
        }

        private static bool InCooldown() =>
            _lastSchemaFailureUtc != DateTime.MinValue && DateTime.UtcNow - _lastSchemaFailureUtc < SchemaRetryCooldown;

        public async Task<IgwRegistrationStateRow?> GetAsync(long projectId)
        {
            await EnsureSchemaAsync().ConfigureAwait(false);
            var rows = await _db.Ado.SqlQueryAsync<IgwRegistrationStateRow>(
                $"SELECT {SelectColumns} FROM {Table} WHERE projectid = @Id",
                new SugarParameter("@Id", projectId)).ConfigureAwait(false);
            return rows.FirstOrDefault();
        }

        public async Task SaveIdentityAsync(long projectId, IgwRegistrationIdentity identity)
        {
            ArgumentNullException.ThrowIfNull(identity);
            await EnsureSchemaAsync().ConfigureAwait(false);
            await _db.Ado.ExecuteCommandAsync(
                $"INSERT INTO {Table} (projectid, registerediotbaseurl, registeredplcip, registeredchannel, registeredat, updatedat) "
                + "VALUES (@Id, @Url, @Ip, @Channel, @Now, @Now) "
                + "ON CONFLICT (projectid) DO UPDATE SET registerediotbaseurl = EXCLUDED.registerediotbaseurl, "
                + "registeredplcip = EXCLUDED.registeredplcip, registeredchannel = EXCLUDED.registeredchannel, "
                + "registeredat = EXCLUDED.registeredat, updatedat = EXCLUDED.updatedat",
                new SugarParameter("@Id", projectId),
                new SugarParameter("@Url", identity.IgwBaseUrl),
                new SugarParameter("@Ip", identity.PlcIp),
                new SugarParameter("@Channel", identity.ChannelName),
                new SugarParameter("@Now", DateTime.Now)).ConfigureAwait(false);
        }

        public async Task SaveGraphFailureAsync(long projectId, DateTime? failedAt, string? error)
        {
            await EnsureSchemaAsync().ConfigureAwait(false);
            var text = failedAt == null || string.IsNullOrEmpty(error)
                ? null
                : error.Length > MaxGraphFailureLength ? error.Substring(0, MaxGraphFailureLength) : error;
            await _db.Ado.ExecuteCommandAsync(
                $"INSERT INTO {Table} (projectid, graphfailedat, graphfailure, updatedat) VALUES (@Id, @At, @Error, @Now) "
                + "ON CONFLICT (projectid) DO UPDATE SET graphfailedat = EXCLUDED.graphfailedat, "
                + "graphfailure = EXCLUDED.graphfailure, updatedat = EXCLUDED.updatedat",
                new SugarParameter("@Id", projectId),
                new SugarParameter("@At", (object?)failedAt ?? DBNull.Value),
                new SugarParameter("@Error", (object?)text ?? DBNull.Value),
                new SugarParameter("@Now", DateTime.Now)).ConfigureAwait(false);
        }

        /// <summary>条件清除：只清属于指定队列的待补跑记录（M-1 fix round 2）</summary>
        internal const string ClearPendingSql =
            "UPDATE " + Table + " SET pendingargsjson = NULL, pendingqueue = NULL, pendingat = NULL, updatedat = @Now "
            + "WHERE projectid = @Id AND pendingqueue = @Queue";

        /// <summary>
        /// 事务内：先确保行存在（不存在的行无法加行锁），再 SELECT ... FOR UPDATE 锁住该项目行读出现有记录，
        /// 按 <see cref="IgwPendingRecordRules.ResolveWrite"/> 算出最终范围与所属队列后写回——并发写入（含其它机器）串行化，不互相覆盖。
        /// <para>警告：本方法自行开启并提交事务，不能在外层事务内调用（嵌套会提前提交 / 回滚外层事务）——
        /// 调用时当前连接已有事务则抛 <see cref="InvalidOperationException"/>，不做任何写入。</para>
        /// </summary>
        public async Task SavePendingAsync(long projectId, string pendingArgsJson, string queue, Func<string, string, string> mergeWithOtherOwner)
        {
            if (string.IsNullOrEmpty(pendingArgsJson))
            {
                throw new ArgumentException("待补跑范围不能为空（清除请用 ClearPendingAsync）", nameof(pendingArgsJson));
            }
            if (string.IsNullOrEmpty(queue))
            {
                throw new ArgumentException("待补跑记录必须有所属队列", nameof(queue));
            }
            var ado = _db.Ado;
            if (ado.Transaction != null)
            {
                throw new InvalidOperationException("SavePendingAsync 自行管理事务，不能在外层事务内调用（请在外层事务提交后再写待补跑范围）");
            }
            await EnsureSchemaAsync().ConfigureAwait(false);
            ado.BeginTran();
            try
            {
                var now = DateTime.Now;
                await ado.ExecuteCommandAsync(
                    $"INSERT INTO {Table} (projectid, updatedat) VALUES (@Id, @Now) ON CONFLICT (projectid) DO NOTHING",
                    new SugarParameter("@Id", projectId),
                    new SugarParameter("@Now", now)).ConfigureAwait(false);
                var rows = await ado.SqlQueryAsync<IgwRegistrationStateRow>(
                    $"SELECT projectid AS \"ProjectId\", pendingargsjson AS \"PendingArgsJson\", pendingqueue AS \"PendingQueue\" "
                    + $"FROM {Table} WHERE projectid = @Id FOR UPDATE",
                    new SugarParameter("@Id", projectId)).ConfigureAwait(false);
                var existing = rows.FirstOrDefault();
                var (json, owner) = IgwPendingRecordRules.ResolveWrite(existing?.PendingArgsJson, existing?.PendingQueue,
                    pendingArgsJson, queue, mergeWithOtherOwner);
                await ado.ExecuteCommandAsync(
                    $"UPDATE {Table} SET pendingargsjson = @Json, pendingqueue = @Queue, pendingat = @Now, updatedat = @Now WHERE projectid = @Id",
                    new SugarParameter("@Id", projectId),
                    new SugarParameter("@Json", json),
                    new SugarParameter("@Queue", owner),
                    new SugarParameter("@Now", now)).ConfigureAwait(false);
                ado.CommitTran();
            }
            catch (Exception ex)
            {
                try
                {
                    ado.RollbackTran();
                }
                catch (Exception rollbackEx)
                {
                    // 回滚失败不能掩盖原始异常（连接断开时回滚本身也会失败，事务随连接关闭由数据库回滚）
                    Console.WriteLine($"[IGW注册状态] 项目 {projectId} 待补跑写入回滚失败：{rollbackEx.Message}（原始错误：{ex.Message}）");
                }
                throw;
            }
        }

        public async Task ClearPendingAsync(long projectId, string queue)
        {
            if (string.IsNullOrEmpty(queue))
            {
                return;
            }
            await EnsureSchemaAsync().ConfigureAwait(false);
            await _db.Ado.ExecuteCommandAsync(ClearPendingSql,
                new SugarParameter("@Id", projectId),
                new SugarParameter("@Queue", queue),
                new SugarParameter("@Now", DateTime.Now)).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<IgwRegistrationStateRow>> ListPendingAsync(string queue)
        {
            await EnsureSchemaAsync().ConfigureAwait(false);
            return await _db.Ado.SqlQueryAsync<IgwRegistrationStateRow>(
                $"SELECT {SelectColumns} FROM {Table} WHERE pendingargsjson IS NOT NULL AND pendingqueue = @Queue ORDER BY pendingat",
                new SugarParameter("@Queue", queue)).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<IgwPeerProjectRow>> ListPeerProjectsAsync(long projectId)
        {
            await EnsureSchemaAsync().ConfigureAwait(false);
            var projects = await _db.Queryable<PLC_ProjectEntity>()
                .Where(p => p.IsDeleted == false && p.Id != projectId)
                .Select(p => new PLC_ProjectEntity { Id = p.Id, ProjectName = p.ProjectName, IotUrl = p.IotUrl, PlcIp = p.PlcIp })
                .ToListAsync().ConfigureAwait(false);
            var states = await _db.Ado.SqlQueryAsync<IgwRegistrationStateRow>($"SELECT {SelectColumns} FROM {Table}").ConfigureAwait(false);
            var stateById = states.GroupBy(s => s.ProjectId).ToDictionary(g => g.Key, g => g.First());
            var channelById = await LoadConfiguredChannelsAsync(projectId).ConfigureAwait(false);
            return projects.Select(p => new IgwPeerProjectRow
            {
                ProjectId = p.Id,
                ProjectName = p.ProjectName,
                IotUrl = p.IotUrl,
                PlcIp = p.PlcIp,
                ConfiguredChannel = channelById != null && channelById.TryGetValue(p.Id, out var channel) ? channel : null,
                ConfiguredChannelUnknown = channelById == null,
                RegisteredIdentity = stateById.TryGetValue(p.Id, out var state) ? state.RegisteredIdentity : null,
            }).ToList();
        }

        /// <summary>
        /// 其它未删除项目的 igwchannelname 列（不在 PLC_ProjectEntity 映射，裸 SQL）；列缺失（42703，迁移未执行）→ null
        /// （通道未知，调用方按「可能占用旧位置」处理，不能当成「都没注册过」）。
        /// </summary>
        private async Task<Dictionary<long, string?>?> LoadConfiguredChannelsAsync(long projectId)
        {
            try
            {
                var table = await _db.Ado.GetDataTableAsync(
                    "SELECT id, igwchannelname FROM ai_plc_project_new "
                    + "WHERE isdeleted = false AND id <> @Id AND igwchannelname IS NOT NULL",
                    new SugarParameter("@Id", projectId)).ConfigureAwait(false);
                var result = new Dictionary<long, string?>();
                foreach (System.Data.DataRow row in table.Rows)
                {
                    result[Convert.ToInt64(row["id"])] = row["igwchannelname"] as string;
                }
                return result;
            }
            catch (Exception ex) when (IgwSqlErrorClassifier.IsUndefinedColumn(ex))
            {
                return null;
            }
        }

        public async Task DeleteAsync(long projectId)
        {
            await EnsureSchemaAsync().ConfigureAwait(false);
            await _db.Ado.ExecuteCommandAsync($"DELETE FROM {Table} WHERE projectid = @Id",
                new SugarParameter("@Id", projectId)).ConfigureAwait(false);
        }
    }
}
