using HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.DiagnosticStrategyFactory.Services
{
    /// <summary>
    /// 诊断验证器接口
    /// </summary>
    public interface IDiagnosticValidator
    {
        /// <summary>
        /// 批量验证数据
        /// </summary>
        /// <param name="dataList">待验证的数据列表</param>
        /// <param name="maxDegreeOfParallelism">最大并发度</param>
        /// <returns>验证结果</returns>
        Task<DiagnosticStrategyFactoryResponseDto> ValidateBatchAsync(
            List<string> dataList,
            int maxDegreeOfParallelism = 5);

        /// <summary>
        /// 验证单条数据
        /// </summary>
        /// <param name="data">待验证的数据</param>
        /// <returns>验证结果</returns>
        Task<ValidationResultDto> ValidateSingleAsync(string data);
    }
}
