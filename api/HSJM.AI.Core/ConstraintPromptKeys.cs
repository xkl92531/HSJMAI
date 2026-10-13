namespace HSJM.AI.Core
{
    /// <summary>
    /// 约束生成相关的提示词模板 Key（存储在 AI_Prompt_Template.PromptKey）。
    /// 跨 AppLayer 多处共用，集中定义避免硬编码字符串散落。
    /// </summary>
    public static class ConstraintPromptKeys
    {
        /// <summary>约束生成 Phase2 系统提示词（喂给 LLM 的 system prompt）。</summary>
        public const string Phase2System = "plc.constraint.phase2.system";

        /// <summary>该提示词在 Prompt_Template.Scenario 上标的场景值。</summary>
        public const string Scenario = "ConstraintGen";
    }
}
