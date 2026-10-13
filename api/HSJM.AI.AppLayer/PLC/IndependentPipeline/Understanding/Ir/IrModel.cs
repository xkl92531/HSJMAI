#nullable enable
using System.Collections.Generic;
using System.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

public static class IrKinds
{
    public const string Const = "Const", Var = "Var", Not = "Not", And = "And", Or = "Or", Xor = "Xor";
    public const string Cmp = "Cmp", Arith = "Arith", Func = "Func", Rise = "Rise", Fall = "Fall";
    public const string Timer = "Timer", Counter = "Counter", CallOut = "CallOut", Node = "Node", Unknown = "Unknown", Port = "Port";
    /// <summary>Instr：博图指令（按原名与引脚列出，未展开语义）；Calc：Calc 指令，Name 为公式、Args 为 Port(INn)。</summary>
    public const string Instr = "Instr", Calc = "Calc";
}

public static class IrStatementKinds
{
    public const string Assign = "Assign", Set = "Set", Reset = "Reset", Call = "Call", Jump = "Jump", JumpNot = "JumpNot",
        Label = "Label", Return = "Return", Action = "Action", Source = "Source", Box = "Box";
    /// <summary>顺控（GRAPH）结构：步 / 转移 / 分支 / 连接，以及未识别的顺控元素；文字都在 Text 里。</summary>
    public const string Step = "Step", Transition = "Transition", Branch = "Branch", Connection = "Connection", Unrecognized = "Unrecognized";
    /// <summary>说明性文字（如顺控块的报警设置，属于配置而非程序逻辑）。</summary>
    public const string Note = "Note";
    /// <summary>顺控的转移/互锁/监控条件（TrCoil/IlCoil/SvCoil）：Target 是条件的显示名（〔转移 T1 条件〕等，不是变量），
    /// Value 是条件表达式。条件只决定顺控背景实例内部的转移/互锁/监控状态，不写任何用户变量；文字与赋值同样写作「名 := 值」。</summary>
    public const string Condition = "Condition";
}

/// <summary>程序逻辑的结构化表示。只描述 XML 里的真实结构，不含任何按名字的推断。
/// Timer/Counter 的 Name 为「部件|实例|端口」，Args 为 Port；CallOut 的 Name 为「被调块|实例|引脚」；
/// Instr 的 Name 为「指令[&lt;模板&gt;][[实例]]|端口」，Args 为已接线输入的 Port；
/// Unknown 只用于真正认不出/缺失的内容（未接线、环路、未识别的访问等），Name 即说明文字。</summary>
public sealed record IrExpr(string Kind, string? Name = null, IReadOnlyList<IrExpr>? Args = null)
{
    public static readonly IrExpr True = new(IrKinds.Const, "TRUE");
    public static IrExpr Var(string name) => new(IrKinds.Var, name);
    public static IrExpr Const(string value) => new(IrKinds.Const, value);
    public static IrExpr Unwired() => new(IrKinds.Unknown, "未接线");
    public static IrExpr Not(IrExpr x) => x.Kind == IrKinds.Not ? x.Args![0] : new(IrKinds.Not, null, [x]);
    public static IrExpr And(IEnumerable<IrExpr?> items) => Join(IrKinds.And, items);
    public static IrExpr Or(IEnumerable<IrExpr?> items) => Join(IrKinds.Or, items);

    public bool IsTrue => Kind == IrKinds.Const && Name == "TRUE";

    static IrExpr Join(string kind, IEnumerable<IrExpr?> items)
    {
        var flat = new List<IrExpr>();
        foreach (var item in items)
        {
            if (item is null) continue;
            if (item.Kind == kind) flat.AddRange(item.Args!);
            else if (kind == IrKinds.And && item.IsTrue) continue;
            else flat.Add(item);
        }
        if (flat.Count == 0) return kind == IrKinds.And ? True : Const("FALSE");
        return flat.Count == 1 ? flat[0] : new(kind, null, flat);
    }

    /// <summary>表达式读到的全部变量名。</summary>
    public IEnumerable<string> Vars()
    {
        if (Kind == IrKinds.Var) yield return Name!;
        if (Args is null) yield break;
        foreach (var arg in Args)
            foreach (var name in arg.Vars()) yield return name;
    }
}

/// <summary>Negated：输出引脚带取反标记（Targets 仍是变量名本身）；FeedsLogic：输出引脚还接到本网络其它部件。</summary>
public sealed record IrPin(string Name, string Section, string Type, IrExpr? Value, IReadOnlyList<string> Targets,
    bool Negated = false, bool FeedsLogic = false);
/// <summary>InstanceScope：导出里背景实例的 Scope（GlobalVariable / LocalVariable），不明为 null。</summary>
public sealed record IrCall(string Callee, string BlockType, string Instance, IrExpr En, IReadOnlyList<IrPin> Pins)
{
    public string? InstanceScope { get; init; }

    /// <summary>FlgNet 里调用盒的部件 UId（其 ENO 原子文字里带同一 UId，见 FlgNetTranslator.EnoPartUid）；其它语言为 null。不进入给 AI 读的文字。</summary>
    public string? PartUid { get; init; }
}
public sealed record IrStatement(string Id, string Kind, string? Target, IrExpr Guard, IrExpr? Value,
    IrCall? Call = null, string? Text = null)
{
    /// <summary>执行本语句时顺带改写的变量（不是 Target、也不是调用引脚目标）：指令盒（非 FB/FC 调用的部件）的背景实例
    /// （整个实例）、边沿存储位、未展开语义的指令接在输入侧端口上的变量（可能是 InOut）。记在第一次求值该部件的语句上；
    /// 不进入给 AI 读的文字。写入判定（IrBlock、ProjectWrites 等）把它们计为本语句的写入。</summary>
    public IReadOnlyList<string>? SideWrites { get; init; }

    /// <summary>文字段（顺控动作）里有块调用或博图指令（CallInfo/Instruction）：被调方可能写文字里没提到的变量。不进入给 AI 读的文字。</summary>
    public bool TextCallsBlock { get; init; }
}
public sealed record IrNode(string Name, IrExpr Value);
/// <summary>UnitKind 取自 PipelineUnit.UnitKind（如 MissingBody 表示块没有任何程序段）。</summary>
public sealed record IrUnit(long UnitId, string BlockName, int? Network, string Language, string RootKind,
    IReadOnlyList<IrStatement> Statements, IReadOnlyList<IrNode> Nodes, IReadOnlyList<string> UnknownParts,
    string UnitKind = "Network", IReadOnlyList<string>? InstructionNames = null)
{
    /// <summary>UnknownParts 中属于博图指令（有原名与引脚、只是翻译器未展开语义）的部分；其余才是真正未识别的 XML。</summary>
    public IReadOnlyList<string> Instructions => InstructionNames ?? [];
}
