using HSJM.AI.IDbService;

namespace HSJM.AI.IgwRegistration.Tests.DiagnosisRead;

/// <summary>D-1 Task 4：CheckConstraintViolation 纯函数测试用的最小数据构造助手。</summary>
internal static class ConstraintTestData
{
    public static PLC_IOConstraintRuleEntity Rule(string op, string name = "R_TEST", long projectId = 1) =>
        new() { ProjectId = projectId, Operator = op, RuleName = name, RuleType = "Test", Priority = "Normal", IsDeleted = false };

    /// <summary>按参数顺序生成规则信号，SortOrder 从 1 递增。</summary>
    public static List<PLC_IOConstraintSignalEntity> Signals(PLC_IOConstraintRuleEntity rule, params (string Name, string Role)[] items) =>
        items.Select((item, index) => new PLC_IOConstraintSignalEntity
        {
            RuleId = rule.Id,
            SignalName = item.Name,
            SignalRole = item.Role,
            SortOrder = index + 1,
        }).ToList();

    public static Dictionary<string, bool> Values(params (string Name, bool Value)[] items) =>
        items.ToDictionary(item => item.Name, item => item.Value);

    public static readonly Dictionary<string, PLC_IOTagEntity> NoTags = new(StringComparer.OrdinalIgnoreCase);
}
