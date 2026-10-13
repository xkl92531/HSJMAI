using HSJM.AI.Core;
using SqlSugar;

namespace HSJM.AI.IDbService;

/// <summary>
/// LineUp 变量采集开关规则（2026-09-12）。
/// 行存在（未软删）= 该变量被屏蔽采集：运行时 ReadPlan 编译时按 CanonicalPlcPath 排除，
/// 引用它的转移条件求值落 Unknown（结构性缺失，不降级连接状态）。
/// 仅允许条件类变量（TransitionCondition 等）；GRAPH 工步执行位（RT_DATA.EXEC_BITS）禁止屏蔽。
/// </summary>
[SugarTable("AI_LineUp_TagCollectionRule")]
public sealed class LineUpTagCollectionRuleEntity : BaseField
{
    public LineUpTagCollectionRuleEntity() => Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();

    [SugarColumn(IsPrimaryKey = true)] public long Id { get; set; }
    [SugarColumn(IsNullable = false)] public long PlcProjectId { get; set; }
    /// <summary>完整 PLC 规范路径（{块名}.{变量路径}，如 DB760_MainGraph.放置过程完成.X）。</summary>
    [SugarColumn(IsNullable = false, Length = 512)] public string VariablePath { get; set; } = string.Empty;
    /// <summary>屏蔽原因（来源说明，如“老网关符号表缺失，转移持续 Bad”）。</summary>
    [SugarColumn(IsNullable = true, Length = 256)] public string? Reason { get; set; }
}
