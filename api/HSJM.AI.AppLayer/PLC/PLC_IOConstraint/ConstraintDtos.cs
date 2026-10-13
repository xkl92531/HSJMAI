using System.Collections.Generic;

namespace HSJM.AI.AppLayer.PLC
{
    /// <summary>手动新增/编辑约束规则的请求体（含信号子表）。</summary>
    public class ConstraintRuleSaveDto
    {
        public long ProjectId { get; set; }
        public string RuleName { get; set; } = string.Empty;
        public string RuleType { get; set; } = string.Empty;
        public string Operator { get; set; } = string.Empty;
        public string? EquipmentId { get; set; }
        public string? Priority { get; set; }
        public decimal? Confidence { get; set; }
        public string? Description { get; set; }
        public string? ValidStates { get; set; }
        public List<ConstraintSignalDto> Signals { get; set; } = new();
    }

    /// <summary>约束信号明细。</summary>
    public class ConstraintSignalDto
    {
        public string SignalName { get; set; } = string.Empty;
        public string? SignalAddress { get; set; }
        public string SignalRole { get; set; } = "Member";
        public int? SortOrder { get; set; }
    }

    /// <summary>保存约束生成系统提示词的请求体。</summary>
    public class ConstraintPromptSaveDto
    {
        public string Content { get; set; } = string.Empty;
    }
}
