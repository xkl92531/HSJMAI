#nullable enable
using System.Linq;
using System.Security;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding;

/// <summary>测试用 StructuredText 构造器。词：以 "$" 开头为局部变量（如 "$Cmd.QF"），以 "#" 开头为字面常数（如 "#1"），
/// 以 "&lt;" 开头为原样 XML，其余为记号（如 ":="、"IF"、"TRUE"）；词与词之间加一个 Blank。</summary>
internal static class SclXml
{
    public static string V(string path, string scope = "LocalVariable") =>
        $"<Access Scope=\"{scope}\"><Symbol>{string.Concat(path.Split('.').Select(c => $"<Component Name=\"{c}\" />"))}</Symbol></Access>";

    public static string C(string value) =>
        $"<Access Scope=\"LiteralConstant\"><Constant><ConstantValue>{SecurityElement.Escape(value)}</ConstantValue></Constant></Access>";

    public static string T(string text) => $"<Token Text=\"{SecurityElement.Escape(text)}\" />";

    public static string Build(params string[] words) => "<StructuredText>"
        + string.Join("<Blank />", words.Select(w => w.StartsWith('$') ? V(w[1..]) : w.StartsWith('#') ? C(w[1..]) : w.StartsWith('<') ? w : T(w)))
        + "</StructuredText>";

    /// <summary>实例.计时器(IN := 变量, PT := 常数)</summary>
    public static string Timer(string instance, string kind, string inVar, string preset) =>
        V(instance) + T(".") + $"<Access Scope=\"Call\"><Instruction Name=\"{kind}\">{T("(")}"
        + $"<Parameter Name=\"IN\">{T(":=")}{V(inVar)}</Parameter>{T(",")}<Parameter Name=\"PT\">{T(":=")}{C(preset)}</Parameter>"
        + $"{T(")")}</Instruction></Access>";

    /// <summary>博图指令/函数调用：Instruction（name 为空时不带 Name 属性）；参数为 (名字, 内容 XML)，名字为空即无名参数。</summary>
    public static string Instr(string? name, params (string? Param, string Content)[] args) =>
        $"<Access Scope=\"Call\"><Instruction{(name is null ? "" : $" Name=\"{name}\"")}>{T("(")}"
        + string.Join(T(","), args.Select(a => a.Param is null ? $"<NamelessParameter>{a.Content}</NamelessParameter>"
            : $"<Parameter Name=\"{a.Param}\">{a.Content}</Parameter>"))
        + $"{T(")")}</Instruction></Access>";

    /// <summary>块调用：CallInfo。</summary>
    public static string Block(string name, string blockType, params (string Param, string Content)[] args) =>
        $"<Access Scope=\"Call\"><CallInfo Name=\"{name}\" BlockType=\"{blockType}\">{T("(")}"
        + string.Join(T(","), args.Select(a => $"<Parameter Name=\"{a.Param}\">{a.Content}</Parameter>"))
        + $"{T(")")}</CallInfo></Access>";
}
