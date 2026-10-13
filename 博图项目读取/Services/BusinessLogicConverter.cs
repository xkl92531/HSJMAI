using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using 博图项目读取.Models;

namespace 博图项目读取.Services
{
    /// <summary>
    /// 业务逻辑转换器 - 将TIA Portal程序块逻辑转换为业务系统格式
    /// </summary>
    public class BusinessLogicConverter
    {
        private readonly Action<string> _logAction;

        public BusinessLogicConverter(Action<string> logAction = null)
        {
            _logAction = logAction ?? (msg => { });
        }

        /// <summary>
        /// 转换程序块为业务逻辑描述
        /// </summary>
        /// <param name="programBlock">程序块</param>
        /// <returns>业务逻辑描述</returns>
        public BusinessLogicDescription ConvertProgramBlock(ProgramBlock programBlock)
        {
            try
            {
                _logAction($"开始转换程序块: {programBlock.Name}");

                var businessLogic = new BusinessLogicDescription
                {
                    BlockName = programBlock.Name,
                    BlockType = programBlock.Type,
                    BlockNumber = programBlock.Number,
                    Language = programBlock.Language,
                    FolderPath = programBlock.FolderPath,
                    Comment = programBlock.Comment,
                    Author = programBlock.Author,
                    CreatedTime = programBlock.CreatedTime,
                    ModifiedTime = programBlock.ModifiedTime
                };

                // 转换网络逻辑
                foreach (var network in programBlock.Networks)
                {
                    var networkLogic = ConvertNetworkLogic(network, programBlock.Language);
                    businessLogic.NetworkLogics.Add(networkLogic);
                }

                // 转换接口信息
                businessLogic.InputParameters = ConvertParameters(programBlock.Interface.Input, "Input");
                businessLogic.OutputParameters = ConvertParameters(programBlock.Interface.Output, "Output");
                businessLogic.StaticVariables = ConvertVariables(programBlock.Interface.Static);

                // 生成业务规则
                businessLogic.BusinessRules = GenerateBusinessRules(programBlock);

                // 生成控制流程
                businessLogic.ControlFlow = GenerateControlFlow(programBlock);

                _logAction($"程序块转换完成: {programBlock.Name}");
                return businessLogic;
            }
            catch (Exception ex)
            {
                _logAction($"转换程序块失败: {programBlock.Name} - {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 转换网络逻辑
        /// </summary>
        private NetworkLogicDescription ConvertNetworkLogic(NetworkInfo network, string language)
        {
            var networkLogic = new NetworkLogicDescription
            {
                NetworkNumber = network.Number,
                Title = network.Title,
                Comment = network.Comment,
                Language = language,
                IsEnabled = network.IsEnabled
            };

            switch (language.ToUpper())
            {
                case "LAD":
                    networkLogic.LogicDescription = ConvertLadderLogic(network);
                    break;
                case "FBD":
                    networkLogic.LogicDescription = ConvertFunctionBlockLogic(network);
                    break;
                case "STL":
                    networkLogic.LogicDescription = ConvertSTLLogic(network);
                    break;
                case "SCL":
                    networkLogic.LogicDescription = ConvertSCLLogic(network);
                    break;
                case "GRAPH":
                    networkLogic.LogicDescription = ConvertGraphLogic(network);
                    break;
                default:
                    networkLogic.LogicDescription = "未知编程语言";
                    break;
            }

            // 提取输入输出信号
            networkLogic.InputSignals = ExtractInputSignals(network);
            networkLogic.OutputSignals = ExtractOutputSignals(network);
            networkLogic.Conditions = ExtractConditions(network);
            networkLogic.Actions = ExtractActions(network);

            return networkLogic;
        }

        /// <summary>
        /// 转换梯形图逻辑
        /// </summary>
        private string ConvertLadderLogic(NetworkInfo network)
        {
            var logic = new StringBuilder();
            logic.AppendLine("=== 梯形图逻辑 ===");

            var contacts = network.Elements.Where(e => e.Type == "Contact").ToList();
            var coils = network.Elements.Where(e => e.Type == "Coil").ToList();
            var functions = network.Elements.Where(e => e.Type == "Function").ToList();

            // 分析触点逻辑
            if (contacts.Any())
            {
                logic.AppendLine("触点条件:");
                foreach (var contact in contacts)
                {
                    string condition = contact.IsNegated ? "NOT " : "";
                    logic.AppendLine($"  - {condition}{contact.Address} ({contact.Comment})");
                }
            }

            // 分析功能块
            if (functions.Any())
            {
                logic.AppendLine("功能块:");
                foreach (var function in functions)
                {
                    logic.AppendLine($"  - {function.Name} {function.Address} ({function.Comment})");
                }
            }

            // 分析输出线圈
            if (coils.Any())
            {
                logic.AppendLine("输出动作:");
                foreach (var coil in coils)
                {
                    string action = coil.IsSet ? "SET " : coil.IsReset ? "RESET " : "";
                    logic.AppendLine($"  - {action}{coil.Address} ({coil.Comment})");
                }
            }

            // 生成逻辑表达式
            logic.AppendLine("逻辑表达式:");
            logic.AppendLine(GenerateLogicExpression(network));

            return logic.ToString();
        }

        /// <summary>
        /// 转换STL逻辑
        /// </summary>
        private string ConvertSTLLogic(NetworkInfo network)
        {
            var logic = new StringBuilder();
            logic.AppendLine("=== 语句表逻辑 ===");

            foreach (var element in network.Elements)
            {
                if (element.Type == "STL_Instruction")
                {
                    logic.AppendLine($"指令: {element.Name}");
                    
                    // 解析STL指令含义
                    var instruction = ParseSTLInstruction(element.Name);
                    if (!string.IsNullOrEmpty(instruction))
                    {
                        logic.AppendLine($"  含义: {instruction}");
                    }
                }
            }

            return logic.ToString();
        }

        /// <summary>
        /// 转换SCL逻辑
        /// </summary>
        private string ConvertSCLLogic(NetworkInfo network)
        {
            var logic = new StringBuilder();
            logic.AppendLine("=== 结构化控制语言逻辑 ===");

            foreach (var element in network.Elements)
            {
                if (element.Type == "SCL_Code")
                {
                    logic.AppendLine("代码块:");
                    logic.AppendLine(element.Comment);
                    
                    // 解析SCL代码结构
                    var structure = ParseSCLStructure(element.Comment);
                    logic.AppendLine("代码结构分析:");
                    logic.AppendLine(structure);
                }
            }

            return logic.ToString();
        }

        /// <summary>
        /// 转换功能块图逻辑
        /// </summary>
        private string ConvertFunctionBlockLogic(NetworkInfo network)
        {
            // 暂时复用梯形图逻辑转换
            return ConvertLadderLogic(network);
        }

        /// <summary>
        /// 转换GRAPH顺序功能图逻辑
        /// </summary>
        private string ConvertGraphLogic(NetworkInfo network)
        {
            var logic = new StringBuilder();
            logic.AppendLine("=== 顺序功能图逻辑 ===");

            // 分析GRAPH步骤
            var steps = network.Elements.Where(e => e.Type == "GRAPH_Step").ToList();
            var transitions = network.Elements.Where(e => e.Type == "GRAPH_Transition").ToList();
            var actions = network.Elements.Where(e => e.Type == "GRAPH_Action").ToList();

            if (steps.Any())
            {
                logic.AppendLine("步骤序列:");
                foreach (var step in steps.OrderBy(s => s.Name))
                {
                    logic.AppendLine($"  步骤 {step.Name}: {step.Comment}");
                    if (step.IsInitialStep)
                    {
                        logic.AppendLine($"    * 初始步骤");
                    }
                    
                    // 查找与此步骤相关的动作
                    var stepActions = actions.Where(a => a.Address == step.Name).ToList();
                    if (stepActions.Any())
                    {
                        logic.AppendLine($"    动作:");
                        foreach (var action in stepActions)
                        {
                            logic.AppendLine($"      - {action.Name}: {action.Comment}");
                        }
                    }
                }
            }

            if (transitions.Any())
            {
                logic.AppendLine("转移条件:");
                foreach (var transition in transitions.OrderBy(t => t.Name))
                {
                    logic.AppendLine($"  转移 {transition.Name}: {transition.Comment}");
                    
                    // 通过连接关系找到转移的源步骤和目标步骤
                    var fromConnection = network.Connections.FirstOrDefault(c => c.ToElementId == transition.Id);
                    var toConnection = network.Connections.FirstOrDefault(c => c.FromElementId == transition.Id);
                    
                    if (fromConnection != null && toConnection != null)
                    {
                        var fromStep = network.Elements.FirstOrDefault(e => e.Id == fromConnection.FromElementId);
                        var toStep = network.Elements.FirstOrDefault(e => e.Id == toConnection.ToElementId);
                        
                        if (fromStep != null && toStep != null)
                        {
                            logic.AppendLine($"    从步骤 {fromStep.Name} 到步骤 {toStep.Name}");
                        }
                    }
                }
            }

            // 生成流程描述
            logic.AppendLine("流程描述:");
            logic.AppendLine(GenerateGraphFlowDescription(network));

            // 生成业务逻辑描述
            logic.AppendLine("业务逻辑:");
            logic.AppendLine(GenerateGraphBusinessLogic(network));

            return logic.ToString();
        }

        /// <summary>
        /// 生成GRAPH流程描述
        /// </summary>
        private string GenerateGraphFlowDescription(NetworkInfo network)
        {
            var description = new StringBuilder();
            var steps = network.Elements.Where(e => e.Type == "GRAPH_Step").OrderBy(s => s.Name).ToList();
            var transitions = network.Elements.Where(e => e.Type == "GRAPH_Transition").ToList();

            description.AppendLine("工艺流程:");
            
            // 找到初始步骤
            var initialStep = steps.FirstOrDefault(s => s.IsInitialStep);
            if (initialStep != null)
            {
                description.AppendLine($"1. 流程从 {initialStep.Name} 开始: {initialStep.Comment}");
                
                // 跟踪流程路径
                var currentStep = initialStep;
                var stepCount = 2;
                var visitedSteps = new HashSet<string>();
                
                while (currentStep != null && !visitedSteps.Contains(currentStep.Name))
                {
                    visitedSteps.Add(currentStep.Name);
                    
                    // 找到从当前步骤出发的转移
                    var outgoingConnection = network.Connections.FirstOrDefault(c => c.FromElementId == currentStep.Id);
                    if (outgoingConnection != null)
                    {
                        var transition = network.Elements.FirstOrDefault(e => e.Id == outgoingConnection.ToElementId && e.Type == "GRAPH_Transition");
                        if (transition != null)
                        {
                            var nextConnection = network.Connections.FirstOrDefault(c => c.FromElementId == transition.Id);
                            if (nextConnection != null)
                            {
                                var nextStep = network.Elements.FirstOrDefault(e => e.Id == nextConnection.ToElementId && e.Type == "GRAPH_Step");
                                if (nextStep != null)
                                {
                                    description.AppendLine($"{stepCount}. 当满足条件 {transition.Comment} 时，转移到 {nextStep.Name}: {nextStep.Comment}");
                                    currentStep = nextStep;
                                    stepCount++;
                                }
                                else
                                {
                                    break;
                                }
                            }
                            else
                            {
                                break;
                            }
                        }
                        else
                        {
                            break;
                        }
                    }
                    else
                    {
                        break;
                    }
                }
            }

            return description.ToString();
        }

        /// <summary>
        /// 生成GRAPH业务逻辑描述
        /// </summary>
        private string GenerateGraphBusinessLogic(NetworkInfo network)
        {
            var businessLogic = new StringBuilder();
            var steps = network.Elements.Where(e => e.Type == "GRAPH_Step").OrderBy(s => s.Name).ToList();
            var actions = network.Elements.Where(e => e.Type == "GRAPH_Action").ToList();

            businessLogic.AppendLine("业务逻辑分析:");

            foreach (var step in steps)
            {
                businessLogic.AppendLine($"步骤 {step.Name} 的业务逻辑:");
                
                // 分析步骤的业务含义
                var businessMeaning = AnalyzeStepBusinessMeaning(step.Name, step.Comment);
                businessLogic.AppendLine($"  业务含义: {businessMeaning}");
                
                // 分析相关动作
                var relatedActions = actions.Where(a => a.Address == step.Name).ToList();
                if (relatedActions.Any())
                {
                    businessLogic.AppendLine($"  执行动作:");
                    foreach (var action in relatedActions)
                    {
                        businessLogic.AppendLine($"    - {action.Name}: {action.Comment}");
                    }
                }
                
                // 分析控制要求
                var controlRequirements = AnalyzeStepControlRequirements(step.Name, step.Comment);
                if (!string.IsNullOrEmpty(controlRequirements))
                {
                    businessLogic.AppendLine($"  控制要求: {controlRequirements}");
                }
                
                businessLogic.AppendLine();
            }

            return businessLogic.ToString();
        }

        /// <summary>
        /// 分析步骤的业务含义
        /// </summary>
        private string AnalyzeStepBusinessMeaning(string stepName, string stepComment)
        {
            var meaning = new StringBuilder();
            
            // 根据步骤名称和注释分析业务含义
            if (stepComment.Contains("启动"))
            {
                meaning.Append("系统启动阶段，进行初始化操作");
            }
            else if (stepComment.Contains("焊接"))
            {
                meaning.Append("执行焊接工艺操作");
                if (stepComment.Contains("准备"))
                {
                    meaning.Append("，准备焊接设备和材料");
                }
                else if (stepComment.Contains("监控"))
                {
                    meaning.Append("，监控焊接过程参数");
                }
                else if (stepComment.Contains("工位"))
                {
                    meaning.Append("，在指定工位进行焊接作业");
                }
            }
            else if (stepComment.Contains("准备"))
            {
                meaning.Append("系统准备阶段，检查设备状态和工艺条件");
            }
            else if (stepComment.Contains("完成") || stepComment.Contains("结束"))
            {
                meaning.Append("工艺完成阶段，进行收尾工作");
            }
            else
            {
                meaning.Append($"执行 {stepComment} 相关的工艺操作");
            }

            return meaning.ToString();
        }

        /// <summary>
        /// 分析步骤的控制要求
        /// </summary>
        private string AnalyzeStepControlRequirements(string stepName, string stepComment)
        {
            var requirements = new StringBuilder();
            
            if (stepComment.Contains("焊接"))
            {
                requirements.Append("需要监控焊接电流、电压、温度等参数；");
                requirements.Append("确保焊接质量符合工艺要求；");
                requirements.Append("记录焊接过程数据");
            }
            else if (stepComment.Contains("启动"))
            {
                requirements.Append("检查安全条件；");
                requirements.Append("确认设备就绪状态；");
                requirements.Append("初始化系统参数");
            }
            else if (stepComment.Contains("监控"))
            {
                requirements.Append("实时监控设备状态；");
                requirements.Append("及时响应异常情况；");
                requirements.Append("记录监控数据");
            }
            else if (stepComment.Contains("准备"))
            {
                requirements.Append("检查设备和材料准备情况；");
                requirements.Append("确认工艺参数设置正确；");
                requirements.Append("验证安全条件");
            }

            return requirements.ToString().TrimEnd('；');
        }

        /// <summary>
        /// 生成逻辑表达式
        /// </summary>
        private string GenerateLogicExpression(NetworkInfo network)
        {
            var expression = new StringBuilder();
            var contacts = network.Elements.Where(e => e.Type == "Contact").ToList();
            var coils = network.Elements.Where(e => e.Type == "Coil").ToList();

            if (contacts.Any() && coils.Any())
            {
                // 构建条件表达式
                var conditions = new List<string>();
                foreach (var contact in contacts)
                {
                    string condition = contact.IsNegated ? $"NOT {contact.Address}" : contact.Address;
                    conditions.Add(condition);
                }

                // 构建输出表达式
                var outputs = coils.Select(c => c.Address).ToList();

                expression.AppendLine($"IF ({string.Join(" AND ", conditions)}) THEN");
                foreach (var output in outputs)
                {
                    expression.AppendLine($"  {output} := TRUE");
                }
                expression.AppendLine("END_IF");
            }

            return expression.ToString();
        }

        /// <summary>
        /// 解析STL指令
        /// </summary>
        private string ParseSTLInstruction(string instruction)
        {
            var parts = instruction.Split(' ');
            if (parts.Length == 0) return "";

            var opCode = parts[0].ToUpper();
            var operand = parts.Length > 1 ? parts[1] : "";

            switch (opCode)
            {
                case "LD":
                    return $"加载 {operand} 的值到累加器";
                case "A":
                    return $"与 {operand} 进行逻辑与运算";
                case "O":
                    return $"与 {operand} 进行逻辑或运算";
                case "AN":
                    return $"与 {operand} 的反相进行逻辑与运算";
                case "ON":
                    return $"与 {operand} 的反相进行逻辑或运算";
                case "=":
                    return $"将累加器结果赋值给 {operand}";
                case "S":
                    return $"置位 {operand}";
                case "R":
                    return $"复位 {operand}";
                case "TON":
                    return $"启动延时器 {operand}";
                case "TOF":
                    return $"关闭延时器 {operand}";
                case "CTU":
                    return $"递增计数器 {operand}";
                case "CTD":
                    return $"递减计数器 {operand}";
                default:
                    return $"未知指令: {instruction}";
            }
        }

        /// <summary>
        /// 解析SCL代码结构
        /// </summary>
        private string ParseSCLStructure(string sclCode)
        {
            var structure = new StringBuilder();
            var lines = sclCode.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                if (string.IsNullOrEmpty(trimmedLine)) continue;

                if (trimmedLine.StartsWith("IF"))
                {
                    structure.AppendLine("- 条件判断语句");
                }
                else if (trimmedLine.StartsWith("FOR"))
                {
                    structure.AppendLine("- 循环语句");
                }
                else if (trimmedLine.StartsWith("WHILE"))
                {
                    structure.AppendLine("- 条件循环语句");
                }
                else if (trimmedLine.Contains(":="))
                {
                    structure.AppendLine("- 赋值语句");
                }
                else if (trimmedLine.StartsWith("CASE"))
                {
                    structure.AppendLine("- 分支选择语句");
                }
            }

            return structure.ToString();
        }

        /// <summary>
        /// 提取输入信号
        /// </summary>
        private List<string> ExtractInputSignals(NetworkInfo network)
        {
            var signals = new List<string>();
            
            // 处理传统的梯形图元素
            signals.AddRange(network.Elements
                .Where(e => e.Type == "Contact" || (e.Address?.StartsWith("I") == true))
                .Select(e => e.Address)
                .Where(addr => !string.IsNullOrEmpty(addr)));
            
            // 处理GRAPH元素
            if (network.Language == "GRAPH")
            {
                // 从GRAPH转移条件中提取输入信号
                var transitions = network.Elements.Where(e => e.Type == "GRAPH_Transition").ToList();
                foreach (var transition in transitions)
                {
                    // 解析转移条件中的输入信号
                    var conditionSignals = ExtractSignalsFromCondition(transition.Comment);
                    signals.AddRange(conditionSignals);
                }
                
                // 从GRAPH动作中提取输入信号
                var actions = network.Elements.Where(e => e.Type == "GRAPH_Action").ToList();
                foreach (var action in actions)
                {
                    var actionSignals = ExtractSignalsFromCondition(action.Comment);
                    signals.AddRange(actionSignals);
                }
            }
            
            return signals.Distinct().ToList();
        }

        /// <summary>
        /// 提取输出信号
        /// </summary>
        private List<string> ExtractOutputSignals(NetworkInfo network)
        {
            var signals = new List<string>();
            
            // 处理传统的梯形图元素
            signals.AddRange(network.Elements
                .Where(e => e.Type == "Coil" || (e.Address?.StartsWith("Q") == true))
                .Select(e => e.Address)
                .Where(addr => !string.IsNullOrEmpty(addr)));
            
            // 处理GRAPH元素
            if (network.Language == "GRAPH")
            {
                // 从GRAPH动作中提取输出信号
                var actions = network.Elements.Where(e => e.Type == "GRAPH_Action").ToList();
                foreach (var action in actions)
                {
                    var outputSignals = ExtractOutputSignalsFromAction(action.Comment);
                    signals.AddRange(outputSignals);
                }
                
                // 从GRAPH步骤中提取输出信号
                var steps = network.Elements.Where(e => e.Type == "GRAPH_Step").ToList();
                foreach (var step in steps)
                {
                    // 步骤激活状态也可以作为输出信号
                    signals.Add($"{step.Name}.X"); // 步骤活动位
                    signals.Add($"{step.Name}.T"); // 步骤时间
                }
            }
            
            return signals.Distinct().ToList();
        }

        /// <summary>
        /// 提取条件
        /// </summary>
        private List<string> ExtractConditions(NetworkInfo network)
        {
            var conditions = new List<string>();
            
            // 处理传统的梯形图元素
            foreach (var element in network.Elements.Where(e => e.Type == "Contact"))
            {
                var condition = element.IsNegated ? $"NOT {element.Address}" : element.Address;
                conditions.Add($"{condition} ({element.Comment})");
            }
            
            // 处理GRAPH元素
            if (network.Language == "GRAPH")
            {
                // 从GRAPH转移条件中提取条件
                var transitions = network.Elements.Where(e => e.Type == "GRAPH_Transition").ToList();
                foreach (var transition in transitions)
                {
                    var conditionText = ParseTransitionCondition(transition.Comment);
                    if (!string.IsNullOrEmpty(conditionText))
                    {
                        conditions.Add($"{transition.Name}: {conditionText}");
                    }
                }
                
                // 从GRAPH步骤监控条件中提取条件
                var steps = network.Elements.Where(e => e.Type == "GRAPH_Step").ToList();
                foreach (var step in steps)
                {
                    if (step.Comment.Contains("监控"))
                    {
                        conditions.Add($"{step.Name}监控条件: {step.Comment}");
                    }
                }
            }
            
            return conditions;
        }

        /// <summary>
        /// 提取动作
        /// </summary>
        private List<string> ExtractActions(NetworkInfo network)
        {
            var actions = new List<string>();
            
            // 处理传统的梯形图元素
            foreach (var element in network.Elements.Where(e => e.Type == "Coil"))
            {
                var action = element.IsSet ? "SET" : element.IsReset ? "RESET" : "ASSIGN";
                actions.Add($"{action} {element.Address} ({element.Comment})");
            }
            
            // 处理GRAPH元素
            if (network.Language == "GRAPH")
            {
                // 从GRAPH动作中提取动作
                var graphActions = network.Elements.Where(e => e.Type == "GRAPH_Action").ToList();
                foreach (var action in graphActions)
                {
                    var actionDescription = ParseGraphAction(action.Name, action.Comment);
                    actions.Add($"{action.Name}: {actionDescription}");
                }
                
                // 从GRAPH步骤中提取动作
                var steps = network.Elements.Where(e => e.Type == "GRAPH_Step").ToList();
                foreach (var step in steps)
                {
                    if (step.IsInitialStep)
                    {
                        actions.Add($"激活初始步骤 {step.Name}");
                    }
                    else
                    {
                        actions.Add($"激活步骤 {step.Name}: {step.Comment}");
                    }
                }
            }
            
            return actions;
        }

        /// <summary>
        /// 从条件文本中提取信号
        /// </summary>
        private List<string> ExtractSignalsFromCondition(string conditionText)
        {
            var signals = new List<string>();
            if (string.IsNullOrEmpty(conditionText)) return signals;
            
            // 使用正则表达式匹配PLC地址格式
            var patterns = new[]
            {
                @"I\d+\.\d+",     // 输入信号 I0.0
                @"Q\d+\.\d+",     // 输出信号 Q0.0
                @"M\d+\.\d+",     // 内部标志 M0.0
                @"DB\d+\.DBX\d+\.\d+", // 数据块位 DB1.DBX0.0
                @"T\d+",          // 定时器 T1
                @"C\d+",          // 计数器 C1
                @"\w+_\w+",       // 符号名称 Start_Button
            };
            
            foreach (var pattern in patterns)
            {
                var matches = System.Text.RegularExpressions.Regex.Matches(conditionText, pattern);
                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    signals.Add(match.Value);
                }
            }
            
            return signals;
        }

        /// <summary>
        /// 从动作文本中提取输出信号
        /// </summary>
        private List<string> ExtractOutputSignalsFromAction(string actionText)
        {
            var signals = new List<string>();
            if (string.IsNullOrEmpty(actionText)) return signals;
            
            // 匹配输出信号
            var patterns = new[]
            {
                @"Q\d+\.\d+",     // 输出信号 Q0.0
                @"M\d+\.\d+",     // 内部标志 M0.0
                @"DB\d+\.DBX\d+\.\d+", // 数据块位 DB1.DBX0.0
            };
            
            foreach (var pattern in patterns)
            {
                var matches = System.Text.RegularExpressions.Regex.Matches(actionText, pattern);
                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    signals.Add(match.Value);
                }
            }
            
            return signals;
        }

