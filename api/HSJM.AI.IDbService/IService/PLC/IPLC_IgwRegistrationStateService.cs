using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace HSJM.AI.IDbService
{
    /// <summary>IGW 注册项目级状态（ai_plc_igwregistrationstate，2026-09-20 hardening）读写服务。全部方法内部先幂等建表。</summary>
    public interface IPLC_IgwRegistrationStateService
    {
        /// <summary>幂等建表建索引（失败只记日志、5 分钟内不重试，不抛出）</summary>
        Task EnsureSchemaAsync();

        Task<IgwRegistrationStateRow?> GetAsync(long projectId);

        /// <summary>登记最近一次成功写入 IGW 的注册身份（I-3）</summary>
        Task SaveIdentityAsync(long projectId, IgwRegistrationIdentity identity);

        /// <summary>记录（failedAt 非空）或清除（failedAt 为 null）GRAPH 注册写台账前失败（N-6）</summary>
        Task SaveGraphFailureAsync(long projectId, DateTime? failedAt, string? error);

        /// <summary>
        /// 写入待补跑范围（M-1，json 必须非空；规则见 <see cref="IgwPendingRecordRules.ResolveWrite"/>，事务内行锁原子执行）：
        /// 无记录 / 记录属于 <paramref name="queue"/> → 覆盖为本次快照；记录属于其它队列 → 用 <paramref name="mergeWithOtherOwner"/>
        /// 取并集、保留原所属队列（由其所属机器恢复）；有范围但无所属队列 → 并集后归本队列。
        /// </summary>
        Task SavePendingAsync(long projectId, string pendingArgsJson, string queue, Func<string, string, string> mergeWithOtherOwner);

        /// <summary>条件清除待补跑范围：只清属于 <paramref name="queue"/> 的记录，其它机器的记录不动（M-1 fix round 2）</summary>
        Task ClearPendingAsync(long projectId, string queue);

        /// <summary>指定本机队列下有待补跑的项目</summary>
        Task<IReadOnlyList<IgwRegistrationStateRow>> ListPendingAsync(string queue);

        /// <summary>除指定项目外全部未删除项目的网关配置 + 登记身份</summary>
        Task<IReadOnlyList<IgwPeerProjectRow>> ListPeerProjectsAsync(long projectId);

        /// <summary>删除项目的状态行（M-4 清理完成后）</summary>
        Task DeleteAsync(long projectId);
    }
}
