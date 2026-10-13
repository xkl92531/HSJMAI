using HSJM.AI.Core;
using HSJM.AI.IDbService;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using SqlSugar;

namespace HSJM.AI.DbService
{
    /// <summary>
    /// 会话上下文服务实现
    /// </summary>
    public class Agent_SessionContextService : AbstractBaseService<Agent_SessionContextEntity>, IAgent_SessionContextService, IAutoInject
    {
        /// <summary>
        /// 获取会话的下一个轮次序号
        /// </summary>
        public async Task<int> GetNextTurnNumberAsync(long sessionId)
        {
            var maxTurn = await Db.Queryable<Agent_SessionContextEntity>()
                .Where(c => c.SessionId == sessionId && c.IsActive == true)
                .MaxAsync(c => (int?)c.TurnNumber);
            return (maxTurn ?? 0) + 1;
        }

        /// <summary>
        /// 获取会话的所有上下文（未提取记忆的）
        /// </summary>
        public async Task<List<Agent_SessionContextEntity>> GetUnextractedContextsAsync(string systemId, string userId, int batchSize = 10)
        {
            return await Db.Queryable<Agent_SessionContextEntity>()
                .Where(c => c.SystemId == systemId
                         && c.UserId == userId
                         && c.IsActive == true
                         && c.IsMemoryExtracted == false
                         && !string.IsNullOrEmpty(c.UserMessage)
                         && !string.IsNullOrEmpty(c.AssistantMessage)) // 必须是完整的对话轮次
                .OrderBy(c => c.TurnNumber, OrderByType.Asc)
                .Take(batchSize)
                .ToListAsync();
        }

        /// <summary>
        /// 标记已提取记忆
        /// </summary>
        public async Task MarkAsExtractedAsync(long contextId)
        {
            await Db.Updateable<Agent_SessionContextEntity>()
                .SetColumns(c => new Agent_SessionContextEntity
                {
                    IsMemoryExtracted = true,
                    ExtractedAt = DateTime.Now
                })
                .Where(c => c.Id == contextId)
                .ExecuteCommandAsync();
        }
    }
}