        /// <summary>
        /// 解析转移条件
        /// </summary>
        private string ParseTransitionCondition(string conditionText)
        {
            if (string.IsNullOrEmpty(conditionText)) return "";
            
            // 简化转移条件描述
            if (conditionText.Contains("启动"))
            {
                return "启动条件满足";
            }
            else if (conditionText.Contains("完成"))
            {
                return "操作完成";
            }
            else if (conditionText.Contains("准备"))
            {
                return "准备就绪";
            }
            else if (conditionText.Contains("监控"))
            {
                return "监控条件满足";
            }
            else
            {
                return conditionText;
            }
        }

        /// <summary>
        /// 解析GRAPH动作
        /// </summary>
        private string ParseGraphAction(string actionName, string actionComment)
        {
            if (string.IsNullOrEmpty(actionComment)) return actionName;
            
            // 根据动作类型生成描述
            if (actionComment.Contains("启动"))
            {
                return "执行启动操作";
            }
            else if (actionComment.Contains("焊接"))
            {
                return "执行焊接工艺";
            }
            else if (actionComment.Contains("监控"))
            {
                return "执行监控操作";
            }
            else if (actionComment.Contains("准备"))
            {
                return "执行准备工作";
            }
            else
            {
                return actionComment;
            }
        }

        /// <summary>
        /// 转换参数
        /// </summary>
        private List<ParameterDescription> ConvertParameters(List<BlockParameter> parameters, string direction)
        {
            return parameters.Select(p => new ParameterDescription
            {
                Name = p.Name,
                DataType = p.DataType,
                Direction = direction,
                Comment = p.Comment,
                DefaultValue = p.DefaultValue?.ToString() ?? ""
            }).ToList();
        }

