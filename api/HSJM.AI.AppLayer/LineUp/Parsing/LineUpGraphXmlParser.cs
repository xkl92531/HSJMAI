using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using HSJM.AI.AppLayer.LineUp.Models;

namespace HSJM.AI.AppLayer.LineUp.Parsing;

public sealed class LineUpGraphXmlParser
{
    public const int MaximumXmlDepth = 128;
    public const int MaximumPartAttributes = LineUpGraphPreviewValidator.MaximumPartAttributes;
    public const int MaximumPartAttributeNameLength = LineUpGraphPreviewValidator.MaximumPartAttributeNameLength;
    public const int MaximumPartAttributeValueLength = LineUpGraphPreviewValidator.MaximumPartAttributeValueLength;

    public async Task<LineUpGraphDocument> ParseAsync(
        LineUpGraphSourceFile source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();

        var logicalFileName = SafeDisplayName(source.FileName);
        try
        {
            var xml = await LoadDocumentAsync(source.Content, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return ParseDocument(xml, source, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (XmlException)
        {
            throw Error(
                "InvalidGraphXml",
                logicalFileName,
                "The graph source could not be parsed safely.");
        }
        catch (GraphStructureException)
        {
            throw Error(
                "InvalidGraphStructure",
                logicalFileName,
                "The graph source does not contain a supported TIA GRAPH structure.");
        }
        catch (ArgumentException)
        {
            throw Error(
                "InvalidGraphStructure",
                logicalFileName,
                "The graph source does not contain a supported TIA GRAPH structure.");
        }
        catch (InvalidOperationException)
        {
            throw Error(
                "InvalidGraphStructure",
                logicalFileName,
                "The graph source does not contain a supported TIA GRAPH structure.");
        }
    }

    private static async Task<XDocument> LoadDocumentAsync(
        string content,
        CancellationToken cancellationToken)
    {
        var safeContent = content ?? string.Empty;
        using (var depthTextReader = new StringReader(safeContent))
        using (var depthReader = XmlReader.Create(depthTextReader, SafeReaderSettings()))
        {
            while (await depthReader.ReadAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (depthReader.Depth > MaximumXmlDepth)
                {
                    throw new XmlException("The XML depth limit was exceeded.");
                }
            }
        }

        using var textReader = new StringReader(safeContent);
        using var xmlReader = XmlReader.Create(textReader, SafeReaderSettings());
        return await XDocument.LoadAsync(xmlReader, LoadOptions.PreserveWhitespace, cancellationToken);
    }

    private static XmlReaderSettings SafeReaderSettings() => new()
    {
        Async = true,
        CloseInput = false,
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = LineUpGraphPackageReader.MaxSingleFileBytes,
        MaxCharactersFromEntities = 0
    };

    private static LineUpGraphDocument ParseDocument(
        XDocument xml,
        LineUpGraphSourceFile source,
        CancellationToken cancellationToken)
    {
        var document = xml.Root ?? throw new GraphStructureException();
        var blocks = ElementsLocal(document, "SW.Blocks.FB").ToList();
        if (blocks.Count != 1)
        {
            throw new GraphStructureException();
        }

        var block = blocks[0];
        var attributeLists = ElementsLocal(block, "AttributeList").ToList();
        if (attributeLists.Count != 1)
        {
            throw new GraphStructureException();
        }

        var attributes = attributeLists[0];
        var blockName = RequiredElementText(attributes, "Name");
        var blockNumber = RequiredIntegerElement(attributes, "Number");
        var programmingLanguage = RequiredElementText(attributes, "ProgrammingLanguage");
        var graphCandidates = ElementsLocal(block, "ObjectList")
            .SelectMany(objectList => ElementsLocal(objectList, "SW.Blocks.CompileUnit"))
            .SelectMany(compileUnit => ElementsLocal(compileUnit, "AttributeList"))
            .SelectMany(attributeList => ElementsLocal(attributeList, "NetworkSource"))
            .SelectMany(networkSource => ElementsLocal(networkSource, "Graph"))
            .ToList();
        if (graphCandidates.Count != 1)
        {
            throw new GraphStructureException();
        }

        var graph = graphCandidates[0];

        var result = new LineUpGraphDocument
        {
            SourceFileName = source.FileName,
            SourceRelativePath = source.RelativePath,
            SourceSha256 = source.Sha256,
            BlockName = blockName,
            BlockNumber = blockNumber,
            ProgrammingLanguage = programmingLanguage,
            InstanceDbName = null,
            SourceName = blockName
        };

        var sequenceNodes = ElementsLocal(graph, "Sequence").ToList();
        if (sequenceNodes.Count == 0)
        {
            throw new GraphStructureException();
        }

        var sequenceKeys = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < sequenceNodes.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sequence = ParseSequence(
                sequenceNodes[index],
                blockName,
                index + 1,
                cancellationToken);
            if (!sequenceKeys.Add(sequence.SourceKey))
            {
                throw new GraphStructureException();
            }

            result.Sequences.Add(sequence);
        }

        return result;
    }

    private static LineUpGraphSequence ParseSequence(
        XElement sequenceNode,
        string blockName,
        int sequenceOrdinal,
        CancellationToken cancellationToken)
    {
        var title = ElementsLocal(sequenceNode, "Title").FirstOrDefault();
        var sequenceName = LocalizedText(title);
        if (string.IsNullOrEmpty(sequenceName))
        {
            sequenceName = AttributeValue(sequenceNode, "Name") ?? $"Sequence {sequenceOrdinal}";
        }

        var sequence = new LineUpGraphSequence
        {
            SourceName = sequenceName,
            SourceKey = LineUpGraphSourceKey.ForSequence(blockName, sequenceName)
        };

        ParseSteps(sequenceNode, blockName, sequence, cancellationToken);
        ParseTransitions(sequenceNode, blockName, sequence, cancellationToken);
        ParseBranches(sequenceNode, blockName, sequence, cancellationToken);
        ParseConnections(sequenceNode, sequence, cancellationToken);
        return sequence;
    }

    private static void ParseSteps(
        XElement sequenceNode,
        string blockName,
        LineUpGraphSequence sequence,
        CancellationToken cancellationToken)
    {
        var stepsContainer = ElementsLocal(sequenceNode, "Steps").FirstOrDefault();
        if (stepsContainer == null)
        {
            return;
        }

        var sourceKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stepNode in ElementsLocal(stepsContainer, "Step"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var number = RequiredIntegerAttribute(stepNode, "Number");
            var sourceName = AttributeValue(stepNode, "Name") ?? $"Step {number}";
            var step = new LineUpGraphStep
            {
                Number = number,
                Init = BooleanAttribute(stepNode, "Init"),
                SourceName = sourceName,
                SourceKey = LineUpGraphSourceKey.ForStep(blockName, sequence.SourceName, number),
                MaximumStepTime = AttributeValue(stepNode, "MaximumStepTime") ?? string.Empty,
                WarningTime = AttributeValue(stepNode, "WarningTime") ?? string.Empty,
                Comment = LocalizedText(ElementsLocal(stepNode, "Comment").FirstOrDefault())
            };
            if (!sourceKeys.Add(step.SourceKey))
            {
                throw new GraphStructureException();
            }

            sequence.Steps.Add(step);
            ParseActions(stepNode, step, sequence, cancellationToken);
            ParseStepLogicNetworks(stepNode, step, cancellationToken);
        }
    }

    private static void ParseActions(
        XElement stepNode,
        LineUpGraphStep step,
        LineUpGraphSequence sequence,
        CancellationToken cancellationToken)
    {
        var actionsContainer = ElementsLocal(stepNode, "Actions").FirstOrDefault();
        if (actionsContainer == null)
        {
            return;
        }

        step.ActionGroupTitle = LocalizedText(ElementsLocal(actionsContainer, "Title").FirstOrDefault());

        var ordinal = 0;
        foreach (var actionNode in ElementsLocal(actionsContainer, "Action"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ordinal++;
            var qualifierAttribute = AttributeLocal(actionNode, "Qualifier");
            var eventAttribute = AttributeLocal(actionNode, "Event");
            var interlockAttribute = AttributeLocal(actionNode, "Interlock");
            var tokenNodes = ElementsLocal(actionNode, "Token").ToList();

            // TIA emits one empty placeholder Action for many steps. Presence of either
            // a qualifier attribute or a Token element is source evidence and must be kept,
            // even when the only token is a line-feed.
            if (qualifierAttribute == null && eventAttribute == null && interlockAttribute == null && tokenNodes.Count == 0)
            {
                continue;
            }

            var tokens = tokenNodes
                .Select(token => AttributeValue(token, "Text") ?? token.Value)
                .ToList();
            var actionText = string.Concat(tokens);
            var sourceName = AttributeValue(actionNode, "Name") ?? actionText;
            var action = new LineUpGraphAction
            {
                Ordinal = ordinal,
                StepNumber = step.Number,
                Qualifier = qualifierAttribute?.Value ?? string.Empty,
                Event = eventAttribute?.Value.Trim(),
                Interlock = NullableBooleanAttribute(actionNode, "Interlock"),
                Tokens = tokens,
                ActionText = actionText,
                SourceName = sourceName,
                SourceKey = LineUpGraphSourceKey.ForAction(step.SourceKey, ordinal)
            };
            sequence.Actions.Add(action);
        }
    }

    private static void ParseTransitions(
        XElement sequenceNode,
        string blockName,
        LineUpGraphSequence sequence,
        CancellationToken cancellationToken)
    {
        var transitionsContainer = ElementsLocal(sequenceNode, "Transitions").FirstOrDefault();
        if (transitionsContainer == null)
        {
            return;
        }

        var sourceKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var transitionNode in ElementsLocal(transitionsContainer, "Transition"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var number = RequiredIntegerAttribute(transitionNode, "Number");
            var sourceName = AttributeValue(transitionNode, "Name") ?? $"Transition {number}";
            var condition = ElementsLocal(transitionNode, "FlgNet").FirstOrDefault();
            var transition = new LineUpGraphTransition
            {
                Number = number,
                IsMissing = BooleanAttribute(transitionNode, "IsMissing"),
                ProgrammingLanguage = AttributeValue(transitionNode, "ProgrammingLanguage") ?? string.Empty,
                RawXml = condition?.ToString(SaveOptions.DisableFormatting) ?? string.Empty,
                SourceName = sourceName,
                SourceKey = LineUpGraphSourceKey.ForTransition(blockName, sequence.SourceName, number)
            };
            if (!sourceKeys.Add(transition.SourceKey))
            {
                throw new GraphStructureException();
            }

            sequence.Transitions.Add(transition);
            transition.ConditionNetworks.AddRange(ParseOptionalTransitionConditionNetwork(
                transitionNode,
                transition.SourceKey,
                transition.ProgrammingLanguage,
                cancellationToken));
        }
    }

    private static void ParseStepLogicNetworks(
        XElement stepNode,
        LineUpGraphStep step,
        CancellationToken cancellationToken)
    {
        var interlocks = ElementsLocal(stepNode, "Interlocks").FirstOrDefault();
        if (interlocks != null)
        {
            step.Interlocks.AddRange(ParseLogicNetworks(
                ElementsLocal(interlocks, "Interlock"),
                step.SourceKey,
                "StepInterlock",
                cancellationToken));
        }

        var supervisions = ElementsLocal(stepNode, "Supervisions").FirstOrDefault();
        if (supervisions != null)
        {
            step.Supervisions.AddRange(ParseLogicNetworks(
                ElementsLocal(supervisions, "Supervision"),
                step.SourceKey,
                "StepSupervision",
                cancellationToken));
        }
    }

    private static IReadOnlyList<LineUpGraphLogicNetwork> ParseOptionalTransitionConditionNetwork(
        XElement transitionNode,
        string transitionSourceKey,
        string programmingLanguage,
        CancellationToken cancellationToken) =>
        ParseLogicNetwork(
            transitionNode,
            transitionSourceKey,
            "TransitionCondition",
            programmingLanguage,
            cancellationToken,
            ordinal: 1,
            requireDirectFlgNet: false);

    private static IReadOnlyList<LineUpGraphLogicNetwork> ParseLogicNetworks(
        IEnumerable<XElement> networkItems,
        string ownerSourceKey,
        string role,
        CancellationToken cancellationToken)
    {
        var items = networkItems.ToList();
        var networks = new List<LineUpGraphLogicNetwork>();
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            networks.AddRange(ParseRequiredStepLogicNetwork(
                item,
                ownerSourceKey,
                role,
                AttributeValue(item, "ProgrammingLanguage") ?? string.Empty,
                cancellationToken,
                index + 1));
        }

        return networks;
    }

    private static IReadOnlyList<LineUpGraphLogicNetwork> ParseRequiredStepLogicNetwork(
        XElement networkItem,
        string ownerSourceKey,
        string role,
        string programmingLanguage,
        CancellationToken cancellationToken,
        int ordinal) =>
        ParseLogicNetwork(
            networkItem,
            ownerSourceKey,
            role,
            programmingLanguage,
            cancellationToken,
            ordinal,
            requireDirectFlgNet: true);

    private static IReadOnlyList<LineUpGraphLogicNetwork> ParseLogicNetwork(
        XElement networkItem,
        string ownerSourceKey,
        string role,
        string programmingLanguage,
        CancellationToken cancellationToken,
        int ordinal,
        bool requireDirectFlgNet)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var flgNets = ElementsLocal(networkItem, "FlgNet").ToList();
        if (flgNets.Count != 1)
        {
            if (!requireDirectFlgNet && flgNets.Count == 0)
            {
                return Array.Empty<LineUpGraphLogicNetwork>();
            }

            throw new GraphStructureException();
        }
        var network = new LineUpGraphLogicNetwork
        {
            OwnerSourceKey = ownerSourceKey,
            Role = role,
            Ordinal = ordinal,
            ProgrammingLanguage = programmingLanguage,
            Title = LocalizedText(ElementsLocal(networkItem, "Title").FirstOrDefault()),
            SourceKey = LineUpGraphSourceKey.ForLogicNetwork(ownerSourceKey, role, ordinal)
        };
        ParseLogicParts(flgNets[0], network, cancellationToken);
        ParseLogicWires(flgNets[0], network, cancellationToken);
        return new[] { network };
    }

    private static void ParseLogicParts(
        XElement flgNet,
        LineUpGraphLogicNetwork network,
        CancellationToken cancellationToken)
    {
        var partsContainer = ElementsLocal(flgNet, "Parts").FirstOrDefault();
        if (partsContainer == null)
        {
            return;
        }

        var uids = new HashSet<int>();
        foreach (var partNode in partsContainer.Elements())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uid = RequiredPositiveIntegerAttribute(partNode, "UId");
            if (!uids.Add(uid) || network.Parts.Count >= 4096)
            {
                throw new GraphStructureException();
            }

            var part = new LineUpGraphLogicPart
            {
                UId = uid,
                PartName = AttributeValue(partNode, "Name") ?? partNode.Name.LocalName,
                Scope = AttributeValue(partNode, "Scope") ?? string.Empty,
                SymbolPath = ParseSymbolPath(partNode),
                ConstantValue = ElementsLocal(
                        ElementsLocal(partNode, "Constant").FirstOrDefault() ?? new XElement("Constant"),
                        "ConstantValue")
                    .FirstOrDefault()?.Value ?? string.Empty,
                Negated = ParseOperandNegation(partNode),
                Attributes = ParsePartAttributes(partNode),
                SourceKey = LineUpGraphSourceKey.ForLogicPart(network.SourceKey, uid)
            };
            foreach (var templateValue in ElementsLocal(partNode, "TemplateValue"))
            {
                var name = AttributeValue(templateValue, "Name") ?? string.Empty;
                if (!part.TemplateValues.TryAdd(name, templateValue.Value))
                {
                    throw new GraphStructureException();
                }
            }

            network.Parts.Add(part);
        }
    }

