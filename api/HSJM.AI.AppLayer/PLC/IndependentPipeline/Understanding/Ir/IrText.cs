#nullable enable
using System.Linq;
using System.Text;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

/// <summary>IR → 给 AI 读的中文文字。每条语句带编号「[块/网络/序号]」。</summary>
public static partial class IrText
{
    public static string Expr(IrExpr e)
    {
        var args = e.Args ?? [];
        switch (e.Kind)
        {
            case IrKinds.Const: case IrKinds.Var: return e.Name!;
            case IrKinds.Not: return $"NOT({Expr(args[0])})";
            case IrKinds.And: return "(" + string.Join(" AND ", args.Select(Expr)) + ")";
            case IrKinds.Or: return "(" + string.Join(" OR ", args.Select(Expr)) + ")";
            case IrKinds.Xor: return "XOR(" + string.Join(", ", args.Select(Expr)) + ")";
            case IrKinds.Cmp: return $"({Expr(args[0])} {e.Name} {Expr(args[1])})";
            case IrKinds.Arith: return "(" + string.Join($" {e.Name} ", args.Select(Expr)) + ")";
            case IrKinds.Func: return $"{e.Name}({string.Join(", ", args.Select(Expr))})";
            case IrKinds.Rise: return $"上升沿({Expr(args[0])})";
            case IrKinds.Fall: return $"下降沿({Expr(args[0])})";
            case IrKinds.Port: return $"{e.Name}={Expr(args[0])}";
            case IrKinds.Node: return $"〔节点 {e.Name}〕";
            case IrKinds.Timer: case IrKinds.Counter:
            {
                var p = e.Name!.Split('|');
                return $"{p[0]}[{p[1]}]({string.Join(", ", args.Select(Expr))}).{p[2]}";
            }
            case IrKinds.CallOut:
            {
                var p = e.Name!.Split('|');
                return $"{p[0]}[{p[1]}].{p[2]}";
            }
            case IrKinds.Instr:
            {
                var name = e.Name ?? "?";
                var bar = name.LastIndexOf('|');
                var head = bar < 0 ? name : name[..bar];
                var port = bar < 0 ? "" : "." + name[(bar + 1)..];
                return $"〔指令 {head}({string.Join(", ", args.Select(Expr))})〕{port}";
            }
            case IrKinds.Calc: return $"Calc({CalcText(e)})";
            case IrKinds.Unknown: return $"〔{e.Name ?? "?"}〕";
            default: return $"〔未知表达式 {e.Kind}〕";
        }
    }

    public static string Statement(IrStatement s)
    {
        var id = $"[{s.Id}] ";
        var guard = s.Guard.IsTrue ? null : Expr(s.Guard);
        switch (s.Kind)
        {
            case IrStatementKinds.Assign: case IrStatementKinds.Condition:
                return guard == null ? $"{id}{s.Target} := {Expr(s.Value!)}" : $"{id}若 {guard} 则 {s.Target} := {Expr(s.Value!)}";
            case IrStatementKinds.Set: return $"{id}若 {guard ?? "TRUE"} 则 置位 {s.Target}（保持为 true）";
            case IrStatementKinds.Reset: return $"{id}若 {guard ?? "TRUE"} 则 复位 {s.Target}（保持为 false）";
            case IrStatementKinds.Jump: return $"{id}若 {guard ?? "TRUE"} 则 跳转到标签 {s.Target}（其后直到标签 {s.Target} 之前的程序段不执行）";
            case IrStatementKinds.JumpNot: return $"{id}若 {guard ?? "TRUE"} 不成立 则 跳转到标签 {s.Target}（其后直到标签 {s.Target} 之前的程序段不执行）";
            case IrStatementKinds.Return: return $"{id}若 {guard ?? "TRUE"} 则 返回（本块后续语句不执行）";
            case IrStatementKinds.Label: return $"{id}标签 {s.Target}";
            case IrStatementKinds.Action:
                return $"{id}动作{(string.IsNullOrEmpty(s.Target) ? "" : $"（{s.Target}）")}：{(string.IsNullOrEmpty(s.Text) ? "（无语句）" : s.Text)}";
            case IrStatementKinds.Step: case IrStatementKinds.Transition: case IrStatementKinds.Branch:
            case IrStatementKinds.Connection: case IrStatementKinds.Unrecognized: case IrStatementKinds.Note:
                return id + s.Text;
            case IrStatementKinds.Source: return $"{id}源码：\n```\n{s.Text}\n```";
            case IrStatementKinds.Call: return CallText(id, s.Call!);
            case IrStatementKinds.Box:
                // FlgNet 的执行语句条件恒为 TRUE；SCL 里 IF 内的计时器调用带条件
                return guard == null
                    ? $"{id}执行 {Expr(s.Value!)}（输出未直接接线，结果可能在别处读取）"
                    : $"{id}若 {guard} 则 执行 {Expr(s.Value!)}（输出未直接接线，结果可能在别处读取）";
            default: return $"{id}〔未知语句 {s.Kind}〕";
        }
    }