        /// <summary>
        /// 转换变量
        /// </summary>
        private List<VariableDescription> ConvertVariables(List<BlockVariable> variables)
        {
            return variables.Select(v => new VariableDescription
            {
                Name = v.Name,
                DataType = v.DataType,
                Comment = v.Comment,
                InitialValue = v.InitialValue?.ToString() ?? "",
                IsRetentive = v.IsRetentive
            }).ToList();
        }

        /// <summary>
        /// 生成业务规则
        /// </summary>
        private List<BusinessRule> GenerateBusinessRules(ProgramBlock programBlock)
        {
            var rules = new List<BusinessRule>();

            foreach (var network in programBlock.Networks)
            {
                var rule = new BusinessRule
                {
                    RuleId = $"{programBlock.Name}_Network_{network.Number}",
                    RuleName = $"{network.Title}",
                    Description = network.Comment,
                    Conditions = ExtractConditions(network),
                    Actions = ExtractActions(network),
                    Priority = network.Number,
                    IsEnabled = network.IsEnabled
                };

                rules.Add(rule);
            }

            return rules;
        }

        /// <summary>
        /// 生成控制流程
        /// </summary>
        private ControlFlowDescription GenerateControlFlow(ProgramBlock programBlock)
        {
            var controlFlow = new ControlFlowDescription
            {
                BlockName = programBlock.Name,
                FlowType = programBlock.Type,
                Steps = new List<ControlStep>()
            };

            // 检查是否为GRAPH类型
            var isGraphBlock = programBlock.Networks.Any(n => n.Language == "GRAPH");
            
            if (isGraphBlock)
            {
                // 处理GRAPH类型的控制流程
                controlFlow.FlowType = "GRAPH";
                GenerateGraphControlFlow(programBlock, controlFlow);
            }
            else
            {
                // 处理传统的网络控制流程
                GenerateNetworkControlFlow(programBlock, controlFlow);
            }

            return controlFlow;
        }

