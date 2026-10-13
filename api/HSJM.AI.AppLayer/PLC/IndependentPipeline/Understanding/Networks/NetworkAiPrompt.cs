#nullable enable
using System.Collections.Generic;
using System.Text;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

/// <summary>普通网络（不含调用的程序段）的 AI 提示词（spec §6）。改动提示词必须升级 Version（进缓存键）。</summary>
public static class NetworkAiPrompt
{
    public const string Version = "network-ai-1";
    public const int MaxChars = 60000;

    public const string System = """
    你是西门子 PLC 程序分析专家。下面是一个程序段（网络），由程序从 TIA 导出的 XML 逐部件如实翻译而来，每条语句前有语句编号，如 [OB1/3/1]。
    任务：列出设备正常时，本程序段里 IO 信号之间一定满足的关系（约束）。

    推理规则：
    1. 只依据翻译出的逻辑语句推理。变量名、注释、块名只能帮助阅读，不能单独作为依据。
    2. 约束里的信号只能是「IO 点」表里地址为 %I 或 %Q 的点名（逐字照抄）；作为 Member 或 Target 的 %Q 必须是本程序段写入的输出。
    3. 每条约束在 evidence 中列出支撑它的语句编号（逐字照抄方括号里的编号，不带方括号），所列语句要提到约束里的每个信号。
    4. 不确定就不输出。不要输出数值关系、跨扫描的步骤先后；只在上升沿/下降沿里出现的信号不要用。
    5. 输出发出后对应输入要经过一段时间才变化、或「为 true 持续一段时间才报警」的关系，timing 写 after_delay，能读出时间就填 delaySeconds；其余写 instant。

    约束类型 operator：AT_MOST_ONE（Member 最多一个为 true）、SAME_STATE（Member 取值始终相同）、EXACTLY_ONE（Member 恰好一个为 true）、
    IMPLIES（Source 为 true 时 Target 为 true）、REQUIRES_0_WHEN_FALSE（Source 为 false 时 Target 必须为 false）、MUST_BE_FALSE（Target 必须为 false）。

    只输出一个 JSON 对象：
    {"understanding":"一两句话","constraints":[{"operator":"…","signals":[{"name":"IO点名","role":"Source|Target|Member"}],"timing":"instant|after_delay","delaySeconds":null,"evidence":["块/网络/序号"]}]}
    """;

    public static string BuildUser(IrUnit unit, IrBlock block, IReadOnlyList<string> callers, IReadOnlyList<PipelineTag> io,
        IReadOnlyList<string> contracts)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# 程序段 {unit.BlockName}/{unit.Network}（{unit.Language}）");
        sb.AppendLine("## 1. 本程序段逻辑（逐部件翻译，未经删减）");
        sb.AppendLine(IrText.Unit(unit));
        sb.AppendLine($"## 2. 所在块 {block.Name} 的接口");
        sb.AppendLine(block.Interface.Count == 0 ? "（无接口）" : InterfaceReader.Render(block.Interface, 200));
        sb.AppendLine($"## 3. 谁调用了 {block.Name}（向上一层）");
        if (callers.Count == 0) sb.AppendLine("（未找到调用处）");
        foreach (var c in callers) sb.AppendLine(c);
        if (contracts.Count > 0)
        {
            sb.AppendLine("## 4. 本段读到的被调块输出所属块的契约（程序已核对，形参名为被调块的形参）");
            foreach (var c in contracts) sb.AppendLine(c);
        }
        sb.AppendLine("## 5. IO 点（约束里只能用地址为 %I 或 %Q 的点）");
        sb.AppendLine("| 点名 | 地址 | 类型 |");
        sb.AppendLine("|---|---|---|");
        foreach (var t in io) sb.AppendLine($"| {t.Name} | {t.Address} | {t.DataType} |");
        return sb.ToString();
    }
}
