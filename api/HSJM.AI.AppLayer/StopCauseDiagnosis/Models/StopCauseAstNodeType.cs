namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Models
{
    /// <summary>
    /// AST 节点类型
    /// </summary>
    public static class StopCauseAstNodeType
    {
        public const string And = "And";
        public const string Or = "Or";
        public const string Not = "Not";
        public const string Contact = "Contact";
        public const string Coil = "Coil";
        public const string Compare = "Compare";
        public const string Set = "Set";
        public const string Reset = "Reset";
        public const string GraphStepActive = "GraphStepActive";
        public const string GraphTransition = "GraphTransition";
        public const string ExternalDependency = "ExternalDependency";
        public const string Reference = "Reference";
        public const string Opaque = "Opaque";
        public const string Anchor = "Anchor";
        public const string Timer = "Timer";
        public const string Move = "Move";
        public const string SetResetFlipFlop = "SetResetFlipFlop";
    }
}