        /// <summary>
        /// 生成GRAPH控制流程
        /// </summary>
        private void GenerateGraphControlFlow(ProgramBlock programBlock, ControlFlowDescription controlFlow)
        {
            foreach (var network in programBlock.Networks)
            {
                if (network.Language != "GRAPH") continue;
                
                // 提取GRAPH步骤
                var steps = network.Elements.Where(e => e.Type == "GRAPH_Step").OrderBy(s => s.Name).ToList();
                var transitions = network.Elements.Where(e => e.Type == "GRAPH_Transition").ToList();
                
                // 为每个GRAPH步骤创建控制步骤
                foreach (var step in steps)
                {
                    var controlStep = new ControlStep
                    {
                        StepNumber = ExtractStepNumber(step.Name),
                        StepName = step.Name,
                        Description = step.Comment,
                        Conditions = new List<string>(),
                        Actions = new List<string>(),
                        NextSteps = new List<int>()
                    };
                    
                    // 添加步骤激活条件
                    if (step.IsInitialStep)
                    {
                        controlStep.Conditions.Add("系统启动");
                    }
                    else
                    {
                        // 查找进入此步骤的转移条件
                        var incomingTransitions = FindIncomingTransitions(step.Id, network);
                        foreach (var transition in incomingTransitions)
                        {
                            controlStep.Conditions.Add($"转移条件: {transition.Comment}");
                        }
                    }
                    
                    // 添加步骤动作
                    var stepActions = network.Elements.Where(e => e.Type == "GRAPH_Action" && e.Address == step.Name).ToList();
                    foreach (var action in stepActions)
                    {
                        controlStep.Actions.Add($"{action.Name}: {action.Comment}");
                    }
                    
                    // 查找此步骤的下一步骤
                    var outgoingTransitions = FindOutgoingTransitions(step.Id, network);
                    foreach (var transition in outgoingTransitions)
                    {
                        var nextStep = FindNextStep(transition.Id, network);
                        if (nextStep != null)
                        {
                            controlStep.NextSteps.Add(ExtractStepNumber(nextStep.Name));
                        }
                    }
                    
                    controlFlow.Steps.Add(controlStep);
                }
            }
        }

