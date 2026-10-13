namespace HSJM.AI.AppLayer.Agent.Services.Plc_diagnosis
{
    /// <summary>
    /// PLD（ParsedLogicDescription）树形 Markdown 渲染 / 解析的共享 token 常量。
    ///
    /// 设计目的：PLD 由 <see cref="SiemensFlgNetConstraintExtractor"/> 渲染、由
    /// <see cref="PldStructuralConstraintAugmenter"/> 用正则消费。两边的 token 文本必须严格一致，
    /// 否则 Augmenter 静默匹配不到锚点 / 引脚 / 触点行。
    ///
    /// 把 token 字符串集中在本类，渲染器 AppendLine 和解析器正则都引用这里，
    /// 调整一个 token（如改语言、改记号）真的"只改一处"，编译器会替你检查所有引用点。
    ///
    /// **不要在本文件之外硬编码这些字符串**。新增 token 也加在这里。
    /// </summary>
    internal static class PldMarkdownTokens
    {
        // === 锚点行 ===
        // 渲染示例：`- 锚点: \`X\` (FB 调用: TypeName)`
        public const string AnchorLabel = "锚点";

        // 锚点括号内此 token → FB 调用类型；其它视为独立 Coil/Graph 锚点。
        public const string FbCallToken = "FB 调用";

        // === FB 调用的引脚行 ===
        // 渲染示例：`  - 输出引脚: \`PinName\` → \`实际信号\``
        public const string OutPinLabel = "输出引脚";

        // 渲染示例：`  - 输入引脚: \`PinName\`` + 子树
        public const string InPinLabel = "输入引脚";

        // === 触点行（布尔表达式叶节点）===
        // 渲染示例：`{indent}- 触点(常开): \`SignalName\``
        public const string ContactOpenLabel = "触点(常开)";

        // 渲染示例：`{indent}- 触点(常闭): \`SignalName\``
        public const string ContactClosedLabel = "触点(常闭)";
    }
}
