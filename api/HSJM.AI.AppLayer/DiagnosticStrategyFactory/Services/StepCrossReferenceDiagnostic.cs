using HSJM.AI.AppLayer.Agent.Services;
using HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.Utility;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using SqlSugar;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using static HSJM.AI.Utility.CommonRedisHelper;

namespace HSJM.AI.AppLayer.DiagnosticStrategyFactory.Services
{
    /// <summary>
    /// 步骤交叉引用诊断实现
    /// 类似TIA Portal的交叉引用功能，分析变量在整个项目中的使用情况
    /// </summary>
    public class StepCrossReferenceDiagnostic : IStepCrossReferenceDiagnostic, IAutoInject
    {
        private readonly ILogger<StepCrossReferenceDiagnostic> _logger;
        private readonly IAITransitionService _aiTransitionService;
        private readonly RequestToHttpHelper _httpHelper;
        private readonly AgentRuntimeService _agentRuntimeService;

        // 🆕 Agent缓存（按RoleId缓存）
        private readonly Dictionary<long, ChatClientAgent> _agentCache = new();
        private readonly SemaphoreSlim _cacheLock = new(1, 1);

        /// <summary>
        /// 步骤交叉引用诊断服务构造函数
        /// </summary>
        /// <param name="logger">日志记录器</param>
        /// <param name="connectionService">连接表服务（查询产线/工站连接关系）</param>
        /// <param name="stepService">步骤表服务（查询工站步骤）</param>
        /// <param name="transitionService">Transition表服务（查询步骤转换条件）</param>
        /// <param name="aITransitionService">AI Transition服务（AI解析转换条件）</param>
        /// <param name="agentRuntimeService">AI Agent运行时服务（调用LLM）</param>
        /// <param name="httpHelper">HTTP请求辅助类（获取实时信号）</param>
        public StepCrossReferenceDiagnostic(
            ILogger<StepCrossReferenceDiagnostic> logger,
            IAITransitionService aITransitionService,
            AgentRuntimeService agentRuntimeService,
            RequestToHttpHelper httpHelper)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _aiTransitionService = aITransitionService ?? throw new ArgumentNullException(nameof(aITransitionService));
            _httpHelper = httpHelper ?? throw new ArgumentNullException(nameof(httpHelper));
            _agentRuntimeService = agentRuntimeService ?? throw new ArgumentNullException(nameof(agentRuntimeService));

            // 验证 AI Agent 配置
            if (AppsettingsConfig.AIAgent == null)
            {
                throw new InvalidOperationException("AIAgent 配置未初始化，请检查 appsettings.json");
            }

            _logger.LogInformation("[AI配置] Agent 最大迭代: {MaxIterations}",
                AppsettingsConfig.AIAgent.MaxIterations);
        }

        /// <summary>
        /// 获取或创建缓存的Agent（线程安全）
        /// </summary>
        private async Task<ChatClientAgent> GetOrCreateAgentAsync(long roleId)
        {
            // 快速路径：如果已缓存，直接返回
            if (_agentCache.TryGetValue(roleId, out var cachedAgent))
            {
                _logger.LogDebug("[Agent缓存] 命中缓存: RoleId={RoleId}", roleId);
                return cachedAgent;
            }

            // 慢速路径：需要创建新Agent
            await _cacheLock.WaitAsync();
            try
            {
                // 双重检查：防止并发创建
                if (_agentCache.TryGetValue(roleId, out cachedAgent))
                {
                    return cachedAgent;
                }

                _logger.LogInformation("[Agent缓存] 创建新Agent: RoleId={RoleId}", roleId);
                var newAgent = await _agentRuntimeService.CreateAgentByRoleAsync(roleId);
                _agentCache[roleId] = newAgent;
                return newAgent;
            }
            finally
            {
                _cacheLock.Release();
            }
        }

        /// <summary>
        /// 清理Agent缓存（在需要刷新时调用）
        /// </summary>
        public void ClearAgentCache()
        {
            _logger.LogInformation("[Agent缓存] 清理缓存，共 {Count} 个Agent", _agentCache.Count);
            _agentCache.Clear();
        }

    