    private static List<string> ParseSymbolPath(XElement partNode)
    {
        var symbols = ElementsLocal(partNode, "Symbol").ToList();
        if (symbols.Count == 0)
        {
            return new List<string>();
        }

        if (symbols.Count != 1)
        {
            throw new GraphStructureException();
        }

        return ElementsLocal(symbols[0], "Component")
            .Select(ParseSymbolComponent)
            .ToList();
    }

    private static string ParseSymbolComponent(XElement component)
    {
        var name = AttributeValue(component, "Name")?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            throw new GraphStructureException();
        }

        var accessModifier = AttributeValue(component, "AccessModifier");
        var directAccesses = ElementsLocal(component, "Access").ToList();
        var allAccesses = component.Descendants()
            .Where(element => element.Name.LocalName == "Access")
            .ToList();
        if (accessModifier == null)
        {
            return allAccesses.Count == 0 ? name : throw new GraphStructureException();
        }

        // 2026-09-28：FB350_*AutoGraph 出现 StackingData[DB57_UserDefine.Int[12]]——数组下标
        // 本身又是数组元素（TIA 合法），下标 Symbol 内嵌第二个 Access（directAccess=1 但
        // allAccess=2）。此前 allAccesses.Count != 1 一刀切拒绝，整块预览失败
        // （InvalidGraphStructure，批次 4625039937220251648 Failed）。放开该限制：
        // 嵌套下标由下方 indexAccess 分支递归 ParseSymbolComponent 渲染进展示符号；
        // 仅保留「必须有且仅有 1 个直接下标 Access」约束。
        if (!string.Equals(accessModifier, "Array", StringComparison.Ordinal) ||
            directAccesses.Count != 1)
        {
            throw new GraphStructureException();
        }

