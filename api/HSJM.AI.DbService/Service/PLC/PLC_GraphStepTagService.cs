using HSJM.AI.IDbService;
using HSJM.AI.Core;
using HSJM.AI.Utility;

namespace HSJM.AI.DbService
{
    /// <summary>
    /// GRAPH步位注册台账服务实现
    /// </summary>
    public class PLC_GraphStepTagService : AbstractBaseService<PLC_GraphStepTagEntity>, IPLC_GraphStepTagService, IAutoInject
    {
        /// <inheritdoc />
        public async Task<List<PLC_GraphStepTagEntity>> GetByProjectAndDbNameAsync(long projectId, string dbName)
        {
            return await GetListAsync(x => !x.IsDeleted && x.ProjectId == projectId && x.DbName == dbName);
        }
    }
}
