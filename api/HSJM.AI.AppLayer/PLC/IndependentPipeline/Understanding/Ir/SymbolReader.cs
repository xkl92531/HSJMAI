#nullable enable
using System.Linq;
using System.Xml.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

public static class SymbolReader
{
    static bool Is(XElement e, string name) => e.Name.LocalName == name;
    static string A(XElement e, string name) => (string?)e.Attribute(name) ?? "";

    public static IrExpr Access(XElement access)
    {
        var constant = access.Elements().FirstOrDefault(e => Is(e, "Constant"));
        if (constant != null)
        {
            // 字面常数带 ConstantValue；符号常量（GlobalConstant/LocalConstant）只有 Constant@Name
            var value = constant.Descendants().FirstOrDefault(e => Is(e, "ConstantValue"))?.Value.Trim();
            if (string.IsNullOrEmpty(value)) value = A(constant, "Name");
            return IrExpr.Const(value.Length == 0 ? "?" : value);
        }
        var symbol = access.Elements().FirstOrDefault(e => Is(e, "Symbol"));
        if (symbol != null) return IrExpr.Var(Symbol(symbol));
        var address = access.Elements().FirstOrDefault(e => Is(e, "Address"));
        if (address != null) return IrExpr.Var(Absolute(address));
        var indirect = access.Elements().FirstOrDefault(e => Is(e, "Indirect"));
        if (indirect != null) return IrExpr.Var(Indirect(indirect));
        // 类型名（如 SCL 类型判断 CASE TypeOf(..) OF TO_PositioningAxis:）：任何带 DataType 的访问都按类型名输出（不是变量）
        var dataType = access.Elements().FirstOrDefault(e => Is(e, "DataType"));
        if (dataType != null && dataType.Value.Trim() is { Length: > 0 } typeName) return IrExpr.Const(typeName);
        var label = access.Elements().FirstOrDefault(e => Is(e, "Label"));
        if (label != null) return IrExpr.Var(A(label, "Name") is { Length: > 0 } labelName ? labelName : "?");
        var scope = A(access, "Scope");
        if (scope == "PredefinedVariable")
        {
            if (access.Elements().Any(e => Is(e, "Component"))) return IrExpr.Var(Components(access));
            var names = access.Descendants().Select(e => A(e, "Name")).Where(n => n.Length > 0).ToArray();
            if (names.Length > 0) return IrExpr.Var("#" + string.Join(".", names));
        }
        if (scope == "Address")
        {
            var child = access.Elements().FirstOrDefault(e => !Is(e, "LineComment") && !Is(e, "Comment"));
            if (child != null)
                return new IrExpr(IrKinds.Unknown,
                    $"未识别的地址 {child.Name.LocalName}({string.Join(",", child.Attributes().Where(a => !a.IsNamespaceDeclaration).Select(a => a.Name.LocalName + "=" + a.Value))})");
        }
        return new IrExpr(IrKinds.Unknown, "未识别的访问 " + scope);
    }

    static string Width(string type) => type switch
    {
        "Bool" => "X",
        "Byte" or "Char" or "SInt" or "USInt" => "B",
        "Word" or "Int" or "UInt" => "W",
        "DWord" or "DInt" or "UDInt" or "Real" => "D",
        _ => "?" + type
    };

    static string Area(string area) =>
        area switch { "Input" => "I", "Output" => "Q", "Memory" => "M", "Local" => "L", "None" => "", var other => other };

    /// <summary>寄存器间接寻址，STL 写法：如 DIW[AR1,P#14.0]；位寻址在 DB/DI 区写 DBX/DIX，在 I/Q/M/L 区不写宽度字母。</summary>
    static string Indirect(XElement indirect)
    {
        var area = Area(A(indirect, "Area"));
        var width = Width(A(indirect, "Width"));
        if (width == "X" && area is not ("DB" or "DI")) width = "";
        var register = A(indirect, "Register");
        var pointer = int.TryParse(A(indirect, "BitOffset"), out var bits) ? $"P#{bits / 8}.{bits % 8}" : "P#?";
        return $"{area}{width}[{(register.Length == 0 ? "?" : register)},{pointer}]";
    }

    public static string Symbol(XElement symbol) => Components(symbol);

    /// <summary>把直接子元素 Component 拼成「a.b[1].c」，含数组下标与位切片。SCL 导出里位切片以 Symbol 下的词元出现
    /// （Component STW1、Token "."、Token "%X4"），按一段「%X4」拼入；FlgNet 的切片在 SliceAccessModifier 属性上。</summary>
    public static string Components(XElement parent) =>
        string.Join(".", parent.Elements().Select(e => Is(e, "Component") ? Component(e)
                : Is(e, "Token") && A(e, "Text").Trim() is { Length: > 1 } t && t[0] == '%' ? t : null)
            .Where(s => s != null));

    static string Component(XElement component)
    {
        var text = A(component, "Name");
        if (component.Elements().Any(e => Is(e, "Token")))
        {
            // SCL 下标带词元（如 Valve[2 + PBValve]、Arr[i, j]）：按文档顺序拼出原样下标，不能把表达式拆成多维下标
            var sb = new System.Text.StringBuilder();
            foreach (var e in component.Elements())
                if (Is(e, "Token")) sb.Append(A(e, "Text").Trim());
                else if (Is(e, "Access")) sb.Append(Access(e).Name ?? "?");
            text += sb.ToString();
        }
        else
        {
            var index = component.Elements().Where(e => Is(e, "Access")).Select(a => Access(a).Name ?? "?").ToArray();
            if (index.Length > 0) text += "[" + string.Join(",", index) + "]";
        }
        var slice = A(component, "SliceAccessModifier");
        if (slice.Length > 0) text += "." + slice;
        return text;
    }

    static string Absolute(XElement address)
    {
        var area = A(address, "Area") switch { "Input" => "I", "Output" => "Q", "Memory" => "M", var other => other };
        var type = A(address, "Type");
        if (!int.TryParse(A(address, "BitOffset"), out var bits)) return $"%{area}?{type}";
        if (area is "DB" or "DI")
        {
            var block = A(address, "BlockNumber");
            var width = Width(type);
            var prefix = block.Length == 0 ? "%" : $"%{area}{block}.";
            return width == "X" ? $"{prefix}{area}X{bits / 8}.{bits % 8}" : $"{prefix}{area}{width}{bits / 8}";
        }
        return type switch
        {
            "Bool" => $"%{area}{bits / 8}.{bits % 8}",
            "Byte" => $"%{area}B{bits / 8}",
            "Word" or "Int" or "UInt" => $"%{area}W{bits / 8}",
            "DWord" or "DInt" or "UDInt" or "Real" => $"%{area}D{bits / 8}",
            _ => $"%{area}:{type}:{bits}"
        };
    }
}