        /// <summary>
        /// 生成网络控制流程
        /// </summary>
        private void GenerateNetworkControlFlow(ProgramBlock programBlock, ControlFlowDescription controlFlow)
        {
            // 根据网络生成控制步骤
            foreach (var network in programBlock.Networks)
            {
                var step = new ControlStep
                {
                    StepNumber = network.Number,
                    StepName = network.Title,
                    Description = network.Comment,
                    Conditions = ExtractConditions(network),
                    Actions = ExtractActions(network),
                    NextSteps = new List<int>()
                };

                // 如果不是最后一个网络，下一步是下一个网络
                if (network.Number < programBlock.Networks.Count)
                {
                    step.NextSteps.Add(network.Number + 1);
                }

                controlFlow.Steps.Add(step);
            }
        }

        /// <summary>
        /// 提取步骤编号
        /// </summary>
        private int ExtractStepNumber(string stepName)
        {
            // 从步骤名称中提取数字，如 "S3" -> 3
            var match = System.Text.RegularExpressions.Regex.Match(stepName, @"\d+");
            if (match.Success && int.TryParse(match.Value, out int stepNumber))
            {
                return stepNumber;
            }
            return 0;
        }

        /// <summary>
        /// 查找进入指定步骤的转移
        /// </summary>
        private List<NetworkElement> FindIncomingTransitions(string stepId, NetworkInfo network)
        {
            var incomingTransitions = new List<NetworkElement>();
            
            // 查找指向此步骤的连接
            var incomingConnections = network.Connections.Where(c => c.ToElementId == stepId).ToList();
            
            foreach (var connection in incomingConnections)
            {
                var transition = network.Elements.FirstOrDefault(e => e.Id == connection.FromElementId && e.Type == "GRAPH_Transition");
                if (transition != null)
                {
                    incomingTransitions.Add(transition);
                }
            }
            
            return incomingTransitions;
        }

        /// <summary>
        /// 查找从指定步骤出发的转移
        /// </summary>
        private List<NetworkElement> FindOutgoingTransitions(string stepId, NetworkInfo network)
        {
            var outgoingTransitions = new List<NetworkElement>();
            
            // 查找从此步骤出发的连接
            var outgoingConnections = network.Connections.Where(c => c.FromElementId == stepId).ToList();
            
            foreach (var connection in outgoingConnections)
            {
                var transition = network.Elements.FirstOrDefault(e => e.Id == connection.ToElementId && e.Type == "GRAPH_Transition");
                if (transition != null)
                {
                    outgoingTransitions.Add(transition);
                }
            }
            
            return outgoingTransitions;
        }

        /// <summary>
        /// 查找转移的下一步骤
        /// </summary>
        private NetworkElement FindNextStep(string transitionId, NetworkInfo network)
        {
            // 查找从转移出发的连接
            var connection = network.Connections.FirstOrDefault(c => c.FromElementId == transitionId);
            if (connection != null)
            {
                return network.Elements.FirstOrDefault(e => e.Id == connection.ToElementId && e.Type == "GRAPH_Step");
            }
            
            return null;
        }

        /// <summary>
        /// 生成故障诊断分析报告
        /// </summary>
        public FaultDiagnosisReport GenerateFaultDiagnosisReport(ProgramBlock programBlock)
        {
            var report = new FaultDiagnosisReport
            {
                BlockName = programBlock.Name,
                BlockType = programBlock.Type,
                GeneratedTime = DateTime.Now,
                FaultPoints = new List<FaultPoint>(),
                DiagnosisRules = new List<DiagnosisRule>(),
                MonitoringPoints = new List<MonitoringPoint>()
            };

            try
            {
                // 分析每个网络的故障点
                foreach (var network in programBlock.Networks)
                {
                    var networkFaultPoints = AnalyzeNetworkFaultPoints(network);
                    report.FaultPoints.AddRange(networkFaultPoints);
                    
                    var networkDiagnosisRules = GenerateNetworkDiagnosisRules(network);
                    report.DiagnosisRules.AddRange(networkDiagnosisRules);
                    
                    var networkMonitoringPoints = ExtractNetworkMonitoringPoints(network);
                    report.MonitoringPoints.AddRange(networkMonitoringPoints);
                }

                // 生成综合分析
                report.Summary = GenerateFaultDiagnosisSummary(report);
                
                _logAction($"故障诊断分析完成: {report.FaultPoints.Count}个故障点, {report.DiagnosisRules.Count}个诊断规则");
            }
            catch (Exception ex)
            {
                _logAction($"生成故障诊断报告失败: {ex.Message}");
            }

            return report;
        }

