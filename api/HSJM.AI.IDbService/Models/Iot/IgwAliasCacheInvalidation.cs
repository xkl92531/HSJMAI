using System;

namespace HSJM.AI.IDbService
{
    /// <summary>
    /// IGW 读别名项目级缓存失效通知（sub-project B）：注册任务写完台账后调用 <see cref="Invalidate"/>，
    /// 读链（PLC_VariableAccessService 按台账拼读别名的项目级缓存，task-8）订阅 <see cref="Invalidated"/> 清掉该项目缓存。
    /// 放在 IDbService：AppLayer（注册）与 DbService（读链）都引用本项目，二者无需互相引用。
    /// </summary>
    public static class IgwAliasCacheInvalidation
    {
        /// <summary>项目台账已变化（参数：项目 Id）。订阅方异常被隔离，不影响注册任务与其它订阅方。</summary>
        public static event Action<long>? Invalidated;

        public static void Invalidate(long projectId)
        {
            var handlers = Invalidated;
            if (handlers == null)
            {
                return;
            }
            foreach (Action<long> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(projectId);
                }
                catch
                {
                    // 缓存失效只是优化：订阅方异常不能让注册任务失败
                }
            }
        }
    }
}
