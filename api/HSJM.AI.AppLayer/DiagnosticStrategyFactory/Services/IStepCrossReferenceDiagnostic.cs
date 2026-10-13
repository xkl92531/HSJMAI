using System.Threading.Tasks;
using HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS;

namespace HSJM.AI.AppLayer.DiagnosticStrategyFactory.Services
{
    /// <summary>
    /// 步骤交叉引用诊断验证器接口
    /// 用于诊断卡住的步骤，并提供变量交叉引用分析（类似TIA Portal的交叉引用功能）
    /// </summary>
    public interface IStepCrossReferenceDiagnostic
    {
        /// <summary>
        /// 诊断卡住的步骤并提供交叉引用分析
        /// </summary>
        /// <param name="request">诊断请求</param>
        /// <param name="diagnosisRoleId">诊断使用的AI RoleId（默认使用配置）</param>
        /// <returns>诊断结果，包含交叉引用和根因分析</returns>
        Task<StepCrossReferenceDiagnosticResponseDto> DiagnoseStepWithCrossReferenceAsync(
            StepCrossReferenceDiagnosticRequestDto request,
            long? diagnosisRoleId = null);
    }
}