        /// <summary>
        /// 从XML中提取变量
        /// </summary>
        private List<string> ExtractVariablesFromXml(string xml)
        {
            var variables = new HashSet<string>();

            // 正则表达式匹配PLC变量
            var patterns = new[]
            {
                @"DB\d+\.DB[BWDX]\d+",      // DB变量
                @"[MIQV]\d+\.\d+",           // M/I/Q/V变量
                @"#\w+",                      // 临时变量
                @"Variable=['""]([^'""]+)['""]" // XML属性中的变量
            };

            foreach (var pattern in patterns)
            {
                var matches = Regex.Matches(xml, pattern, RegexOptions.IgnoreCase);
                foreach (Match match in matches)
                {
                    var variable = match.Value;
                    if (variable.StartsWith("Variable="))
                    {
                        variable = match.Groups[1].Value;
                    }
                    variables.Add(variable);
                }
            }

            return variables.ToList();
        }

        /// <summary>
        /// 分析失败的条件（增强版：结合AI的期望值推断）
        /// </summary>
        private List<FailedCondition> AnalyzeFailedConditionsWithAI(
            string transitionXml,
            Dictionary<string, string> actualValues,
            List<string> variables,
            AIStructureAnalysis? aiAnalysis)
        {
            var failedConditions = new List<FailedCondition>();

            foreach (var variable in variables)
            {
                if (actualValues.TryGetValue(variable, out var actualValue))
                {
                    // 优先使用AI推断的期望值
                    string expectedValue = "?";
                    if (aiAnalysis?.ExpectedValueRanges != null &&
                        aiAnalysis.ExpectedValueRanges.TryGetValue(variable, out var aiExpected))
                    {
                        expectedValue = aiExpected;
                    }
                    else
                    {
                        // 回退到正则提取
                        var constantMatch = System.Text.RegularExpressions.Regex.Match(transitionXml,
                            $@"<Input\s+Name=['""]IN2['""][^>]*Constant=['""]([^'""]+)['""]",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                        if (constantMatch.Success)
                        {
                            expectedValue = constantMatch.Groups[1].Value;
                        }
                    }

                    failedConditions.Add(new FailedCondition
                    {
                        Variable = variable,
                        ExpectedValue = expectedValue,
                        ActualValue = actualValue,
                        Operator = "满足条件",
                        Reason = $"变量 {variable} 的值 {actualValue} 不满足期望条件 {expectedValue}"
                    });
                }
                else
                {
                    failedConditions.Add(new FailedCondition
                    {
                        Variable = variable,
                        ExpectedValue = aiAnalysis?.ExpectedValueRanges?.GetValueOrDefault(variable, "?") ?? "?",
                        ActualValue = "缺失",
                        Operator = "?",
                        Reason = $"变量 {variable} 的值未提供"
                    });
                }
            }

            return failedConditions;
        }

        /// <summary>
        /// 分析失败的条件（保留原方法兼容性）
        /// </summary>
        private List<FailedCondition> AnalyzeFailedConditions(
            string transitionXml,
            Dictionary<string, string> actualValues,
            List<string> variables)
        {
            var failedConditions = new List<FailedCondition>();

            foreach (var variable in variables)
            {
                if (actualValues.TryGetValue(variable, out var actualValue))
                {
                    // 尝试从XML中提取期望值
                    var constantMatch = Regex.Match(transitionXml,
                        $@"<Input\s+Name=['""]IN2['""][^>]*Constant=['""]([^'""]+)['""]",
                        RegexOptions.IgnoreCase);

                    if (constantMatch.Success)
                    {
                        var expectedValue = constantMatch.Groups[1].Value;

                        failedConditions.Add(new FailedCondition
                        {
                            Variable = variable,
                            ExpectedValue = expectedValue,
                            ActualValue = actualValue,
                            Operator = "满足条件",
                            Reason = $"变量 {variable} 的值 {actualValue} 不满足期望条件"
                        });
                    }
                }
                else
                {
                    failedConditions.Add(new FailedCondition
                    {
                        Variable = variable,
                        ExpectedValue = "?",
                        ActualValue = "缺失",
                        Operator = "?",
                        Reason = $"变量 {variable} 的值未提供"
                    });
                }
            }

            return failedConditions;
        }

    
        /// <summary>
        /// 获取变量类型
        /// </summary>
        private string GetVariableType(string variableName)
        {
            if (variableName.StartsWith("DB")) return "DB块";
            if (variableName.StartsWith("M")) return "标记位";
            if (variableName.StartsWith("I")) return "输入";
            if (variableName.StartsWith("Q")) return "输出";
            if (variableName.StartsWith("#")) return "临时变量";
            return "未知";
        }

        /// <summary>
        /// 根因分析（从诊断上下文）
        /// </summary>
        private RootCauseAnalysis AnalyzeRootCauseFromContext(DiagnosticContext context)
        {
            var rootCause = new RootCauseAnalysis
            {
                Confidence = 0.8
            };

            // 找到所有失败的条件
            var allFailedConditions = context.TransitionAnalyses
                .SelectMany(t => t.FailedConditions)
                .ToList();

            if (!allFailedConditions.Any())
            {
                rootCause.Description = "没有找到明确的失败条件";
                rootCause.Confidence = 0.3;
                return rootCause;
            }

            // 找到最可能的根因变量（出现次数最多的）
            var variableFrequency = allFailedConditions
                .GroupBy(fc => fc.Variable)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault();

            if (variableFrequency != null)
            {
                rootCause.RootCauseVariable = variableFrequency.Key;
                rootCause.Description = $"变量 {variableFrequency.Key} 在 {variableFrequency.Count()} 个条件中不满足";

                // 分析可能的原因
                var crossRef = context.CrossReferences
                    .FirstOrDefault(cr => cr.VariableName == variableFrequency.Key);

                if (crossRef != null && crossRef.References.Any())
                {
                    rootCause.PossibleCauses.Add(new PossibleCause
                    {
                        Cause = $"变量在 {crossRef.References.Count} 个位置被引用，可能上游逻辑未正确设置该值",
                        RelatedLocation = string.Join(", ", crossRef.References.Take(3).Select(r => r.Location)),
                        Probability = 0.7
                    });
                }
                else
                {
                    rootCause.PossibleCauses.Add(new PossibleCause
                    {
                        Cause = "未找到变量的其他引用，可能是硬件输入或外部PLC写入",
                        Probability = 0.6
                    });
                }

                // 建议的解决方案
                rootCause.SuggestedSolutions.Add($"检查 {rootCause.RootCauseVariable} 的值来源");
                rootCause.SuggestedSolutions.Add("检查相关的传感器或硬件设备是否正常");
                rootCause.SuggestedSolutions.Add("检查上游工步的动作是否正确执行");
                rootCause.SuggestedSolutions.Add("使用PLC监控工具实时查看变量值变化");
            }

            return rootCause;
        }

     
        /// <summary>
        /// 构建诊断系统提示词
        /// </summary>
        private string BuildDiagnosticSystemPrompt()
        {
            return @"你是一个工业自动化系统的高级智能诊断 Agent。

# 你的任务
对卡住的步骤进行全面诊断，包括：
1. 找到卡住的步骤
2. 分析所有出边转换条件
3. 进行变量交叉引用分析
4. 执行根因分析
5. 生成完整的诊断报告

# 重要：Redis 数据结构说明

Redis Key 格式：
prod:block:{产线}:{工站}:{工步}:{动作}:{ActionAddress}

关键点：
- Redis Key 的最后一段（Block 字段）存储的是 ActionAddress（动作地址）
- ActionAddress 是步骤的唯一标识符
- 你需要使用 ActionAddress 来查询数据库中的步骤

# 查询步骤的正确流程

方法 1：通过 ActionAddress 查询（推荐）
1. 从 Redis 数据中提取 Block 字段的值（这就是 ActionAddress）
2. 使用 query_step_table 工具，传入 action_address 参数

方法 2：尝试解析为 StepId（备用）
- 如果 Block 字段是纯数字，也可能是 StepId
- 使用 validate_step_exists 验证

# 可用工具

## 数据获取工具
- query_redis_blocks: 查询 Redis 中的块值数据
- query_step_table: 查询步骤表，获取步骤信息
  重要参数: action_address - 用于通过 ActionAddress 查询步骤
- query_connection_table: 查询连接表，获取步骤间关系
- query_transition_table: 查询转换表，获取转换逻辑
- validate_step_exists: 批量验证步骤是否存在
- get_step_connections: 获取步骤的所有出边连接

## 分析工具
- analyze_transition_condition: 分析单个转换条件是否满足
- analyze_variable_cross_reference: 分析变量的交叉引用
- perform_root_cause_analysis: 执行根因分析

# 诊断流程建议

阶段1：定位步骤 (必须)
1. 分析 Redis 数据，提取 ActionAddress
2. 使用 query_step_table 通过 action_address 查询步骤
3. 验证步骤存在且有效

阶段2：分析转换 (推荐)
1. 使用 get_step_connections 获取所有出边
2. 对每个连接调用 analyze_transition_condition
3. 识别哪些转换失败了

阶段3：交叉引用 (可选)
1. 从失败的转换中提取变量
2. 对关键变量调用 analyze_variable_cross_reference
3. 找出变量使用的异常模式

阶段4：根因分析 (推荐)
1. 收集所有失败的转换
2. 调用 perform_root_cause_analysis
3. 确定最可能的根本原因

# 输出格式

最终输出步骤 ID（纯数字），我会自动查询完整信息并生成报告。
如果失败，输出 NULL。

# 重要提示
- 优先使用 ActionAddress 查询，这是最可靠的方法
- 你可以自由决定调用哪些工具
- 可以多次调用同一个工具
- 如果某个工具失败，尝试其他方法
- 优先使用简单方法，必要时才使用复杂分析";
        }

        /// <summary>
        /// 构建诊断用户提示词
        /// </summary>
        private string BuildDiagnosticUserPrompt(
            List<BlockValueModel> redisData, 
            StepCrossReferenceDiagnosticRequestDto request)
        {
            var dataJson = JsonSerializer.Serialize(redisData, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });

            return $@"# 诊断请求

## Redis 数据
以下是从 Redis 查询到的块值数据：
```json
{dataJson}
```

## 查询条件（用户指定的过滤条件）
- **产线代码**: {request.ProductionLineCode ?? "未指定（查询所有产线）"}
- **工站代码**: {request.WorkstationCode ?? "未指定（查询所有工站）"}
- **工步代码**: {request.StepCode ?? "未指定（查询所有工步）"}
- **动作代码**: {request.ActionCode ?? "未指定（查询所有动作，如Run/Stop/Pause等）"}
- **块标识**: {request.ActionAddress ?? "未指定（查询所有块）"}
- **卡住状态值**: {request.StuckStatusValue ?? "未指定"}（在 Redis Value 字段中，此值表示步骤卡住）

## 数据说明
- Redis Key 格式: `prod:block:{{产线}}:{{工站}}:{{工步}}:{{动作}}:{{块}}`
- Value 字段: 表示当前状态值，通常 '{request.StuckStatusValue}' 表示卡住状态
- Block 字段: 可能包含步骤 ID 或其他标识符

## 诊断任务
请分析上述 Redis 数据，使用数据库工具验证并找到卡住的步骤。

### 分析步骤建议：
1. 从 Redis 数据中提取可能的步骤 ID（通常在 Block 字段中）
2. 使用 `validate_step_exists` 工具验证这些 ID 是否在数据库中存在
3. 如果 Block 不是有效 ID，尝试使用 `query_step_table` 根据产线、工站、工步等条件查询
4. 返回找到的有效步骤 ID（纯数字）

你可以自由调用任何需要的工具。";
        }

