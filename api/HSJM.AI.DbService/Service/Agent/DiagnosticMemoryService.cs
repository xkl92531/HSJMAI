using HSJM.AI.Core;
using HSJM.AI.IDbService;

namespace HSJM.AI.DbService
{
    /// <summary>
    /// AI诊断记忆服务
    /// </summary>
    public class DiagnosticMemoryService : AbstractBaseService<DiagnosticMemoryEntity>, IDiagnosticMemoryService, IAutoInject
    {
    }
}