        var indexAccess = directAccesses[0];
        if (!string.Equals(AttributeValue(indexAccess, "Scope"), "LiteralConstant", StringComparison.Ordinal))
        {
            // 2026-09-15：FB460_MainGraph 出现 OKtableData[DB410_OP040_User.Status.LineNGPick.Number]
            // —— TIA 合法的运行期变量下标（NG 拿取工位号做数组索引）。此前直接
            // GraphStructureException 让整块预览失败（InvalidGraphStructure，FB460 两个批次均 Failed）。
            // 降级：把下标符号路径渲染进 Component 名（与字面常量下标的 Name[3] 形态一致），
            // 下游 LAD 网络编译按不可静态解析的展示符号处理（监控只绑 EXEC_BITS 步位，不消费该下标）。
            var indexSymbol = ElementsLocal(indexAccess, "Symbol").FirstOrDefault();
            var indexComponents = indexSymbol is null
                ? Array.Empty<string>()
                : ElementsLocal(indexSymbol, "Component").Select(ParseSymbolComponent).ToArray();
            var indexText = indexComponents.Length == 0 ? "?" : string.Join(".", indexComponents);
            return $"{name}[{indexText}]";
        }

        var constants = ElementsLocal(indexAccess, "Constant").ToList();
        if (constants.Count != 1)
        {
            throw new GraphStructureException();
        }

