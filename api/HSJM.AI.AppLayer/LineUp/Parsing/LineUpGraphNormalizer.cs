using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HSJM.AI.AppLayer.LineUp.Models;

namespace HSJM.AI.AppLayer.LineUp.Parsing;

public sealed class LineUpGraphNormalizer
{
    public LineUpGraphPreview Normalize(LineUpGraphDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var preview = new LineUpGraphPreview
        {
            SourceFileName = document.SourceFileName,
            SourceRelativePath = document.SourceRelativePath,
            SourceSha256 = document.SourceSha256,
            BlockName = document.BlockName,
            BlockNumber = document.BlockNumber,
            ProgrammingLanguage = document.ProgrammingLanguage,
            InstanceDbName = document.InstanceDbName
        };

        var crossSequenceIndex = LineUpGraphCrossSequenceIndex.Build(document);
        var crossSequenceWiring = crossSequenceIndex.Analyze(document);
        foreach (var sequence in document.Sequences.OrderBy(item => item.SourceKey, StringComparer.Ordinal))
        {
            var result = NormalizeSequence(sequence, crossSequenceIndex, crossSequenceWiring);
            preview.Sequences.Add(result.Sequence);
            preview.Issues.AddRange(result.Issues);
        }

        ValidateLogicNetworkIdentity(preview);

        preview.Issues = preview.Issues
            .OrderBy(item => item.Severity, StringComparer.Ordinal)
            .ThenBy(item => item.IssueType, StringComparer.Ordinal)
            .ThenBy(item => item.SourceKey, StringComparer.Ordinal)
            .ThenBy(item => item.Message, StringComparer.Ordinal)
            .ToList();
        return preview;
    }

