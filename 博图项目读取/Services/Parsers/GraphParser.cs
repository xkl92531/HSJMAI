using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using 博图项目读取.Models;

namespace 博图项目读取.Services.Parsers
{
    /// <summary>
    /// GRAPH（顺序功能图）解析器
    /// </summary>
    public class GraphParser : BaseBlockParser
    {
        public GraphParser(Action<string> logAction) : base(logAction)
        {
        }

        public override string SupportedLanguage => "GRAPH";

        protected override async Task ParseXmlContentAsync(XDocument xmlDoc, ProgramBlock programBlock)
        {
            try
            {
                _logAction($"开始解析GRAPH程序块: {programBlock.Name}");

                // 解析接口信息
                ParseInterfaceInfo(xmlDoc, programBlock);

                // 解析GRAPH序列
                var graphSequence = await ParseGraphSequenceAsync(xmlDoc, programBlock);
                if (graphSequence != null)
                {
                    // 将GRAPH序列转换为网络信息
                    await ConvertGraphToNetworksAsync(graphSequence, programBlock);
                }

                _logAction($"GRAPH程序块解析完成: {programBlock.Name}");
            }
            catch (Exception ex)
            {
                _logAction($"解析GRAPH程序块失败: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 解析GRAPH序列
        /// </summary>
        private async Task<GraphSequence> ParseGraphSequenceAsync(XDocument xmlDoc, ProgramBlock programBlock)
        {
            try
            {
                // 查找CompileUnit中的Graph节点
                var graphNode = xmlDoc.Descendants()
                    .FirstOrDefault(x => x.Name.LocalName == "Graph");

                if (graphNode == null)
                {
                    _logAction("未找到Graph节点");
                    return null;
                }

                var graphSequence = new GraphSequence
                {
                    Name = programBlock.Name,
                    BlockName = programBlock.Name,
                    Comment = "GRAPH顺序功能图序列"
                };

                // 查找Sequence节点
                var sequenceNode = graphNode.Descendants()
                    .FirstOrDefault(x => x.Name.LocalName == "Sequence");

                if (sequenceNode != null)
                {
                    // 解析序列标题和注释
                    var titleNode = sequenceNode.Element(XName.Get("Title", graphNode.Name.NamespaceName));
                    if (titleNode != null)
                    {
                        graphSequence.Comment = GetMultiLanguageText(titleNode) ?? graphSequence.Comment;
                    }

                    // 解析步骤
                    await ParseGraphStepsAsync(sequenceNode, graphSequence);

                    // 解析转换条件
                    await ParseGraphTransitionsAsync(sequenceNode, graphSequence);
                    
                    // 解析连接关系
                    await ParseGraphConnectionsAsync(sequenceNode, graphSequence);
                }

                // 解析前置操作
                await ParsePreOperationsAsync(graphNode, graphSequence);

                // 构建嵌套流程结构
                var flowBuilder = new FlowBuilder(_logAction);
                var nestedFlow = flowBuilder.BuildFlowFromGraph(graphSequence);
                
                // 生成流程分析报告
                var flowAnalyzer = new FlowAnalyzer(_logAction);
                var flowReport = flowAnalyzer.GenerateFlowReport(nestedFlow);
                _logAction("=== 嵌套流程结构 ===");
                _logAction(flowReport);

                _logAction($"GRAPH序列解析完成: {graphSequence.Steps.Count}个步骤, {graphSequence.Transitions.Count}个转换");
                return graphSequence;
            }
            catch (Exception ex)
            {
                _logAction($"解析GRAPH序列失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 解析GRAPH连接关系（增强版）
        /// </summary>
        private async Task ParseGraphConnectionsAsync(XElement sequenceNode, GraphSequence graphSequence)
        {
            try
            {
                var connectionsNode = sequenceNode.Element(XName.Get("Connections", sequenceNode.Name.NamespaceName));
                if (connectionsNode == null) return;

                var connections = new List<GraphConnection>();
                var branches = new Dictionary<int, GraphBranch>();

                // 第一步：解析所有连接
                foreach (var connectionNode in connectionsNode.Elements())
                {
                    if (connectionNode.Name.LocalName == "Connection")
                    {
                        var connection = ParseSingleConnection(connectionNode);
                        if (connection != null)
                        {
                            connections.Add(connection);
                        }
                    }
                }

                // 第二步：解析分支信息
                var branchesNode = sequenceNode.Element(XName.Get("Branches", sequenceNode.Name.NamespaceName));
                if (branchesNode != null)
                {
                    foreach (var branchNode in branchesNode.Elements())
                    {
                        if (branchNode.Name.LocalName == "Branch")
                        {
                            var branch = ParseSingleBranch(branchNode);
                            if (branch != null)
                            {
                                branches[branch.Number] = branch;
                            }
                        }
                    }
                }

                // 第三步：建立步骤和转换条件的直接连接关系
                foreach (var connection in connections)
                {
                    switch (connection.Type)
                    {
                        case "Step->Transition":
                            // 步骤直接连接到转换条件
                            var step = graphSequence.Steps.FirstOrDefault(s => s.Number == connection.FromStepNumber);
                            var transition = graphSequence.Transitions.FirstOrDefault(t => t.Number == connection.ToTransitionNumber);
                            if (step != null && transition != null)
                            {
                                transition.FromStep = step.Name;
                                transition.FromStepNumber = step.Number;
                                if (!step.OutgoingTransitions.Contains(transition.Number))
                                {
                                    step.OutgoingTransitions.Add(transition.Number);
                                }
                            }
                            break;

                        case "Transition->Step":
                            // 转换条件直接连接到步骤
                            var trans = graphSequence.Transitions.FirstOrDefault(t => t.Number == connection.FromTransitionNumber);
                            var targetStep = graphSequence.Steps.FirstOrDefault(s => s.Number == connection.ToStepNumber);
                            if (trans != null && targetStep != null)
                            {
                                trans.ToStep = targetStep.Name;
                                trans.ToStepNumber = targetStep.Number;
                                if (!targetStep.IncomingTransitions.Contains(trans.Number))
                                {
                                    targetStep.IncomingTransitions.Add(trans.Number);
                                }
                            }
                            break;

                        case "Step->Branch":
                            // 步骤连接到分支（并行或选择分支）
                            var sourceStep = graphSequence.Steps.FirstOrDefault(s => s.Number == connection.FromStepNumber);
                            if (sourceStep != null && branches.ContainsKey(connection.ToBranchNumber))
                            {
                                var branch = branches[connection.ToBranchNumber];
                                branch.InputStep = sourceStep.Number;
                                sourceStep.OutputBranch = connection.ToBranchNumber;
                            }
                            break;

                        case "Branch->Transition":
                            // 分支连接到转换条件
                            if (branches.ContainsKey(connection.FromBranchNumber))
                            {
                                var branch = branches[connection.FromBranchNumber];
                                var trans2 = graphSequence.Transitions.FirstOrDefault(t => t.Number == connection.ToTransitionNumber);
                                if (trans2 != null)
                                {
                                    if (!branch.OutputTransitions.ContainsKey(connection.FromBranchOut))
                                    {
                                        branch.OutputTransitions[connection.FromBranchOut] = new List<int>();
                                    }
                                    branch.OutputTransitions[connection.FromBranchOut].Add(trans2.Number);
                                    trans2.FromBranch = connection.FromBranchNumber;
                                    trans2.FromBranchOut = connection.FromBranchOut;
                                }
                            }
                            break;

                        case "Transition->Branch":
                            // 转换条件连接到分支
                            var trans3 = graphSequence.Transitions.FirstOrDefault(t => t.Number == connection.FromTransitionNumber);
                            if (trans3 != null && branches.ContainsKey(connection.ToBranchNumber))
                            {
                                var branch = branches[connection.ToBranchNumber];
                                if (!branch.InputTransitions.ContainsKey(connection.ToBranchIn))
                                {
                                    branch.InputTransitions[connection.ToBranchIn] = new List<int>();
                                }
                                branch.InputTransitions[connection.ToBranchIn].Add(trans3.Number);
                                trans3.ToBranch = connection.ToBranchNumber;
                                trans3.ToBranchIn = connection.ToBranchIn;
                            }
                            break;

                        case "Branch->Step":
                            // 分支直接连接到步骤
                            if (branches.ContainsKey(connection.FromBranchNumber))
                            {
                                var branch = branches[connection.FromBranchNumber];
                                var targetStep2 = graphSequence.Steps.FirstOrDefault(s => s.Number == connection.ToStepNumber);
                                if (targetStep2 != null)
                                {
                                    if (!branch.OutputSteps.ContainsKey(connection.FromBranchOut))
                                    {
                                        branch.OutputSteps[connection.FromBranchOut] = new List<int>();
                                    }
                                    branch.OutputSteps[connection.FromBranchOut].Add(targetStep2.Number);
                                    // 为了链路追踪，也要设置步骤的输入分支信息
                                    targetStep2.InputBranch = connection.FromBranchNumber;
                                    targetStep2.InputBranchOut = connection.FromBranchOut;
                                }
                            }
                            break;
                    }
                }

                // 第四步：通过分支建立间接连接关系
                foreach (var branch in branches.Values)
                {
                    // 处理通过分支的连接
                    if (branch.InputStep > 0)
                    {
                        var inputStep = graphSequence.Steps.FirstOrDefault(s => s.Number == branch.InputStep);
                        if (inputStep != null)
                        {
                            // 将分支的所有输出转换条件关联到输入步骤
                            foreach (var outputTransitions in branch.OutputTransitions.Values)
                            {
                                foreach (var transitionNumber in outputTransitions)
                                {
                                    var transition = graphSequence.Transitions.FirstOrDefault(t => t.Number == transitionNumber);
                                    if (transition != null)
                                    {
                                        // 如果转换条件还没有设置FromStep，则设置为通过分支的步骤
                                        if (string.IsNullOrEmpty(transition.FromStep))
                                        {
                                            transition.FromStep = inputStep.Name;
                                            transition.FromStepNumber = inputStep.Number;
                                        }
                                        if (!inputStep.OutgoingTransitions.Contains(transitionNumber))
                                        {
                                            inputStep.OutgoingTransitions.Add(transitionNumber);
                                        }
                                    }
                                }
                            }
                            
                            // 将分支的所有输出步骤关联到输入步骤（用于链路追踪）
                            foreach (var outputSteps in branch.OutputSteps.Values)
                            {
                                foreach (var stepNumber in outputSteps)
                                {
                                    var targetStep = graphSequence.Steps.FirstOrDefault(s => s.Number == stepNumber);
                                    if (targetStep != null && !targetStep.IncomingTransitions.Any())
                                    {
                                        // 如果目标步骤没有直接的输入转换条件，建立与源步骤的间接关系
                                        targetStep.IndirectFromStep = inputStep.Name;
                                        targetStep.IndirectFromStepNumber = inputStep.Number;
                                    }
                                }
                            }
                        }
                    }
                    
                    // 处理分支输入的转换条件
                    foreach (var inputTransitions in branch.InputTransitions.Values)
                    {
                        foreach (var transitionNumber in inputTransitions)
                        {
                            var transition = graphSequence.Transitions.FirstOrDefault(t => t.Number == transitionNumber);
                            if (transition != null)
                            {
                                // 查找分支的输出步骤，建立转换条件到目标步骤的关系
                                foreach (var outputSteps in branch.OutputSteps.Values)
                                {
                                    foreach (var stepNumber in outputSteps)
                                    {
                                        var targetStep = graphSequence.Steps.FirstOrDefault(s => s.Number == stepNumber);
                                        if (targetStep != null)
                                        {
                                            // 建立转换条件到目标步骤的关系
                                            if (string.IsNullOrEmpty(transition.ToStep))
                                            {
                                                transition.ToStep = targetStep.Name;
                                                transition.ToStepNumber = targetStep.Number;
                                            }
                                            if (!targetStep.IncomingTransitions.Contains(transitionNumber))
                                            {
                                                targetStep.IncomingTransitions.Add(transitionNumber);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                // 第五步：处理跳转连接（Jump类型）
                var jumpConnections = connections.Where(c => c.LinkType == "Jump").ToList();
                foreach (var jumpConnection in jumpConnections)
                {
                    if (jumpConnection.Type == "Transition->Step")
                    {
                        var trans = graphSequence.Transitions.FirstOrDefault(t => t.Number == jumpConnection.FromTransitionNumber);
                        var targetStep = graphSequence.Steps.FirstOrDefault(s => s.Number == jumpConnection.ToStepNumber);
                        if (trans != null && targetStep != null)
                        {
                            trans.ToStep = targetStep.Name;
                            trans.ToStepNumber = targetStep.Number;
                            trans.IsJump = true; // 标记为跳转
                            if (!targetStep.IncomingTransitions.Contains(trans.Number))
                            {
                                targetStep.IncomingTransitions.Add(trans.Number);
                            }
                        }
                    }
                }

                // 第六步：建立完整的链路关系
                BuildCompleteConnectionChain(graphSequence, branches);

                _logAction($"解析到 {connections.Count} 个连接关系，{branches.Count} 个分支");
                _logAction($"其中包含 {jumpConnections.Count} 个跳转连接");
            }
            catch (Exception ex)
            {
                _logAction($"解析GRAPH连接关系失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 建立完整的连接链路关系
        /// </summary>
        private void BuildCompleteConnectionChain(GraphSequence graphSequence, Dictionary<int, GraphBranch> branches)
        {
            try
            {
                // 处理所有转换条件，确保它们都有正确的目标步骤
                foreach (var transition in graphSequence.Transitions)
                {
                    // 如果转换条件没有直接的目标步骤，但有目标分支
                    if (string.IsNullOrEmpty(transition.ToStep) && transition.ToBranch > 0)
                    {
                        if (branches.ContainsKey(transition.ToBranch))
                        {
                            var branch = branches[transition.ToBranch];
                            
                            // 查找分支的输出步骤
                            var outputSteps = new List<GraphStep>();
                            foreach (var outputStepList in branch.OutputSteps.Values)
                            {
                                foreach (var stepNumber in outputStepList)
                                {
                                    var step = graphSequence.Steps.FirstOrDefault(s => s.Number == stepNumber);
                                    if (step != null)
                                    {
                                        outputSteps.Add(step);
                                    }
                                }
                            }
                            
                            // 如果只有一个输出步骤，直接设置为目标步骤
                            if (outputSteps.Count == 1)
                            {
                                transition.ToStep = outputSteps[0].Name;
                                transition.ToStepNumber = outputSteps[0].Number;
                                if (!outputSteps[0].IncomingTransitions.Contains(transition.Number))
                                {
                                    outputSteps[0].IncomingTransitions.Add(transition.Number);
                                }
                            }
                            else if (outputSteps.Count > 1)
                            {
                                // 如果有多个输出步骤，设置为分支描述
                                var stepNames = outputSteps.Select(s => s.Name).ToList();
                                transition.ToStep = $"分支{transition.ToBranch}({string.Join(",", stepNames)})";
                                transition.ToStepNumber = outputSteps[0].Number; // 使用第一个步骤的编号
                                
                                // 为所有输出步骤添加此转换条件
                                foreach (var step in outputSteps)
                                {
                                    if (!step.IncomingTransitions.Contains(transition.Number))
                                    {
                                        step.IncomingTransitions.Add(transition.Number);
                                    }
                                }
                            }
                        }
                    }
                }

                // 处理所有步骤，确保它们都有正确的输出转换条件
                foreach (var step in graphSequence.Steps)
                {
                    // 如果步骤没有直接的输出转换条件，但有输出分支
                    if (!step.OutgoingTransitions.Any() && step.OutputBranch > 0)
                    {
                        if (branches.ContainsKey(step.OutputBranch))
                        {
                            var branch = branches[step.OutputBranch];
                            
                            // 查找分支的输出转换条件
                            var outputTransitions = new List<GraphTransition>();
                            foreach (var outputTransitionList in branch.OutputTransitions.Values)
                            {
                                foreach (var transitionNumber in outputTransitionList)
                                {
                                    var transition = graphSequence.Transitions.FirstOrDefault(t => t.Number == transitionNumber);
                                    if (transition != null)
                                    {
                                        outputTransitions.Add(transition);
                                    }
                                }
                            }
                            
                            // 将所有输出转换条件添加到步骤的输出转换条件列表
                            foreach (var transition in outputTransitions)
                            {
                                if (!step.OutgoingTransitions.Contains(transition.Number))
                                {
                                    step.OutgoingTransitions.Add(transition.Number);
                                }
                                
                                // 确保转换条件的源步骤设置正确
                                if (string.IsNullOrEmpty(transition.FromStep))
                                {
                                    transition.FromStep = step.Name;
                                    transition.FromStepNumber = step.Number;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logAction($"建立完整连接链路关系失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 解析单个连接
        /// </summary>
        private GraphConnection ParseSingleConnection(XElement connectionNode)
        {
            try
            {
                var connection = new GraphConnection();
                
                var nodeFromElement = connectionNode.Element(XName.Get("NodeFrom", connectionNode.Name.NamespaceName));
                var nodeToElement = connectionNode.Element(XName.Get("NodeTo", connectionNode.Name.NamespaceName));
                var linkTypeElement = connectionNode.Element(XName.Get("LinkType", connectionNode.Name.NamespaceName));

                connection.LinkType = linkTypeElement?.Value ?? "Direct";

                if (nodeFromElement != null && nodeToElement != null)
                {
                    // 解析From节点
                    var fromStepRef = nodeFromElement.Element(XName.Get("StepRef", connectionNode.Name.NamespaceName));
                    var fromTransitionRef = nodeFromElement.Element(XName.Get("TransitionRef", connectionNode.Name.NamespaceName));
                    var fromBranchRef = nodeFromElement.Element(XName.Get("BranchRef", connectionNode.Name.NamespaceName));

                    // 解析To节点
                    var toStepRef = nodeToElement.Element(XName.Get("StepRef", connectionNode.Name.NamespaceName));
                    var toTransitionRef = nodeToElement.Element(XName.Get("TransitionRef", connectionNode.Name.NamespaceName));
                    var toBranchRef = nodeToElement.Element(XName.Get("BranchRef", connectionNode.Name.NamespaceName));

                    // 确定连接类型和参数
                    if (fromStepRef != null && toTransitionRef != null)
                    {
                        connection.Type = "Step->Transition";
                        connection.FromStepNumber = int.Parse(fromStepRef.Attribute("Number")?.Value ?? "0");
                        connection.ToTransitionNumber = int.Parse(toTransitionRef.Attribute("Number")?.Value ?? "0");
                    }
                    else if (fromTransitionRef != null && toStepRef != null)
                    {
                        connection.Type = "Transition->Step";
                        connection.FromTransitionNumber = int.Parse(fromTransitionRef.Attribute("Number")?.Value ?? "0");
                        connection.ToStepNumber = int.Parse(toStepRef.Attribute("Number")?.Value ?? "0");
                    }
                    else if (fromStepRef != null && toBranchRef != null)
                    {
                        connection.Type = "Step->Branch";
                        connection.FromStepNumber = int.Parse(fromStepRef.Attribute("Number")?.Value ?? "0");
                        connection.ToBranchNumber = int.Parse(toBranchRef.Attribute("Number")?.Value ?? "0");
                        connection.ToBranchIn = int.Parse(toBranchRef.Attribute("In")?.Value ?? "0");
                    }
                    else if (fromBranchRef != null && toTransitionRef != null)
                    {
                        connection.Type = "Branch->Transition";
                        connection.FromBranchNumber = int.Parse(fromBranchRef.Attribute("Number")?.Value ?? "0");
                        connection.FromBranchOut = int.Parse(fromBranchRef.Attribute("Out")?.Value ?? "0");
                        connection.ToTransitionNumber = int.Parse(toTransitionRef.Attribute("Number")?.Value ?? "0");
                    }
                    else if (fromTransitionRef != null && toBranchRef != null)
                    {
                        connection.Type = "Transition->Branch";
                        connection.FromTransitionNumber = int.Parse(fromTransitionRef.Attribute("Number")?.Value ?? "0");
                        connection.ToBranchNumber = int.Parse(toBranchRef.Attribute("Number")?.Value ?? "0");
                        connection.ToBranchIn = int.Parse(toBranchRef.Attribute("In")?.Value ?? "0");
                    }
                    else if (fromBranchRef != null && toStepRef != null)
                    {
                        connection.Type = "Branch->Step";
                        connection.FromBranchNumber = int.Parse(fromBranchRef.Attribute("Number")?.Value ?? "0");
                        connection.FromBranchOut = int.Parse(fromBranchRef.Attribute("Out")?.Value ?? "0");
                        connection.ToStepNumber = int.Parse(toStepRef.Attribute("Number")?.Value ?? "0");
                    }
                }

                return connection;
            }
            catch (Exception ex)
            {
                _logAction($"解析单个连接失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 解析单个分支
        /// </summary>
        private GraphBranch ParseSingleBranch(XElement branchNode)
        {
            try
            {
                var branch = new GraphBranch
                {
                    Number = int.Parse(branchNode.Attribute("Number")?.Value ?? "0"),
                    Type = branchNode.Attribute("Type")?.Value ?? "Alternative", // Alternative, Parallel, etc.
                    Name = branchNode.Attribute("Name")?.Value ?? $"Branch_{branchNode.Attribute("Number")?.Value}"
                };

                return branch;
            }
            catch (Exception ex)
            {
                _logAction($"解析单个分支失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 解析前置操作
        /// </summary>
        private async Task ParsePreOperationsAsync(XElement graphNode, GraphSequence graphSequence)
        {
            try
            {
                var preOperationsNode = graphNode.Element(XName.Get("PreOperations", graphNode.Name.NamespaceName));
                if (preOperationsNode == null) return;

                foreach (var operationNode in preOperationsNode.Elements())
                {
                    if (operationNode.Name.LocalName == "PermanentOperation")
                    {
                        var operation = new GraphAction
                        {
                            Name = "前置操作",
                            Type = ActionType.Normal, // 使用Normal代替PreOperation
                            Qualifier = ActionQualifier.None,
                            AssociatedStep = "PreOperation"
                        };

                        // 解析标题
                        var titleNode = operationNode.Element(XName.Get("Title", graphNode.Name.NamespaceName));
                        if (titleNode != null)
                        {
                            operation.Comment = GetMultiLanguageText(titleNode) ?? operation.Comment;
                        }

                        // 解析FlgNet逻辑
                        var flgNetNode = operationNode.Element(XName.Get("FlgNet", graphNode.Name.NamespaceName));
                        if (flgNetNode != null)
                        {
                            operation.Code = await ParseFlgNetLogicAsync(flgNetNode);
                        }

                        graphSequence.Actions.Add(operation);
                    }
                }
            }
            catch (Exception ex)
            {
                _logAction($"解析前置操作失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 解析GRAPH步骤
        /// </summary>
        private async Task ParseGraphStepsAsync(XElement sequenceNode, GraphSequence graphSequence)
        {
            try
            {
                var stepsNode = sequenceNode.Element(XName.Get("Steps", sequenceNode.Name.NamespaceName));
                if (stepsNode == null) return;

                foreach (var stepNode in stepsNode.Elements())
                {
                    if (stepNode.Name.LocalName == "Step")
                    {
                        var step = await ParseSingleStepAsync(stepNode);
                        if (step != null)
                        {
                            graphSequence.Steps.Add(step);
                        }
                    }
                }

                _logAction($"解析到 {graphSequence.Steps.Count} 个GRAPH步骤");
            }
            catch (Exception ex)
            {
                _logAction($"解析GRAPH步骤失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 解析单个步骤
        /// </summary>
        private async Task<GraphStep> ParseSingleStepAsync(XElement stepNode)
        {
            try
            {
                var step = new GraphStep
                {
                    Number = int.Parse(stepNode.Attribute("Number")?.Value ?? "0"),
                    Name = stepNode.Attribute("Name")?.Value ?? $"步骤{stepNode.Attribute("Number")?.Value}",
                    IsInitialStep = bool.Parse(stepNode.Attribute("Init")?.Value ?? "false"),
                    Comment = stepNode.Attribute("Name")?.Value ?? ""
                };

                // 解析时间设置
                var maxTimeAttr = stepNode.Attribute("MaximumStepTime");
                if (maxTimeAttr != null)
                {
                    step.MaxTime = ParseTimeSpan(maxTimeAttr.Value);
                }

                var warningTimeAttr = stepNode.Attribute("WarningTime");
                if (warningTimeAttr != null)
                {
                    step.MinTime = ParseTimeSpan(warningTimeAttr.Value);
                }

                // 解析动作
                await ParseStepActionsAsync(stepNode, step);

                // 解析监控条件
                await ParseStepSupervisionsAsync(stepNode, step);

                // 解析互锁条件
                await ParseStepInterlocksAsync(stepNode, step);

                _logAction($"解析步骤: {step.Name} (S{step.Number})");
                return step;
            }
            catch (Exception ex)
            {
                _logAction($"解析单个步骤失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 解析步骤动作
        /// </summary>
        private async Task ParseStepActionsAsync(XElement stepNode, GraphStep step)
        {
            try
            {
                var actionsNode = stepNode.Element(XName.Get("Actions", stepNode.Name.NamespaceName));
                if (actionsNode == null) return;

                // 解析动作标题
                var titleNode = actionsNode.Element(XName.Get("Title", stepNode.Name.NamespaceName));
                string actionTitle = GetMultiLanguageText(titleNode) ?? "步骤动作";

                foreach (var actionNode in actionsNode.Elements())
                {
                    if (actionNode.Name.LocalName == "Action")
                    {
                        var action = new GraphAction
                        {
                            Name = $"{step.Name}_动作",
                            Comment = actionTitle,
                            Type = ActionType.Normal,
                            AssociatedStep = step.Name
                        };

                        // 解析限定符
                        var qualifierAttr = actionNode.Attribute("Qualifier");
                        if (qualifierAttr != null)
                        {
                            action.Qualifier = ParseActionQualifier(qualifierAttr.Value);
                        }

                        // 解析动作代码
                        var codeTokens = new List<string>();
                        foreach (var tokenNode in actionNode.Elements())
                        {
                            if (tokenNode.Name.LocalName == "Token")
                            {
                                var tokenText = tokenNode.Attribute("Text")?.Value;
                                if (!string.IsNullOrEmpty(tokenText))
                                {
                                    // 处理换行符
                                    if (tokenText == "&#xA;")
                                    {
                                        codeTokens.Add("\n");
                                    }
                                    else
                                    {
                                        codeTokens.Add(tokenText);
                                    }
                                }
                            }
                        }

                        action.Code = string.Join("", codeTokens);

                        if (!string.IsNullOrEmpty(action.Code))
                        {
                            step.Actions.Add(action);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logAction($"解析步骤动作失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 解析步骤监控条件
        /// </summary>
        private async Task ParseStepSupervisionsAsync(XElement stepNode, GraphStep step)
        {
            try
            {
                var supervisionsNode = stepNode.Element(XName.Get("Supervisions", stepNode.Name.NamespaceName));
                if (supervisionsNode == null) return;

                foreach (var supervisionNode in supervisionsNode.Elements())
                {
                    if (supervisionNode.Name.LocalName == "Supervision")
                    {
                        var flgNetNode = supervisionNode.Element(XName.Get("FlgNet", stepNode.Name.NamespaceName));
                        if (flgNetNode != null)
                        {
                            var supervisionCode = await ParseFlgNetLogicAsync(flgNetNode);
                            step.SupervisionCondition = supervisionCode;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logAction($"解析步骤监控条件失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 解析步骤互锁条件
        /// </summary>
        private async Task ParseStepInterlocksAsync(XElement stepNode, GraphStep step)
        {
            try
            {
                var interlocksNode = stepNode.Element(XName.Get("Interlocks", stepNode.Name.NamespaceName));
                if (interlocksNode == null) return;

                foreach (var interlockNode in interlocksNode.Elements())
                {
                    if (interlockNode.Name.LocalName == "Interlock")
                    {
                        var flgNetNode = interlockNode.Element(XName.Get("FlgNet", stepNode.Name.NamespaceName));
                        if (flgNetNode != null)
                        {
                            var interlockCode = await ParseFlgNetLogicAsync(flgNetNode);
                            // 这里可以添加到步骤的互锁条件中
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logAction($"解析步骤互锁条件失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 解析GRAPH转换条件
        /// </summary>
        private async Task ParseGraphTransitionsAsync(XElement sequenceNode, GraphSequence graphSequence)
        {
            try
            {
                var transitionsNode = sequenceNode.Element(XName.Get("Transitions", sequenceNode.Name.NamespaceName));
                if (transitionsNode == null) return;

                foreach (var transitionNode in transitionsNode.Elements())
                {
                    if (transitionNode.Name.LocalName == "Transition")
                    {
                        var transition = await ParseSingleTransitionAsync(transitionNode);
                        if (transition != null)
                        {
                            graphSequence.Transitions.Add(transition);
                        }
                    }
                }

                _logAction($"解析到 {graphSequence.Transitions.Count} 个GRAPH转换条件");
            }
            catch (Exception ex)
            {
                _logAction($"解析GRAPH转换条件失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 解析单个转换条件
        /// </summary>
        private async Task<GraphTransition> ParseSingleTransitionAsync(XElement transitionNode)
        {
            try
            {
                var transition = new GraphTransition
                {
                    Name = transitionNode.Attribute("Name")?.Value ?? "未命名转换",
                    Number = int.Parse(transitionNode.Attribute("Number")?.Value ?? "0"),
                    Type = TransitionType.Normal,
                    Comment = transitionNode.Attribute("Name")?.Value ?? ""
                };

                // 解析编程语言
                var programmingLanguage = transitionNode.Attribute("ProgrammingLanguage")?.Value ?? "LAD";

                // 解析FlgNet逻辑
                var flgNetNode = transitionNode.Element(XName.Get("FlgNet", transitionNode.Name.NamespaceName));
                if (flgNetNode != null)
                {
                    transition.Condition = await ParseFlgNetLogicAsync(flgNetNode);
                    
                    // 解析详细的逻辑元素
                    var logicDetails = await ParseTransitionLogicDetailsAsync(flgNetNode);
                    transition.LogicElements = logicDetails.LogicElements;
                    transition.DbVariables = logicDetails.DbVariables;
                    transition.InputSignals = logicDetails.InputSignals;
                    transition.OutputSignals = logicDetails.OutputSignals;
                    transition.Timers = logicDetails.Timers;
                    transition.Counters = logicDetails.Counters;
                    transition.MemoryFlags = logicDetails.MemoryFlags;
                }

                _logAction($"解析转换条件: {transition.Name} (T{transition.Number})");
                return transition;
            }
            catch (Exception ex)
            {
                _logAction($"解析单个转换条件失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 解析转换条件的详细逻辑
        /// </summary>
        private async Task<TransitionLogicDetails> ParseTransitionLogicDetailsAsync(XElement flgNetNode)
        {
            var details = new TransitionLogicDetails();

            try
            {
                var partsNode = flgNetNode.Element(XName.Get("Parts", flgNetNode.Name.NamespaceName));
                if (partsNode == null) return details;

                foreach (var partNode in partsNode.Elements())
                {
                    if (partNode.Name.LocalName == "Access")
                    {
                        var accessInfo = ParseAccessNode(partNode);
                        if (accessInfo != null)
                        {
                            details.LogicElements.Add(accessInfo.Description);
                            
                            // 根据访问类型分类
                            if (accessInfo.Address.StartsWith("DB"))
                            {
                                details.DbVariables.Add(new DbVariable
                                {
                                    FullAddress = accessInfo.Address,
                                    DbNumber = ExtractDbNumber(accessInfo.Address),
                                    DataType = accessInfo.DataType,
                                    Description = accessInfo.Description
                                });
                            }
                            else if (accessInfo.Address.StartsWith("I"))
                            {
                                details.InputSignals.Add($"{accessInfo.Address} ({accessInfo.Description})");
                            }
                            else if (accessInfo.Address.StartsWith("Q"))
                            {
                                details.OutputSignals.Add($"{accessInfo.Address} ({accessInfo.Description})");
                            }
                            else if (accessInfo.Address.StartsWith("M"))
                            {
                                details.MemoryFlags.Add($"{accessInfo.Address} ({accessInfo.Description})");
                            }
                            else if (accessInfo.Address.StartsWith("T"))
                            {
                                details.Timers.Add($"{accessInfo.Address} ({accessInfo.Description})");
                            }
                            else if (accessInfo.Address.StartsWith("C"))
                            {
                                details.Counters.Add($"{accessInfo.Address} ({accessInfo.Description})");
                            }
                        }
                    }
                    else if (partNode.Name.LocalName == "Part")
                    {
                        var partName = partNode.Attribute("Name")?.Value;
                        if (!string.IsNullOrEmpty(partName))
                        {
                            var partDescription = GetPartDescription(partName);
                            if (!string.IsNullOrEmpty(partDescription))
                            {
                                details.LogicElements.Add(partDescription);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logAction($"解析转换条件详细逻辑失败: {ex.Message}");
            }

            return details;
        }

        /// <summary>
        /// 解析Access节点
        /// </summary>
        private AccessInfo ParseAccessNode(XElement accessNode)
        {
            try
            {
                var scope = accessNode.Attribute("Scope")?.Value;
                var symbolNode = accessNode.Element(XName.Get("Symbol", accessNode.Name.NamespaceName));
                
                if (symbolNode != null)
                {
                    var components = new List<string>();
                    var arrayIndices = new List<string>();
                    
                    foreach (var componentNode in symbolNode.Elements())
                    {
                        if (componentNode.Name.LocalName == "Component")
                        {
                            var componentName = componentNode.Attribute("Name")?.Value;
                            if (!string.IsNullOrEmpty(componentName))
                            {
                                components.Add(componentName);
                                
                                // 检查是否有数组访问
                                var arrayAccess = componentNode.Attribute("AccessModifier")?.Value;
                                if (arrayAccess == "Array")
                                {
                                    var accessNode2 = componentNode.Element(XName.Get("Access", componentNode.Name.NamespaceName));
                                    if (accessNode2 != null)
                                    {
                                        var constantNode2 = accessNode2.Element(XName.Get("Constant", componentNode.Name.NamespaceName));
                                        if (constantNode2 != null)
                                        {
                                            var constantValue = constantNode2.Element(XName.Get("ConstantValue", componentNode.Name.NamespaceName))?.Value;
                                            if (!string.IsNullOrEmpty(constantValue))
                                            {
                                                arrayIndices.Add(constantValue);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    
                    if (components.Any())
                    {
                        var address = string.Join(".", components);
                        if (arrayIndices.Any())
                        {
                            address += $"[{string.Join(",", arrayIndices)}]";
                        }
                        
                        return new AccessInfo
                        {
                            Scope = scope,
                            Address = address,
                            DataType = "BOOL", // 默认类型，可以根据实际情况调整
                            Description = $"{scope}: {address}"
                        };
                    }
                }
                
                // 处理常量
                var constantNode = accessNode.Element(XName.Get("Constant", accessNode.Name.NamespaceName));
                if (constantNode != null)
                {
                    var constantType = constantNode.Element(XName.Get("ConstantType", accessNode.Name.NamespaceName))?.Value;
                    var constantValue = constantNode.Element(XName.Get("ConstantValue", accessNode.Name.NamespaceName))?.Value;
                    
                    return new AccessInfo
                    {
                        Scope = scope,
                        Address = constantValue,
                        DataType = constantType,
                        Description = $"常量: {constantValue} ({constantType})"
                    };
                }
            }
            catch (Exception ex)
            {
                _logAction($"解析Access节点失败: {ex.Message}");
            }
            
            return null;
        }

        /// <summary>
        /// 解析FlgNet逻辑（增强版）
        /// </summary>
        private async Task<string> ParseFlgNetLogicAsync(XElement flgNetNode)
        {
            try
            {
                var partsNode = flgNetNode.Element(XName.Get("Parts", flgNetNode.Name.NamespaceName));
                var wiresNode = flgNetNode.Element(XName.Get("Wires", flgNetNode.Name.NamespaceName));
                
                if (partsNode == null) return "无条件";
                
                // 收集所有的逻辑元素
                var logicElements = new Dictionary<string, LogicElement>();
                var partPositions = new Dictionary<string, (int X, int Y)>();
                
                foreach (var partNode in partsNode.Elements())
                {
                    var uid = partNode.Attribute("UId")?.Value;
                    if (string.IsNullOrEmpty(uid)) continue;
                    
                    // 获取位置信息
                    var xAttr = partNode.Attribute("X");
                    var yAttr = partNode.Attribute("Y");
                    if (xAttr != null && yAttr != null)
                    {
                        partPositions[uid] = (int.Parse(xAttr.Value), int.Parse(yAttr.Value));
                    }
                    
                    if (partNode.Name.LocalName == "Access")
                    {
                        var accessInfo = ParseAccessNode(partNode);
                        if (accessInfo != null)
                        {
                            // 检查是否为取反
                            var negated = partNode.Attribute("Negated")?.Value == "true";
                            var variableValue = negated ? $"NOT {accessInfo.Address}" : accessInfo.Address;
                            
                            logicElements[uid] = new LogicElement
                            {
                                Type = "Variable",
                                Value = variableValue,
                                Description = accessInfo.Description,
                                OriginalName = accessInfo.Address,
                                IsNegated = negated
                            };
                        }
                    }
                    else if (partNode.Name.LocalName == "Part")
                    {
                        var partName = partNode.Attribute("Name")?.Value;
                        var partDescription = GetPartDescription(partName);
                        
                        logicElements[uid] = new LogicElement
                        {
                            Type = "Operator",
                            Value = partDescription,
                            OriginalName = partName,
                            Description = $"逻辑运算符: {partDescription}"
                        };
                    }
                }
                
                // 解析连线关系（增强版）
                var wireConnections = ParseWireConnections(wiresNode);
                
                // 构建详细的逻辑表达式（增强版）
                var expression = BuildEnhancedLogicExpression(logicElements, wireConnections, partPositions);
                if (!string.IsNullOrEmpty(expression))
                {
                    return expression;
                }
                
                // 如果无法构建完整表达式，则按位置排序输出
                var sortedElements = logicElements.Values
                    .OrderBy(e => partPositions.ContainsKey(logicElements.FirstOrDefault(kv => kv.Value == e).Key) ? 
                        partPositions[logicElements.FirstOrDefault(kv => kv.Value == e).Key].Y : 0)
                    .ThenBy(e => partPositions.ContainsKey(logicElements.FirstOrDefault(kv => kv.Value == e).Key) ? 
                        partPositions[logicElements.FirstOrDefault(kv => kv.Value == e).Key].X : 0)
                    .ToList();
                
                var variables = sortedElements.Where(e => e.Type == "Variable").Select(e => e.Value).ToList();
                var operators = sortedElements.Where(e => e.Type == "Operator").Select(e => e.Value).ToList();
                
                if (variables.Any() && operators.Any())
                {
                    return $"({string.Join(" ", variables)}) {string.Join(" ", operators)}";
                }
                
                return string.Join(" ", variables.Concat(operators));
            }
            catch (Exception ex)
            {
                _logAction($"解析FlgNet逻辑失败: {ex.Message}");
                return "解析失败";
            }
        }

        /// <summary>
        /// 解析连线连接关系（增强版）
        /// </summary>
        private Dictionary<string, List<WireConnection>> ParseWireConnections(XElement wiresNode)
        {
            var wireConnections = new Dictionary<string, List<WireConnection>>();
            
            if (wiresNode == null) return wireConnections;
            
            foreach (var wireNode in wiresNode.Elements())
            {
                if (wireNode.Name.LocalName == "Wire")
                {
                    var wireUid = wireNode.Attribute("UId")?.Value;
                    var connections = new List<WireConnection>();
                    
                    foreach (var connectionNode in wireNode.Elements())
                    {
                        if (connectionNode.Name.LocalName == "NameCon")
                        {
                            var targetUid = connectionNode.Attribute("UId")?.Value;
                            var connectionName = connectionNode.Attribute("Name")?.Value;
                            
                            if (!string.IsNullOrEmpty(targetUid))
                            {
                                connections.Add(new WireConnection
                                {
                                    TargetUid = targetUid,
                                    ConnectionName = connectionName,
                                    ConnectionType = "NameCon"
                                });
                            }
                        }
                        else if (connectionNode.Name.LocalName == "IdentCon")
                        {
                            var targetUid = connectionNode.Attribute("UId")?.Value;
                            
                            if (!string.IsNullOrEmpty(targetUid))
                            {
                                connections.Add(new WireConnection
                                {
                                    TargetUid = targetUid,
                                    ConnectionName = "IdentCon",
                                    ConnectionType = "IdentCon"
                                });
                            }
                        }
                        else if (connectionNode.Name.LocalName == "OpenCon")
                        {
                            var targetUid = connectionNode.Attribute("UId")?.Value;
                            
                            if (!string.IsNullOrEmpty(targetUid))
                            {
                                connections.Add(new WireConnection
                                {
                                    TargetUid = targetUid,
                                    ConnectionName = "OpenCon",
                                    ConnectionType = "OpenCon"
                                });
                            }
                        }
                        else if (connectionNode.Name.LocalName == "Powerrail")
                        {
                            connections.Add(new WireConnection
                            {
                                TargetUid = "Powerrail",
                                ConnectionName = "Powerrail",
                                ConnectionType = "Powerrail"
                            });
                        }
                    }
                    
                    if (!string.IsNullOrEmpty(wireUid))
                    {
                        wireConnections[wireUid] = connections;
                    }
                }
            }
            
            return wireConnections;
        }

        /// <summary>
        /// 构建增强的逻辑表达式
        /// </summary>
        private string BuildEnhancedLogicExpression(Dictionary<string, LogicElement> logicElements, 
            Dictionary<string, List<WireConnection>> wireConnections, Dictionary<string, (int X, int Y)> partPositions)
        {
            try
            {
                // 构建连接图
                var connectionGraph = BuildConnectionGraph(logicElements, wireConnections);
                
                // 查找表达式的根节点（输出节点）
                var rootNodes = FindRootNodes(connectionGraph, logicElements);
                
                if (rootNodes.Any())
                {
                    var expressions = new List<string>();
                    foreach (var rootNode in rootNodes)
                    {
                        var expression = BuildExpressionFromNode(rootNode, connectionGraph, logicElements, new HashSet<string>());
                        if (!string.IsNullOrEmpty(expression))
                        {
                            expressions.Add(expression);
                        }
                    }
                    
                    if (expressions.Any())
                    {
                        return expressions.Count == 1 ? expressions[0] : $"({string.Join(" AND ", expressions)})";
                    }
                }
                
                // 如果无法构建连接图，使用改进的位置排序方法
                return BuildPositionBasedExpression(logicElements, wireConnections, partPositions);
            }
            catch (Exception ex)
            {
                _logAction($"构建增强逻辑表达式失败: {ex.Message}");
                return "";
            }
        }

        /// <summary>
        /// 构建连接图
        /// </summary>
        private Dictionary<string, List<string>> BuildConnectionGraph(Dictionary<string, LogicElement> logicElements, 
            Dictionary<string, List<WireConnection>> wireConnections)
        {
            var connectionGraph = new Dictionary<string, List<string>>();
            
            // 初始化所有节点
            foreach (var uid in logicElements.Keys)
            {
                connectionGraph[uid] = new List<string>();
            }
            
            // 建立连接关系
            foreach (var wire in wireConnections.Values)
            {
                var connectedNodes = wire.Where(c => logicElements.ContainsKey(c.TargetUid))
                                       .Select(c => c.TargetUid)
                                       .ToList();
                
                if (connectedNodes.Count >= 2)
                {
                    // 对于每条连线，建立节点之间的连接关系
                    for (int i = 0; i < connectedNodes.Count; i++)
                    {
                        for (int j = i + 1; j < connectedNodes.Count; j++)
                        {
                            var node1 = connectedNodes[i];
                            var node2 = connectedNodes[j];
                            
                            // 根据元素类型和位置确定连接方向
                            var element1 = logicElements[node1];
                            var element2 = logicElements[node2];
                            
                            if (element1.Type == "Variable" && element2.Type == "Operator")
                            {
                                connectionGraph[node2].Add(node1); // 变量连接到运算符
                            }
                            else if (element1.Type == "Operator" && element2.Type == "Variable")
                            {
                                connectionGraph[node1].Add(node2); // 运算符连接到变量
                            }
                            else if (element1.Type == "Operator" && element2.Type == "Operator")
                            {
                                // 运算符之间的连接，根据运算符类型确定优先级
                                var priority1 = GetOperatorPriority(element1.OriginalName);
                                var priority2 = GetOperatorPriority(element2.OriginalName);
                                
                                if (priority1 > priority2)
                                {
                                    connectionGraph[node2].Add(node1); // 高优先级连接到低优先级
                                }
                                else
                                {
                                    connectionGraph[node1].Add(node2); // 低优先级连接到高优先级
                                }
                            }
                            else
                            {
                                // 变量之间的连接，建立双向连接
                                connectionGraph[node1].Add(node2);
                                connectionGraph[node2].Add(node1);
                            }
                        }
                    }
                }
            }
            
            return connectionGraph;
        }

        /// <summary>
        /// 获取运算符优先级
        /// </summary>
        private int GetOperatorPriority(string operatorName)
        {
            return operatorName switch
            {
                "Not" => 3,
                "And" => 2,
                "Or" => 1,
                "Xor" => 1,
                "Ge" => 4,
                "Lt" => 4,
                "Gt" => 4,
                "Le" => 4,
                "Eq" => 4,
                "Ne" => 4,
                _ => 0
            };
        }

        /// <summary>
        /// 查找根节点（输出节点）
        /// </summary>
        private List<string> FindRootNodes(Dictionary<string, List<string>> connectionGraph, 
            Dictionary<string, LogicElement> logicElements)
        {
            var rootNodes = new List<string>();
            
            // 查找没有被其他节点连接的节点作为根节点
            var connectedNodes = new HashSet<string>();
            foreach (var connections in connectionGraph.Values)
            {
                foreach (var node in connections)
                {
                    connectedNodes.Add(node);
                }
            }
            
            foreach (var node in connectionGraph.Keys)
            {
                if (!connectedNodes.Contains(node))
                {
                    rootNodes.Add(node);
                }
            }
            
            // 如果没有找到根节点，选择运算符作为根节点
            if (!rootNodes.Any())
            {
                rootNodes = logicElements.Where(kv => kv.Value.Type == "Operator")
                                       .Select(kv => kv.Key)
                                       .ToList();
            }
            
            return rootNodes;
        }

        /// <summary>
        /// 从节点构建表达式
        /// </summary>
        private string BuildExpressionFromNode(string nodeUid, Dictionary<string, List<string>> connectionGraph, 
            Dictionary<string, LogicElement> logicElements, HashSet<string> visited)
        {
            if (visited.Contains(nodeUid) || !logicElements.ContainsKey(nodeUid))
            {
                return "";
            }
            
            visited.Add(nodeUid);
            var element = logicElements[nodeUid];
            
            if (element.Type == "Variable")
            {
                return element.Value;
            }
            else if (element.Type == "Operator")
            {
                var inputExpressions = new List<string>();
                
                if (connectionGraph.ContainsKey(nodeUid))
                {
                    foreach (var inputNode in connectionGraph[nodeUid])
                    {
                        var inputExpression = BuildExpressionFromNode(inputNode, connectionGraph, logicElements, visited);
                        if (!string.IsNullOrEmpty(inputExpression))
                        {
                            inputExpressions.Add(inputExpression);
                        }
                    }
                }
                
                if (inputExpressions.Any())
                {
                    return BuildOperatorExpression(element.OriginalName, inputExpressions);
                }
                else
                {
                    return element.Value;
                }
            }
            
            return "";
        }

        /// <summary>
        /// 构建运算符表达式
        /// </summary>
        private string BuildOperatorExpression(string operatorName, List<string> inputExpressions)
        {
            if (!inputExpressions.Any()) return "";
            
            switch (operatorName)
            {
                case "And":
                    return inputExpressions.Count == 1 ? inputExpressions[0] : 
                           $"({string.Join(" AND ", inputExpressions)})";
                           
                case "Or":
                    return inputExpressions.Count == 1 ? inputExpressions[0] : 
                           $"({string.Join(" OR ", inputExpressions)})";
                           
                case "Not":
                    return inputExpressions.Count == 1 ? $"NOT {inputExpressions[0]}" : 
                           $"NOT ({string.Join(" AND ", inputExpressions)})";
                           
                case "Xor":
                    return inputExpressions.Count == 1 ? inputExpressions[0] : 
                           $"({string.Join(" XOR ", inputExpressions)})";
                           
                case "Ge":
                case "Lt":
                case "Gt":
                case "Le":
                case "Eq":
                case "Ne":
                    if (inputExpressions.Count >= 2)
                    {
                        var op = GetPartDescription(operatorName);
                        return $"({inputExpressions[0]} {op} {inputExpressions[1]})";
                    }
                    return inputExpressions.Count == 1 ? inputExpressions[0] : "";
                    
                default:
                    return inputExpressions.Count == 1 ? inputExpressions[0] : 
                           $"({string.Join(" ", inputExpressions)})";
            }
        }

        /// <summary>
        /// 基于位置构建表达式（备用方法）
        /// </summary>
        private string BuildPositionBasedExpression(Dictionary<string, LogicElement> logicElements, 
            Dictionary<string, List<WireConnection>> wireConnections, Dictionary<string, (int X, int Y)> partPositions)
        {
            var expressionParts = new List<string>();
            var processedElements = new HashSet<string>();
            
            // 按Y坐标排序处理逻辑元素（从上到下）
            var sortedElements = logicElements
                .OrderBy(kv => partPositions.ContainsKey(kv.Key) ? partPositions[kv.Key].Y : 0)
                .ThenBy(kv => partPositions.ContainsKey(kv.Key) ? partPositions[kv.Key].X : 0)
                .ToList();
            
            // 查找比较运算符并构建比较表达式
            var comparisonOps = sortedElements.Where(kv => 
                kv.Value.Type == "Operator" && 
                IsComparisonOperator(kv.Value.OriginalName)).ToList();
            
            foreach (var compOp in comparisonOps)
            {
                var opUid = compOp.Key;
                var connectedVars = FindConnectedVariables(opUid, wireConnections, logicElements);
                
                if (connectedVars.Count >= 2)
                {
                    var leftVar = connectedVars[0];
                    var rightVar = connectedVars[1];
                    var compExpression = $"({leftVar} {compOp.Value.Value} {rightVar})";
                    expressionParts.Add(compExpression);
                    processedElements.Add(opUid);
                    
                    // 标记已处理的变量
                    foreach (var varUid in logicElements.Where(kv => connectedVars.Contains(kv.Value.Value)).Select(kv => kv.Key))
                    {
                        processedElements.Add(varUid);
                    }
                }
            }
            
            // 查找逻辑运算符
            var logicOps = sortedElements.Where(kv => 
                kv.Value.Type == "Operator" && 
                IsLogicOperator(kv.Value.OriginalName) &&
                !processedElements.Contains(kv.Key)).ToList();
            
            // 处理NOT运算符
            var notOps = logicOps.Where(kv => kv.Value.OriginalName == "Not").ToList();
            foreach (var notOp in notOps)
            {
                var opUid = notOp.Key;
                var connectedVars = FindConnectedVariables(opUid, wireConnections, logicElements);
                
                if (connectedVars.Count >= 1)
                {
                    var notExpression = $"NOT {connectedVars[0]}";
                    expressionParts.Add(notExpression);
                    processedElements.Add(opUid);
                }
            }
            
            // 处理未处理的变量
            var remainingVars = sortedElements.Where(kv => 
                kv.Value.Type == "Variable" && 
                !processedElements.Contains(kv.Key)).ToList();
            
            foreach (var varElement in remainingVars)
            {
                expressionParts.Add(varElement.Value.Value);
            }
            
            // 处理AND/OR运算符
            var andOrOps = logicOps.Where(kv => 
                kv.Value.OriginalName == "And" || kv.Value.OriginalName == "Or").ToList();
            
            if (andOrOps.Any() && expressionParts.Count > 1)
            {
                var primaryOp = andOrOps.First();
                var opSymbol = primaryOp.Value.Value;
                
                // 如果有多个表达式，用逻辑运算符连接
                return $"({string.Join($" {opSymbol} ", expressionParts)})";
            }
            
            // 如果只有一个表达式或没有逻辑运算符，直接返回
            return expressionParts.Count == 1 ? expressionParts[0] : string.Join(" AND ", expressionParts);
        }

        /// <summary>
        /// 判断是否为比较运算符
        /// </summary>
        private bool IsComparisonOperator(string operatorName)
        {
            return operatorName == "Ge" || operatorName == "Lt" || operatorName == "Gt" || 
                   operatorName == "Le" || operatorName == "Eq" || operatorName == "Ne";
        }

        /// <summary>
        /// 判断是否为逻辑运算符
        /// </summary>
        private bool IsLogicOperator(string operatorName)
        {
            return operatorName == "And" || operatorName == "Or" || operatorName == "Not" || operatorName == "Xor";
        }

        /// <summary>
        /// 查找连接到指定元素的变量
        /// </summary>
        private List<string> FindConnectedVariables(string targetUid, Dictionary<string, List<WireConnection>> wireConnections, Dictionary<string, LogicElement> logicElements)
        {
            var connectedVars = new List<string>();
            
            try
            {
                // 查找所有连接到目标元素的连线
                foreach (var wire in wireConnections.Values)
                {
                    var targetConnection = wire.FirstOrDefault(c => c.TargetUid == targetUid);
                    if (targetConnection != null)
                    {
                        // 查找此连线的其他连接（源变量）
                        foreach (var connection in wire)
                        {
                            if (connection.TargetUid != targetUid && logicElements.ContainsKey(connection.TargetUid))
                            {
                                var element = logicElements[connection.TargetUid];
                                if (element.Type == "Variable")
                                {
                                    connectedVars.Add(element.Value);
                                }
                            }
                        }
                    }
                }
                
                // 如果没找到连接的变量，查找输入连接
                foreach (var wire in wireConnections)
                {
                    foreach (var connection in wire.Value)
                    {
                        if (connection.TargetUid == targetUid)
                        {
                            // 查找此连线的源
                            foreach (var sourceConnection in wire.Value)
                            {
                                if (sourceConnection != connection && logicElements.ContainsKey(sourceConnection.TargetUid))
                                {
                                    var element = logicElements[sourceConnection.TargetUid];
                                    if (element.Type == "Variable")
                                    {
                                        connectedVars.Add(element.Value);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logAction($"查找连接变量失败: {ex.Message}");
            }
            
            return connectedVars.Distinct().ToList();
        }

        /// <summary>
        /// 获取部件描述
        /// </summary>
        private string GetPartDescription(string partName)
        {
            return partName switch
            {
                "Contact" => "触点",
                "Coil" => "线圈",
                "TrCoil" => "转换线圈",
                "SvCoil" => "监控线圈",
                "IlCoil" => "互锁线圈",
                "Ge" => ">=",
                "Lt" => "<",
                "Gt" => ">",
                "Le" => "<=",
                "Eq" => "=",
                "Ne" => "!=",
                "And" => "AND",
                "Or" => "OR",
                "Not" => "NOT",
                "Xor" => "XOR",
                _ => partName
            };
        }

        /// <summary>
        /// 将GRAPH序列转换为网络信息
        /// </summary>
        private async Task ConvertGraphToNetworksAsync(GraphSequence graphSequence, ProgramBlock programBlock)
        {
            try
            {
                // 创建日志文件
                var logFileName = $"GRAPH解析日志_{programBlock.Name}_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
                var logFilePath = Path.Combine(Directory.GetCurrentDirectory(), "Logs", logFileName);
                
                // 确保日志目录存在
                var logDirectory = Path.GetDirectoryName(logFilePath);
                if (!Directory.Exists(logDirectory))
                {
                    Directory.CreateDirectory(logDirectory);
                }

                var logLines = new List<string>();
                
                // 写入日志头部
                logLines.Add("=".PadRight(80, '='));
                logLines.Add($"GRAPH程序块解析详细日志");
                logLines.Add($"解析时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                logLines.Add($"程序块名称: {programBlock.Name}");
                logLines.Add("=".PadRight(80, '='));
                logLines.Add("");

                // 基本统计信息
                logLines.Add("=== 基本统计信息 ===");
                logLines.Add($"总步骤数: {graphSequence.Steps.Count}");
                logLines.Add($"总转换条件数: {graphSequence.Transitions.Count}");
                logLines.Add($"总动作数: {graphSequence.Actions.Count}");
                logLines.Add("");

                // 工步执行顺序
                logLines.Add("=== 工步执行顺序 ===");
                var sortedSteps = graphSequence.Steps.OrderBy(s => s.Number).ToList();
                foreach (var step in sortedSteps)
                {
                    logLines.Add($"步骤 S{step.Number}: {step.Name}");
                    logLines.Add($"  ├─ 是否初始步骤: {(step.IsInitialStep ? "是" : "否")}");
                    logLines.Add($"  ├─ 最大时间: {step.MaxTime.TotalSeconds}秒");
                    logLines.Add($"  ├─ 最小时间: {step.MinTime.TotalSeconds}秒");
                    logLines.Add($"  ├─ 注释: {step.Comment}");
                    logLines.Add($"  ├─ 监控条件: {step.SupervisionCondition ?? "无"}");
                    logLines.Add($"  └─ 动作数量: {step.Actions.Count}");
                    
                    // 步骤动作详情
                    if (step.Actions.Any())
                    {
                        logLines.Add($"     动作详情:");
                        for (int i = 0; i < step.Actions.Count; i++)
                        {
                            var action = step.Actions[i];
                            var prefix = i == step.Actions.Count - 1 ? "     └─" : "     ├─";
                            logLines.Add($"{prefix} 动作名称: {action.Name}");
                            logLines.Add($"        ├─ 限定符: {action.Qualifier}");
                            logLines.Add($"        ├─ 动作类型: {action.Type}");
                            logLines.Add($"        └─ 动作代码: {action.Code?.Replace("\n", "\\n") ?? "无"}");
                        }
                    }
                    logLines.Add("");
                }

                // 转换条件详细分析
                logLines.Add("=== 转换条件详细分析 ===");
                var sortedTransitions = graphSequence.Transitions.OrderBy(t => t.Number).ToList();
                foreach (var transition in sortedTransitions)
                {
                    logLines.Add($"转换 T{transition.Number}: {transition.Name}");
                    logLines.Add($"  ├─ 转换类型: {transition.Type}");
                    logLines.Add($"  ├─ 注释: {transition.Comment}");
                    logLines.Add($"  ├─ 转换条件: {transition.Condition}");
                    logLines.Add($"  ├─ 从步骤: {transition.FromStep ?? "未指定"}");
                    logLines.Add($"  └─ 到步骤: {transition.ToStep ?? "未指定"}");
                    
                    // 逻辑元素
                    if (transition.LogicElements.Any())
                    {
                        logLines.Add($"     逻辑元素 ({transition.LogicElements.Count}个):");
                        for (int i = 0; i < transition.LogicElements.Count; i++)
                        {
                            var element = transition.LogicElements[i];
                            var prefix = i == transition.LogicElements.Count - 1 ? "     └─" : "     ├─";
                            logLines.Add($"{prefix} {element}");
                        }
                    }
                    
                    // DB变量
                    if (transition.DbVariables.Any())
                    {
                        logLines.Add($"     DB变量 ({transition.DbVariables.Count}个):");
                        for (int i = 0; i < transition.DbVariables.Count; i++)
                        {
                            var dbVar = transition.DbVariables[i];
                            var prefix = i == transition.DbVariables.Count - 1 ? "     └─" : "     ├─";
                            logLines.Add($"{prefix} {dbVar.FullAddress} (DB{dbVar.DbNumber}) - {dbVar.DataType} - {dbVar.Description}");
                        }
                    }
                    
                    // 输入信号
                    if (transition.InputSignals.Any())
                    {
                        logLines.Add($"     输入信号 ({transition.InputSignals.Count}个):");
                        for (int i = 0; i < transition.InputSignals.Count; i++)
                        {
                            var signal = transition.InputSignals[i];
                            var prefix = i == transition.InputSignals.Count - 1 ? "     └─" : "     ├─";
                            logLines.Add($"{prefix} {signal}");
                        }
                    }
                    
                    // 输出信号
                    if (transition.OutputSignals.Any())
                    {
                        logLines.Add($"     输出信号 ({transition.OutputSignals.Count}个):");
                        for (int i = 0; i < transition.OutputSignals.Count; i++)
                        {
                            var signal = transition.OutputSignals[i];
                            var prefix = i == transition.OutputSignals.Count - 1 ? "     └─" : "     ├─";
                            logLines.Add($"{prefix} {signal}");
                        }
                    }
                    
                    // 定时器
                    if (transition.Timers.Any())
                    {
                        logLines.Add($"     定时器 ({transition.Timers.Count}个):");
                        for (int i = 0; i < transition.Timers.Count; i++)
                        {
                            var timer = transition.Timers[i];
                            var prefix = i == transition.Timers.Count - 1 ? "     └─" : "     ├─";
                            logLines.Add($"{prefix} {timer}");
                        }
                    }
                    
                    // 计数器
                    if (transition.Counters.Any())
                    {
                        logLines.Add($"     计数器 ({transition.Counters.Count}个):");
                        for (int i = 0; i < transition.Counters.Count; i++)
                        {
                            var counter = transition.Counters[i];
                            var prefix = i == transition.Counters.Count - 1 ? "     └─" : "     ├─";
                            logLines.Add($"{prefix} {counter}");
                        }
                    }
                    
                    // 内存标志
                    if (transition.MemoryFlags.Any())
                    {
                        logLines.Add($"     内存标志 ({transition.MemoryFlags.Count}个):");
                        for (int i = 0; i < transition.MemoryFlags.Count; i++)
                        {
                            var flag = transition.MemoryFlags[i];
                            var prefix = i == transition.MemoryFlags.Count - 1 ? "     └─" : "     ├─";
                            logLines.Add($"{prefix} {flag}");
                        }
                    }
                    logLines.Add("");
                }

                // 前置操作
                if (graphSequence.Actions.Any())
                {
                    logLines.Add("=== 前置操作 ===");
                    foreach (var action in graphSequence.Actions)
                    {
                        logLines.Add($"前置操作: {action.Name}");
                        logLines.Add($"  ├─ 类型: {action.Type}");
                        logLines.Add($"  ├─ 限定符: {action.Qualifier}");
                        logLines.Add($"  ├─ 代码: {action.Code}");
                        logLines.Add($"  ├─ 注释: {action.Comment}");
                        logLines.Add($"  └─ 关联步骤: {action.AssociatedStep}");
                        logLines.Add("");
                    }
                }

                // 工步执行流程分析
                logLines.Add("=== 工步执行流程分析 ===");
                var initialSteps = graphSequence.Steps.Where(s => s.IsInitialStep).ToList();
                if (initialSteps.Any())
                {
                    logLines.Add("初始步骤:");
                    foreach (var step in initialSteps)
                    {
                        logLines.Add($"  → S{step.Number}: {step.Name}");
                    }
                    logLines.Add("");
                }

                // 构建实际的执行流程
                logLines.Add("实际执行流程:");
                var executionFlow = BuildExecutionFlow(graphSequence);
                foreach (var flowStep in executionFlow)
                {
                    logLines.Add($"  {flowStep}");
                }
                logLines.Add("");

                // 步骤转换关系（基于Connection解析）
                logLines.Add("=== 步骤转换关系（基于Connection解析）===");
                
                // 按步骤编号排序，但初始步骤优先
                var sortedStepsByFlow = graphSequence.Steps
                    .OrderBy(s => s.IsInitialStep ? 0 : 1)
                    .ThenBy(s => s.Number)
                    .ToList();
                
                foreach (var step in sortedStepsByFlow)
                {
                    // 查找从此步骤出发的所有转换条件（包括通过分支的）
                    var directTransitions = step.OutgoingTransitions
                        .Select(tNum => graphSequence.Transitions.FirstOrDefault(t => t.Number == tNum))
                        .Where(t => t != null)
                        .ToList();
                    
                    if (directTransitions.Any())
                    {
                        logLines.Add($"  S{step.Number}({step.Name}) 可转换到:");
                        
                        // 按转换条件编号排序
                        var orderedTransitions = directTransitions.OrderBy(t => t.Number).ToList();
                        
                        foreach (var transition in orderedTransitions)
                        {
                            var toStep = graphSequence.Steps.FirstOrDefault(s => s.Number == transition.ToStepNumber);
                            var toStepName = toStep?.Name ?? "未知步骤";
                            
                            // 检查是否通过分支
                            var connectionInfo = "";
                            if (transition.FromBranch > 0)
                            {
                                connectionInfo = $" (通过分支{transition.FromBranch}输出{transition.FromBranchOut})";
                            }
                            
                            // 检查是否为跳转
                            var jumpInfo = transition.IsJump ? " [跳转]" : "";
                            
                            logLines.Add($"    ├─ 转换条件 T{transition.Number}: {transition.Name}{connectionInfo}{jumpInfo}");
                            logLines.Add($"    │  ├─ 条件表达式: {transition.Condition}");
                            
                            // 显示详细的逻辑元素
                            if (transition.LogicElements.Any())
                            {
                                logLines.Add($"    │  ├─ 逻辑元素:");
                                foreach (var element in transition.LogicElements.Take(3)) // 只显示前3个
                                {
                                    logLines.Add($"    │  │  └─ {element}");
                                }
                                if (transition.LogicElements.Count > 3)
                                {
                                    logLines.Add($"    │  │  └─ ... 还有{transition.LogicElements.Count - 3}个逻辑元素");
                                }
                            }
                            
                            // 显示涉及的变量（简化显示）
                            var variableCount = transition.DbVariables.Count + transition.InputSignals.Count + 
                                               transition.OutputSignals.Count + transition.MemoryFlags.Count + 
                                               transition.Timers.Count + transition.Counters.Count;
                            
                            if (variableCount > 0)
                            {
                                logLines.Add($"    │  ├─ 涉及变量总数: {variableCount}");
                                
                                // 显示主要变量类型
                                var variableTypes = new List<string>();
                                if (transition.DbVariables.Any()) variableTypes.Add($"DB变量{transition.DbVariables.Count}个");
                                if (transition.InputSignals.Any()) variableTypes.Add($"输入信号{transition.InputSignals.Count}个");
                                if (transition.OutputSignals.Any()) variableTypes.Add($"输出信号{transition.OutputSignals.Count}个");
                                if (transition.MemoryFlags.Any()) variableTypes.Add($"内存标志{transition.MemoryFlags.Count}个");
                                if (transition.Timers.Any()) variableTypes.Add($"定时器{transition.Timers.Count}个");
                                if (transition.Counters.Any()) variableTypes.Add($"计数器{transition.Counters.Count}个");
                                
                                logLines.Add($"    │  │  └─ 包含: {string.Join(", ", variableTypes)}");
                            }
                            
                            logLines.Add($"    │  └─ 目标步骤: S{transition.ToStepNumber}({toStepName})");
                            logLines.Add($"    │");
                        }
                        
                        logLines.Add($"    └─ 总计 {directTransitions.Count} 个转换条件");
                    }
                    else
                    {
                        // 查找转换到此步骤的条件
                        var incomingTransitions = step.IncomingTransitions
                            .Select(tNum => graphSequence.Transitions.FirstOrDefault(t => t.Number == tNum))
                            .Where(t => t != null)
                            .ToList();
                        
                        if (incomingTransitions.Any())
                        {
                            logLines.Add($"  S{step.Number}({step.Name}) 由以下转换条件到达:");
                            foreach (var transition in incomingTransitions.OrderBy(t => t.Number))
                            {
                                var fromStep = graphSequence.Steps.FirstOrDefault(s => s.Number == transition.FromStepNumber);
                                var fromStepName = fromStep?.Name ?? "未知步骤";
                                
                                var connectionInfo = "";
                                if (transition.ToBranch > 0)
                                {
                                    connectionInfo = $" (通过分支{transition.ToBranch}输入{transition.ToBranchIn})";
                                }
                                
                                var jumpInfo = transition.IsJump ? " [跳转]" : "";
                                
                                logLines.Add($"    └─ 从 S{transition.FromStepNumber}({fromStepName}) 通过 T{transition.Number}({transition.Condition}){connectionInfo}{jumpInfo}");
                            }
                        }
                        else if (!step.IsInitialStep)
                        {
                            logLines.Add($"  S{step.Number}({step.Name}) → [可能是终止步骤或连接信息缺失]");
                        }
                    }
                    logLines.Add("");
                }
                
                // 添加分支信息汇总
                logLines.Add("=== 分支信息汇总 ===");
                var branchSteps = graphSequence.Steps.Where(s => s.OutputBranch > 0).ToList();
                if (branchSteps.Any())
                {
                    logLines.Add("发现以下分支结构:");
                    foreach (var step in branchSteps.OrderBy(s => s.OutputBranch))
                    {
                        var branchTransitions = graphSequence.Transitions
                            .Where(t => t.FromBranch == step.OutputBranch)
                            .OrderBy(t => t.FromBranchOut)
                            .ToList();
                        
                        if (branchTransitions.Any())
                        {
                            logLines.Add($"  分支{step.OutputBranch}: 从步骤S{step.Number}({step.Name})分出");
                            
                            var groupedByOut = branchTransitions.GroupBy(t => t.FromBranchOut).OrderBy(g => g.Key);
                            foreach (var group in groupedByOut)
                            {
                                var transitions = group.ToList();
                                logLines.Add($"    输出{group.Key}: {transitions.Count}个转换条件");
                                foreach (var trans in transitions)
                                {
                                    var toStep = graphSequence.Steps.FirstOrDefault(s => s.Number == trans.ToStepNumber);
                                    var toStepName = toStep?.Name ?? "未知步骤";
                                    logLines.Add($"      └─ T{trans.Number}({trans.Condition}) → S{trans.ToStepNumber}({toStepName})");
                                }
                            }
                        }
                    }
                }
                else
                {
                    logLines.Add("未发现分支结构");
                }
                logLines.Add("");
                
                // 添加转换条件汇总表（增强版）
                logLines.Add("=== 转换条件汇总表（增强版）===");
                logLines.Add("转换编号 | 转换名称 | 源步骤 | 目标步骤 | 分支信息 | 连接类型 | 条件表达式");
                logLines.Add("-".PadRight(100, '-'));
                
                var transitionsSummary = graphSequence.Transitions.OrderBy(t => t.Number).ToList();
                foreach (var transition in transitionsSummary)
                {
                    var fromStepName = graphSequence.Steps.FirstOrDefault(s => s.Number == transition.FromStepNumber)?.Name ?? "未知";
                    var toStepName = graphSequence.Steps.FirstOrDefault(s => s.Number == transition.ToStepNumber)?.Name ?? "未知";
                    
                    var branchInfo = "";
                    if (transition.FromBranch > 0)
                    {
                        branchInfo = $"分支{transition.FromBranch}出{transition.FromBranchOut}";
                    }
                    else if (transition.ToBranch > 0)
                    {
                        branchInfo = $"分支{transition.ToBranch}入{transition.ToBranchIn}";
                    }
                    else
                    {
                        branchInfo = "直连";
                    }
                    
                    var connectionType = transition.IsJump ? "跳转" : "直连";
                    
                    logLines.Add($"T{transition.Number.ToString().PadRight(8)} | {transition.Name.PadRight(12)} | S{transition.FromStepNumber}({fromStepName})".PadRight(20) + 
                                $" | S{transition.ToStepNumber}({toStepName})".PadRight(20) + 
                                $" | {branchInfo.PadRight(10)} | {connectionType.PadRight(8)} | {transition.Condition}");
                }
                logLines.Add("");
                
                // 添加执行路径分析
                logLines.Add("=== 可能的执行路径分析 ===");
                var executionPaths = AnalyzeExecutionPaths(graphSequence);
                for (int i = 0; i < executionPaths.Count; i++)
                {
                    logLines.Add($"路径 {i + 1}: {executionPaths[i]}");
                }
                logLines.Add("");

                // 网络创建过程
                logLines.Add("=== 网络创建过程 ===");
                
                // 创建GRAPH概览网络
                var overviewNetwork = new NetworkInfo
                {
                    Number = 1,
                    Title = "GRAPH概览",
                    Comment = $"包含{graphSequence.Steps.Count}个步骤和{graphSequence.Transitions.Count}个转换条件",
                    Language = "GRAPH",
                    IsEnabled = true
                };

                logLines.Add($"创建概览网络: {overviewNetwork.Title}");
                logLines.Add($"  ├─ 网络编号: {overviewNetwork.Number}");
                logLines.Add($"  ├─ 注释: {overviewNetwork.Comment}");
                logLines.Add($"  └─ 语言: {overviewNetwork.Language}");
                logLines.Add("");

                // 添加步骤元素
                logLines.Add("添加步骤元素:");
                foreach (var step in graphSequence.Steps)
                {
                    var stepElement = new NetworkElement
                    {
                        Id = Guid.NewGuid().ToString(),
                        Type = "GRAPH_Step",
                        Name = step.Name,
                        Address = $"S{step.Number}",
                        Comment = $"{step.Comment} (最大时间:{step.MaxTime.TotalSeconds}s)",
                        X = step.Number % 10,
                        Y = step.Number / 10,
                        IsInitialStep = step.IsInitialStep,
                        Properties = new Dictionary<string, object>
                        {
                            ["StepNumber"] = step.Number,
                            ["IsInitialStep"] = step.IsInitialStep,
                            ["MaxTime"] = step.MaxTime.ToString(),
                            ["ActionsCount"] = step.Actions.Count,
                            ["SupervisionCondition"] = step.SupervisionCondition ?? ""
                        }
                    };
                    
                    overviewNetwork.Elements.Add(stepElement);
                    logLines.Add($"  ├─ S{step.Number}: {step.Name} (位置: {stepElement.X},{stepElement.Y})");
                }

                // 添加转换条件元素
                logLines.Add("添加转换条件元素:");
                foreach (var transition in graphSequence.Transitions)
                {
                    var transitionElement = new NetworkElement
                    {
                        Id = Guid.NewGuid().ToString(),
                        Type = "GRAPH_Transition",
                        Name = transition.Name,
                        Address = $"T{transition.Number}",
                        Comment = $"{transition.Comment} - 条件: {transition.Condition}",
                        X = transition.Number % 10,
                        Y = transition.Number / 10 + 20,
                        Properties = new Dictionary<string, object>
                        {
                            ["TransitionNumber"] = transition.Number,
                            ["Condition"] = transition.Condition,
                            ["LogicElements"] = transition.LogicElements,
                            ["DbVariables"] = transition.DbVariables.Count,
                            ["InputSignals"] = transition.InputSignals.Count,
                            ["OutputSignals"] = transition.OutputSignals.Count
                        }
                    };
                    
                    overviewNetwork.Elements.Add(transitionElement);
                    logLines.Add($"  ├─ T{transition.Number}: {transition.Name} (位置: {transitionElement.X},{transitionElement.Y})");
                }

                programBlock.Networks.Add(overviewNetwork);
                logLines.Add($"概览网络创建完成，包含 {overviewNetwork.Elements.Count} 个元素");
                logLines.Add("");

                // 创建详细步骤网络
                logLines.Add("创建详细步骤网络:");
                await CreateDetailedStepNetworksAsync(graphSequence, programBlock);
                logLines.Add($"  └─ 创建了 {graphSequence.Steps.Count} 个步骤详细网络");

                // 创建详细转换条件网络
                logLines.Add("创建详细转换条件网络:");
                await CreateDetailedTransitionNetworksAsync(graphSequence, programBlock);
                logLines.Add($"  └─ 创建了 {graphSequence.Transitions.Count} 个转换条件详细网络");

                // 最终统计
                logLines.Add("");
                logLines.Add("=== 最终统计 ===");
                logLines.Add($"总共创建了 {programBlock.Networks.Count} 个网络");
                logLines.Add($"  ├─ 1个概览网络");
                logLines.Add($"  ├─ {graphSequence.Steps.Count}个步骤详细网络");
                logLines.Add($"  └─ {graphSequence.Transitions.Count}个转换条件详细网络");
                logLines.Add("");
                logLines.Add("=".PadRight(80, '='));
                logLines.Add("GRAPH解析完成");
                logLines.Add("=".PadRight(80, '='));

                // 将日志写入文件
                File.WriteAllLines(logFilePath, logLines, System.Text.Encoding.UTF8);
                
                // 同时输出到控制台
                _logAction($"GRAPH解析日志已保存到: {logFilePath}");
                _logAction($"总共创建了 {programBlock.Networks.Count} 个网络");
                _logAction($"  - 1个概览网络");
                _logAction($"  - {graphSequence.Steps.Count}个步骤详细网络");
                _logAction($"  - {graphSequence.Transitions.Count}个转换条件详细网络");
            }
            catch (Exception ex)
            {
                _logAction($"GRAPH转换为网络失败: {ex.Message}");
                _logAction($"错误堆栈: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// 创建详细步骤网络
        /// </summary>
        private async Task CreateDetailedStepNetworksAsync(GraphSequence graphSequence, ProgramBlock programBlock)
        {
            int networkNumber = 2;
            
            foreach (var step in graphSequence.Steps)
            {
                var stepNetwork = new NetworkInfo
                {
                    Number = networkNumber++,
                    Title = $"步骤 S{step.Number}: {step.Name}",
                    Comment = $"步骤详细信息和动作",
                    Language = "GRAPH_Step",
                    IsEnabled = true
                };

                // 添加步骤信息元素
                var stepInfoElement = new NetworkElement
                {
                    Id = Guid.NewGuid().ToString(),
                    Type = "Step_Info",
                    Name = step.Name,
                    Address = $"S{step.Number}",
                    Comment = step.Comment,
                    X = 1, Y = 1,
                    Properties = new Dictionary<string, object>
                    {
                        ["StepNumber"] = step.Number,
                        ["IsInitialStep"] = step.IsInitialStep,
                        ["MaxTime"] = step.MaxTime.ToString(),
                        ["MinTime"] = step.MinTime.ToString(),
                        ["SupervisionCondition"] = step.SupervisionCondition ?? ""
                    }
                };
                stepNetwork.Elements.Add(stepInfoElement);

                // 添加动作元素
                int actionY = 2;
                foreach (var action in step.Actions)
                {
                    var actionElement = new NetworkElement
                    {
                        Id = Guid.NewGuid().ToString(),
                        Type = "Step_Action",
                        Name = action.Name,
                        Address = $"A{step.Number}_{actionY}",
                        Comment = $"限定符: {action.Qualifier}, 代码: {action.Code}",
                        X = 1, Y = actionY++,
                        Properties = new Dictionary<string, object>
                        {
                            ["ActionType"] = action.Type.ToString(),
                            ["Qualifier"] = action.Qualifier.ToString(),
                            ["Code"] = action.Code ?? "",
                            ["AssociatedStep"] = action.AssociatedStep
                        }
                    };
                    stepNetwork.Elements.Add(actionElement);
                }

                programBlock.Networks.Add(stepNetwork);
            }
        }

        /// <summary>
        /// 创建详细转换条件网络
        /// </summary>
        private async Task CreateDetailedTransitionNetworksAsync(GraphSequence graphSequence, ProgramBlock programBlock)
        {
            int networkNumber = programBlock.Networks.Count + 1;
            
            foreach (var transition in graphSequence.Transitions)
            {
                var transitionNetwork = new NetworkInfo
                {
                    Number = networkNumber++,
                    Title = $"转换 T{transition.Number}: {transition.Name}",
                    Comment = $"转换条件详细逻辑",
                    Language = "GRAPH_Transition",
                    IsEnabled = true
                };

                // 添加转换条件信息元素
                var transitionInfoElement = new NetworkElement
                {
                    Id = Guid.NewGuid().ToString(),
                    Type = "Transition_Info",
                    Name = transition.Name,
                    Address = $"T{transition.Number}",
                    Comment = $"条件: {transition.Condition}",
                    X = 1, Y = 1,
                    Properties = new Dictionary<string, object>
                    {
                        ["TransitionNumber"] = transition.Number,
                        ["Condition"] = transition.Condition,
                        ["Type"] = transition.Type.ToString(),
                        ["FromStep"] = transition.FromStep,
                        ["ToStep"] = transition.ToStep
                    }
                };
                transitionNetwork.Elements.Add(transitionInfoElement);

                // 添加DB变量元素
                int elementY = 2;
                foreach (var dbVar in transition.DbVariables)
                {
                    var dbElement = new NetworkElement
                    {
                        Id = Guid.NewGuid().ToString(),
                        Type = "DB_Variable",
                        Name = dbVar.FullAddress,
                        Address = dbVar.FullAddress,
                        Comment = $"DB{dbVar.DbNumber} - {dbVar.Description}",
                        X = 1, Y = elementY++,
                        Properties = new Dictionary<string, object>
                        {
                            ["DbNumber"] = dbVar.DbNumber,
                            ["DataType"] = dbVar.DataType,
                            ["Offset"] = dbVar.Offset,
                            ["BitNumber"] = dbVar.BitNumber
                        }
                    };
                    transitionNetwork.Elements.Add(dbElement);
                }

                // 添加输入信号元素
                foreach (var inputSignal in transition.InputSignals)
                {
                    var inputElement = new NetworkElement
                    {
                        Id = Guid.NewGuid().ToString(),
                        Type = "Input_Signal",
                        Name = inputSignal,
                        Address = inputSignal.Split('(')[0].Trim(),
                        Comment = inputSignal,
                        X = 2, Y = elementY++
                    };
                    transitionNetwork.Elements.Add(inputElement);
                }

                // 添加输出信号元素
                foreach (var outputSignal in transition.OutputSignals)
                {
                    var outputElement = new NetworkElement
                    {
                        Id = Guid.NewGuid().ToString(),
                        Type = "Output_Signal",
                        Name = outputSignal,
                        Address = outputSignal.Split('(')[0].Trim(),
                        Comment = outputSignal,
                        X = 3, Y = elementY++
                    };
                    transitionNetwork.Elements.Add(outputElement);
                }

                programBlock.Networks.Add(transitionNetwork);
            }
        }

        /// <summary>
        /// 解析接口信息
        /// </summary>
        private void ParseInterfaceInfo(XDocument xmlDoc, ProgramBlock programBlock)
        {
            try
            {
                var interfaceNode = xmlDoc.Descendants()
                    .FirstOrDefault(x => x.Name.LocalName == "Interface");

                if (interfaceNode == null) return;

                var sectionsNode = interfaceNode.Element(XName.Get("Sections", interfaceNode.Name.NamespaceName));
                if (sectionsNode == null) return;

                foreach (var sectionNode in sectionsNode.Elements())
                {
                    if (sectionNode.Name.LocalName == "Section")
                    {
                        var sectionName = sectionNode.Attribute("Name")?.Value;
                        
                        switch (sectionName)
                        {
                            case "Input":
                                ParseInterfaceSection(sectionNode, programBlock.Interface.Input, "Input");
                                break;
                            case "Output":
                                ParseInterfaceSection(sectionNode, programBlock.Interface.Output, "Output");
                                break;
                            case "InOut":
                                ParseInterfaceSection(sectionNode, programBlock.Interface.InOut, "InOut");
                                break;
                            case "Static":
                                ParseInterfaceSectionForStatic(sectionNode, programBlock.Interface.Static, "Static");
                                break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logAction($"解析接口信息失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 解析接口节
        /// </summary>
        private void ParseInterfaceSection(XElement sectionNode, List<BlockParameter> parameters, string direction)
        {
            foreach (var memberNode in sectionNode.Elements())
            {
                if (memberNode.Name.LocalName == "Member")
                {
                    var parameter = new BlockParameter
                    {
                        Name = memberNode.Attribute("Name")?.Value ?? "",
                        DataType = memberNode.Attribute("Datatype")?.Value ?? "",
                        Direction = direction,
                        Comment = GetMultiLanguageText(memberNode.Element(XName.Get("Comment", memberNode.Name.NamespaceName))) ?? ""
                    };

                    // 解析默认值
                    var startValueNode = memberNode.Element(XName.Get("StartValue", memberNode.Name.NamespaceName));
                    if (startValueNode != null)
                    {
                        parameter.DefaultValue = startValueNode.Value;
                    }

                    parameters.Add(parameter);
                }
            }
        }

        /// <summary>
        /// 解析静态接口节
        /// </summary>
        private void ParseInterfaceSectionForStatic(XElement sectionNode, List<BlockVariable> variables, string direction)
        {
            foreach (var memberNode in sectionNode.Elements())
            {
                if (memberNode.Name.LocalName == "Member")
                {
                    var variable = new BlockVariable
                    {
                        Name = memberNode.Attribute("Name")?.Value ?? "",
                        DataType = memberNode.Attribute("Datatype")?.Value ?? "",
                        Comment = GetMultiLanguageText(memberNode.Element(XName.Get("Comment", memberNode.Name.NamespaceName))) ?? ""
                    };

                    // 解析初始值
                    var startValueNode = memberNode.Element(XName.Get("StartValue", memberNode.Name.NamespaceName));
                    if (startValueNode != null)
                    {
                        variable.InitialValue = startValueNode.Value;
                    }

                    variables.Add(variable);
                }
            }
        }

        /// <summary>
        /// 解析动作限定符
        /// </summary>
        private ActionQualifier ParseActionQualifier(string qualifier)
        {
            return qualifier switch
            {
                "N" => ActionQualifier.N,
                "S" => ActionQualifier.S,
                "R" => ActionQualifier.R,
                "L" => ActionQualifier.L,
                "D" => ActionQualifier.D,
                "P" => ActionQualifier.P,
                "SD" => ActionQualifier.SD,
                "DS" => ActionQualifier.DS,
                "SL" => ActionQualifier.SL,
                _ => ActionQualifier.None
            };
        }

        /// <summary>
        /// 解析时间跨度
        /// </summary>
        private TimeSpan ParseTimeSpan(string timeString)
        {
            if (string.IsNullOrEmpty(timeString)) return TimeSpan.Zero;

            try
            {
                // 处理 T#10S 格式
                if (timeString.StartsWith("T#"))
                {
                    var timeValue = timeString.Substring(2);
                    
                    if (timeValue.EndsWith("S"))
                    {
                        var seconds = double.Parse(timeValue.Substring(0, timeValue.Length - 1));
                        return TimeSpan.FromSeconds(seconds);
                    }
                    else if (timeValue.EndsWith("MS"))
                    {
                        var milliseconds = double.Parse(timeValue.Substring(0, timeValue.Length - 2));
                        return TimeSpan.FromMilliseconds(milliseconds);
                    }
                    else if (timeValue.EndsWith("M"))
                    {
                        var minutes = double.Parse(timeValue.Substring(0, timeValue.Length - 1));
                        return TimeSpan.FromMinutes(minutes);
                    }
                    else if (timeValue.EndsWith("H"))
                    {
                        var hours = double.Parse(timeValue.Substring(0, timeValue.Length - 1));
                        return TimeSpan.FromHours(hours);
                    }
                }

                return TimeSpan.Zero;
            }
            catch
            {
                return TimeSpan.Zero;
            }
        }

        /// <summary>
        /// 获取多语言文本
        /// </summary>
        private string GetMultiLanguageText(XElement textNode)
        {
            if (textNode == null) return null;

            // 优先获取中文
            var zhText = textNode.Elements()
                .FirstOrDefault(x => x.Name.LocalName == "MultiLanguageText" && 
                                   x.Attribute("Lang")?.Value == "zh-CN")?
                .Value;

            if (!string.IsNullOrEmpty(zhText)) return zhText;

            // 获取英文
            var enText = textNode.Elements()
                .FirstOrDefault(x => x.Name.LocalName == "MultiLanguageText" && 
                                   x.Attribute("Lang")?.Value == "en-GB")?
                .Value;

            if (!string.IsNullOrEmpty(enText)) return enText;

            // 获取第一个可用文本
            return textNode.Elements()
                .FirstOrDefault(x => x.Name.LocalName == "MultiLanguageText")?
                .Value;
        }

        /// <summary>
        /// 从地址字符串中提取DB号
        /// </summary>
        private int ExtractDbNumber(string address)
        {
            if (string.IsNullOrEmpty(address)) return 0;
            
            var match = System.Text.RegularExpressions.Regex.Match(address, @"DB(\d+)");
            if (match.Success)
            {
                return int.Parse(match.Groups[1].Value);
            }
            return 0;
        }

        /// <summary>
        /// 构建执行流程字符串
        /// </summary>
        private List<string> BuildExecutionFlow(GraphSequence graphSequence)
        {
            var flow = new List<string>();
            var visitedSteps = new HashSet<string>();
            var initialSteps = graphSequence.Steps.Where(s => s.IsInitialStep).ToList();

            foreach (var initialStep in initialSteps)
            {
                VisitStep(initialStep, graphSequence, visitedSteps, flow);
            }
            return flow;
        }

        /// <summary>
        /// 递归访问步骤及其转换
        /// </summary>
        private void VisitStep(GraphStep step, GraphSequence graphSequence, HashSet<string> visitedSteps, List<string> flow)
        {
            if (visitedSteps.Contains(step.Name)) return;

            visitedSteps.Add(step.Name);
            flow.Add($"S{step.Number}: {step.Name}");

            var nextTransitions = graphSequence.Transitions
                .Where(t => t.FromStep == step.Name || t.FromStep == $"S{step.Number}")
                .OrderBy(t => t.Number)
                .ToList();

            foreach (var transition in nextTransitions)
            {
                var toStep = graphSequence.Steps.FirstOrDefault(s => s.Name == transition.ToStep || s.Number == transition.ToStepNumber);
                if (toStep != null)
                {
                    VisitStep(toStep, graphSequence, visitedSteps, flow);
                }
            }
        }

        /// <summary>
        /// 分析可能的执行路径
        /// </summary>
        private List<string> AnalyzeExecutionPaths(GraphSequence graphSequence)
        {
            var paths = new List<string>();
            var visitedSteps = new HashSet<string>();
            var initialSteps = graphSequence.Steps.Where(s => s.IsInitialStep).ToList();

            foreach (var initialStep in initialSteps)
            {
                var currentPath = new List<string>();
                currentPath.Add($"S{initialStep.Number}: {initialStep.Name}");
                FindAllPaths(initialStep, graphSequence, visitedSteps, currentPath, paths);
            }
            return paths;
        }

        /// <summary>
        /// 递归查找所有可能的执行路径
        /// </summary>
        private void FindAllPaths(GraphStep currentStep, GraphSequence graphSequence, HashSet<string> visitedSteps, List<string> currentPath, List<string> allPaths)
        {
            // 添加深度限制防止栈溢出
            if (currentPath.Count > 50)
            {
                allPaths.Add(string.Join(" → ", currentPath) + " → [路径过长，已截断]");
                return;
            }
            
            // 检查是否已经访问过此步骤（防止循环）
            if (visitedSteps.Contains(currentStep.Name))
            {
                allPaths.Add(string.Join(" → ", currentPath) + " → " + currentStep.Name + " → [检测到循环]");
                return;
            }

            visitedSteps.Add(currentStep.Name);

            var nextTransitions = graphSequence.Transitions
                .Where(t => t.FromStep == currentStep.Name || t.FromStep == $"S{currentStep.Number}")
                .OrderBy(t => t.Number)
                .ToList();

            // 如果没有下一个转换，这是一个终止步骤
            if (!nextTransitions.Any())
            {
                allPaths.Add(string.Join(" → ", currentPath));
                visitedSteps.Remove(currentStep.Name); // 回溯
                return;
            }

            foreach (var transition in nextTransitions)
            {
                var toStep = graphSequence.Steps.FirstOrDefault(s => s.Name == transition.ToStep || s.Number == transition.ToStepNumber);
                if (toStep != null)
                {
                    var newPath = new List<string>(currentPath);
                    newPath.Add($"S{toStep.Number}: {toStep.Name}");
                    FindAllPaths(toStep, graphSequence, new HashSet<string>(visitedSteps), newPath, allPaths);
                }
            }
            
            visitedSteps.Remove(currentStep.Name); // 回溯
        }
    }

    /// <summary>
    /// 转换条件逻辑详细信息
    /// </summary>
    public class TransitionLogicDetails
    {
        public List<string> LogicElements { get; set; } = new List<string>();
        public List<DbVariable> DbVariables { get; set; } = new List<DbVariable>();
        public List<string> InputSignals { get; set; } = new List<string>();
        public List<string> OutputSignals { get; set; } = new List<string>();
        public List<string> Timers { get; set; } = new List<string>();
        public List<string> Counters { get; set; } = new List<string>();
        public List<string> MemoryFlags { get; set; } = new List<string>();
    }

    /// <summary>
    /// 访问信息
    /// </summary>
    public class AccessInfo
    {
        public string Scope { get; set; }
        public string Address { get; set; }
        public string DataType { get; set; }
        public string Description { get; set; }
    }

    /// <summary>
    /// 逻辑元素
    /// </summary>
    public class LogicElement
    {
        public string Type { get; set; } // "Variable" or "Operator"
        public string Value { get; set; }
        public string OriginalName { get; set; }
        public string Description { get; set; }
        public bool IsNegated { get; set; } // 新增属性，表示是否取反
    }

    /// <summary>
    /// 连线连接信息
    /// </summary>
    public class WireConnection
    {
        public string TargetUid { get; set; }
        public string ConnectionName { get; set; }
        public string ConnectionType { get; set; }
    }

    /// <summary>
    /// GRAPH连接信息
    /// </summary>
    public class GraphConnection
    {
        public string LinkType { get; set; } // "Direct", "Jump", "Branch"
        public string Type { get; set; } // "Step->Transition", "Transition->Step", "Step->Branch", "Branch->Transition", "Transition->Branch", "Branch->Step"
        public int FromStepNumber { get; set; }
        public int ToTransitionNumber { get; set; }
        public int FromTransitionNumber { get; set; }
        public int ToStepNumber { get; set; }
        public int FromBranchNumber { get; set; }
        public int ToBranchNumber { get; set; }
        public int FromBranchOut { get; set; }
        public int ToBranchIn { get; set; }
    }

    /// <summary>
    /// GRAPH分支信息
    /// </summary>
    public class GraphBranch
    {
        public int Number { get; set; }
        public string Type { get; set; } // "Alternative", "Parallel", etc.
        public string Name { get; set; }
        public int InputStep { get; set; } // 输入步骤编号
        public Dictionary<int, List<int>> OutputTransitions { get; set; } = new Dictionary<int, List<int>>(); // 分支输出转换条件
        public Dictionary<int, List<int>> InputTransitions { get; set; } = new Dictionary<int, List<int>>(); // 分支输入转换条件
        public Dictionary<int, List<int>> OutputSteps { get; set; } = new Dictionary<int, List<int>>(); // 分支输出步骤
    }

    /// <summary>
    /// 嵌套的程序流程节点基类
    /// </summary>
    public abstract class FlowNode
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; }
        public string Comment { get; set; }
        public FlowNodeType NodeType { get; set; }
        public FlowNode Parent { get; set; }
        public List<FlowNode> Children { get; set; } = new List<FlowNode>();
        public Dictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();
        
        /// <summary>
        /// 获取节点的完整路径
        /// </summary>
        public string GetFullPath()
        {
            var path = new List<string>();
            var current = this;
            while (current != null)
            {
                path.Insert(0, current.Name);
                current = current.Parent;
            }
            return string.Join(" → ", path);
        }
        
        /// <summary>
        /// 查找子节点
        /// </summary>
        public T FindChild<T>(string name) where T : FlowNode
        {
            return Children.OfType<T>().FirstOrDefault(c => c.Name == name);
        }
        
        /// <summary>
        /// 添加子节点
        /// </summary>
        public void AddChild(FlowNode child)
        {
            child.Parent = this;
            Children.Add(child);
        }
    }

    /// <summary>
    /// 流程节点类型
    /// </summary>
    public enum FlowNodeType
    {
        Sequence,       // 顺序执行
        Step,           // 单个步骤
        Transition,     // 转换条件
        Branch,         // 分支结构
        Parallel,       // 并行分支
        Alternative,    // 选择分支
        Loop,           // 循环结构
        Jump,           // 跳转
        SubSequence     // 子序列
    }

    /// <summary>
    /// 程序序列节点（根节点）
    /// </summary>
    public class SequenceFlowNode : FlowNode
    {
        public SequenceFlowNode()
        {
            NodeType = FlowNodeType.Sequence;
        }
        
        public FlowNode InitialNode { get; set; }
        public List<FlowNode> FinalNodes { get; set; } = new List<FlowNode>();
        public Dictionary<string, FlowNode> NodeMap { get; set; } = new Dictionary<string, FlowNode>();
        
        /// <summary>
        /// 获取执行路径
        /// </summary>
        public List<string> GetExecutionPaths()
        {
            var paths = new List<string>();
            if (InitialNode != null)
            {
                var visited = new HashSet<string>();
                TraverseExecutionPath(InitialNode, new List<string>(), paths, visited, 0, 50); // 减少最大深度
            }
            return paths;
        }
        
        private void TraverseExecutionPath(FlowNode node, List<string> currentPath, List<string> allPaths, HashSet<string> visited, int depth, int maxDepth)
        {
            // 防止无限递归 - 深度限制
            if (depth > maxDepth)
            {
                allPaths.Add(string.Join(" → ", currentPath) + " → [路径过长，已截断]");
                return;
            }
            
            // 检查当前路径中是否已经包含此节点（防止直接循环）
            var nodeName = node.Name;
            if (currentPath.Contains(nodeName))
            {
                allPaths.Add(string.Join(" → ", currentPath) + " → " + nodeName + " → [检测到循环]");
                return;
            }
            
            // 添加当前节点到路径
            currentPath.Add(nodeName);
            
            // 检查是否为终止节点
            if (node.Children.Count == 0 || FinalNodes.Contains(node))
            {
                allPaths.Add(string.Join(" → ", currentPath));
            }
            else
            {
                // 递归处理子节点
                foreach (var child in node.Children)
                {
                    var newPath = new List<string>(currentPath);
                    TraverseExecutionPath(child, newPath, allPaths, visited, depth + 1, maxDepth);
                }
            }
        }
    }

    /// <summary>
    /// 步骤流程节点
    /// </summary>
    public class StepFlowNode : FlowNode
    {
        public StepFlowNode()
        {
            NodeType = FlowNodeType.Step;
        }
        
        public int StepNumber { get; set; }
        public bool IsInitialStep { get; set; }
        public bool IsFinalStep { get; set; }
        public TimeSpan MaxTime { get; set; }
        public TimeSpan WarningTime { get; set; }
        public List<GraphAction> Actions { get; set; } = new List<GraphAction>();
        public List<string> Supervisions { get; set; } = new List<string>();
        public List<string> Interlocks { get; set; } = new List<string>();
        
        /// <summary>
        /// 获取下一个可能的转换条件
        /// </summary>
        public List<TransitionFlowNode> GetNextTransitions()
        {
            return Children.OfType<TransitionFlowNode>().ToList();
        }
    }

    /// <summary>
    /// 转换条件流程节点
    /// </summary>
    public class TransitionFlowNode : FlowNode
    {
        public TransitionFlowNode()
        {
            NodeType = FlowNodeType.Transition;
        }
        
        public int TransitionNumber { get; set; }
        public string Condition { get; set; }
        public TransitionType Type { get; set; }
        public bool IsJump { get; set; }
        public List<string> LogicElements { get; set; } = new List<string>();
        public List<DbVariable> DbVariables { get; set; } = new List<DbVariable>();
        
        /// <summary>
        /// 获取目标步骤
        /// </summary>
        public List<StepFlowNode> GetTargetSteps()
        {
            return Children.OfType<StepFlowNode>().ToList();
        }
        
        /// <summary>
        /// 获取目标分支
        /// </summary>
        public List<BranchFlowNode> GetTargetBranches()
        {
            return Children.OfType<BranchFlowNode>().ToList();
        }
    }

    /// <summary>
    /// 分支流程节点
    /// </summary>
    public class BranchFlowNode : FlowNode
    {
        public BranchFlowNode()
        {
            NodeType = FlowNodeType.Branch;
        }
        
        public int BranchNumber { get; set; }
        public BranchType Type { get; set; }
        public int InputPortCount { get; set; }
        public int OutputPortCount { get; set; }
        public Dictionary<int, List<FlowNode>> InputPorts { get; set; } = new Dictionary<int, List<FlowNode>>();
        public Dictionary<int, List<FlowNode>> OutputPorts { get; set; } = new Dictionary<int, List<FlowNode>>();
        
        /// <summary>
        /// 添加输入端口连接
        /// </summary>
        public void AddInputPort(int port, FlowNode node)
        {
            if (!InputPorts.ContainsKey(port))
                InputPorts[port] = new List<FlowNode>();
            InputPorts[port].Add(node);
        }
        
        /// <summary>
        /// 添加输出端口连接
        /// </summary>
        public void AddOutputPort(int port, FlowNode node)
        {
            if (!OutputPorts.ContainsKey(port))
                OutputPorts[port] = new List<FlowNode>();
            OutputPorts[port].Add(node);
            AddChild(node);
        }
    }

    /// <summary>
    /// 并行分支节点
    /// </summary>
    public class ParallelFlowNode : BranchFlowNode
    {
        public ParallelFlowNode()
        {
            NodeType = FlowNodeType.Parallel;
            Type = BranchType.Parallel;
        }
        
        /// <summary>
        /// 获取并行路径
        /// </summary>
        public List<List<FlowNode>> GetParallelPaths()
        {
            var paths = new List<List<FlowNode>>();
            foreach (var outputPort in OutputPorts.Values)
            {
                paths.Add(outputPort);
            }
            return paths;
        }
    }

    /// <summary>
    /// 选择分支节点
    /// </summary>
    public class AlternativeFlowNode : BranchFlowNode
    {
        public AlternativeFlowNode()
        {
            NodeType = FlowNodeType.Alternative;
            Type = BranchType.Alternative;
        }
        
        /// <summary>
        /// 获取选择路径
        /// </summary>
        public List<List<FlowNode>> GetAlternativePaths()
        {
            var paths = new List<List<FlowNode>>();
            foreach (var outputPort in OutputPorts.Values)
            {
                paths.Add(outputPort);
            }
            return paths;
        }
    }

    /// <summary>
    /// 跳转节点
    /// </summary>
    public class JumpFlowNode : FlowNode
    {
        public JumpFlowNode()
        {
            NodeType = FlowNodeType.Jump;
        }
        
        public FlowNode TargetNode { get; set; }
        public string JumpCondition { get; set; }
        public JumpType Type { get; set; }
    }

    /// <summary>
    /// 分支类型
    /// </summary>
    public enum BranchType
    {
        Alternative,    // 选择分支
        Parallel,       // 并行分支
        SimStart,       // 并行开始
        SimEnd          // 并行结束
    }

    /// <summary>
    /// 跳转类型
    /// </summary>
    public enum JumpType
    {
        Forward,        // 向前跳转
        Backward,       // 向后跳转（循环）
        Conditional     // 条件跳转
    }

    /// <summary>
    /// 流程构建器
    /// </summary>
    public class FlowBuilder
    {
        private readonly Action<string> _logAction;
        
        public FlowBuilder(Action<string> logAction)
        {
            _logAction = logAction;
        }
        
        /// <summary>
        /// 从扁平结构构建嵌套流程
        /// </summary>
        public SequenceFlowNode BuildFlowFromGraph(GraphSequence graphSequence)
        {
            var sequenceNode = new SequenceFlowNode
            {
                Name = graphSequence.Name,
                Comment = graphSequence.Comment
            };
            
            // 保存原始数据用于构建关系
            sequenceNode.Properties["OriginalSequence"] = graphSequence;
            
            // 创建节点映射
            var stepNodes = new Dictionary<int, StepFlowNode>();
            var transitionNodes = new Dictionary<int, TransitionFlowNode>();
            var branchNodes = new Dictionary<int, BranchFlowNode>();
            
            // 创建步骤节点
            foreach (var step in graphSequence.Steps)
            {
                var stepNode = new StepFlowNode
                {
                    Name = step.Name,
                    Comment = step.Comment,
                    StepNumber = step.Number,
                    IsInitialStep = step.IsInitialStep,
                    IsFinalStep = false, // 根据实际需要设置
                    MaxTime = step.MaxTime,
                    WarningTime = TimeSpan.Zero, // 根据实际需要设置
                    Actions = step.Actions,
                    Supervisions = new List<string>(), // 根据实际需要设置
                    Interlocks = new List<string>() // 修正为字符串列表
                };
                
                stepNodes[step.Number] = stepNode;
                sequenceNode.NodeMap[$"S{step.Number}"] = stepNode;
                
                if (step.IsInitialStep)
                {
                    sequenceNode.InitialNode = stepNode;
                }
                
                // 检查是否为终止步骤（没有输出转换条件）
                if (!step.OutgoingTransitions.Any())
                {
                    stepNode.IsFinalStep = true;
                    sequenceNode.FinalNodes.Add(stepNode);
                }
            }
            
            // 创建转换条件节点
            foreach (var transition in graphSequence.Transitions)
            {
                var transitionNode = new TransitionFlowNode
                {
                    Name = transition.Name,
                    Comment = transition.Comment,
                    TransitionNumber = transition.Number,
                    Condition = transition.Condition,
                    Type = transition.Type,
                    IsJump = transition.IsJump,
                    LogicElements = transition.LogicElements,
                    DbVariables = transition.DbVariables
                };
                
                transitionNodes[transition.Number] = transitionNode;
                sequenceNode.NodeMap[$"T{transition.Number}"] = transitionNode;
            }
            
            // 构建嵌套关系
            BuildNestedRelationships(stepNodes, transitionNodes, branchNodes, sequenceNode);
            
            // 重新检查并设置终止节点（在构建关系后）
            foreach (var stepNode in stepNodes.Values)
            {
                if (stepNode.Children.Count == 0 && !sequenceNode.FinalNodes.Contains(stepNode))
                {
                    stepNode.IsFinalStep = true;
                    sequenceNode.FinalNodes.Add(stepNode);
                }
            }
            
            _logAction($"构建嵌套流程完成: {stepNodes.Count}个步骤, {transitionNodes.Count}个转换条件, {sequenceNode.FinalNodes.Count}个终止节点");
            return sequenceNode;
        }
        
        private void BuildNestedRelationships(
            Dictionary<int, StepFlowNode> stepNodes,
            Dictionary<int, TransitionFlowNode> transitionNodes,
            Dictionary<int, BranchFlowNode> branchNodes,
            SequenceFlowNode sequenceNode)
        {
            // 从原始GraphSequence中获取连接关系
            var graphSequence = sequenceNode.Properties.ContainsKey("OriginalSequence") ? 
                (GraphSequence)sequenceNode.Properties["OriginalSequence"] : null;
            if (graphSequence == null) return;

            // 建立步骤到转换条件的嵌套关系
            foreach (var stepNode in stepNodes.Values)
            {
                // 查找此步骤的所有输出转换条件
                var step = graphSequence.Steps.FirstOrDefault(s => s.Number == stepNode.StepNumber);
                if (step != null)
                {
                    foreach (var transitionNumber in step.OutgoingTransitions)
                    {
                        if (transitionNodes.ContainsKey(transitionNumber))
                        {
                            var transitionNode = transitionNodes[transitionNumber];
                            stepNode.AddChild(transitionNode);
                            
                            // 查找转换条件的目标步骤
                            var transition = graphSequence.Transitions.FirstOrDefault(t => t.Number == transitionNumber);
                            if (transition != null && transition.ToStepNumber > 0)
                            {
                                if (stepNodes.ContainsKey(transition.ToStepNumber))
                                {
                                    var targetStep = stepNodes[transition.ToStepNumber];
                                    transitionNode.AddChild(targetStep);
                                }
                            }
                        }
                    }
                }
            }
            
            _logAction("嵌套关系构建完成");
        }
    }

    /// <summary>
    /// 流程分析器
    /// </summary>
    public class FlowAnalyzer
    {
        private readonly Action<string> _logAction;
        
        public FlowAnalyzer(Action<string> logAction)
        {
            _logAction = logAction;
        }
        
        /// <summary>
        /// 分析流程复杂度
        /// </summary>
        public FlowComplexity AnalyzeComplexity(SequenceFlowNode sequenceNode)
        {
            var complexity = new FlowComplexity();
            
            AnalyzeNode(sequenceNode, complexity, 0);
            
            _logAction($"流程复杂度分析完成: 深度{complexity.MaxDepth}, 分支{complexity.BranchCount}, 循环{complexity.LoopCount}");
            return complexity;
        }
        
        private void AnalyzeNode(FlowNode node, FlowComplexity complexity, int depth)
        {
            complexity.MaxDepth = Math.Max(complexity.MaxDepth, depth);
            complexity.TotalNodes++;
            
            switch (node.NodeType)
            {
                case FlowNodeType.Branch:
                case FlowNodeType.Parallel:
                case FlowNodeType.Alternative:
                    complexity.BranchCount++;
                    break;
                case FlowNodeType.Loop:
                    complexity.LoopCount++;
                    break;
                case FlowNodeType.Jump:
                    complexity.JumpCount++;
                    break;
            }
            
            foreach (var child in node.Children)
            {
                AnalyzeNode(child, complexity, depth + 1);
            }
        }
        
        /// <summary>
        /// 生成流程报告
        /// </summary>
        public string GenerateFlowReport(SequenceFlowNode sequenceNode)
        {
            var report = new StringBuilder();
            
            report.AppendLine($"=== 程序流程分析报告 ===");
            report.AppendLine($"序列名称: {sequenceNode.Name}");
            report.AppendLine($"序列说明: {sequenceNode.Comment}");
            report.AppendLine();
            
            var complexity = AnalyzeComplexity(sequenceNode);
            report.AppendLine($"复杂度统计:");
            report.AppendLine($"  总节点数: {complexity.TotalNodes}");
            report.AppendLine($"  最大深度: {complexity.MaxDepth}");
            report.AppendLine($"  分支数量: {complexity.BranchCount}");
            report.AppendLine($"  循环数量: {complexity.LoopCount}");
            report.AppendLine($"  跳转数量: {complexity.JumpCount}");
            report.AppendLine($"  复杂度等级: {complexity.GetComplexityLevel()}");
            report.AppendLine();
            
            report.AppendLine($"执行路径:");
            try
            {
                var paths = sequenceNode.GetExecutionPaths();
                if (paths.Count > 0)
                {
                    for (int i = 0; i < Math.Min(paths.Count, 10); i++) // 限制显示路径数量
                    {
                        report.AppendLine($"  路径{i + 1}: {paths[i]}");
                    }
                    if (paths.Count > 10)
                    {
                        report.AppendLine($"  ... 还有{paths.Count - 10}条路径");
                    }
                }
                else
                {
                    report.AppendLine("  未找到有效的执行路径");
                }
            }
            catch (Exception ex)
            {
                report.AppendLine($"  路径分析出错: {ex.Message}");
            }
            report.AppendLine();
            
            report.AppendLine($"详细结构:");
            try
            {
                GenerateNodeReport(sequenceNode, report, 0);
            }
            catch (Exception ex)
            {
                report.AppendLine($"  结构分析出错: {ex.Message}");
            }
            
            return report.ToString();
        }
        
        private void GenerateNodeReport(FlowNode node, StringBuilder report, int indent)
        {
            var indentStr = new string(' ', indent * 2);
            var nodeTypeStr = GetNodeTypeDescription(node.NodeType);
            
            report.AppendLine($"{indentStr}{nodeTypeStr}: {node.Name}");
            
            if (!string.IsNullOrEmpty(node.Comment))
            {
                report.AppendLine($"{indentStr}  说明: {node.Comment}");
            }
            
            // 显示节点特定信息
            switch (node)
            {
                case StepFlowNode stepNode:
                    report.AppendLine($"{indentStr}  步骤编号: S{stepNode.StepNumber}");
                    if (stepNode.MaxTime > TimeSpan.Zero)
                        report.AppendLine($"{indentStr}  最大时间: {stepNode.MaxTime}");
                    if (stepNode.Actions.Count > 0)
                        report.AppendLine($"{indentStr}  动作数量: {stepNode.Actions.Count}");
                    break;
                    
                case TransitionFlowNode transitionNode:
                    report.AppendLine($"{indentStr}  转换编号: T{transitionNode.TransitionNumber}");
                    if (!string.IsNullOrEmpty(transitionNode.Condition))
                        report.AppendLine($"{indentStr}  条件: {transitionNode.Condition}");
                    if (transitionNode.IsJump)
                        report.AppendLine($"{indentStr}  类型: 跳转");
                    break;
                    
                case BranchFlowNode branchNode:
                    report.AppendLine($"{indentStr}  分支编号: B{branchNode.BranchNumber}");
                    report.AppendLine($"{indentStr}  分支类型: {branchNode.Type}");
                    report.AppendLine($"{indentStr}  输入端口: {branchNode.InputPortCount}");
                    report.AppendLine($"{indentStr}  输出端口: {branchNode.OutputPortCount}");
                    break;
            }
            
            foreach (var child in node.Children)
            {
                GenerateNodeReport(child, report, indent + 1);
            }
        }
        
        private string GetNodeTypeDescription(FlowNodeType nodeType)
        {
            return nodeType switch
            {
                FlowNodeType.Sequence => "序列",
                FlowNodeType.Step => "步骤",
                FlowNodeType.Transition => "转换",
                FlowNodeType.Branch => "分支",
                FlowNodeType.Parallel => "并行",
                FlowNodeType.Alternative => "选择",
                FlowNodeType.Loop => "循环",
                FlowNodeType.Jump => "跳转",
                FlowNodeType.SubSequence => "子序列",
                _ => nodeType.ToString()
            };
        }
    }

    /// <summary>
    /// 流程复杂度信息
    /// </summary>
    public class FlowComplexity
    {
        public int TotalNodes { get; set; }
        public int MaxDepth { get; set; }
        public int BranchCount { get; set; }
        public int LoopCount { get; set; }
        public int JumpCount { get; set; }
        
        public string GetComplexityLevel()
        {
            var score = BranchCount * 2 + LoopCount * 3 + JumpCount * 2 + MaxDepth;
            
            return score switch
            {
                var s when s <= 5 => "简单",
                var s when s <= 15 => "中等",
                var s when s <= 30 => "复杂",
                _ => "非常复杂"
            };
        }
    }
}