        var constantValues = ElementsLocal(constants[0], "ConstantValue").ToList();
        if (constantValues.Count != 1)
        {
            throw new GraphStructureException();
        }

        var constantValue = constantValues[0];
        if (constantValue.Nodes().Any(node => node is not XText))
        {
            throw new GraphStructureException();
        }

        var literal = constantValue.Value;
        if (!IsSignedBaseTenInteger(literal) ||
            !int.TryParse(literal, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var index))
        {
            throw new GraphStructureException();
        }

        return $"{name}[{index.ToString(CultureInfo.InvariantCulture)}]";
    }

    private static bool IsSignedBaseTenInteger(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var digitStart = value[0] is '+' or '-' ? 1 : 0;
        return digitStart < value.Length && value.AsSpan(digitStart).IndexOfAnyExceptInRange('0', '9') < 0;
    }

    private static bool ParseOperandNegation(XElement partNode)
    {
        var negations = ElementsLocal(partNode, "Negated").ToList();
        if (negations.Count == 0)
        {
            return false;
        }

        if (negations.Count != 1 ||
            !string.Equals(AttributeValue(negations[0], "Name"), "operand", StringComparison.Ordinal))
        {
            throw new GraphStructureException();
        }

        return true;
    }

    private static Dictionary<string, string> ParsePartAttributes(XElement partNode)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var attribute in partNode.Attributes()
                     .Where(attribute => !attribute.IsNamespaceDeclaration)
                     .OrderBy(attribute => attribute.Name.ToString(), StringComparer.Ordinal))
        {
            var name = attribute.Name.ToString();
            var value = attribute.Value;
            if (attributes.Count >= MaximumPartAttributes ||
                name.Length is 0 or > MaximumPartAttributeNameLength ||
                value.Length > MaximumPartAttributeValueLength ||
                !attributes.TryAdd(name, value))
            {
                throw new GraphStructureException();
            }
        }

        return attributes;
    }

    private static void ParseLogicWires(
        XElement flgNet,
        LineUpGraphLogicNetwork network,
        CancellationToken cancellationToken)
    {
        var wiresContainer = ElementsLocal(flgNet, "Wires").FirstOrDefault();
        if (wiresContainer == null)
        {
            return;
        }

        var parts = network.Parts.ToDictionary(part => part.UId);
        var uids = new HashSet<int>();
        foreach (var wireNode in ElementsLocal(wiresContainer, "Wire"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uid = RequiredPositiveIntegerAttribute(wireNode, "UId");
            if (!uids.Add(uid) || network.Wires.Count >= 8192)
            {
                throw new GraphStructureException();
            }

            var wire = new LineUpGraphLogicWire
            {
                UId = uid,
                SourceKey = LineUpGraphSourceKey.ForLogicWire(network.SourceKey, uid)
            };
            foreach (var endpointNode in wireNode.Elements())
            {
                var endpointUid = AttributeValue(endpointNode, "UId") is { } rawUid
                    ? RequiredPositiveInteger(rawUid)
                    : (int?)null;
                var endpoint = new LineUpGraphLogicEndpoint
                {
                    Kind = endpointNode.Name.LocalName,
                    UId = endpointUid,
                    Name = AttributeValue(endpointNode, "Name") ?? string.Empty
                };
                wire.Endpoints.Add(endpoint);

                if (endpoint.Kind is "IdentCon" or "NameCon")
                {
                    if (endpointUid is null || !parts.TryGetValue(endpointUid.Value, out var part))
                    {
                        throw new GraphStructureException();
                    }

                    if (endpoint.Kind == "NameCon")
                    {
                        part.PinNames.Add(endpoint.Name);
                    }
                }
            }

            network.Wires.Add(wire);
        }
    }

    private static void ParseBranches(
        XElement sequenceNode,
        string blockName,
        LineUpGraphSequence sequence,
        CancellationToken cancellationToken)
    {
        var branchesContainer = ElementsLocal(sequenceNode, "Branches").FirstOrDefault();
        if (branchesContainer == null)
        {
            return;
        }

        var sourceKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var branchNode in ElementsLocal(branchesContainer, "Branch"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var number = RequiredIntegerAttribute(branchNode, "Number");
            var type = AttributeValue(branchNode, "Type") ?? string.Empty;
            var branch = new LineUpGraphBranch
            {
                Number = number,
                Type = type,
                Cardinality = OptionalIntegerAttribute(branchNode, "Cardinality"),
                SourceName = AttributeValue(branchNode, "Name") ?? type,
                SourceKey = LineUpGraphSourceKey.ForBranch(blockName, sequence.SourceName, number)
            };
            if (!sourceKeys.Add(branch.SourceKey))
            {
                throw new GraphStructureException();
            }

            sequence.Branches.Add(branch);
        }
    }

    private static void ParseConnections(
        XElement sequenceNode,
        LineUpGraphSequence sequence,
        CancellationToken cancellationToken)
    {
        var connectionsContainer = ElementsLocal(sequenceNode, "Connections").FirstOrDefault();
        if (connectionsContainer == null)
        {
            return;
        }

        var ordinal = 0;
        foreach (var connectionNode in ElementsLocal(connectionsContainer, "Connection"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ordinal++;
            var fromContainer = ElementsLocal(connectionNode, "NodeFrom").FirstOrDefault()
                ?? throw new GraphStructureException();
            var toContainer = ElementsLocal(connectionNode, "NodeTo").FirstOrDefault()
                ?? throw new GraphStructureException();
            // 2026-09-28：FB151（PLC2）序列最终转换直连序列结束标记
            // <NodeTo><EndConnection/></NodeTo>（TIA Openness 合法输出，无 Step/Transition/Branch
            // 编号）。此前 ParseEndpoint 只认三种 Ref → 整批 InvalidGraphStructure（批次
            // 4625046158325190656 Failed，全文件 291 条连接仅此 1 条）。序列终止标记
            // 不构成可导航边，整条连接跳过；对侧真实节点记入 EndAnchors，
            // 归一化据此豁免 TransitionWithoutOutgoingEdge（批次 4625048850988666880
            // 曾因最终转换无出边被 GraphTopologyBlocking 阻断提交）。
            if (IsEndConnection(fromContainer) || IsEndConnection(toContainer))
            {
                var anchor = TryParseEndAnchor(fromContainer) ?? TryParseEndAnchor(toContainer);
                if (anchor != null)
                {
                    sequence.EndAnchors.Add(anchor);
                }

                continue;
            }
            var from = ParseEndpoint(fromContainer);
            var to = ParseEndpoint(toContainer);
            var fromKey = EndpointKey(from);
            var toKey = EndpointKey(to);
            var sourceName = $"{fromKey}>{toKey}";
            sequence.RawConnections.Add(new LineUpGraphRawConnection
            {
                From = from,
                To = to,
                LinkType = ElementsLocal(connectionNode, "LinkType").FirstOrDefault()?.Value ?? "Direct",
                Ordinal = ordinal,
                SourceName = sourceName,
                SourceKey = LineUpGraphSourceKey.ForEdge(
                    sequence.SourceKey,
                    fromKey,
                    toKey,
                    ordinal)
            });
        }
    }

    private static bool IsEndConnection(XElement container) =>
        ElementsLocal(container, "EndConnection").Any();

    /// <summary>从 EndConnection 连接的对侧节点容器提取锚点（类型+编号），无真实引用时返回 null。</summary>
    private static LineUpGraphSequenceEndAnchor? TryParseEndAnchor(XElement container)
    {
        var reference = container.Elements()
            .FirstOrDefault(element => element.Name.LocalName is "StepRef" or "TransitionRef" or "BranchRef");
        if (reference == null)
        {
            return null;
        }

        var kind = reference.Name.LocalName switch
        {
            "StepRef" => LineUpGraphEndpointKind.Step,
            "TransitionRef" => LineUpGraphEndpointKind.Transition,
            "BranchRef" => LineUpGraphEndpointKind.Branch,
            _ => (LineUpGraphEndpointKind?)null
        };
        if (kind is null)
        {
            return null;
        }

        var number = AttributeValue(reference, "Number");
        return number != null && int.TryParse(number, out var parsed)
            ? new LineUpGraphSequenceEndAnchor(kind.Value, parsed)
            : null;
    }

    private static LineUpGraphEndpoint ParseEndpoint(XElement container)
    {
        var references = container.Elements()
            .Where(element => element.Name.LocalName is "StepRef" or "TransitionRef" or "BranchRef")
            .ToList();
        if (references.Count != 1)
        {
            throw new GraphStructureException();
        }

        var reference = references[0];
        var endpoint = new LineUpGraphEndpoint
        {
            Kind = reference.Name.LocalName switch
            {
                "StepRef" => LineUpGraphEndpointKind.Step,
                "TransitionRef" => LineUpGraphEndpointKind.Transition,
                "BranchRef" => LineUpGraphEndpointKind.Branch,
                _ => throw new GraphStructureException()
            },
            Number = RequiredIntegerAttribute(reference, "Number")
        };

        var outPort = AttributeLocal(reference, "Out");
        var inPort = AttributeLocal(reference, "In");
        if (endpoint.Kind == LineUpGraphEndpointKind.Branch)
        {
            if ((outPort == null) == (inPort == null))
            {
                throw new GraphStructureException();
            }

            if (outPort != null)
            {
                endpoint.PortKind = LineUpGraphBranchPortKind.Out;
                endpoint.Port = RequiredInteger(outPort.Value);
            }
            else if (inPort != null)
            {
                endpoint.PortKind = LineUpGraphBranchPortKind.In;
                endpoint.Port = RequiredInteger(inPort.Value);
            }
        }
        else if (outPort != null || inPort != null)
        {
            throw new GraphStructureException();
        }

        return endpoint;
    }

    private static string EndpointKey(LineUpGraphEndpoint endpoint)
    {
        var key = endpoint.Kind switch
        {
            LineUpGraphEndpointKind.Step => $"step:{endpoint.Number}",
            LineUpGraphEndpointKind.Transition => $"transition:{endpoint.Number}",
            LineUpGraphEndpointKind.Branch => $"branch:{endpoint.Number}",
            _ => throw new GraphStructureException()
        };
        return endpoint.PortKind switch
        {
            LineUpGraphBranchPortKind.In => $"{key}/in:{endpoint.Port}",
            LineUpGraphBranchPortKind.Out => $"{key}/out:{endpoint.Port}",
            _ => key
        };
    }

    private static IEnumerable<XElement> ElementsLocal(XContainer parent, string localName) =>
        parent.Elements().Where(element => element.Name.LocalName == localName);

    private static XAttribute? AttributeLocal(XElement element, string localName) =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == localName);

    private static string? AttributeValue(XElement element, string localName) =>
        AttributeLocal(element, localName)?.Value;

    private static string RequiredElementText(XElement parent, string localName)
    {
        var value = ElementsLocal(parent, localName).FirstOrDefault()?.Value?.Trim();
        return string.IsNullOrEmpty(value) ? throw new GraphStructureException() : value;
    }

    private static int RequiredIntegerElement(XElement parent, string localName) =>
        RequiredInteger(RequiredElementText(parent, localName));

    private static int RequiredIntegerAttribute(XElement element, string localName)
    {
        var value = AttributeValue(element, localName);
        return value == null ? throw new GraphStructureException() : RequiredInteger(value);
    }

    private static int RequiredPositiveIntegerAttribute(XElement element, string localName)
    {
        var value = AttributeValue(element, localName);
        return value == null ? throw new GraphStructureException() : RequiredPositiveInteger(value);
    }

    private static int OptionalIntegerAttribute(XElement element, string localName)
    {
        var value = AttributeValue(element, localName);
        return value == null ? 0 : RequiredInteger(value);
    }

    private static int RequiredInteger(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : throw new GraphStructureException();

    private static int RequiredPositiveInteger(string value)
    {
        var result = RequiredInteger(value);
        return result > 0 ? result : throw new GraphStructureException();
    }

    private static bool BooleanAttribute(XElement element, string localName)
    {
        var value = AttributeValue(element, localName);
        return value switch
        {
            null => false,
            "true" => true,
            "false" => false,
            "1" => true,
            "0" => false,
            _ => throw new GraphStructureException()
        };
    }

    private static bool? NullableBooleanAttribute(XElement element, string localName)
    {
        var value = AttributeValue(element, localName);
        return value switch
        {
            null => null,
            "true" => true,
            "false" => false,
            "1" => true,
            "0" => false,
            _ => throw new GraphStructureException()
        };
    }

    private static string LocalizedText(XElement? parent)
    {
        if (parent == null)
        {
            return string.Empty;
        }

        var translations = parent.DescendantsAndSelf()
            .Where(element => element.Name.LocalName == "MultiLanguageText")
            .Select(element => new
            {
                Language = AttributeValue(element, "Lang"),
                Text = element.Value.Trim()
            })
            .ToList();
        if (translations.Count > 0)
        {
            return translations.FirstOrDefault(item =>
                       string.Equals(item.Language, "zh-CN", StringComparison.OrdinalIgnoreCase))?.Text
                   ?? translations[0].Text;
        }

        return parent.Value.Trim();
    }

    private static string SafeDisplayName(string fileName)
    {
        var normalized = (fileName ?? string.Empty).Replace('\\', '/');
        var separatorIndex = normalized.LastIndexOf('/');
        var leaf = separatorIndex >= 0 ? normalized[(separatorIndex + 1)..] : normalized;
        try
        {
            leaf = leaf.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            leaf = string.Empty;
        }

        var safe = new string(leaf
            .Where(character => !char.IsControl(character))
            .Take(128)
            .ToArray());
        return string.IsNullOrEmpty(safe) ? "upload" : safe;
    }

    private static LineUpImportException Error(string code, string logicalFileName, string summary) =>
        new(code, SafeDisplayName(logicalFileName), summary);

    private sealed class GraphStructureException : Exception;
}
