#nullable enable
using System.Xml.Linq;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

/// <summary>测试用 FlgNet 网络构造器（与博图导出的 Parts/Wires 结构一致，不带命名空间）。</summary>
internal sealed class FlgNetBuilder(string defaultScope = "LocalVariable")
{
    readonly List<XElement> parts = [];
    readonly List<XElement> wires = [];
    int uid = 1;

    int Next() => uid++;

    public static XElement Rail() => new("Powerrail");
    public static XElement Id(int u) => new("IdentCon", new XAttribute("UId", u));
    public static XElement Pin(int u, string name) => new("NameCon", new XAttribute("UId", u), new XAttribute("Name", name));

    public int Access(string symbol, string? scope = null)
    {
        var id = Next();
        parts.Add(new XElement("Access", new XAttribute("Scope", scope ?? defaultScope), new XAttribute("UId", id),
            new XElement("Symbol", symbol.Split('.').Select(c => new XElement("Component", new XAttribute("Name", c))))));
        return id;
    }

    public int Part(string name, int? card = null)
    {
        var id = Next();
        var part = new XElement("Part", new XAttribute("Name", name), new XAttribute("UId", id));
        if (card != null)
            part.Add(new XElement("TemplateValue", new XAttribute("Name", "Card"), new XAttribute("Type", "Cardinality"), card));
        parts.Add(part);
        return id;
    }

    public void Wire(params XElement[] connectors) => wires.Add(new XElement("Wire", new XAttribute("UId", Next()), connectors));

    /// <summary>触点：operand 接变量（scope 为 null 时用默认 Scope），返回部件 UId（in/out 由调用方连线）。</summary>
    public int Contact(string symbol, bool negated = false, string? scope = null)
    {
        var contact = Part("Contact");
        if (negated) parts[^1].Add(new XElement("Negated", new XAttribute("Name", "operand")));
        Wire(Id(Access(symbol, scope)), Pin(contact, "operand"));
        return contact;
    }

    /// <summary>从左母线起串联触点驱动一个线圈（kind = Coil / SCoil / RCoil），返回线圈 UId。</summary>
    public int Series(string kind, string target, params (string Symbol, bool Negated)[] contacts)
    {
        var coil = Part(kind);
        Wire(Id(Access(target)), Pin(coil, "operand"));
        XElement from = Rail();
        foreach (var (symbol, negated) in contacts)
        {
            var c = Contact(symbol, negated);
            Wire(from, Pin(c, "in"));
            from = Pin(c, "out");
        }
        Wire(from, Pin(coil, "in"));
        return coil;
    }

    /// <summary>给部件（Part / Call）的某个端口加取反标记 &lt;Negated Name="port"/&gt;。</summary>
    public void Negate(int partUid, string port) =>
        parts.Single(p => (string?)p.Attribute("UId") == partUid.ToString()).Add(new XElement("Negated", new XAttribute("Name", port)));

    /// <summary>无符号的绝对地址访问（如 Area="Output" Type="Byte" BitOffset="8" 即 %QB1）。</summary>
    public int AbsoluteAccess(string area, string type, int bitOffset, string scope = "GlobalVariable")
    {
        var id = Next();
        parts.Add(new XElement("Access", new XAttribute("Scope", scope), new XAttribute("UId", id),
            new XElement("Address", new XAttribute("Area", area), new XAttribute("Type", type), new XAttribute("BitOffset", bitOffset))));
        return id;
    }

    /// <summary>从左母线起若干单触点并联进一个 O 块，返回 O 的 UId（取 out 继续连线）。</summary>
    public int Or(params string[] symbols)
    {
        var or = Part("O", symbols.Length);
        var contacts = symbols.Select(s => Contact(s)).ToArray();
        Wire([Rail(), .. contacts.Select(c => Pin(c, "in"))]);
        for (var i = 0; i < contacts.Length; i++) Wire(Pin(contacts[i], "out"), Pin(or, "in" + (i + 1)));
        return or;
    }

    /// <summary>从 source 端口起串联触点，返回最后一个触点的 UId。</summary>
    public int Chain(XElement source, params (string Symbol, bool Negated)[] contacts)
    {
        var last = -1;
        foreach (var (symbol, negated) in contacts)
        {
            var c = Contact(symbol, negated);
            Wire(source, Pin(c, "in"));
            source = Pin(c, "out");
            last = c;
        }
        return last;
    }

    /// <summary>线圈：in 接 source 端口，operand 接目标变量。</summary>
    public int Coil(string kind, string target, XElement source)
    {
        var coil = Part(kind);
        Wire(Id(Access(target)), Pin(coil, "operand"));
        Wire(source, Pin(coil, "in"));
        return coil;
    }

    /// <summary>接通延时定时器：IN 接 source，返回定时器 UId（取 Q 继续连线）。</summary>
    public int Timer(string kind, XElement source)
    {
        var timer = Part(kind);
        Wire(source, Pin(timer, "IN"));
        return timer;
    }