        /// <summary>
        /// 分析网络故障点
        /// </summary>
        private List<FaultPoint> AnalyzeNetworkFaultPoints(NetworkInfo network)
        {
            var faultPoints = new List<FaultPoint>();

            try
            {
                // 分析GRAPH网络的故障点
                if (network.Language == "GRAPH")
                {
                    faultPoints.AddRange(AnalyzeGraphFaultPoints(network));
                }
                // 分析梯形图网络的故障点
                else if (network.Language == "LAD")
                {
                    faultPoints.AddRange(AnalyzeLadderFaultPoints(network));
                }
                // 分析功能块图网络的故障点
                else if (network.Language == "FBD")
                {
                    faultPoints.AddRange(AnalyzeFunctionBlockFaultPoints(network));
                }
                // 分析STL网络的故障点
                else if (network.Language == "STL")
                {
                    faultPoints.AddRange(AnalyzeSTLFaultPoints(network));
                }
            }
            catch (Exception ex)
            {
                _logAction($"分析网络故障点失败: {network.Number} - {ex.Message}");
            }

            return faultPoints;
        }

        /// <summary>
        /// 分析GRAPH故障点
        /// </summary>
        private List<FaultPoint> AnalyzeGraphFaultPoints(NetworkInfo network)
        {
            var faultPoints = new List<FaultPoint>();

            // 分析步骤卡死点
            var steps = network.Elements.Where(e => e.Type == "GRAPH_Step").ToList();
            foreach (var step in steps)
            {
                faultPoints.Add(new FaultPoint
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = $"{step.Name}_StepTimeout",
                    Description = $"步骤 {step.Name} 执行超时",
                    Type = FaultType.Timeout,
                    Severity = FaultSeverity.Medium,
                    Location = $"网络{network.Number}.{step.Name}",
                    MonitoringCondition = $"{step.Name}.T > MaxTime",
                    PossibleCauses = new List<string>
                    {
                        "转移条件不满足",
                        "输入信号异常",
                        "DB变量值错误",
                        "设备故障"
                    },
                    DiagnosisActions = new List<string>
                    {
                        $"检查 {step.Name} 的转移条件",
                        "检查相关输入信号状态",
                        "检查DB变量值",
                        "检查设备运行状态"
                    }
                });
            }

            // 分析转移条件故障点
            var transitions = network.Elements.Where(e => e.Type == "GRAPH_Transition").ToList();
            foreach (var transition in transitions)
            {
                faultPoints.Add(new FaultPoint
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = $"{transition.Name}_ConditionFault",
                    Description = $"转移条件 {transition.Name} 异常",
                    Type = FaultType.Logic,
                    Severity = FaultSeverity.High,
                    Location = $"网络{network.Number}.{transition.Name}",
                    MonitoringCondition = $"{transition.Name}.Condition = FALSE AND Expected = TRUE",
                    PossibleCauses = new List<string>
                    {
                        "DB变量值异常",
                        "输入信号故障",
                        "定时器故障",
                        "逻辑条件错误"
                    },
                    DiagnosisActions = new List<string>
                    {
                        $"检查 {transition.Comment} 中的所有条件",
                        "逐一验证DB变量值",
                        "检查输入信号状态",
                        "检查定时器状态"
                    }
                });
            }

