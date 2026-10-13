namespace HSJM.AI.IDbService;

/// <summary>
/// LineUp 变量采集开关规则仓储（2026-09-12）。
/// 行存在（未软删）= 屏蔽采集。配套内存缓存见 AppLayer 的 LineUpTagCollectionRuleCache。
/// </summary>
public interface ILineUpTagCollectionRuleRepository
{
    /// <summary>列出项目当前被屏蔽的变量完整路径（未软删行）。</summary>
    Task<IReadOnlyList<string>> ListDisabledAsync(
        long plcProjectId,
        CancellationToken cancellationToken);

    /// <summary>屏蔽一个变量（幂等：已有未软删行则仅刷新原因）。</summary>
    Task DisableAsync(
        long plcProjectId,
        string variablePath,
        string? reason,
        CancellationToken cancellationToken);

    /// <summary>恢复采集（软删匹配的未删行）。无匹配时为 no-op。</summary>
    Task EnableAsync(
        long plcProjectId,
        string variablePath,
        CancellationToken cancellationToken);
}