        /// <summary>
        /// 定义所有数据库查询工具
        /// </summary>
        private List<AIToolDefinition> DefineAllDatabaseTools()
        {
            return new List<AIToolDefinition>
            {
                // ========== Redis 查询工具 ==========
                new AIToolDefinition
                {
                    Name = "query_redis_blocks",
                    Description = "从 Redis 查询块值数据，用于获取卡住步骤的候选信息",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            line = new { type = "string", description = "产线代码（可选）" },
                            station = new { type = "string", description = "工站代码（可选）" },
                            step = new { type = "string", description = "工步代码（可选）" },
                            action = new { type = "string", description = "动作代码（可选）" },
                            block = new { type = "string", description = "块标识（可选）" },
                            status_value = new { type = "string", description = "状态值，如'1'表示卡住" }
                        }
                    }
                },

                // ========== 数据库查询工具 ==========
                // Step 表查询工具
                new AIToolDefinition
                {
                    Name = "query_step_table",
                    Description = "查询步骤表（Step），支持多种查询条件，包括通过 ActionAddress 查询",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            step_id = new { type = "number", description = "步骤ID" },
                            production_line = new { type = "string", description = "产线代码" },
                            step_name = new { type = "string", description = "步骤名称" },
                            step_number = new { type = "number", description = "步骤编号" },
                            action_address = new { type = "string", description = "动作地址，Redis Block 字段中的值" },
                            limit = new { type = "number", description = "返回结果数量限制，默认10" }
                        }
                    }
                },

                // Connection 表查询工具
                new AIToolDefinition
                {
                    Name = "query_connection_table",
                    Description = "查询连接表（Connection），查询步骤之间的连接关系",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            node_from = new { type = "number", description = "源节点编号" },
                            node_to = new { type = "number", description = "目标节点编号" },
                            node_from_type = new { type = "number", description = "源节点类型（1=Step, 2=Transition）" },
                            node_to_type = new { type = "number", description = "目标节点类型（1=Step, 2=Transition）" },
                            limit = new { type = "number", description = "返回结果数量限制，默认10" }
                        }
                    }
                },

                // Transition 表查询工具
                new AIToolDefinition
                {
                    Name = "query_transition_table",
                    Description = "查询转换表（Transition），获取转换条件和逻辑",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            transition_number = new { type = "number", description = "转换编号" },
                            transition_name = new { type = "string", description = "转换名称" },
                            limit = new { type = "number", description = "返回结果数量限制，默认10" }
                        }
                    }
                },

                // 辅助工具
                new AIToolDefinition
                {
                    Name = "validate_step_exists",
                    Description = "批量验证步骤是否存在",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            step_ids = new
                            {
                                type = "array",
                                items = new { type = "number" },
                                description = "步骤ID列表"
                            }
                        },
                        required = new[] { "step_ids" }
                    }
                },

                new AIToolDefinition
                {
                    Name = "get_step_connections",
                    Description = "获取步骤的所有出边连接",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            step_number = new { type = "number", description = "步骤编号" }
                        },
                        required = new[] { "step_number" }
                    }
                },

                // ========== 分析工具 ==========
                new AIToolDefinition
                {
                    Name = "analyze_transition_condition",
                    Description = "分析转换条件是否满足，验证变量值",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            connection_id = new { type = "number", description = "连接ID" },
                            actual_values = new 
                            { 
                                type = "object",
                                description = "实际变量值字典，格式: {\"变量名\": \"值\"}"
                            }
                        },
                        required = new[] { "connection_id" }
                    }
                },

                new AIToolDefinition
                {
                    Name = "analyze_variable_cross_reference",
                    Description = "分析变量的交叉引用，查找变量在哪些地方被使用",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            variable_name = new { type = "string", description = "变量名" },
                            current_value = new { type = "string", description = "当前值" },
                            production_line = new { type = "string", description = "产线代码" },
                            depth = new { type = "number", description = "分析深度，默认1" }
                        },
                        required = new[] { "variable_name" }
                    }
                },

                new AIToolDefinition
                {
                    Name = "perform_root_cause_analysis",
                    Description = "根据失败的条件进行根因分析，找出最可能的原因",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            failed_transitions = new 
                            { 
                                type = "array",
                                description = "失败的转换ID列表"
                            }
                        },
                        required = new[] { "failed_transitions" }
                    }
                }
            };
        }

  
        /*
         * ❌ 旧的实现代码已废弃
         * 新版使用 AgentRuntimeService 简化调用，不再需要手动管理多轮对话
         * 如果需要多轮工具调用，应该使用 AgentRuntimeService.RunAgentWithReActAsync
         */

        /// <summary>
        /// 执行数据库查询工具
        /// </summary>
        private async Task<string> ExecuteDatabaseToolAsync(AIToolCall toolCall)
        {
            _logger.LogInformation("[工具执行] 执行工具: {Tool}", toolCall.Name);

            try
            {
                return toolCall.Name switch
                {
                 
                    _ => JsonSerializer.Serialize(new { error = "Unknown tool", tool_name = toolCall.Name })
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[工具执行] 工具执行失败: {Tool}", toolCall.Name);
                return JsonSerializer.Serialize(new { error = ex.Message, stack_trace = ex.StackTrace });
            }
        }

        #region 数据库查询工具实现


   


    

        /// <summary>
        /// 工具：Redis 查询块值
        /// </summary>
        private async Task<string> Tool_QueryRedisBlocks(JsonElement arguments)
        {
            try
            {
                _logger.LogDebug("[工具] 查询Redis块值");

                // 构建查询条件
                var query = new CommonRedisHelper.BlockQueryCondition();

                if (arguments.TryGetProperty("line", out var lineEl))
                    query.Line = lineEl.GetString();

                if (arguments.TryGetProperty("station", out var stationEl))
                    query.Station = stationEl.GetString();

                if (arguments.TryGetProperty("step", out var stepEl))
                    query.Step = stepEl.GetString();

                if (arguments.TryGetProperty("action", out var actionEl))
                    query.Action = actionEl.GetString();

                if (arguments.TryGetProperty("action_address", out var actionAddressEl))
                    query.ActionAddress = actionAddressEl.GetString();

                if (arguments.TryGetProperty("status_value", out var statusEl))
                    query.StringValue = statusEl.GetString();

                // 查询 Redis
                var results = await CommonRedisHelper.GetBlockValuesByScan(query);

                if (results == null || !results.Any())
                {
                    return JsonSerializer.Serialize(new 
                    { 
                        success = true,
                        count = 0,
                        message = "No data found in Redis",
                        blocks = new List<object>()
                    });
                }

                return JsonSerializer.Serialize(new
                {
                    success = true,
                    count = results.Count,
                    blocks = results.Select(r => new
                    {
                        block_key = r.BlockKey,
                        line = r.Line,
                        station = r.Station,
                        step = r.Step,
                        action = r.Action,
                        block = r.Block,
                        value = r.Value
                    })
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[工具] Redis查询失败");
                return JsonSerializer.Serialize(new 
                { 
                    success = false,
                    error = ex.Message
                });
            }
        }





#endregion

/// <summary>
/// 从Redis获取卡住的步骤
/// </summary>
/// <param name="request">诊断请求</param>
/// <returns>Redis数据列表，如果未找到则返回null</returns>
private async Task<List<BlockValueModel>> GetStuckStepIdFromRedisAsync(StepCrossReferenceDiagnosticRequestDto request)
        {
            try
            {
                // 构建Redis查询条件（包含所有可用字段）
                var redisQuery = new CommonRedisHelper.BlockQueryCondition
                {
                    Line = request.ProductionLineCode,
                    Station = request.WorkstationCode,
                    Step = request.StepCode,
                    Action = request.ActionCode,          // ✅ 添加动作代码
                    ActionAddress = request.ActionAddress,                // ✅ 添加动作地址
                    StringValue = request.StuckStatusValue // ✅ 添加状态值过滤
                };

                _logger.LogDebug("[Redis查询] 查询条件: Line={Line}, Station={Station}, Step={Step}, Action={Action}, ActionAddress={ActionAddress}, StatusValue={StatusValue}",
                    request.ProductionLineCode, request.WorkstationCode, request.StepCode, 
                    request.ActionCode, request.ActionAddress, request.StuckStatusValue);

                // 使用SCAN命令查询Redis（生产环境推荐）
                var redisResults = await CommonRedisHelper.GetBlockValuesByScan(redisQuery);

                _logger.LogInformation("[Redis查询] 查询到 {Count} 条记录", redisResults?.Count ?? 0);

                return redisResults;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Redis查询] 查询失败");
                return null;
            }
        }


/// <summary>
/// 生成诊断总结
/// </summary>
private string GenerateDiagnosticSummary(StepCrossReferenceDiagnosticResponseDto response)
        {
            var sb = new StringBuilder();

            sb.AppendLine($"🔍 步骤交叉引用诊断总结");
            sb.AppendLine(new string('=', 60));
            sb.AppendLine();

            sb.AppendLine($"📍 卡住的步骤: {response.StuckStep?.StepName} (ID: {response.StuckStep?.StepId})");
            sb.AppendLine($"   类型: {response.StuckStep?.StepType}");
            sb.AppendLine();

            // 转换条件
            sb.AppendLine($"📊 出边转换数量: {response.OutgoingTransitions.Count}");
            var passedCount = response.OutgoingTransitions.Count(t => t.IsPassed);
            sb.AppendLine($"   ✅ 通过: {passedCount}");
            sb.AppendLine($"   ❌ 未通过: {response.OutgoingTransitions.Count - passedCount}");
            sb.AppendLine();

            // 失败的转换
            var failedTransitions = response.OutgoingTransitions.Where(t => !t.IsPassed).ToList();
            if (failedTransitions.Any())
            {
                sb.AppendLine("❌ 未通过的转换:");
                foreach (var trans in failedTransitions)
                {
                    sb.AppendLine($"   • {trans.TransitionName} → {trans.TargetStepName}");
                    foreach (var failed in trans.FailedConditions)
                    {
                        sb.AppendLine($"     - {failed.Variable}: 期望满足条件, 实际 {failed.ActualValue}");
                    }
                }
                sb.AppendLine();
            }

            // 交叉引用
            if (response.VariableCrossReferences.Any())
            {
                sb.AppendLine($"🔗 变量交叉引用分析: {response.VariableCrossReferences.Count} 个变量");
                foreach (var crossRef in response.VariableCrossReferences)
                {
                    sb.AppendLine($"   • {crossRef.VariableName} ({crossRef.VariableType}) = {crossRef.CurrentValue}");
                    sb.AppendLine($"     引用次数: {crossRef.References.Count}");
                    if (crossRef.HasAnomalies)
                    {
                        sb.AppendLine($"     ⚠️ {crossRef.AnomalyDescription}");
                    }
                }
                sb.AppendLine();
            }

            // 根因分析
            if (response.RootCause != null && !string.IsNullOrEmpty(response.RootCause.RootCauseVariable))
            {
                sb.AppendLine("💡 根因分析:");
                sb.AppendLine($"   根本原因变量: {response.RootCause.RootCauseVariable}");
                sb.AppendLine($"   描述: {response.RootCause.Description}");
                sb.AppendLine($"   置信度: {response.RootCause.Confidence:P0}");
                sb.AppendLine();

                if (response.RootCause.PossibleCauses.Any())
                {
                    sb.AppendLine("   可能的原因:");
                    foreach (var cause in response.RootCause.PossibleCauses)
                    {
                        sb.AppendLine($"   • {cause.Cause}");
                        if (!string.IsNullOrEmpty(cause.RelatedLocation))
                        {
                            sb.AppendLine($"     相关位置: {cause.RelatedLocation}");
                        }
                        sb.AppendLine($"     可能性: {cause.Probability:P0}");
                    }
                    sb.AppendLine();
                }

                if (response.RootCause.SuggestedSolutions.Any())
                {
                    sb.AppendLine("   💡 建议的解决方案:");
                    for (int i = 0; i < response.RootCause.SuggestedSolutions.Count; i++)
                    {
                        sb.AppendLine($"   {i + 1}. {response.RootCause.SuggestedSolutions[i]}");
                    }
                }
            }

            return sb.ToString();
        }

        public Task<StepCrossReferenceDiagnosticResponseDto> DiagnoseStepWithCrossReferenceAsync(StepCrossReferenceDiagnosticRequestDto request, long? diagnosisRoleId = null)
        {
            throw new NotImplementedException();
        }

        #region AI工具定义模型

        /// <summary>
        /// 诊断上下文 - 存储诊断过程中的所有数据
        /// </summary>
        public class DiagnosticContext
        {
         

            /// <summary>
            /// 转换分析结果列表
            /// </summary>
            public List<TransitionConditionAnalysis> TransitionAnalyses { get; set; } = new();

            /// <summary>
            /// 交叉引用分析结果列表
            /// </summary>
            public List<VariableCrossReference> CrossReferences { get; set; } = new();

            /// <summary>
            /// 根因分析结果
            /// </summary>
            public RootCauseAnalysis RootCause { get; set; }

            /// <summary>
            /// 元数据字典 - 存储诊断过程中的额外信息
            /// </summary>
            public Dictionary<string, object> Metadata { get; set; } = new();

            /// <summary>
            /// 添加元数据
            /// </summary>
            public void AddMetadata(string key, object value)
            {
                Metadata[key] = value;
            }

            /// <summary>
            /// 获取元数据
            /// </summary>
            public T GetMetadata<T>(string key, T defaultValue = default)
            {
                if (Metadata.TryGetValue(key, out var value) && value is T typedValue)
                {
                    return typedValue;
                }
                return defaultValue;
            }
        }

        /// <summary>
        /// AI工具定义
        /// </summary>
        private class AIToolDefinition
        {
            public string Name { get; set; }
            public string Description { get; set; }
            public object Parameters { get; set; }
        }

        /// <summary>
        /// AI响应
        /// </summary>
        private class AIResponse
        {
            public string Content { get; set; }
            public List<AIToolCall> ToolCalls { get; set; }
            public bool IsComplete { get; set; }
        }

        /// <summary>
        /// AI工具调用
        /// </summary>
        private class AIToolCall
        {
            public string Name { get; set; }
            public JsonElement Arguments { get; set; }
        }

        #endregion
    }
}