    private static SequenceNormalizationResult NormalizeSequence(
        LineUpGraphSequence sequence,
        LineUpGraphCrossSequenceIndex crossSequenceIndex,
        LineUpGraphCrossSequenceIndex.Wiring crossSequenceWiring)
    {
        var issues = new List<LineUpGraphPreviewIssue>();
        var result = CopySequence(sequence, issues);
        var stepKeys = sequence.Steps
            .GroupBy(item => item.Number)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.SourceKey, StringComparer.Ordinal).First().SourceKey);
        var transitionKeys = sequence.Transitions
            .GroupBy(item => item.Number)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.SourceKey, StringComparer.Ordinal).First().SourceKey);
        var branches = sequence.Branches
            .GroupBy(item => item.Number)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.SourceKey, StringComparer.Ordinal).First());

        // 2026-09-28（FB250/PLC2）：S7-GRAPH 子程序跨序列调用/返回连接——端点声明在兄弟
        // Sequence（如主序列 T184→子序列 Step204 调用、子序列 T222→主序列 Step2 返回）。
        // 此类连接分流出来，不参与本序列边界校验与边归一化（否则一律 DanglingEdge 并连带给
        // 调用侧转换报 TransitionWithoutOutgoingEdge）；豁免信息由文档级 Wiring 提供
        // （子程序入口步在主序列的连接里，逐序列独立分析看不见）。真正两边都不存在的
        // 引用仍走 DanglingEdge。
        var localConnections = new List<LineUpGraphRawConnection>();
        foreach (var connection in sequence.RawConnections)
        {
            if (crossSequenceIndex.IsExternal(sequence, connection.From) ||
                crossSequenceIndex.IsExternal(sequence, connection.To))
            {
                issues.Add(Issue(
                    "Warning",
                    "CrossSequenceEdge",
                    connection.SourceKey,
                    "A raw connection references a node declared in another Sequence (subroutine call/return); it is excluded from this Sequence's normalized edges."));
                continue;
            }

            localConnections.Add(connection);
        }

        var canonicalConnections = CanonicalizeRawConnections(sequence.SourceKey, localConnections, issues);
        var handled = new HashSet<string>(StringComparer.Ordinal);
        var boundaryInvalidBranchNumbers = new HashSet<int>();
        var validConnections = new List<RawConnectionView>();
        foreach (var connection in canonicalConnections)
        {
            if (ValidateConnectionBoundary(
                    sequence.SourceKey,
                    connection,
                    stepKeys,
                    transitionKeys,
                    branches,
                    issues))
            {
                validConnections.Add(connection);
            }
            else
            {
                MarkIncidentBranches(connection.Connection, branches, boundaryInvalidBranchNumbers);
                handled.Add(connection.StructuralFingerprint);
            }
        }

        var candidates = new List<EdgeCandidate>();
        NormalizeRealNodeConnections(
            sequence.SourceKey,
            validConnections,
            stepKeys,
            transitionKeys,
            candidates,
            issues,
            handled);

        var compositeBranchNumbers = HandleCompositeBranchComponents(
            sequence,
            validConnections,
            branches,
            stepKeys,
            transitionKeys,
            candidates,
            issues,
            handled);
        NormalizeBranchConnections(
            sequence,
            validConnections,
            branches,
            compositeBranchNumbers,
            boundaryInvalidBranchNumbers,
            stepKeys,
            transitionKeys,
            candidates,
            issues,
            handled);

        foreach (var unhandled in canonicalConnections
                     .Where(item => !handled.Contains(item.StructuralFingerprint))
                     .OrderBy(item => item.StructuralFingerprint, StringComparer.Ordinal))
        {
            issues.Add(Issue(
                "Blocking",
                "DanglingEdge",
                StructuralIssueKey(sequence.SourceKey, unhandled.StructuralFingerprint),
                "A raw connection could not be represented by a normalized edge."));
            handled.Add(unhandled.StructuralFingerprint);
        }

        result.Edges = MaterializeEdges(sequence.SourceKey, candidates);
        DeriveNodeStateAndValidateReachability(
            sequence,
            result,
            issues,
            crossSequenceWiring.OutgoingExternalTransitionNumbers(sequence),
            crossSequenceWiring.ExternalEntryStepNumbers(sequence));
        return new SequenceNormalizationResult(result, issues);
    }

    private static LineUpGraphPreviewSequence CopySequence(
        LineUpGraphSequence sequence,
        ICollection<LineUpGraphPreviewIssue> issues)
    {
        var result = new LineUpGraphPreviewSequence
        {
            SourceKey = sequence.SourceKey,
            SourceName = sequence.SourceName,
            DisplayName = sequence.DisplayName
        };
        var stepKeys = sequence.Steps
            .GroupBy(item => item.Number)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.SourceKey, StringComparer.Ordinal).First().SourceKey);

        result.Nodes.AddRange(sequence.Steps.Select(step => new LineUpGraphPreviewNode
        {
            NodeKind = "Step",
            SourceKey = step.SourceKey,
            SourceName = step.SourceName,
            DisplayName = step.DisplayName,
            Number = step.Number,
            IsInitial = step.Init,
            MaximumStepTime = step.MaximumStepTime,
            WarningTime = step.WarningTime,
            Comment = step.Comment,
            ActionGroupTitle = step.ActionGroupTitle,
            LogicNetworks = step.Interlocks.Concat(step.Supervisions).Select(CopyLogicNetwork).ToList()
        }));
        result.Nodes.AddRange(sequence.Transitions.Select(transition => new LineUpGraphPreviewNode
        {
            NodeKind = "Transition",
            SourceKey = transition.SourceKey,
            SourceName = transition.SourceName,
            DisplayName = transition.DisplayName,
            Number = transition.Number,
            ProgrammingLanguage = transition.ProgrammingLanguage,
            IsMissing = transition.IsMissing,
            RawXml = transition.RawXml,
            LogicNetworks = transition.ConditionNetworks.Select(CopyLogicNetwork).ToList()
        }));
        result.Nodes = result.Nodes.OrderBy(item => item.SourceKey, StringComparer.Ordinal).ToList();

        foreach (var action in sequence.Actions.OrderBy(item => item.SourceKey, StringComparer.Ordinal))
        {
            var hasStep = stepKeys.TryGetValue(action.StepNumber, out var stepSourceKey);
            result.Actions.Add(new LineUpGraphPreviewAction
            {
                SourceKey = action.SourceKey,
                StepSourceKey = hasStep ? stepSourceKey! : string.Empty,
                SourceName = action.SourceName,
                DisplayName = action.DisplayName,
                Ordinal = action.Ordinal,
                StepNumber = action.StepNumber,
                Qualifier = action.Qualifier,
                Event = action.Event,
                Interlock = action.Interlock,
                Tokens = action.Tokens.ToList(),
                ActionText = action.ActionText
            });
            if (!hasStep)
            {
                issues.Add(Issue(
                    "Blocking",
                    "ActionStepMissing",
                    action.SourceKey,
                    "An action does not reference an existing Step."));
            }
        }

        return result;
    }

    private static void ValidateLogicNetworkIdentity(LineUpGraphPreview preview)
    {
        var sourceKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in preview.Sequences.SelectMany(sequence => sequence.Nodes)
                     .OrderBy(node => node.SourceKey, StringComparer.Ordinal))
        {
            foreach (var network in node.LogicNetworks.OrderBy(network => network.SourceKey, StringComparer.Ordinal))
            {
                ValidateLogicSourceKey(network.SourceKey, node.SourceKey, sourceKeys, preview.Issues);
                if (!string.Equals(network.OwnerSourceKey, node.SourceKey, StringComparison.Ordinal))
                {
                    preview.Issues.Add(Issue(
                        "Blocking",
                        "LogicNetworkOwnerMismatch",
                        network.SourceKey,
                        "A logic network owner does not match its normalized node."));
                }

                var roleIsValid = node.NodeKind switch
                {
                    "Step" => network.Role is "StepInterlock" or "StepSupervision",
                    "Transition" => network.Role == "TransitionCondition",
                    _ => false
                };
                if (!roleIsValid)
                {
                    preview.Issues.Add(Issue(
                        "Blocking",
                        "LogicNetworkRoleInvalid",
                        network.SourceKey,
                        "A logic network role is not valid for its normalized node."));
                }

                foreach (var part in network.Parts.OrderBy(part => part.SourceKey, StringComparer.Ordinal))
                {
                    ValidateLogicSourceKey(part.SourceKey, network.SourceKey, sourceKeys, preview.Issues);
                }

                foreach (var wire in network.Wires.OrderBy(wire => wire.SourceKey, StringComparer.Ordinal))
                {
                    ValidateLogicSourceKey(wire.SourceKey, network.SourceKey, sourceKeys, preview.Issues);
                }
            }
        }
    }

    private static void ValidateLogicSourceKey(
        string sourceKey,
        string ownerSourceKey,
        ISet<string> sourceKeys,
        ICollection<LineUpGraphPreviewIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(sourceKey))
        {
            issues.Add(Issue(
                "Blocking",
                "LogicSourceKeyInvalid",
                ownerSourceKey,
                "A logic network element has an empty immutable source key."));
            return;
        }

        if (!sourceKeys.Add(sourceKey))
        {
            issues.Add(Issue(
                "Blocking",
                "LogicSourceKeyDuplicate",
                sourceKey,
                "A logic network element source key is duplicated globally."));
        }
    }

    private static LineUpGraphLogicNetwork CopyLogicNetwork(LineUpGraphLogicNetwork source) => new()
    {
        SourceKey = source.SourceKey,
        OwnerSourceKey = source.OwnerSourceKey,
        Role = source.Role,
        Ordinal = source.Ordinal,
        ProgrammingLanguage = source.ProgrammingLanguage,
        Title = source.Title,
        Parts = source.Parts.Select(CopyLogicPart).ToList(),
        Wires = source.Wires.Select(CopyLogicWire).ToList()
    };

    private static LineUpGraphLogicPart CopyLogicPart(LineUpGraphLogicPart source) => new()
    {
        SourceKey = source.SourceKey,
        UId = source.UId,
        PartName = source.PartName,
        Scope = source.Scope,
        SymbolPath = source.SymbolPath.ToList(),
        ConstantValue = source.ConstantValue,
        Negated = source.Negated,
        Attributes = new Dictionary<string, string>(source.Attributes, StringComparer.Ordinal),
        TemplateValues = new Dictionary<string, string>(source.TemplateValues, StringComparer.Ordinal),
        PinNames = source.PinNames.ToList()
    };

    private static LineUpGraphLogicWire CopyLogicWire(LineUpGraphLogicWire source) => new()
    {
        SourceKey = source.SourceKey,
        UId = source.UId,
        Endpoints = source.Endpoints.Select(CopyLogicEndpoint).ToList()
    };

    private static LineUpGraphLogicEndpoint CopyLogicEndpoint(LineUpGraphLogicEndpoint source) => new()
    {
        Kind = source.Kind,
        UId = source.UId,
        Name = source.Name
    };

    private static List<RawConnectionView> CanonicalizeRawConnections(
        string sequenceKey,
        IEnumerable<LineUpGraphRawConnection> connections,
        ICollection<LineUpGraphPreviewIssue> issues)
    {
        var groups = connections
            .Select(connection => new RawConnectionView(
                connection,
                RawConnectionFingerprint(connection),
                CanonicalLinkToken(connection.LinkType)))
            .GroupBy(item => item.StructuralFingerprint, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToList();
        var canonical = new List<RawConnectionView>();
        foreach (var group in groups)
        {
            canonical.Add(group.First());
            if (group.Count() > 1)
            {
                issues.Add(Issue(
                    "Warning",
                    "DuplicateEdge",
                    StructuralIssueKey(sequenceKey, group.Key),
                    "One or more structurally duplicate raw connections were ignored."));
            }
        }

        return canonical;
    }

    private static bool ValidateConnectionBoundary(
        string sequenceKey,
        RawConnectionView connection,
        IReadOnlyDictionary<int, string> stepKeys,
        IReadOnlyDictionary<int, string> transitionKeys,
        IReadOnlyDictionary<int, LineUpGraphBranch> branches,
        ICollection<LineUpGraphPreviewIssue> issues)
    {
        var sourceKey = StructuralIssueKey(sequenceKey, connection.StructuralFingerprint);
        if (!EndpointExists(connection.Connection.From, stepKeys, transitionKeys, branches) ||
            !EndpointExists(connection.Connection.To, stepKeys, transitionKeys, branches))
        {
            issues.Add(Issue(
                "Blocking",
                "DanglingEdge",
                sourceKey,
                "A raw connection references an endpoint that does not exist."));
            return false;
        }

        if (!HasValidEndpointPort(connection.Connection.From, isSource: true) ||
            !HasValidEndpointPort(connection.Connection.To, isSource: false))
        {
            issues.Add(Issue(
                "Blocking",
                "InvalidEndpointPort",
                sourceKey,
                "A raw connection uses an invalid endpoint port."));
            return false;
        }

        if (connection.CanonicalLinkType == null)
        {
            issues.Add(Issue(
                "Blocking",
                "UnknownLinkType",
                sourceKey,
                "A raw connection uses an unsupported LinkType."));
            return false;
        }

        if ((connection.Connection.From.Kind == LineUpGraphEndpointKind.Branch ||
             connection.Connection.To.Kind == LineUpGraphEndpointKind.Branch) &&
            connection.CanonicalLinkType != "Direct")
        {
            issues.Add(Issue(
                "Blocking",
                "JumpInBranchPath",
                sourceKey,
                "Branch path segments must use Direct LinkType."));
            return false;
        }

        return true;
    }

    private static void NormalizeRealNodeConnections(
        string sequenceKey,
        IEnumerable<RawConnectionView> connections,
        IReadOnlyDictionary<int, string> stepKeys,
        IReadOnlyDictionary<int, string> transitionKeys,
        ICollection<EdgeCandidate> candidates,
        ICollection<LineUpGraphPreviewIssue> issues,
        ISet<string> handled)
    {
        foreach (var connection in connections.Where(item =>
                     item.Connection.From.Kind != LineUpGraphEndpointKind.Branch &&
                     item.Connection.To.Kind != LineUpGraphEndpointKind.Branch))
        {
            var from = connection.Connection.From;
            var to = connection.Connection.To;
            if (connection.CanonicalLinkType == "Direct" && IsAlternatingRealNodePair(from.Kind, to.Kind))
            {
                AddCandidate(
                    candidates,
                    stepKeys,
                    transitionKeys,
                    from,
                    to,
                    "Sequence",
                    null,
                    connection.StructuralFingerprint);
            }
            else if (connection.CanonicalLinkType == "Jump" &&
                     from.Kind == LineUpGraphEndpointKind.Transition &&
                     to.Kind == LineUpGraphEndpointKind.Step)
            {
                AddCandidate(
                    candidates,
                    stepKeys,
                    transitionKeys,
                    from,
                    to,
                    "Jump",
                    null,
                    connection.StructuralFingerprint);
            }
            else
            {
                issues.Add(Issue(
                    "Blocking",
                    connection.CanonicalLinkType == "Jump" ? "InvalidJumpShape" : "InvalidConnectionShape",
                    StructuralIssueKey(sequenceKey, connection.StructuralFingerprint),
                    "A raw connection has an unsupported real-node shape."));
            }

            handled.Add(connection.StructuralFingerprint);
        }
    }

    /// <summary>
    /// 2026-09-15 修复（FB360_MainGraph「ocv测试抓取」阻断）：Branch↔Branch 直连链（如
    /// AltEnd(选择汇合)→SimBegin(并行分叉)，XML 实例 Branch:15[Out=0]→Branch:9[In=0]）原先整片
    /// 判 Blocking 并丢弃关联连接，导致链边界实节点（Transition:24/38 无出边、Step:23/31 等
    /// 19+2 节点不可达）连锁阻断。现按「边界透明展开」：分量边界入（分量外实节点→分量内 Branch）
    /// × 边界出（分量内 Branch→分量外实节点）交叉直连。选择汇合(OR 入)+并行分叉(AND 出)时交叉积
    /// 精确等价；其余组合对监控语义（可达性/步位状态）为安全过近似，降级 Warning 说明。
    /// 单侧边界为空的悬空链仍 Blocking（诚实报错）。
    /// </summary>
    private static HashSet<int> HandleCompositeBranchComponents(
        LineUpGraphSequence sequence,
        IReadOnlyCollection<RawConnectionView> connections,
        IReadOnlyDictionary<int, LineUpGraphBranch> branches,
        IReadOnlyDictionary<int, string> stepKeys,
        IReadOnlyDictionary<int, string> transitionKeys,
        ICollection<EdgeCandidate> candidates,
        ICollection<LineUpGraphPreviewIssue> issues,
        ISet<string> handled)
    {
        var branchToBranch = connections.Where(item =>
                item.Connection.From.Kind == LineUpGraphEndpointKind.Branch &&
                item.Connection.To.Kind == LineUpGraphEndpointKind.Branch)
            .ToList();
        var adjacency = new Dictionary<int, HashSet<int>>();
        foreach (var connection in branchToBranch)
        {
            var from = connection.Connection.From.Number;
            var to = connection.Connection.To.Number;
            AddNeighbor(adjacency, from, to);
            AddNeighbor(adjacency, to, from);
        }

        var composite = new HashSet<int>();
        foreach (var start in adjacency.Keys.OrderBy(item => item))
        {
            if (composite.Contains(start))
            {
                continue;
            }

            var component = new HashSet<int>();
            var queue = new Queue<int>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!component.Add(current))
                {
                    continue;
                }

                if (adjacency.TryGetValue(current, out var neighbors))
                {
                    foreach (var neighbor in neighbors.OrderBy(item => item))
                    {
                        queue.Enqueue(neighbor);
                    }
                }
            }

            composite.UnionWith(component);
            var componentKeys = component
                .Where(branches.ContainsKey)
                .Select(number => branches[number].SourceKey)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToList();

            var boundaryInputs = connections.Where(item =>
                    item.Connection.To.Kind == LineUpGraphEndpointKind.Branch &&
                    component.Contains(item.Connection.To.Number) &&
                    item.Connection.From.Kind != LineUpGraphEndpointKind.Branch)
                .ToList();
            var boundaryOutputs = connections.Where(item =>
                    item.Connection.From.Kind == LineUpGraphEndpointKind.Branch &&
                    component.Contains(item.Connection.From.Number) &&
                    item.Connection.To.Kind != LineUpGraphEndpointKind.Branch)
                .ToList();

            if (boundaryInputs.Count == 0 || boundaryOutputs.Count == 0)
            {
                issues.Add(Issue(
                    "Blocking",
                    "DanglingCompositeBranch",
                    componentKeys.FirstOrDefault() ?? sequence.SourceKey,
                    "A Branch-to-Branch path has no real-node boundary on one side and cannot be expanded."));
            }
            else
            {
                issues.Add(Issue(
                    "Warning",
                    "CompositeBranchExpanded",
                    componentKeys.FirstOrDefault() ?? sequence.SourceKey,
                    "A Branch-to-Branch path was expanded to direct cross edges between its boundary real nodes."));
                foreach (var input in boundaryInputs.OrderBy(item => item.StructuralFingerprint, StringComparer.Ordinal))
                {
                    foreach (var output in boundaryOutputs
                                 .OrderBy(item => item.Connection.From.Number)
                                 .ThenBy(item => item.Connection.From.Port))
                    {
                        AddCandidate(
                            candidates,
                            stepKeys,
                            transitionKeys,
                            input.Connection.From,
                            output.Connection.To,
                            "Sequence",
                            output.Connection.From.Port,
                            string.Join(
                                "|",
                                input.StructuralFingerprint,
                                string.Join(",", componentKeys),
                                output.StructuralFingerprint));
                    }
                }
            }

            foreach (var incident in connections.Where(item =>
                         IsIncidentToAnyBranch(item.Connection, component)))
            {
                handled.Add(incident.StructuralFingerprint);
            }
        }

        return composite;
    }

    private static void NormalizeBranchConnections(
        LineUpGraphSequence sequence,
        IReadOnlyCollection<RawConnectionView> connections,
        IReadOnlyDictionary<int, LineUpGraphBranch> branches,
        ISet<int> compositeBranchNumbers,
        ISet<int> boundaryInvalidBranchNumbers,
        IReadOnlyDictionary<int, string> stepKeys,
        IReadOnlyDictionary<int, string> transitionKeys,
        ICollection<EdgeCandidate> candidates,
        ICollection<LineUpGraphPreviewIssue> issues,
        ISet<string> handled)
    {
        foreach (var branch in branches.Values.OrderBy(item => item.SourceKey, StringComparer.Ordinal))
        {
            var incident = connections.Where(item =>
                    item.Connection.From.Kind == LineUpGraphEndpointKind.Branch &&
                    item.Connection.From.Number == branch.Number ||
                    item.Connection.To.Kind == LineUpGraphEndpointKind.Branch &&
                    item.Connection.To.Number == branch.Number)
                .ToList();
            if (boundaryInvalidBranchNumbers.Contains(branch.Number))
            {
                issues.Add(Issue(
                    "Blocking",
                    "InvalidBranchShape",
                    branch.SourceKey,
                    "A Branch has a canonical incident that failed boundary validation, so its strict incident-shape contract is contaminated."));
                MarkHandled(incident, handled);
                continue;
            }

            if (compositeBranchNumbers.Contains(branch.Number))
            {
                continue;
            }

            var branchType = CanonicalBranchType(branch.Type);
            if (branchType == null)
            {
                issues.Add(Issue(
                    "Blocking",
                    "UnknownBranchType",
                    branch.SourceKey,
                    "A Branch uses an unsupported type."));
                MarkHandled(incident, handled);
                continue;
            }

            var incoming = incident.Where(item =>
                    item.Connection.To.Kind == LineUpGraphEndpointKind.Branch &&
                    item.Connection.To.Number == branch.Number)
                .ToList();
            var outgoing = incident.Where(item =>
                    item.Connection.From.Kind == LineUpGraphEndpointKind.Branch &&
                    item.Connection.From.Number == branch.Number)
                .ToList();
            if (!HasStrictIncidentShape(branchType, branch.Cardinality, incoming, outgoing))
            {
                issues.Add(Issue(
                    "Blocking",
                    "InvalidBranchShape",
                    branch.SourceKey,
                    "A Branch does not match its strict incident-shape contract."));
                MarkHandled(incident, handled);
                continue;
            }

            foreach (var input in incoming.OrderBy(item => item.Connection.To.Port))
            {
                TraverseBranchPath(
                    sequence,
                    branch,
                    branchType,
                    input,
                    outgoing,
                    stepKeys,
                    transitionKeys,
                    candidates,
                    issues,
                    new HashSet<string>(StringComparer.Ordinal),
                    depth: 0,
                    maxDepth: sequence.RawConnections.Count + 1);
            }

            MarkHandled(incident, handled);
        }
    }

    private static void TraverseBranchPath(
        LineUpGraphSequence sequence,
        LineUpGraphBranch branch,
        string branchType,
        RawConnectionView input,
        IReadOnlyCollection<RawConnectionView> outgoing,
        IReadOnlyDictionary<int, string> stepKeys,
        IReadOnlyDictionary<int, string> transitionKeys,
        ICollection<EdgeCandidate> candidates,
        ICollection<LineUpGraphPreviewIssue> issues,
        HashSet<string> pathVisited,
        int depth,
        int maxDepth)
    {
        if (depth >= maxDepth || !pathVisited.Add(branch.SourceKey))
        {
            issues.Add(Issue(
                "Blocking",
                "DanglingEdge",
                branch.SourceKey,
                "Branch traversal exceeded its bounded path guard."));
            return;
        }

        var edgeKind = branchType switch
        {
            "AltBegin" => "SelectionSplit",
            "AltEnd" => "SelectionMerge",
            "SimBegin" => "ParallelSplit",
            "SimEnd" => "ParallelMerge",
            _ => throw new InvalidOperationException("Unsupported canonical Branch type.")
        };
        foreach (var output in outgoing.OrderBy(item => item.Connection.From.Port))
        {
            var branchOrder = branchType is "AltBegin" or "SimBegin"
                ? output.Connection.From.Port
                : input.Connection.To.Port;
            var pathFingerprint = string.Join(
                "|",
                input.StructuralFingerprint,
                branch.SourceKey,
                branchType,
                output.StructuralFingerprint);
            AddCandidate(
                candidates,
                stepKeys,
                transitionKeys,
                input.Connection.From,
                output.Connection.To,
                edgeKind,
                branchOrder,
                pathFingerprint);
        }
    }

    private static bool HasStrictIncidentShape(
        string branchType,
        int cardinality,
        IReadOnlyCollection<RawConnectionView> incoming,
        IReadOnlyCollection<RawConnectionView> outgoing)
    {
        if (cardinality < 2 || incoming.Any(item => item.CanonicalLinkType != "Direct") ||
            outgoing.Any(item => item.CanonicalLinkType != "Direct"))
        {
            return false;
        }

        return branchType switch
        {
            "AltBegin" => HasSingleInput(incoming, LineUpGraphEndpointKind.Step) &&
                          HasOrderedOutputs(outgoing, LineUpGraphEndpointKind.Transition, cardinality),
            "AltEnd" => HasOrderedInputs(incoming, LineUpGraphEndpointKind.Transition, cardinality) &&
                        HasSingleOutput(outgoing, LineUpGraphEndpointKind.Step),
            "SimBegin" => HasSingleInput(incoming, LineUpGraphEndpointKind.Transition) &&
                          HasOrderedOutputs(outgoing, LineUpGraphEndpointKind.Step, cardinality),
            "SimEnd" => HasOrderedInputs(incoming, LineUpGraphEndpointKind.Step, cardinality) &&
                        HasSingleOutput(outgoing, LineUpGraphEndpointKind.Transition),
            _ => false
        };
    }

    private static bool HasSingleInput(
        IReadOnlyCollection<RawConnectionView> incoming,
        LineUpGraphEndpointKind expectedKind) =>
        incoming.Count == 1 &&
        incoming.Single().Connection.From.Kind == expectedKind &&
        incoming.Single().Connection.From.PortKind == LineUpGraphBranchPortKind.None &&
        incoming.Single().Connection.From.Port == null &&
        incoming.Single().Connection.To.PortKind == LineUpGraphBranchPortKind.In &&
        incoming.Single().Connection.To.Port == 0;

    private static bool HasSingleOutput(
        IReadOnlyCollection<RawConnectionView> outgoing,
        LineUpGraphEndpointKind expectedKind) =>
        outgoing.Count == 1 &&
        outgoing.Single().Connection.To.Kind == expectedKind &&
        outgoing.Single().Connection.To.PortKind == LineUpGraphBranchPortKind.None &&
        outgoing.Single().Connection.To.Port == null &&
        outgoing.Single().Connection.From.PortKind == LineUpGraphBranchPortKind.Out &&
        outgoing.Single().Connection.From.Port == 0;

    private static bool HasOrderedInputs(
        IReadOnlyCollection<RawConnectionView> incoming,
        LineUpGraphEndpointKind expectedKind,
        int cardinality) =>
        incoming.Count == cardinality &&
        incoming.All(item => item.Connection.From.Kind == expectedKind &&
                             item.Connection.From.PortKind == LineUpGraphBranchPortKind.None &&
                             item.Connection.From.Port == null &&
                             item.Connection.To.PortKind == LineUpGraphBranchPortKind.In) &&
        incoming.Select(item => item.Connection.To.Port).OrderBy(item => item)
            .SequenceEqual(Enumerable.Range(0, cardinality).Select(item => (int?)item));

    private static bool HasOrderedOutputs(
        IReadOnlyCollection<RawConnectionView> outgoing,
        LineUpGraphEndpointKind expectedKind,
        int cardinality) =>
        outgoing.Count == cardinality &&
        outgoing.All(item => item.Connection.To.Kind == expectedKind &&
                             item.Connection.To.PortKind == LineUpGraphBranchPortKind.None &&
                             item.Connection.To.Port == null &&
                             item.Connection.From.PortKind == LineUpGraphBranchPortKind.Out) &&
        outgoing.Select(item => item.Connection.From.Port).OrderBy(item => item)
            .SequenceEqual(Enumerable.Range(0, cardinality).Select(item => (int?)item));

    private static void DeriveNodeStateAndValidateReachability(
        LineUpGraphSequence source,
        LineUpGraphPreviewSequence result,
        ICollection<LineUpGraphPreviewIssue> issues,
        IReadOnlyCollection<int> externalOutgoingTransitionNumbers,
        IReadOnlyCollection<int> externalEntryStepNumbers)
    {
        var outgoing = result.Edges
            .GroupBy(item => item.FromSourceKey)
            .ToDictionary(group => group.Key, group => group.Select(item => item.ToSourceKey).ToList(), StringComparer.Ordinal);
        var outgoingKeys = outgoing.Keys.ToHashSet(StringComparer.Ordinal);
        var jumpSourceKeys = result.Edges.Where(item => item.EdgeKind == "Jump")
            .Select(item => item.FromSourceKey)
            .ToHashSet(StringComparer.Ordinal);
        // 2026-09-28（FB151/PLC2）：直连序列终点 EndConnection 的最终转换无归一化出边，
        // 属合法拓扑（解析期已把终止边跳过并记入 EndAnchors），豁免其
        // TransitionWithoutOutgoingEdge（此前 GraphTopologyBlocking 阻断提交）。
        // 2026-09-28（FB250/PLC2）：出边为跨序列子程序调用边的转换同理豁免。
        var exemptedTransitionNumbers = source.EndAnchors
            .Where(anchor => anchor.Kind == LineUpGraphEndpointKind.Transition)
            .Select(anchor => anchor.Number)
            .Concat(externalOutgoingTransitionNumbers)
            .ToHashSet();
        var endTransitionKeys = source.Transitions
            .Where(transition => exemptedTransitionNumbers.Contains(transition.Number))
            .Select(transition => transition.SourceKey)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var node in result.Nodes)
        {
            if (node.NodeKind == "Step")
            {
                node.IsFinal = !outgoingKeys.Contains(node.SourceKey);
            }
            else
            {
                node.IsJump = jumpSourceKeys.Contains(node.SourceKey);
                if (!outgoingKeys.Contains(node.SourceKey) && !endTransitionKeys.Contains(node.SourceKey))
                {
                    issues.Add(Issue(
                        "Blocking",
                        "TransitionWithoutOutgoingEdge",
                        node.SourceKey,
                        "A Transition must have at least one outgoing normalized edge."));
                }
            }
        }

        var initialKeys = result.Nodes.Where(item => item.NodeKind == "Step" && item.IsInitial)
            .Select(item => item.SourceKey)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToList();
        // 2026-09-28（FB250/PLC2）：被调用序列（子程序）无 Init 步是 S7-GRAPH 标准拓扑，
        // 其起点由主序列的调用边带入（文档级 Wiring 解析）。有外部入口的序列豁免
        // MissingInitialStep，并以入口步作可达性 BFS 种子。
        var externalEntryKeys = externalEntryStepNumbers
            .Select(number => source.Steps.Where(step => step.Number == number)
                .Select(step => step.SourceKey)
                .FirstOrDefault())
            .Where(key => !string.IsNullOrEmpty(key))
            .Select(key => key!)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToList();
        if (initialKeys.Count == 0 && externalEntryKeys.Count == 0)
        {
            issues.Add(Issue(
                "Blocking",
                "MissingInitialStep",
                source.SourceKey,
                "A Sequence must contain at least one initial Step."));
            return;
        }

        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var reachabilitySeeds = initialKeys.Concat(externalEntryKeys).ToList();
        if (initialKeys.Count == 0)
        {
            // 2026-09-28（FB250/PLC2）：子程序不标 Init，且除外部入口外可能存在无本地入边的
            // 前置步（如 FB250 子程序 200→T200→入口204 段，TIA 运行期按条件进入，静态拓扑
            // 看不到入边）。把无入边步视为隐式入口一并播种，否则误报 UnreachableNode。
            var incomingKeys = result.Edges.Select(edge => edge.ToSourceKey).ToHashSet(StringComparer.Ordinal);
            reachabilitySeeds.AddRange(result.Nodes
                .Where(node => node.NodeKind == "Step" && !incomingKeys.Contains(node.SourceKey))
                .Select(node => node.SourceKey));
        }

        var queue = new Queue<string>(reachabilitySeeds);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!reachable.Add(current) || !outgoing.TryGetValue(current, out var targets))
            {
                continue;
            }

            foreach (var target in targets.OrderBy(item => item, StringComparer.Ordinal))
            {
                queue.Enqueue(target);
            }
        }

        foreach (var node in result.Nodes.Where(item => !reachable.Contains(item.SourceKey)))
        {
            issues.Add(Issue(
                "Blocking",
                "UnreachableNode",
                node.SourceKey,
                "A real graph node is not reachable from any initial Step."));
        }
    }

    private static List<LineUpGraphPreviewEdge> MaterializeEdges(
        string sequenceKey,
        IEnumerable<EdgeCandidate> candidates)
    {
        var ordered = candidates
            .OrderBy(item => item.FromSourceKey, StringComparer.Ordinal)
            .ThenBy(item => item.EdgeKind, StringComparer.Ordinal)
            .ThenBy(item => item.BranchOrder)
            .ThenBy(item => item.ToSourceKey, StringComparer.Ordinal)
            .ThenBy(item => item.PathFingerprint, StringComparer.Ordinal)
            .ToList();
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<LineUpGraphPreviewEdge>();
        foreach (var candidate in ordered)
        {
            var identity = $"{candidate.FromSourceKey}\n{candidate.ToSourceKey}\n{candidate.EdgeKind}\n{candidate.BranchOrder}";
            occurrences.TryGetValue(identity, out var occurrence);
            occurrence++;
            occurrences[identity] = occurrence;
            result.Add(new LineUpGraphPreviewEdge
            {
                SourceKey = LineUpGraphSourceKey.ForNormalizedEdge(
                    sequenceKey,
                    candidate.FromSourceKey,
                    candidate.ToSourceKey,
                    candidate.EdgeKind,
                    candidate.BranchOrder,
                    occurrence),
                FromSourceKey = candidate.FromSourceKey,
                ToSourceKey = candidate.ToSourceKey,
                FromNodeKind = candidate.FromNodeKind,
                ToNodeKind = candidate.ToNodeKind,
                EdgeKind = candidate.EdgeKind,
                BranchOrder = candidate.BranchOrder
            });
        }

        return result;
    }

    private static void AddCandidate(
        ICollection<EdgeCandidate> candidates,
        IReadOnlyDictionary<int, string> stepKeys,
        IReadOnlyDictionary<int, string> transitionKeys,
        LineUpGraphEndpoint from,
        LineUpGraphEndpoint to,
        string edgeKind,
        int? branchOrder,
        string pathFingerprint)
    {
        if (!TryNodeKey(from, stepKeys, transitionKeys, out var fromKey) ||
            !TryNodeKey(to, stepKeys, transitionKeys, out var toKey))
        {
            return;
        }

        candidates.Add(new EdgeCandidate(
            fromKey,
            toKey,
            from.Kind.ToString(),
            to.Kind.ToString(),
            edgeKind,
            branchOrder,
            pathFingerprint));
    }

    private static bool TryNodeKey(
        LineUpGraphEndpoint endpoint,
        IReadOnlyDictionary<int, string> stepKeys,
        IReadOnlyDictionary<int, string> transitionKeys,
        out string sourceKey)
    {
        if (endpoint.Kind == LineUpGraphEndpointKind.Step && stepKeys.TryGetValue(endpoint.Number, out sourceKey!))
        {
            return true;
        }

        if (endpoint.Kind == LineUpGraphEndpointKind.Transition &&
            transitionKeys.TryGetValue(endpoint.Number, out sourceKey!))
        {
            return true;
        }

        sourceKey = string.Empty;
        return false;
    }

    private static bool EndpointExists(
        LineUpGraphEndpoint endpoint,
        IReadOnlyDictionary<int, string> stepKeys,
        IReadOnlyDictionary<int, string> transitionKeys,
        IReadOnlyDictionary<int, LineUpGraphBranch> branches) => endpoint.Kind switch
    {
        LineUpGraphEndpointKind.Step => stepKeys.ContainsKey(endpoint.Number),
        LineUpGraphEndpointKind.Transition => transitionKeys.ContainsKey(endpoint.Number),
        LineUpGraphEndpointKind.Branch => branches.ContainsKey(endpoint.Number),
        _ => false
    };

    private static bool HasValidEndpointPort(LineUpGraphEndpoint endpoint, bool isSource) => endpoint.Kind switch
    {
        LineUpGraphEndpointKind.Step or LineUpGraphEndpointKind.Transition =>
            endpoint.PortKind == LineUpGraphBranchPortKind.None && endpoint.Port == null,
        LineUpGraphEndpointKind.Branch when isSource =>
            endpoint.PortKind == LineUpGraphBranchPortKind.Out && endpoint.Port.HasValue,
        LineUpGraphEndpointKind.Branch =>
            endpoint.PortKind == LineUpGraphBranchPortKind.In && endpoint.Port.HasValue,
        _ => false
    };

    private static bool IsAlternatingRealNodePair(LineUpGraphEndpointKind from, LineUpGraphEndpointKind to) =>
        from == LineUpGraphEndpointKind.Step && to == LineUpGraphEndpointKind.Transition ||
        from == LineUpGraphEndpointKind.Transition && to == LineUpGraphEndpointKind.Step;

    private static string? CanonicalBranchType(string value) => value?.Trim().ToLowerInvariant() switch
    {
        "altbegin" => "AltBegin",
        "altend" => "AltEnd",
        "simbegin" => "SimBegin",
        "simend" => "SimEnd",
        _ => null
    };

    private static string? CanonicalLinkToken(string value) => value?.Trim().ToLowerInvariant() switch
    {
        "direct" => "Direct",
        "jump" => "Jump",
        _ => null
    };

    private static string RawConnectionFingerprint(LineUpGraphRawConnection connection) =>
        $"{EndpointFingerprint(connection.From)}>{EndpointFingerprint(connection.To)}:" +
        $"{connection.LinkType?.Trim().ToLowerInvariant()}";

    private static string EndpointFingerprint(LineUpGraphEndpoint endpoint) =>
        $"{endpoint.Kind.ToString().ToLowerInvariant()}:{endpoint.Number}:" +
        $"{endpoint.PortKind.ToString().ToLowerInvariant()}:{endpoint.Port?.ToString() ?? "-"}";

    private static string StructuralIssueKey(string sequenceKey, string fingerprint)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint));
        return $"{sequenceKey}/raw:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static LineUpGraphPreviewIssue Issue(
        string severity,
        string issueType,
        string? sourceKey,
        string message) => new()
    {
        Severity = severity,
        IssueType = issueType,
        SourceKey = sourceKey,
        Message = message
    };

    private static void AddNeighbor(IDictionary<int, HashSet<int>> adjacency, int from, int to)
    {
        if (!adjacency.TryGetValue(from, out var neighbors))
        {
            neighbors = new HashSet<int>();
            adjacency[from] = neighbors;
        }

        neighbors.Add(to);
    }

    private static bool IsIncidentToAnyBranch(LineUpGraphRawConnection connection, ISet<int> branchNumbers) =>
        connection.From.Kind == LineUpGraphEndpointKind.Branch && branchNumbers.Contains(connection.From.Number) ||
        connection.To.Kind == LineUpGraphEndpointKind.Branch && branchNumbers.Contains(connection.To.Number);

    private static void MarkIncidentBranches(
        LineUpGraphRawConnection connection,
        IReadOnlyDictionary<int, LineUpGraphBranch> branches,
        ISet<int> branchNumbers)
    {
        if (connection.From.Kind == LineUpGraphEndpointKind.Branch &&
            branches.ContainsKey(connection.From.Number))
        {
            branchNumbers.Add(connection.From.Number);
        }

        if (connection.To.Kind == LineUpGraphEndpointKind.Branch &&
            branches.ContainsKey(connection.To.Number))
        {
            branchNumbers.Add(connection.To.Number);
        }
    }

    private static void MarkHandled(IEnumerable<RawConnectionView> connections, ISet<string> handled)
    {
        foreach (var connection in connections)
        {
            handled.Add(connection.StructuralFingerprint);
        }
    }

    /// <summary>
    /// 2026-09-28（FB250/PLC2）：跨序列（S7-GRAPH 子程序）索引。按 Sequence SourceKey 记录
    /// 各序列声明的 Step/Transition/Branch 编号，用于识别「未声明于本序列、但声明于兄弟序列」
    /// 的跨序列引用（子程序调用/返回边）。SourceKey 在解析期已保证唯一。
    /// </summary>
    private sealed class LineUpGraphCrossSequenceIndex
    {
        private readonly Dictionary<string, Dictionary<LineUpGraphEndpointKind, HashSet<int>>> declarationsBySequence
            = new(StringComparer.Ordinal);

        public static LineUpGraphCrossSequenceIndex Build(LineUpGraphDocument document)
        {
            var index = new LineUpGraphCrossSequenceIndex();
            foreach (var sequence in document.Sequences)
            {
                index.declarationsBySequence[sequence.SourceKey] = new()
                {
                    [LineUpGraphEndpointKind.Step] = sequence.Steps.Select(item => item.Number).ToHashSet(),
                    [LineUpGraphEndpointKind.Transition] = sequence.Transitions.Select(item => item.Number).ToHashSet(),
                    [LineUpGraphEndpointKind.Branch] = sequence.Branches.Select(item => item.Number).ToHashSet()
                };
            }

            return index;
        }

        /// <summary>端点未声明于本序列、但声明于任一兄弟序列 → 跨序列引用。</summary>
        public bool IsExternal(LineUpGraphSequence sequence, LineUpGraphEndpoint endpoint) =>
            !DeclaredLocally(sequence.SourceKey, endpoint) && DeclaredByAnyOther(sequence.SourceKey, endpoint);

        /// <summary>
        /// 文档级子程序接线分析：遍历所有序列的原始连接，产出每个序列的
        /// ① 调用侧豁免集（本序列转换的出边指向兄弟序列节点）；
        /// ② 被调侧入口步集（任意序列连接的目标步声明在本序列且对外部连接方为跨序列引用）。
        /// 入口信息在调用方序列的连接里，必须文档级一次算全。
        /// </summary>
        public Wiring Analyze(LineUpGraphDocument document)
        {
            var outgoing = declarationsBySequence.Keys.ToDictionary(
                key => key, _ => new HashSet<int>(), StringComparer.Ordinal);
            var entries = declarationsBySequence.Keys.ToDictionary(
                key => key, _ => new HashSet<int>(), StringComparer.Ordinal);
            foreach (var sequence in document.Sequences)
            {
                foreach (var connection in sequence.RawConnections)
                {
                    if (connection.From.Kind == LineUpGraphEndpointKind.Transition &&
                        DeclaredLocally(sequence.SourceKey, connection.From) &&
                        IsExternal(sequence, connection.To))
                    {
                        outgoing[sequence.SourceKey].Add(connection.From.Number);
                    }

                    if (IsExternal(sequence, connection.To) &&
                        connection.To.Kind == LineUpGraphEndpointKind.Step)
                    {
                        foreach (var target in OwnerSequences(connection.To))
                        {
                            entries[target].Add(connection.To.Number);
                        }
                    }
                }
            }

            return new Wiring(outgoing, entries);
        }

        private IEnumerable<string> OwnerSequences(LineUpGraphEndpoint endpoint) =>
            declarationsBySequence
                .Where(pair => pair.Value.TryGetValue(endpoint.Kind, out var numbers) && numbers.Contains(endpoint.Number))
                .Select(pair => pair.Key);

        private bool DeclaredLocally(string sequenceKey, LineUpGraphEndpoint endpoint) =>
            declarationsBySequence.TryGetValue(sequenceKey, out var table) &&
            table.TryGetValue(endpoint.Kind, out var numbers) &&
            numbers.Contains(endpoint.Number);

        private bool DeclaredByAnyOther(string sequenceKey, LineUpGraphEndpoint endpoint) =>
            declarationsBySequence.Any(pair =>
                !string.Equals(pair.Key, sequenceKey, StringComparison.Ordinal) &&
                pair.Value.TryGetValue(endpoint.Kind, out var numbers) &&
                numbers.Contains(endpoint.Number));

        /// <summary>每序列的跨序列接线结果（供可达性/初始步校验豁免）。</summary>
        public sealed class Wiring(
            Dictionary<string, HashSet<int>> outgoingExternalTransitions,
            Dictionary<string, HashSet<int>> externalEntrySteps)
        {
            public IReadOnlyCollection<int> OutgoingExternalTransitionNumbers(LineUpGraphSequence sequence) =>
                outgoingExternalTransitions.TryGetValue(sequence.SourceKey, out var numbers)
                    ? numbers
                    : Array.Empty<int>();

            public IReadOnlyCollection<int> ExternalEntryStepNumbers(LineUpGraphSequence sequence) =>
                externalEntrySteps.TryGetValue(sequence.SourceKey, out var numbers)
                    ? numbers
                    : Array.Empty<int>();
        }
    }

    private sealed record RawConnectionView(
        LineUpGraphRawConnection Connection,
        string StructuralFingerprint,
        string? CanonicalLinkType);

    private sealed record EdgeCandidate(
        string FromSourceKey,
        string ToSourceKey,
        string FromNodeKind,
        string ToNodeKind,
        string EdgeKind,
        int? BranchOrder,
        string PathFingerprint);

    private sealed record SequenceNormalizationResult(
        LineUpGraphPreviewSequence Sequence,
        List<LineUpGraphPreviewIssue> Issues);
}
