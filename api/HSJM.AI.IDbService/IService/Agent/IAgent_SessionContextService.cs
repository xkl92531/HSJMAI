using System.Collections.Generic;
using System.Threading.Tasks;

namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 会话上下文服务接口
    /// </summary>
    public interface IAgent_SessionContextService : IBaseService<Agent_SessionContextEntity>
    {
        /// <summary>
        /// 获取会话的下一个轮次序号
        /// </summary>
        Task<int> GetNextTurnNumberAsync(long sessionId);

        /// <summary>
        /// 获取会话的所有上下文（未提取记忆的）
        /// </summary>
        Task<List<Agent_SessionContextEntity>> GetUnextractedContextsAsync(string systemId, string userId, int batchSize = 10);

        /// <summary>
        /// 标记已提取记忆
        /// </summary>
        Task MarkAsExtractedAsync(long contextId);
    }
}