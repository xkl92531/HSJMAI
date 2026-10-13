#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

/// <summary>按程序段根元素分派：顺控块整体（Graph）、FlgNet（含顺控 Transition/Interlock/Supervision 包装）、
/// STL（StatementList）、StructuredText、动作段、纯文本源码。</summary>
public static class UnitTranslator
{
    /// <param name="tags">点表：其中的 Bool 点让 SCL 里读它们的逻辑运算可以按逻辑翻译（审查 I-1）。同一项目的各处翻译须传同一份点表，
    /// 译文（进 FB 契约缓存键）才一致；不传则只认块接口里的 Bool 成员。</param>
    public static IrUnit Translate(PipelineUnit unit, IReadOnlyList<PipelineTag>? tags = null)
    {
        var prefix = $"{unit.BlockName}/{unit.NetworkOrder ?? 0}";
        var raw = (unit.RawContent ?? "").Trim();
        if (raw.Length == 0) return Make(unit, "NoSource", [], [], []);
        if (!raw.StartsWith('<'))
            return Make(unit, "Text", [Source(prefix, raw)], [], []);
        XElement root;
        try { root = XElement.Parse(raw); }
        catch (XmlException ex) { return Make(unit, "Invalid", [Source(prefix, "（XML 无法解析：" + ex.Message + "）")], [], ["InvalidXml"]); }

        var rootKind = root.Name.LocalName;
        if (rootKind == "Graph") return new GraphWalker(prefix).Translate(unit, root);
        if (rootKind == "StatementList")
        {
            var stlUnknown = new SortedSet<string>(StringComparer.Ordinal);
            var text = StlText.Render(root, stlUnknown);
            return Make(unit, rootKind, [Source(prefix, text)], [], stlUnknown.ToArray());
        }

        var labels = root.Descendants().Where(e => e.Name.LocalName == "LabelDeclaration")
            .Select(d => d.Descendants().FirstOrDefault(x => x.Name.LocalName == "Label"))
            .Where(l => l != null).Select((l, i) => new IrStatement($"{prefix}/L{i + 1}", IrStatementKinds.Label,
                (string?)l!.Attribute("Name") ?? "?", IrExpr.True, null)).ToList();

        var flg = root.DescendantsAndSelf().FirstOrDefault(e => e.Name.LocalName == "FlgNet");
        if (flg != null)
        {
            var result = FlgNetTranslator.TranslateFull(flg, prefix);
            return Make(unit, rootKind, labels.Concat(result.Statements).ToArray(), result.Nodes, result.UnknownParts, result.Instructions);
        }
        var sclUnknown = new SortedSet<string>(StringComparer.Ordinal);
        if (rootKind == "Actions")
            return Make(unit, rootKind, Actions(root, i => $"{prefix}/{i}", sclUnknown), [], sclUnknown.ToArray());
        var st = root.DescendantsAndSelf().FirstOrDefault(e => e.Name.LocalName == "StructuredText");
        if (st != null)
            return Make(unit, rootKind, SclTranslator.Translate(st, prefix, sclUnknown, KnownBool(unit.InterfaceXml, tags)), [], sclUnknown.ToArray());
        return Make(unit, rootKind, [Source(prefix, "（未识别的程序段格式：" + rootKind + "）")], [], [rootKind]);
    }

