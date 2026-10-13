#nullable enable
using System.Collections.Generic;
using System.Text;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

public sealed record LowerContractInfo(string Callee, string Summary);

/// <summary>FB 契约的 AI 提示词。改动提示词必须升级 Version（进缓存键，旧缓存自动失效）。</summary>
public static class FbContractPrompt
{
    public const string Version = "fb-contract-2";
    public const int MaxBodyChars = 60000;

    public const string System = """
    你是西门子 PLC 程序分析专家。下面是一个功能块（FB）的接口与全部程序体，由程序从 TIA 导出的 XML 逐部件如实翻译而来，每条语句前有语句编号，如 [FB/5/1]。
    任务：读懂这个 FB 在做什么，用「形参」描述它对外保证的关系，供调用处代入实参后使用。

    推理规则：
    1. 只依据翻译出的逻辑语句推理。变量名、注释、块名只能帮助阅读，不能单独作为依据。
    2. 每条关系必须在 evidence 中列出支撑它的语句编号（逐字照抄方括号里的编号）。
    3. 不确定就不输出。不要输出数值关系、跨扫描的步骤先后。
    4. 只「指认」变量，不要自己化简条件：到位条件、传感器个数等由程序在调用处代入实参后计算。

    关系类型（kind）：
    - mutex：两个输出形参在程序逻辑上不能同时为 true。字段 outputs（两个）。
    - feedback：某个输出形参发出后，经过一段动作时间，代表「到位」的变量应为 true。字段 output、positionVar（代表到位的内部变量或形参，照抄程序中的完整名字，如 Cmd.IWP）、delayParam（动作超时时间的形参名或常数，没有就省略）。
    - timeoutAlarm：输出发出后到位变量在规定时间内未为 true 时置位的报警位。字段 alarm（报警输出形参，可含成员路径，如 A_Word.Alarm1）、output、positionVar、delayParam。只用于「本块的某个输出发出后等待到位」的报警。
    - expectedStateAlarm：某个 Bool 输入形参（可先经延时滤波）偏离期望状态并持续规定时间后置位的报警位，期望状态由配置输入形参决定（如指定期望为 ON 还是 OFF 的输入）。每个报警位单独一条。字段 alarm（报警位，形参或本块变量的完整名）、input（被监视的 Input 形参本身，不要写滤波后的内部变量或输出形参）、configInputs（决定期望状态的输入形参列表，没有就写 []）、expectedStates（数组，每项 {"when":{"配置输入形参":true/false,…},"expected":true/false}：when 写全部配置输入的取值，expected 是此时被监视输入正常应有的值；只列出该报警可能置位的组合）、delayParam（置位延时的形参名或常数）。
    - gate：某个输入形参为 false 时，若干输出形参必须为 false（如急停）。字段 input、outputs。
    - contradiction：两个输入形参同时为 true 即为异常（如同一执行机构两端的到位信号），字段 inputs（两个）、delaySeconds（程序中的消抖时间，秒，可省略）、alarm（对应报警位，可省略）。
    - other：其它值得说明的行为，字段 text，只作说明。

    只输出一个 JSON 对象，语句编号必须出现在材料中：
    {"fb":"块名","understanding":"一两句话","relations":[{"kind":"…", …, "evidence":["块/网络/序号", …]}]}
    """;

    public static string BuildUser(IrBlock block, IReadOnlyList<LowerContractInfo> lower)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# 功能块 {block.Name}");
        sb.AppendLine("## 1. 接口（含默认值）");
        sb.AppendLine(InterfaceReader.Render(block.Interface, 200));
        sb.AppendLine("## 2. 程序体（逐部件翻译，未经删减）");
        foreach (var unit in block.Units) sb.AppendLine(IrText.Unit(unit));
        if (lower.Count > 0)
        {
            sb.AppendLine("## 3. 本块调用的下层功能块的契约（程序已核对，形参名为下层块的形参）");
            foreach (var l in lower) sb.AppendLine($"- {l.Callee}：{l.Summary}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// 超长 FB 的一段材料（spec §3.2 规则 5）：完整接口 + 本段网络 + 下层契约；说明只依据本段推理。
    /// 只用于超过预算的块，未超预算的块仍用 <see cref="BuildUser"/>（缓存键不变）。
    /// </summary>
    public static string BuildSegmentUser(IrBlock block, IReadOnlyList<IrUnit> segment, int index, int count,
        IReadOnlyList<string> skipped, IReadOnlyList<LowerContractInfo> lower)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# 功能块 {block.Name}（程序体过长，分 {count} 段提供，这是第 {index} 段）");
        sb.AppendLine("说明：本段只包含下面列出的网络。只依据本段语句给出关系；需要其它段的语句才能确定的关系不要输出；evidence 只能引用本段出现的语句编号。");
        if (skipped.Count > 0) sb.AppendLine("以下网络单独超过长度上限、未提供：" + string.Join("、", skipped));
        sb.AppendLine("## 1. 接口（含默认值）");
        sb.AppendLine(InterfaceReader.Render(block.Interface, 200));
        sb.AppendLine($"## 2. 程序体第 {index}/{count} 段（逐部件翻译，未经删减）");
        foreach (var unit in segment) sb.AppendLine(IrText.Unit(unit));
        if (lower.Count > 0)
        {
            sb.AppendLine("## 3. 本块调用的下层功能块的契约（程序已核对，形参名为下层块的形参）");
            foreach (var l in lower) sb.AppendLine($"- {l.Callee}：{l.Summary}");
        }
        return sb.ToString();
    }
}
