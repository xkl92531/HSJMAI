using System.Collections.Generic;
using System.Threading.Tasks;

namespace HSJM.AI.IDbService
{
    /// <summary>
    /// IGW 自动注册台账服务接口（AI_PLC_IgwRegistration，物理表 ai_plc_igwregistration，spec §4.5）。
    /// </summary>
    public interface IPLC_IgwRegistrationService : IBaseService<PLC_IgwRegistrationEntity>
    {
        /// <summary>
        /// 替换指定项目的台账行（原子操作，单事务）：
        /// 1. 先**硬删**该项目下 (RootKind, RootName) 命中 <paramref name="entities"/> 自带键、或命中 <paramref name="removedRoots"/>
        ///    （已不再需要注册的根，如整块被删除、GRAPH 绑定被撤销）的全部旧行（含历史软删行，M-5 2026-09-20 起不再软删）；
        /// 2. 再插入 <paramref name="entities"/>。
        /// <paramref name="entities"/> 的 ProjectId 由本方法统一写为 <paramref name="projectId"/>（调用方无需预先填充）。
        /// </summary>
        Task ReplaceForProjectAsync(
            long projectId,
            IEnumerable<PLC_IgwRegistrationEntity> entities,
            IEnumerable<(string RootKind, string RootName)> removedRoots);

        /// <summary>
        /// 加载指定项目全部未删除台账行，折叠为读链直接可用的纯内存快照（见 <see cref="IgwLedgerSnapshotBuilder"/>）。
        /// 项目从未注册过 / 台账为空时返回 <see cref="IgwLedgerSnapshot.Empty"/>。
        /// RenamesJson 解析失败的行会被跳过并记一条日志（不影响其余行）。
        /// </summary>
        Task<IgwLedgerSnapshot> GetLedgerAsync(long projectId);

        /// <summary>
        /// 读链热路径专用（PLC_VariableAccessService 按台账拼读别名，task-8）：同 <see cref="GetLedgerAsync"/>，
        /// 但**不**调用 <see cref="EnsureLedgerSchemaAsync"/>——建表由启动初始化器与注册任务负责，读链不跑 DDL。
        /// 表不存在等错误直接抛出，由读链捕获后按空台账回退。
        /// </summary>
        Task<IgwLedgerSnapshot> GetLedgerForReadAsync(long projectId);

        /// <summary>
        /// 读取项目的 IGW 注册通道名（<c>ai_plc_project_new.igwchannelname</c>，裸表/裸列参数化原生 SQL，
        /// 不经 <see cref="PLC_ProjectEntity"/> 映射——该实体故意不映射本列，见其注释）。
        /// 列缺失（迁移脚本/启动补列尚未执行）时优雅降级返回 null，不抛异常。
        /// </summary>
        Task<string?> GetChannelNameAsync(long projectId);

        /// <summary>
        /// 写入项目的 IGW 注册通道名（同 <see cref="GetChannelNameAsync"/> 的裸表/裸列原生 SQL 路径）。
        /// 列缺失时记日志并跳过（不抛异常，不阻断调用方的注册流程）。
        /// </summary>
        Task SetChannelNameAsync(long projectId, string channelName);

        /// <summary>
        /// 确保台账表（AI_PLC_IgwRegistration / 物理表 ai_plc_igwregistration）存在，供启动阶段的宿主初始化器
        /// （IgwLedgerSchemaInitializer）显式调用一次；本服务内部的写/读方法也会懒加载兜底调用本方法
        /// （失败后 5 分钟内跳过重试，避免每次调用都重跑 DDL）。
        /// </summary>
        Task EnsureLedgerSchemaAsync();

        /// <summary>
        /// 幂等补出项目表的 IgwChannelName 物理列（<c>ALTER TABLE ai_plc_project_new ADD COLUMN IF NOT EXISTS
        /// igwchannelname varchar(128)</c>）。只应由启动阶段的宿主初始化器调用一次；内部已 try-catch，失败只记日志、
        /// 不抛出（Get/Set 方法各自独立处理列缺失，不依赖本方法先被调用过）。
        /// </summary>
        Task EnsureProjectChannelColumnAsync();

        /// <summary>
        /// 硬删全部项目的软删台账行（<see cref="PLC_IgwRegistrationSchemaDdl.PurgeSoftDeletedSql"/>），返回删除行数。
        /// 启动初始化器调用；表不存在时由 <see cref="EnsureLedgerSchemaAsync"/> 先建表。
        /// </summary>
        Task<int> PurgeSoftDeletedAsync();

        /// <summary>硬删指定项目的全部台账行（含软删行；M-4 删除项目清理 IGW 成功后调用）</summary>
        Task DeleteProjectLedgerAsync(long projectId);
    }
}