    /// <summary>确定是 Bool 的名字：块接口里类型为 Bool 的成员（含结构内成员的点路径；数组元素按「Array[…] of Bool」），
    /// 点表里唯一且类型为 Bool 的点，以及 I/Q/M 位地址。忽略大小写。</summary>
    static Func<string, bool> KnownBool(string interfaceXml, IReadOnlyList<PipelineTag>? tags)
    {
        var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Walk(IfaceMember m, string prefix)
        {
            var name = prefix.Length == 0 ? m.Name : prefix + "." + m.Name;
            types.TryAdd(name, (m.DataType ?? "").Trim().Trim('"'));
            foreach (var c in m.Children) Walk(c, name);
        }
        foreach (var m in InterfaceReader.Read(interfaceXml)) Walk(m, "");
        var boolTags = tags is null ? null : BoolTags(tags);
        return name =>
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            var n = name.Trim();
            if (boolTags is not null && boolTags.Contains(n)) return true;
            if (BitAddress.IsMatch(n)) return true;
            // 去掉下标拼出接口路径：最后一段是下标时看数组元素类型
            var segs = IrBlock.Segments(n);
            var path = new List<string>();
            var lastIsIndex = false;
            foreach (var seg in segs)
            {
                lastIsIndex = seg.StartsWith('[');
                if (!lastIsIndex) path.Add(seg.Trim('"'));
            }
            if (!types.TryGetValue(string.Join(".", path), out var type)) return false;
            if (!lastIsIndex) return string.Equals(type, "Bool", StringComparison.OrdinalIgnoreCase);
            var of = type.LastIndexOf(" of ", StringComparison.OrdinalIgnoreCase);
            return type.StartsWith("Array", StringComparison.OrdinalIgnoreCase) && of > 0
                && string.Equals(type[(of + 4)..].Trim().Trim('"'), "Bool", StringComparison.OrdinalIgnoreCase);
        };
    }

    static readonly System.Text.RegularExpressions.Regex BitAddress =
        new(@"^%[IQMEA]\d+\.[0-7]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<PipelineTag>, HashSet<string>> boolTagCache = new();

    /// <summary>点表里名字唯一且类型为 Bool 的点（同一份点表只算一次）。</summary>
    static HashSet<string> BoolTags(IReadOnlyList<PipelineTag> tags) => boolTagCache.GetValue(tags, t =>
        t.Where(x => !string.IsNullOrWhiteSpace(x.Name)).GroupBy(x => x.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1 && string.Equals(g.Single().DataType?.Trim(), "Bool", StringComparison.OrdinalIgnoreCase))
            .Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase));

    /// <summary>没有子元素也没有文字的元素（如 &lt;PostOperations/&gt;）：没有任何内容可丢，直接略过。</summary>
    static bool IsEmpty(XElement e) => !e.HasElements && e.Value.Trim().Length == 0;

    static IrStatement Unrecognized(string id, string name) =>
        new(id, IrStatementKinds.Unrecognized, null, IrExpr.True, null, null, $"〔未识别的顺控元素 {name}〕");

    /// <summary>顺控动作：只丢弃完全为空的 &lt;Action/&gt;；有限定符/事件/互锁属性而没有词元的动作照样保留；
    /// Actions 下的其它非空元素明示为未识别的顺控元素。</summary>
    internal static IReadOnlyList<IrStatement> Actions(XElement actions, Func<int, string> id, ISet<string> unknown)
    {
        var list = new List<IrStatement>();
        foreach (var child in actions.Elements())
        {
            var name = child.Name.LocalName;
            if (name is "Title" or "Comment") continue;
            if (name != "Action")
            {
                if (IsEmpty(child)) continue;
                list.Add(Unrecognized(id(list.Count + 1), name));
                unknown.Add(name);
                continue;
            }
            if (!child.HasElements && !child.Attributes().Any(a => !a.IsNamespaceDeclaration)) continue;
            var described = new List<string>();
            if ((string?)child.Attribute("Qualifier") is { } qualifier) described.Add("限定符 " + qualifier);
            if ((string?)child.Attribute("Event") is { } @event) described.Add("事件 " + @event.Trim());
            if ((string?)child.Attribute("Interlock") is { } interlock) described.Add("受互锁 " + interlock);
            list.Add(new IrStatement(id(list.Count + 1), IrStatementKinds.Action, string.Join("，", described),
                IrExpr.True, null, null, SclText.Render(child, unknown))
            { TextCallsBlock = child.Descendants().Any(e => e.Name.LocalName is "CallInfo" or "Instruction") });
        }
        return list;
    }

    static IrStatement Source(string prefix, string text) =>
        new($"{prefix}/1", IrStatementKinds.Source, null, IrExpr.True, null, null, text);

    static IrUnit Make(PipelineUnit unit, string rootKind, IReadOnlyList<IrStatement> statements,
        IReadOnlyList<IrNode> nodes, IReadOnlyList<string> unknown, IReadOnlyList<string>? instructions = null) =>
        new(unit.Id, unit.BlockName, unit.NetworkOrder, unit.Language, rootKind, statements, nodes, unknown, unit.UnitKind, instructions);

    /// <summary>顺控块（NetworkSource/Graph）：逐序列按文档顺序翻译步、动作、互锁、监控、转移、分支、连接。
    /// 任何子元素都要么被翻译、要么明示（报警设置为配置说明，其余为未识别的顺控元素），只有完全为空的元素才略过。</summary>
    sealed class GraphWalker(string prefix)
    {
        readonly List<IrStatement> statements = [];
        readonly List<IrNode> nodes = [];
        readonly SortedSet<string> unknown = new(StringComparer.Ordinal);
        readonly SortedSet<string> instructions = new(StringComparer.Ordinal);
        readonly Dictionary<string, int> otherCounts = new(StringComparer.Ordinal);
        /// <summary>多序列时节点名加序列前缀（Q{s}.），避免不同序列的同号步/转移节点重名。</summary>
        string nodeSequence = "";

        static bool Is(XElement e, string name) => e.Name.LocalName == name;
        static string? Attr(XElement e, string name) => (string?)e.Attribute(name);

        public IrUnit Translate(PipelineUnit unit, XElement graph)
        {
            var sequenceCount = graph.Elements().Count(e => Is(e, "Sequence"));
            var index = 0;
            foreach (var child in graph.Elements())
            {
                switch (child.Name.LocalName)
                {
                    case "Sequence":
                        index++;
                        nodeSequence = sequenceCount > 1 ? $"Q{index}." : "";
                        Sequence(child, sequenceCount > 1 ? $"{prefix}/Q{index}" : prefix);
                        nodeSequence = "";
                        break;
                    case "Title": case "Comment": break;
                    default: TopLevel(child, prefix); break;
                }
            }
            var all = new SortedSet<string>(unknown, StringComparer.Ordinal);
            all.UnionWith(instructions);
            return new IrUnit(unit.Id, unit.BlockName, unit.NetworkOrder, unit.Language, "Graph", statements, nodes,
                all.ToArray(), unit.UnitKind, instructions.ToArray());
        }

        /// <summary>遍历容器的子元素：期望的元素交给 handle，标题/注释略过，其余交给 Other。</summary>
        void Container(XElement container, string expected, string basePrefix, Action<XElement> handle)
        {
            foreach (var child in container.Elements())
            {
                if (Is(child, expected)) handle(child);
                else if (child.Name.LocalName is not ("Title" or "Comment")) Other(child, basePrefix);
            }
        }

        void Sequence(XElement sequence, string sp)
        {
            var connection = 0;
            foreach (var child in sequence.Elements())
            {
                switch (child.Name.LocalName)
                {
                    case "Title": case "Comment": break;
                    case "Steps": Container(child, "Step", sp, step => Step(step, sp)); break;
                    case "Transitions": Container(child, "Transition", sp, transition => Transition(transition, sp)); break;
                    case "Branches":
                        Container(child, "Branch", sp, branch =>
                        {
                            var number = Attr(branch, "Number") ?? "?";
                            Add($"{sp}/B{number}", IrStatementKinds.Branch, $"分支 {number}（{Attr(branch, "Type") ?? "?"}）");
                        });
                        break;
                    case "Connections":
                        Container(child, "Connection", sp, link =>
                            Add($"{sp}/C{++connection}", IrStatementKinds.Connection,
                                $"连接：{Endpoint(link, "NodeFrom")} → {Endpoint(link, "NodeTo")}"));
                        break;
                    default: TopLevel(child, sp); break;
                }
            }
        }

        void Step(XElement step, string sp)
        {
            var number = Attr(step, "Number") ?? "?";
            var name = Attr(step, "Name") ?? "";
            var notes = new List<string>();
            if (string.Equals(Attr(step, "Init"), "true", StringComparison.OrdinalIgnoreCase)) notes.Add("初始步");
            if (Attr(step, "MaximumStepTime") is { Length: > 0 } maximum) notes.Add("最长步时间 " + maximum);
            if (Attr(step, "WarningTime") is { Length: > 0 } warning) notes.Add("警告时间 " + warning);
            var id = $"{sp}/S{number}";
            Add(id, IrStatementKinds.Step,
                $"步 {number}{(name.Length == 0 ? "" : " " + name)}{(notes.Count == 0 ? "" : "（" + string.Join("，", notes) + "）")}");
            // 固定顺序：动作 → 互锁 → 监控 → 其它子元素（文档顺序）
            foreach (var actions in step.Elements().Where(e => Is(e, "Actions")))
                statements.AddRange(UnitTranslator.Actions(actions, i => $"{id}A{i}", unknown));
            var interlocks = 0;
            foreach (var container in step.Elements().Where(e => Is(e, "Interlocks")))
                Container(container, "Interlock", id, owner => Networks(owner, $"S{number}I", sp, id, ref interlocks));
            var supervisions = 0;
            foreach (var container in step.Elements().Where(e => Is(e, "Supervisions")))
                Container(container, "Supervision", id, owner => Networks(owner, $"S{number}V", sp, id, ref supervisions));
            foreach (var child in step.Elements())
                if (child.Name.LocalName is not ("Title" or "Comment" or "Actions" or "Interlocks" or "Supervisions"))
                    Other(child, id);
        }

        /// <summary>一个互锁/监控条件：逐个翻译其 FlgNet；没有 FlgNet 的非空条件交给 Other 明示。</summary>
        void Networks(XElement owner, string tag, string sp, string stepId, ref int k)
        {
            var flgs = owner.Descendants().Where(d => Is(d, "FlgNet")).ToList();
            if (flgs.Count == 0) { Other(owner, stepId); return; }
            foreach (var flg in flgs)
            {
                k++;
                FlgNet(flg, $"{sp}/{tag}{k}", $"{nodeSequence}{tag}{k}.");
            }
        }

        void Transition(XElement transition, string sp)
        {
            var number = Attr(transition, "Number") ?? "?";
            var name = Attr(transition, "Name") ?? "";
            var flgs = transition.Descendants().Where(d => Is(d, "FlgNet")).ToList();
            Add($"{sp}/T{number}", IrStatementKinds.Transition,
                $"转移 {number}{(name.Length == 0 ? "" : " " + name)}{(flgs.Count == 0 ? "（未导出条件网络）" : "")}");
            for (var i = 0; i < flgs.Count; i++)
                FlgNet(flgs[i], flgs.Count == 1 ? $"{sp}/T{number}" : $"{sp}/T{number}F{i + 1}",
                    $"{nodeSequence}T{number}{(flgs.Count == 1 ? "" : $"F{i + 1}")}.", $"〔转移 T{number} 条件〕");
        }

        /// <summary>没有专门处理的元素：完全为空则略过；AlarmsSettings 是配置，输出说明；含 FlgNet 则逐个翻译；
        /// 否则明示为未识别的顺控元素并记录。</summary>
        void Other(XElement element, string basePrefix)
        {
            if (IsEmpty(element)) return;
            var name = element.Name.LocalName;
            if (name == "AlarmsSettings")
            {
                Add($"{basePrefix}/{name}{Next(name)}", IrStatementKinds.Note, "报警设置（配置项，非程序逻辑）");
                return;
            }
            var roots = element.Descendants()
                .Where(d => IsLogicRoot(d) && !d.Ancestors().TakeWhile(a => a != element).Any(IsLogicRoot)).ToList();
            if (roots.Count == 0)
            {
                statements.Add(Unrecognized($"{basePrefix}/{name}{Next(name)}", name));
                unknown.Add(name);
                return;
            }
            foreach (var root in roots)
            {
                var k = Next(name);
                var id = $"{basePrefix}/{name}{k}";
                if (Is(root, "FlgNet")) FlgNet(root, id, $"{nodeSequence}{name}{k}.");
                else
                    statements.Add(new IrStatement(id, IrStatementKinds.Source, null, IrExpr.True, null, null,
                        Is(root, "StatementList") ? StlText.Render(root, unknown) : SclText.Render(root, unknown)));
            }
        }

        static bool IsLogicRoot(XElement e) => e.Name.LocalName is "FlgNet" or "StructuredText" or "StatementList";

        /// <summary>Graph/Sequence 的直接子元素：子树里没有任何程序内容（FlgNet/StructuredText/StatementList/Token/Access，
        /// 也没有非空白文字）时静默略过，如 &lt;PostOperations&gt;&lt;PermanentOperation ProgrammingLanguage="LAD"/&gt;&lt;/PostOperations&gt;；
        /// 前置/后置操作同样适用。AlarmsSettings 仍输出配置说明。</summary>
        void TopLevel(XElement child, string basePrefix)
        {
            if (child.Name.LocalName != "AlarmsSettings" && !HasLogic(child)) return;
            Other(child, basePrefix);
        }

        static bool HasLogic(XElement e) =>
            e.Value.Trim().Length > 0
            || e.Descendants().Any(d => d.Name.LocalName is "FlgNet" or "StructuredText" or "StatementList" or "Token" or "Access");

        int Next(string name) => otherCounts[name] = otherCounts.GetValueOrDefault(name) + 1;

        void FlgNet(XElement flg, string id, string nodePrefix, string transitionTarget = "〔转移条件〕")
        {
            var result = FlgNetTranslator.TranslateFull(flg, id, transitionTarget, nodePrefix);
            statements.AddRange(result.Statements);
            nodes.AddRange(result.Nodes);
            instructions.UnionWith(result.Instructions);
            unknown.UnionWith(result.UnknownParts.Except(result.Instructions));
        }

        static string Endpoint(XElement connection, string side)
        {
            var container = connection.Elements().FirstOrDefault(e => Is(e, side));
            var reference = container?.Elements().FirstOrDefault();
            if (reference == null) return "?";
            var number = Attr(reference, "Number") ?? "?";
            return reference.Name.LocalName switch
            {
                "EndConnection" => "序列结束",
                "StepRef" => $"步 {number}",
                "TransitionRef" => $"转移 {number}",
                "BranchRef" => $"分支 {number}",
                var other => $"〔{other}〕"
            };
        }

        void Add(string id, string kind, string text) =>
            statements.Add(new IrStatement(id, kind, null, IrExpr.True, null, null, text));
    }
}