    static string CallText(string id, IrCall call)
    {
        var sb = new StringBuilder();
        sb.Append($"{id}调用 {call.BlockType} {call.Callee}，实例 {(call.Instance.Length == 0 ? "（无）" : call.Instance)}，");
        sb.Append(call.En.IsTrue ? "EN 未接线（每次扫描都执行）" : $"仅当 {Expr(call.En)} 时执行");
        foreach (var pin in call.Pins)
        {
            var section = pin.Section switch { "Input" => "输入", "Output" => "输出", "InOut" => "输入输出", var other => other };
            if (pin.Section == "Output")
                sb.Append($"\n  - {section} {pin.Name} ({pin.Type}) → {OutputDestination(pin)}");
            else
                sb.Append($"\n  - {section} {pin.Name} ({pin.Type}) ← {(pin.Value == null ? "未接线（取接口默认值）" : Expr(pin.Value))}");
        }
        return sb.ToString();
    }

    static string OutputDestination(IrPin pin)
    {
        var written = pin.Targets.Count == 0 ? null : string.Join(", ", pin.Targets) + (pin.Negated ? "（取反后写入）" : "");
        return (written, pin.FeedsLogic) switch
        {
            (not null, true) => written + "；并接至本网络后续逻辑",
            (not null, false) => written,
            (null, true) => "接至本网络后续逻辑",
            _ => "未接线"
        };
    }

    public static string Unit(IrUnit unit)
    {
        var sb = new StringBuilder();
        var kind = unit.RootKind is "FlgNet" or "StructuredText" or "Text" ? "" : "，" + unit.RootKind;
        sb.Append($"**{unit.BlockName}** 网络 {unit.Network}（{unit.Language}{kind}）");
        var all = unit.Statements.SelectMany(s => new[] { s.Guard, s.Value }.Concat(s.Call?.Pins.Select(p => p.Value) ?? []))
            .Concat(unit.Nodes.Select(n => n.Value)).Where(e => e != null).Cast<IrExpr>().ToArray();
        if (all.Any(e => Contains(e, IrKinds.Timer)))
            sb.Append("\n说明：TON[实例](IN, PT).Q 表示 IN 持续为真达到 PT 后为真；TOF 为 IN 变假后延时 PT 再变假；TP 为 IN 上升沿后输出 PT 宽的脉冲。");
        foreach (var node in unit.Nodes) sb.Append($"\n〔节点 {node.Name}〕 = {Expr(node.Value)}");
        if (unit.RootKind == "NoSource" || unit.UnitKind == "MissingBody")
            sb.Append("\n（本程序段没有程序源码：可能是加密/受保护块或缺失，不要推断其功能）");
        else if (unit.Statements.Count == 0) sb.Append("\n（本网络没有写入任何变量）");
        foreach (var statement in unit.Statements) sb.Append('\n').Append(Statement(statement));
        var instructions = unit.Instructions;
        if (instructions.Count > 0)
            sb.Append("\n注：以下为博图指令，翻译器按原名与引脚列出、未展开其语义，可按博图指令手册理解：" + string.Join("、", instructions));
        var unrecognized = unit.UnknownParts.Where(p => !instructions.Contains(p)).ToArray();
        if (unrecognized.Length > 0) sb.Append("\n注意：本程序段含未识别的内容 " + string.Join("、", unrecognized) + "，其含义不要推断。");
        return sb.ToString();
    }

    /// <summary>把 Calc 公式里的 INn（整词、不区分大小写）换成对应输入的文字；复合表达式加括号。</summary>
    static string CalcText(IrExpr calc)
    {
        var inputs = (calc.Args ?? []).Where(a => a.Kind == IrKinds.Port && a.Name != null)
            .ToDictionary(a => a.Name!.ToUpperInvariant(), a => a.Args![0]);
        return System.Text.RegularExpressions.Regex.Replace(calc.Name ?? "", @"\bIN\d+\b", m =>
        {
            if (!inputs.TryGetValue(m.Value.ToUpperInvariant(), out var input)) return m.Value;
            var text = Expr(input);
            return input.Kind is IrKinds.Var or IrKinds.Const or IrKinds.And or IrKinds.Or or IrKinds.Cmp or IrKinds.Arith
                ? text : "(" + text + ")";
        }, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    static bool Contains(IrExpr e, string kind) => e.Kind == kind || (e.Args?.Any(a => Contains(a, kind)) ?? false);
}
