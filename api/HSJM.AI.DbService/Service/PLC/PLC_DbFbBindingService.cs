using System.Collections.Concurrent;
using HSJM.AI.IDbService;
using HSJM.AI.Core;
using HSJM.AI.Utility;

namespace HSJM.AI.DbService
{
    /// <summary>
    /// DB块与FB绑定关系服务实现
    /// </summary>
    public class PLC_DbFbBindingService : AbstractBaseService<PLC_DbFbBindingEntity>, IPLC_DbFbBindingService, IAutoInject
    {
        /// <summary>
        /// S7-GRAPH 工步执行位（步序号激活位）在实例 DB 中的标准路径后缀
        /// </summary>
        public const string StepVarSuffix = "RT_DATA.EXEC_BITS.EXEC_BITS";

        /// <summary>
        /// "projectId|FB名" → 工步变量前缀 的进程级缓存。
        /// 值约定：null 不可入字典——未命中/查无绑定存 string.Empty，返回时转回 null。
        /// 上传重传 DB 块后调用 <see cref="InvalidateStepPrefixCache"/> 清空。
        /// </summary>
        private static readonly ConcurrentDictionary<string, string> _stepPrefixCache =
            new(StringComparer.OrdinalIgnoreCase);

        /// <inheritdoc />
        public async Task<string> GetStepVarPrefixByFbNameAsync(string fbName, long projectId)
        {
            var key = fbName?.Trim();
            if (string.IsNullOrEmpty(key) || projectId <= 0) return null;

            var cacheKey = $"{projectId}|{key}";
            if (_stepPrefixCache.TryGetValue(cacheKey, out var cached))
            {
                return cached.Length == 0 ? null : cached;
            }

            // 始终带项目过滤（同名 FB 跨项目存在）；同 FB 多实例 DB 时取字典序第一个保证确定性
            var dbName = (await GetListAsync(x =>
                    !x.IsDeleted
                    && x.ProjectId == projectId
                    && x.InstanceOfName == key))
                .Where(x => !string.IsNullOrEmpty(x.DbName))
                .OrderBy(x => x.DbName, StringComparer.Ordinal)
                .Select(x => x.DbName)
                .FirstOrDefault();

            var prefix = string.IsNullOrEmpty(dbName) ? null : $"{dbName}.{StepVarSuffix}";
            _stepPrefixCache[cacheKey] = prefix ?? string.Empty;
            return prefix;
        }

        /// <summary>
        /// 清空工步前缀缓存（上传事务提交后调用，防止重传换绑后读到旧 DB 名）
        /// </summary>
        public void InvalidateStepPrefixCache() => _stepPrefixCache.Clear();
    }
}