            return faultPoints;
        }

        /// <summary>
        /// 分析梯形图故障点
        /// </summary>
        private List<FaultPoint> AnalyzeLadderFaultPoints(NetworkInfo network)
        {
            var faultPoints = new List<FaultPoint>();

            // 分析触点故障
            var contacts = network.Elements.Where(e => e.Type == "Contact").ToList();
            foreach (var contact in contacts)
            {
                faultPoints.Add(new FaultPoint
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = $"{contact.Address}_ContactFault",
                    Description = $"触点 {contact.Address} 信号异常",
                    Type = FaultType.Signal,
                    Severity = FaultSeverity.Medium,
                    Location = $"网络{network.Number}.{contact.Address}",
                    MonitoringCondition = $"{contact.Address} 状态异常",
                    PossibleCauses = new List<string>
                    {
                        "输入设备故障",
                        "接线松动",
                        "信号干扰",
                        "PLC输入模块故障"
                    },
                    DiagnosisActions = new List<string>
                    {
                        $"检查 {contact.Address} 对应的物理设备",
                        "检查接线是否牢固",
                        "测量信号电压",
                        "检查PLC输入模块状态"
                    }
                });
            }

            // 分析输出线圈故障
            var coils = network.Elements.Where(e => e.Type == "Coil").ToList();
            foreach (var coil in coils)
            {
                faultPoints.Add(new FaultPoint
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = $"{coil.Address}_CoilFault",
                    Description = $"输出线圈 {coil.Address} 异常",
                    Type = FaultType.Output,
                    Severity = FaultSeverity.High,
                    Location = $"网络{network.Number}.{coil.Address}",
                    MonitoringCondition = $"{coil.Address} 输出异常",
                    PossibleCauses = new List<string>
                    {
                        "输出设备故障",
                        "负载过载",
                        "PLC输出模块故障",
                        "保险丝熔断"
                    },
                    DiagnosisActions = new List<string>
                    {
                        $"检查 {coil.Address} 对应的负载设备",
                        "检查负载电流是否正常",
                        "检查PLC输出模块状态",
                        "检查保险丝状态"
                    }
                });
            }

            return faultPoints;
        }

        /// <summary>
        /// 分析功能块图故障点
        /// </summary>
        private List<FaultPoint> AnalyzeFunctionBlockFaultPoints(NetworkInfo network)
        {
            var faultPoints = new List<FaultPoint>();

            var functions = network.Elements.Where(e => e.Type == "Function").ToList();
            foreach (var function in functions)
            {
                // 根据功能块类型分析不同的故障点
                if (function.Properties.ContainsKey("FunctionType"))
                {
                    var functionType = function.Properties["FunctionType"].ToString();
                    
                    switch (functionType)
                    {
                        case "TON":
                        case "TOF":
                        case "TP":
                            faultPoints.Add(new FaultPoint
                            {
                                Id = Guid.NewGuid().ToString(),
                                Name = $"{function.Address}_TimerFault",
                                Description = $"定时器 {function.Address} 异常",
                                Type = FaultType.Timer,
                                Severity = FaultSeverity.Medium,
                                Location = $"网络{network.Number}.{function.Address}",
                                MonitoringCondition = $"{function.Address}.Q 状态异常",
                                PossibleCauses = new List<string>
                                {
                                    "定时器参数设置错误",
                                    "输入信号异常",
                                    "系统时钟故障"
                                },
                                DiagnosisActions = new List<string>
                                {
                                    "检查定时器预设值",
                                    "检查输入信号状态",
                                    "检查系统时钟"
                                }
                            });
                            break;
                            
                        case "PID":
                            faultPoints.Add(new FaultPoint
                            {
                                Id = Guid.NewGuid().ToString(),
                                Name = $"{function.Address}_PIDFault",
                                Description = $"PID控制器 {function.Address} 异常",
                                Type = FaultType.Control,
                                Severity = FaultSeverity.High,
                                Location = $"网络{network.Number}.{function.Address}",
                                MonitoringCondition = $"{function.Address} 输出异常",
                                PossibleCauses = new List<string>
                                {
                                    "PID参数设置不当",
                                    "过程值信号异常",
                                    "设定值错误",
                                    "执行器故障"
                                },
                                DiagnosisActions = new List<string>
                                {
                                    "检查PID参数设置",
                                    "检查过程值信号",
                                    "检查设定值",
                                    "检查执行器状态"
                                }
                            });
                            break;
                    }
                }
            }

            return faultPoints;
        }

        /// <summary>
        /// 分析STL故障点
        /// </summary>
        private List<FaultPoint> AnalyzeSTLFaultPoints(NetworkInfo network)
        {
            var faultPoints = new List<FaultPoint>();

            var instructions = network.Elements.Where(e => e.Type == "STL_Instruction").ToList();
            
            // 分析可能的逻辑错误
            faultPoints.Add(new FaultPoint
            {
                Id = Guid.NewGuid().ToString(),
                Name = $"Network{network.Number}_STLLogicFault",
                Description = $"STL逻辑执行异常",
                Type = FaultType.Logic,
                Severity = FaultSeverity.Medium,
                Location = $"网络{network.Number}",
                MonitoringCondition = "STL逻辑执行结果异常",
                PossibleCauses = new List<string>
                {
                    "指令序列错误",
                    "操作数地址错误",
                    "数据类型不匹配",
                    "栈溢出"
                },
                DiagnosisActions = new List<string>
                {
                    "检查STL指令序列",
                    "验证操作数地址",
                    "检查数据类型",
                    "检查栈状态"
                }
            });

            return faultPoints;
        }

        /// <summary>
        /// 生成网络诊断规则
        /// </summary>
        private List<DiagnosisRule> GenerateNetworkDiagnosisRules(NetworkInfo network)
        {
            var rules = new List<DiagnosisRule>();

            // 为GRAPH网络生成诊断规则
            if (network.Language == "GRAPH")
            {
                rules.AddRange(GenerateGraphDiagnosisRules(network));
            }
            // 为梯形图网络生成诊断规则
            else if (network.Language == "LAD")
            {
                rules.AddRange(GenerateLadderDiagnosisRules(network));
            }

            return rules;
        }

        /// <summary>
        /// 生成GRAPH诊断规则
        /// </summary>
        private List<DiagnosisRule> GenerateGraphDiagnosisRules(NetworkInfo network)
        {
            var rules = new List<DiagnosisRule>();

            var steps = network.Elements.Where(e => e.Type == "GRAPH_Step").ToList();
            var transitions = network.Elements.Where(e => e.Type == "GRAPH_Transition").ToList();

            // 步骤超时诊断规则
            foreach (var step in steps)
            {
                rules.Add(new DiagnosisRule
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = $"{step.Name}_TimeoutRule",
                    Description = $"步骤 {step.Name} 超时诊断",
                    Condition = $"{step.Name}.X = TRUE AND {step.Name}.T > MaxTime",
                    Action = $"报告步骤 {step.Name} 执行超时",
                    Priority = 5,
                    Category = "Timeout"
                });
            }

            // 转移条件诊断规则
            foreach (var transition in transitions)
            {
                rules.Add(new DiagnosisRule
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = $"{transition.Name}_ConditionRule",
                    Description = $"转移条件 {transition.Name} 诊断",
                    Condition = $"PreviousStep.X = TRUE AND {transition.Name}.Condition = FALSE FOR > 10s",
                    Action = $"报告转移条件 {transition.Name} 异常",
                    Priority = 8,
                    Category = "Logic"
                });
            }

            return rules;
        }

        /// <summary>
        /// 生成梯形图诊断规则
        /// </summary>
        private List<DiagnosisRule> GenerateLadderDiagnosisRules(NetworkInfo network)
        {
            var rules = new List<DiagnosisRule>();

            var contacts = network.Elements.Where(e => e.Type == "Contact").ToList();
            var coils = network.Elements.Where(e => e.Type == "Coil").ToList();

            // 输入信号诊断规则
            foreach (var contact in contacts)
            {
                rules.Add(new DiagnosisRule
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = $"{contact.Address}_SignalRule",
                    Description = $"输入信号 {contact.Address} 诊断",
                    Condition = $"{contact.Address} 状态异常持续时间 > 5s",
                    Action = $"报告输入信号 {contact.Address} 异常",
                    Priority = 6,
                    Category = "Signal"
                });
            }

            // 输出信号诊断规则
            foreach (var coil in coils)
            {
                rules.Add(new DiagnosisRule
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = $"{coil.Address}_OutputRule",
                    Description = $"输出信号 {coil.Address} 诊断",
                    Condition = $"{coil.Address} 输出与期望不符",
                    Action = $"报告输出信号 {coil.Address} 异常",
                    Priority = 7,
                    Category = "Output"
                });
            }

            return rules;
        }

        /// <summary>
        /// 提取网络监控点
        /// </summary>
        private List<MonitoringPoint> ExtractNetworkMonitoringPoints(NetworkInfo network)
        {
            var monitoringPoints = new List<MonitoringPoint>();

            // 提取所有输入输出信号作为监控点
            var inputSignals = ExtractInputSignals(network);
            var outputSignals = ExtractOutputSignals(network);

            foreach (var signal in inputSignals)
            {
                monitoringPoints.Add(new MonitoringPoint
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = signal,
                    Type = MonitoringType.Input,
                    Address = signal,
                    Description = $"输入信号 {signal} 监控",
                    DataType = GetSignalDataType(signal),
                    SamplingInterval = TimeSpan.FromMilliseconds(100),
                    ScanInterval = 100,
                    AlarmThreshold = "异常状态",
                    MinValue = 0,
                    MaxValue = 1,
                    Unit = "",
                    IsEnabled = true
                });
            }

            foreach (var signal in outputSignals)
            {
                monitoringPoints.Add(new MonitoringPoint
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = signal,
                    Type = MonitoringType.Output,
                    Address = signal,
                    Description = $"输出信号 {signal} 监控",
                    DataType = GetSignalDataType(signal),
                    SamplingInterval = TimeSpan.FromMilliseconds(100),
                    ScanInterval = 100,
                    AlarmThreshold = "异常状态",
                    MinValue = 0,
                    MaxValue = 1,
                    Unit = "",
                    IsEnabled = true
                });
            }

            // 为GRAPH网络添加步骤状态监控点
            if (network.Language == "GRAPH")
            {
                var steps = network.Elements.Where(e => e.Type == "GRAPH_Step").ToList();
                foreach (var step in steps)
                {
                    monitoringPoints.Add(new MonitoringPoint
                    {
                        Id = Guid.NewGuid().ToString(),
                        Name = $"{step.Name}_Status",
                        Type = MonitoringType.Status,
                        Address = $"{step.Name}.X",
                        Description = $"步骤 {step.Name} 状态监控",
                        DataType = "BOOL",
                        SamplingInterval = TimeSpan.FromMilliseconds(50),
                        ScanInterval = 50,
                        AlarmThreshold = "超时",
                        MinValue = 0,
                        MaxValue = 1,
                        Unit = "",
                        IsEnabled = true
                    });

                    monitoringPoints.Add(new MonitoringPoint
                    {
                        Id = Guid.NewGuid().ToString(),
                        Name = $"{step.Name}_Time",
                        Type = MonitoringType.Time,
                        Address = $"{step.Name}.T",
                        Description = $"步骤 {step.Name} 时间监控",
                        DataType = "TIME",
                        SamplingInterval = TimeSpan.FromMilliseconds(100),
                        ScanInterval = 100,
                        AlarmThreshold = "MaxTime",
                        MinValue = 0,
                        MaxValue = 3600000, // 1小时（毫秒）
                        Unit = "ms",
                        IsEnabled = true
                    });
                }
            }

            return monitoringPoints;
        }

        /// <summary>
        /// 获取信号数据类型
        /// </summary>
        private string GetSignalDataType(string signal)
        {
            // 根据信号地址判断数据类型
            if (signal.StartsWith("I") || signal.StartsWith("Q") || signal.StartsWith("M"))
            {
                if (signal.Contains("."))
                {
                    return "BOOL"; // 位信号
                }
                else
                {
                    return "WORD"; // 字信号
                }
            }
            else if (signal.StartsWith("DB"))
            {
                if (signal.Contains("DBX"))
                {
                    return "BOOL";
                }
                else if (signal.Contains("DBW"))
                {
                    return "WORD";
                }
                else if (signal.Contains("DBD"))
                {
                    return "DWORD";
                }
                else if (signal.Contains("DBB"))
                {
                    return "BYTE";
                }
            }
            else if (signal.StartsWith("T"))
            {
                return "TIME";
            }
            else if (signal.StartsWith("C"))
            {
                return "COUNTER";
            }
            
            return "UNKNOWN";
        }

        /// <summary>
        /// 生成故障诊断总结
        /// </summary>
        private string GenerateFaultDiagnosisSummary(FaultDiagnosisReport report)
        {
            var summary = new StringBuilder();
            
            summary.AppendLine($"=== 故障诊断分析总结 ===");
            summary.AppendLine($"程序块: {report.BlockName} ({report.BlockType})");
            summary.AppendLine($"分析时间: {report.GeneratedTime:yyyy-MM-dd HH:mm:ss}");
            summary.AppendLine();
            
            summary.AppendLine($"故障点统计:");
            var faultTypeGroups = report.FaultPoints.GroupBy(f => f.Type);
            foreach (var group in faultTypeGroups)
            {
                summary.AppendLine($"  {group.Key}: {group.Count()} 个");
            }
            summary.AppendLine();
            
            summary.AppendLine($"严重级别统计:");
            var severityGroups = report.FaultPoints.GroupBy(f => f.Severity);
            foreach (var group in severityGroups)
            {
                summary.AppendLine($"  {group.Key}: {group.Count()} 个");
            }
            summary.AppendLine();
            
            summary.AppendLine($"监控点统计:");
            var monitoringTypeGroups = report.MonitoringPoints.GroupBy(m => m.Type);
            foreach (var group in monitoringTypeGroups)
            {
                summary.AppendLine($"  {group.Key}: {group.Count()} 个");
            }
            summary.AppendLine();
            
            summary.AppendLine($"诊断规则: {report.DiagnosisRules.Count} 条");
            summary.AppendLine();
            
            summary.AppendLine("建议:");
            summary.AppendLine("1. 定期检查高严重级别的故障点");
            summary.AppendLine("2. 建立实时监控系统监控关键信号");
            summary.AppendLine("3. 制定故障处理预案");
            summary.AppendLine("4. 定期维护和校验设备");
            
            return summary.ToString();
        }
    }

    // 业务逻辑描述相关的数据模型
    public class BusinessLogicDescription
    {
        public string BlockName { get; set; } = string.Empty;
        public string BlockType { get; set; } = string.Empty;
        public int BlockNumber { get; set; }
        public string Language { get; set; } = string.Empty;
        public string FolderPath { get; set; } = string.Empty;
        public string Comment { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public DateTime CreatedTime { get; set; }
        public DateTime ModifiedTime { get; set; }
        
        public List<NetworkLogicDescription> NetworkLogics { get; set; } = new List<NetworkLogicDescription>();
        public List<ParameterDescription> InputParameters { get; set; } = new List<ParameterDescription>();
        public List<ParameterDescription> OutputParameters { get; set; } = new List<ParameterDescription>();
        public List<VariableDescription> StaticVariables { get; set; } = new List<VariableDescription>();
        public List<BusinessRule> BusinessRules { get; set; } = new List<BusinessRule>();
        public ControlFlowDescription ControlFlow { get; set; } = new ControlFlowDescription();
    }

    public class NetworkLogicDescription
    {
        public int NetworkNumber { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Comment { get; set; } = string.Empty;
        public string Language { get; set; } = string.Empty;
        public bool IsEnabled { get; set; }
        public string LogicDescription { get; set; } = string.Empty;
        public List<string> InputSignals { get; set; } = new List<string>();
        public List<string> OutputSignals { get; set; } = new List<string>();
        public List<string> Conditions { get; set; } = new List<string>();
        public List<string> Actions { get; set; } = new List<string>();
    }

    public class ParameterDescription
    {
        public string Name { get; set; } = string.Empty;
        public string DataType { get; set; } = string.Empty;
        public string Direction { get; set; } = string.Empty;
        public string Comment { get; set; } = string.Empty;
        public string DefaultValue { get; set; } = string.Empty;
    }

    public class VariableDescription
    {
        public string Name { get; set; } = string.Empty;
        public string DataType { get; set; } = string.Empty;
        public string Comment { get; set; } = string.Empty;
        public string InitialValue { get; set; } = string.Empty;
        public bool IsRetentive { get; set; }
    }

    public class BusinessRule
    {
        public string RuleId { get; set; } = string.Empty;
        public string RuleName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public List<string> Conditions { get; set; } = new List<string>();
        public List<string> Actions { get; set; } = new List<string>();
        public int Priority { get; set; }
        public bool IsEnabled { get; set; }
    }

    public class ControlFlowDescription
    {
        public string BlockName { get; set; } = string.Empty;
        public string FlowType { get; set; } = string.Empty;
        public List<ControlStep> Steps { get; set; } = new List<ControlStep>();
    }

    public class ControlStep
    {
        public int StepNumber { get; set; }
        public string StepName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public List<string> Conditions { get; set; } = new List<string>();
        public List<string> Actions { get; set; } = new List<string>();
        public List<int> NextSteps { get; set; } = new List<int>();
    }

    // 故障诊断相关的数据模型
    public class FaultDiagnosisReport
    {
        public string BlockName { get; set; } = string.Empty;
        public string BlockType { get; set; } = string.Empty;
        public DateTime GeneratedTime { get; set; }
        public List<FaultPoint> FaultPoints { get; set; } = new List<FaultPoint>();
        public List<DiagnosisRule> DiagnosisRules { get; set; } = new List<DiagnosisRule>();
        public List<MonitoringPoint> MonitoringPoints { get; set; } = new List<MonitoringPoint>();
        public string Summary { get; set; } = string.Empty;
    }
} 