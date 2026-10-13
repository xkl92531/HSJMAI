using System.Collections.Generic;

namespace HSJM.AI.AppLayer.Agent.Services.Plc_diagnosis
{
    /// <summary>
    /// PLD 锚点块的语义类型。
    /// <list type="bullet">
    ///   <item><c>FbCall</c>：PLC 块内一次 FB/FC 调用（含输入/输出引脚 + 各引脚连接的触点子树）。</item>
    ///   <item><c>Coil</c>：独立 Coil 输出或 Graph 联锁/转移/监视条件（含一个布尔表达式 → 一组触点）。</item>
    /// </list>
    /// 厂商无关：FbCall 表达"功能块调用 + 引脚映射"语义；Coil 表达"布尔条件 → 单输出"语义。
    /// </summary>
    public enum PldAnchorType { FbCall, Coil }

    /// <summary>
    /// 触点引用：信号名 + 极性（常开 / 常闭）。语义跨厂商通用。
    /// </summary>
    /// <param name="Polarity">"NO" = 常开（信号原值）；"NC" = 常闭（信号取反）。</param>
    /// <param name="Name">信号名（IO Tag 全名 / DB 变量路径）。</param>
    public sealed record ContactRef(string Polarity, string Name);

    /// <summary>
    /// PLC 程序块 LogicUnit 内的一个锚点块的结构化表示。
    ///
    /// 渲染器（如 <see cref="SiemensFlgNetConstraintExtractor"/>）从厂商 XML 抽取此结构 →
    /// 既可渲染为 LLM 可读的树形 Markdown（PLD），也可被 <see cref="PldStructuralConstraintAugmenter"/>
    /// 直接消费做约束推理。
    ///
    /// 这是 PLC 程序结构的厂商无关中间表示，新增厂商支持时只要写一个对应的「XML → 此结构」适配器。
    /// </summary>
    public sealed class PldAnchorBlock
    {
        /// <summary>语义类型（FB 调用 / 独立 Coil）。</summary>
        public PldAnchorType Type { get; set; }

        /// <summary>锚点名（FB 调用实例名 / Coil 主输出信号名）。</summary>
        public string Anchor { get; set; } = string.Empty;

        /// <summary>类型描述（如 "FB 调用: FB3002_Valve" / "Graph 联锁条件"）。供调试 / LLM 提示用，约束生成不依赖这里的文本。</summary>
        public string TypeText { get; set; } = string.Empty;

        /// <summary>仅 FbCall 类型：输出引脚 → 外部实际信号（如 "QFwd" → "OP030_YV01_W"）。</summary>
        public Dictionary<string, string> OutPins { get; } = new(System.StringComparer.OrdinalIgnoreCase);

        /// <summary>仅 FbCall 类型：输入引脚 → 引脚子树触点列表（含极性）。</summary>
        public Dictionary<string, List<ContactRef>> InPinContacts { get; } = new(System.StringComparer.OrdinalIgnoreCase);

        /// <summary>仅 Coil 类型：锚点子树直接触点列表（含极性）。</summary>
        public List<ContactRef> DirectContacts { get; } = new();
    }
}
