using HSJM.AI.IDbService;

namespace HSJM.AI.IDbService
{
    /// <summary>
    /// AI诊断记忆服务接口（支持多场景：约束生成、卡滞诊断、能量流分析）
    /// </summary>
    public interface IDiagnosticMemoryService : IBaseService<DiagnosticMemoryEntity>
    {
    }
}