    /// <summary>FB 调用部件。pins = (引脚名, "Input"/"Output"/"InOut")。</summary>
    public int Call(string fbName, params (string Name, string Section)[] pins)
    {
        var id = Next();
        parts.Add(new XElement("Call", new XAttribute("UId", id),
            new XElement("CallInfo", new XAttribute("Name", fbName), new XAttribute("BlockType", "FB"),
                pins.Select(p => new XElement("Parameter", new XAttribute("Name", p.Name),
                    new XAttribute("Section", p.Section), new XAttribute("Type", "Bool"))))));
        return id;
    }

    /// <summary>字面常数访问（默认 LiteralConstant；带类型的常量如 T#2S 在博图导出里是 TypedConstant），返回 Access 的 UId。</summary>
    public int Constant(string value, string scope = "LiteralConstant")
    {
        var id = Next();
        parts.Add(new XElement("Access", new XAttribute("Scope", scope), new XAttribute("UId", id),
            new XElement("Constant", new XElement("ConstantValue", value))));
        return id;
    }

    /// <summary>符号常量访问（GlobalConstant/LocalConstant，只有 Constant@Name、没有 ConstantValue），返回 Access 的 UId。</summary>
    public int NamedConstant(string name, string scope = "GlobalConstant")
    {
        var id = Next();
        parts.Add(new XElement("Access", new XAttribute("Scope", scope), new XAttribute("UId", id),
            new XElement("Constant", new XAttribute("Name", name))));
        return id;
    }

    /// <summary>带背景实例的部件（如 TON），instance 按 '.' 拆成 Component。</summary>
    public int InstancePart(string name, string instance)
    {
        var id = Part(name);
        parts[^1].Add(new XElement("Instance", new XAttribute("Scope", "LocalVariable"),
            instance.Split('.').Select(c => new XElement("Component", new XAttribute("Name", c)))));
        return id;
    }

    /// <summary>之后的 FB 调用写入的 Instance Scope（默认 LocalVariable，即多重背景；全局背景 DB 用 GlobalVariable）。</summary>
    public string CallInstanceScope { get; set; } = "LocalVariable";

    /// <summary>带实例名的 FB 调用。pins = (引脚名, "Input"/"Output"/"InOut", 类型)。</summary>
    public int CallWithInstance(string fbName, string instance, params (string Name, string Section, string Type)[] pins)
    {
        var id = Next();
        parts.Add(new XElement("Call", new XAttribute("UId", id),
            new XElement("CallInfo", new XAttribute("Name", fbName), new XAttribute("BlockType", "FB"),
                new XElement("Instance", new XAttribute("Scope", CallInstanceScope),
                    instance.Split('.').Select(c => new XElement("Component", new XAttribute("Name", c)))),
                pins.Select(p => new XElement("Parameter", new XAttribute("Name", p.Name),
                    new XAttribute("Section", p.Section), new XAttribute("Type", p.Type))))));
        return id;
    }

    /// <summary>带实例名且接实参的 FB 调用：Actual 非空的引脚连到一个全局变量 Access（输入/InOut 引脚读它；输出引脚写它，连线以引脚为驱动端）。</summary>
    public int CallWired(string fbName, string instance, params (string Pin, string Section, string Type, string? Actual)[] pins)
    {
        var call = CallWithInstance(fbName, instance, pins.Select(p => (p.Pin, p.Section, p.Type)).ToArray());
        foreach (var p in pins.Where(p => p.Actual is not null))
        {
            var access = Access(p.Actual!, "GlobalVariable");
            if (p.Section == "Output") Wire(Pin(call, p.Pin), Id(access));   // 输出引脚：驱动端在前（与博图导出一致）
            else Wire(Id(access), Pin(call, p.Pin));
        }
        return call;
    }

    public string Build() => new XElement("FlgNet", new XElement("Parts", parts), new XElement("Wires", wires))
        .ToString(SaveOptions.DisableFormatting);

    /// <summary>接口 XML；temps 非空时追加 Temp 段（成员一律 Bool）；statics 非空时追加 Static 段（原样放入的成员元素）。</summary>
    public static string Interface(IEnumerable<string> inputs, IEnumerable<string> outputs, IEnumerable<string>? temps = null,
        IEnumerable<XElement>? statics = null) =>
        new XElement("Interface", new XElement("Sections",
            new XElement("Section", new XAttribute("Name", "Input"),
                inputs.Select(n => new XElement("Member", new XAttribute("Name", n), new XAttribute("Datatype", "Bool")))),
            new XElement("Section", new XAttribute("Name", "Output"),
                outputs.Select(n => new XElement("Member", new XAttribute("Name", n), new XAttribute("Datatype", "Bool")))),
            temps == null ? null : new XElement("Section", new XAttribute("Name", "Temp"),
                temps.Select(n => new XElement("Member", new XAttribute("Name", n), new XAttribute("Datatype", "Bool")))),
            statics == null ? null : new XElement("Section", new XAttribute("Name", "Static"), statics)))
        .ToString(SaveOptions.DisableFormatting);
}
