using HSJM.AI.Core;
using HSJM.AI.IDbService;

namespace HSJM.AI.DbService
{
    /// <summary>
    /// AI诊断快照服务
    /// </summary>
    public class DiagnosticSnapshotService : AbstractBaseService<DiagnosticSnapshotEntity>, IDiagnosticSnapshotService, IAutoInject
    {
    }
}