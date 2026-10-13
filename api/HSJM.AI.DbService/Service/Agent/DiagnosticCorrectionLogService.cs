using HSJM.AI.Core;
using HSJM.AI.IDbService;

namespace HSJM.AI.DbService
{
    /// <summary>
    /// AI诊断校正日志服务
    /// </summary>
    public class DiagnosticCorrectionLogService : AbstractBaseService<DiagnosticCorrectionLogEntity>, IDiagnosticCorrectionLogService, IAutoInject
    {
    }
}