using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using HSJM.AI.Core;

namespace HSJM.AI.AppLayer.Agent.Services
{
    /// <summary>
    /// WorkflowBackgroundJobService 分部类 - 智能路由与决策
    /// 🆕 增强版：真正使用 PlannerAgentID，智能推荐专家，生成有意义的任务名
    /// </summary>
    public partial class WorkflowBackgroundJobService
    {
        #region 智能路由增强

        /// <summary>
        /// 🆕 智能路由决策（真正使用 Router 的推理能力）
        /// </summary>
        private async Task<RouterDecisionResult> SmartRouterDecisionAsync(
            long routerRoleId,
            string userInput,
            Dictionary<long, string> availableExperts,
            System.Threading.CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("[智能路由] Router={RoleId} 开始分析需求", routerRoleId);

                // 1. 创建 Router Agent（真正使用 PlannerAgentID）
                var routerAgent = await _agentRuntimeService.CreateAgentByRoleAsync(routerRoleId);

                // 2. 构建智能 Prompt（让 Router 真正思考）
                var routerPrompt = BuildRouterPrompt(userInput, availableExperts);

                // 3. 执行 Router 推理（使用原生消息格式）
                // ✅ Router 不需要历史对话上下文，传 null 即可
                var routerResponse = await _agentRuntimeService.RunAgentAsync(routerAgent, routerPrompt, null, cancellationToken);
                var routerDecision = routerResponse.Content ?? "";

                _logger.LogDebug("[Router决策] 原始输出: {Decision}", routerDecision);

                // 4. 解析 Router 的决策（结构化输出）
                var decision = ParseRouterDecision(routerDecision, availableExperts);

                _logger.LogInformation("[智能路由] 推荐 {Count} 个专家: {Experts}",
                    decision.SelectedExperts.Count,
                    string.Join(", ", decision.SelectedExperts.Values));

                return decision;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[智能路由] Router 决策失败，使用降级策略");
                
                // 降级策略：全部专家
                return new RouterDecisionResult
                {
                    SelectedExperts = availableExperts,
                    TaskComplexity = "未知",
                    ReasoningProcess = "Router 决策失败，使用降级策略",
                    Confidence = 0.5
                };
            }
        }

        /// <summary>
        /// 构建 Router Prompt（让 AI 真正思考）
        /// </summary>
        private string BuildRouterPrompt(string userInput, Dictionary<long, string> availableExperts)
        {
            var expertList = string.Join("\n", availableExperts.Select((kv, idx) => 
                $"{idx + 1}. **{kv.Value}**（ID: {kv.Key}）"));

            return $@"# 任务分析与专家推荐

## 你的角色
你是一个智能任务路由器，负责分析用户需求并推荐最合适的专家。

## 用户需求
```
{userInput}
```

## 可用专家列表
{expertList}

## 分析要求
1. **需求分析**：理解用户的核心诉求
2. **专家匹配**：选择最合适的专家（1-3个，不要全选）
3. **任务分解**：为每个专家生成具体的任务描述
4. **复杂度评估**：简单/中等/复杂

## 输出格式（严格按JSON格式）
```json
{{
    ""需求分析"": ""用户想要...[简短描述]"",
    ""推荐专家"": [
        {{
            ""专家名称"": ""XXX专家"",
            ""专家ID"": 123,
            ""任务描述"": ""具体要做什么"",
            ""优先级"": 1,
            ""预估耗时"": ""10分钟""
        }}
    ],
    ""任务复杂度"": ""简单|中等|复杂"",
    ""信心指数"": 0.95
}}
```

## 注意事项
- 不要推荐所有专家，只选择最合适的
- 任务描述要具体、可执行
- 优先级：1=最高，2=次要，3=可选
- 信心指数：0-1，表示推荐的置信度
";
        }

        /// <summary>
        /// 解析 Router 决策结果（处理结构化输出）
        /// </summary>
        private RouterDecisionResult ParseRouterDecision(
            string routerDecision,
            Dictionary<long, string> availableExperts)
        {
            try
            {
                // 尝试提取 JSON（可能包裹在 markdown 代码块中）
                var jsonStart = routerDecision.IndexOf("{");
                var jsonEnd = routerDecision.LastIndexOf("}") + 1;
                
                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    var jsonStr = routerDecision.Substring(jsonStart, jsonEnd - jsonStart);
                    var parsed = JsonSerializer.Deserialize<RouterDecisionDto>(jsonStr, 
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (parsed?.推荐专家 != null && parsed.推荐专家.Count > 0)
                    {
                        var selectedExperts = new Dictionary<long, ExpertTaskInfo>();
                        
                        foreach (var expert in parsed.推荐专家)
                        {
                            // 匹配专家ID
                            if (availableExperts.ContainsKey(expert.专家ID))
                            {
                                selectedExperts[expert.专家ID] = new ExpertTaskInfo
                                {
                                    ExpertName = expert.专家名称,
                                    TaskDescription = expert.任务描述 ?? "分析任务",
                                    Priority = expert.优先级 ?? 1,
                                    EstimatedDuration = expert.预估耗时 ?? "15分钟"
                                };
                            }
                        }

                        return new RouterDecisionResult
                        {
                            SelectedExperts = selectedExperts.ToDictionary(
                                kv => kv.Key, 
                                kv => kv.Value.ExpertName),
                            ExpertTaskInfos = selectedExperts,
                            TaskComplexity = parsed.任务复杂度 ?? "中等",
                            ReasoningProcess = parsed.需求分析 ?? "分析完成",
                            Confidence = parsed.信心指数 ?? 0.8
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Router解析] JSON 解析失败，使用降级策略");
            }

            // 降级策略：基于关键词匹配
            return FallbackRouterDecision(routerDecision, availableExperts);
        }

        /// <summary>
        /// 降级策略：基于关键词匹配
        /// </summary>
        private RouterDecisionResult FallbackRouterDecision(
            string routerDecision,
            Dictionary<long, string> availableExperts)
        {
            var selected = new Dictionary<long, ExpertTaskInfo>();

            foreach (var (expertId, expertName) in availableExperts)
            {
                // 简单的关键词匹配
                if (routerDecision.Contains(expertName, StringComparison.OrdinalIgnoreCase))
                {
                    selected[expertId] = new ExpertTaskInfo
                    {
                        ExpertName = expertName,
                        TaskDescription = $"分析与{expertName}相关的内容",
                        Priority = 1,
                        EstimatedDuration = "15分钟"
                    };
                }
            }

            // 如果一个都没匹配到，返回全部
            if (selected.Count == 0)
            {
                _logger.LogWarning("[降级策略] 未匹配到任何专家，返回全部");
                foreach (var (expertId, expertName) in availableExperts)
                {
                    selected[expertId] = new ExpertTaskInfo
                    {
                        ExpertName = expertName,
                        TaskDescription = "全面分析",
                        Priority = 1,
                        EstimatedDuration = "15分钟"
                    };
                }
            }

            return new RouterDecisionResult
            {
                SelectedExperts = selected.ToDictionary(kv => kv.Key, kv => kv.Value.ExpertName),
                ExpertTaskInfos = selected,
                TaskComplexity = "中等",
                ReasoningProcess = "使用关键词匹配",
                Confidence = 0.6
            };
        }

        /// <summary>
        /// 🆕 生成智能化任务名称（根据用户需求和专家特长）
        /// </summary>
        private string GenerateSmartTaskName(
            string userInput,
            string expertName,
            string taskDescription)
        {
            // 提取用户需求的关键词
            var keywords = ExtractKeywords(userInput);
            var keywordStr = string.Join("、", keywords.Take(2));

            // 生成有意义的任务名
            if (!string.IsNullOrEmpty(keywordStr))
            {
                return $"{expertName}：{taskDescription}（关于{keywordStr}）";
            }
            else
            {
                return $"{expertName}：{taskDescription}";
            }
        }

        /// <summary>
        /// 简单的关键词提取（后续可以使用 NLP）
        /// </summary>
        private List<string> ExtractKeywords(string text)
        {
            // 停用词
            var stopWords = new HashSet<string> 
            { 
                "的", "了", "是", "在", "我", "有", "和", "就", "不", "人", "都", "一", "一个",
                "上", "也", "很", "到", "说", "要", "去", "你", "会", "着", "没有", "看", "好"
            };

            var words = text
                .Replace("，", " ")
                .Replace("。", " ")
                .Replace("？", " ")
                .Replace("！", " ")
                .Split(' ', '、', '，', '。', '？', '！', '\n', '\r')
                .Where(w => w.Length >= 2 && !stopWords.Contains(w))
                .Take(5)
                .ToList();

            return words;
        }

        #endregion

        #region DTO 类定义

        /// <summary>
        /// Router 决策结果
        /// </summary>
        private class RouterDecisionResult
        {
            public Dictionary<long, string> SelectedExperts { get; set; } = new();
            public Dictionary<long, ExpertTaskInfo> ExpertTaskInfos { get; set; } = new();
            public string TaskComplexity { get; set; } = "中等";
            public string ReasoningProcess { get; set; } = "";
            public double Confidence { get; set; } = 0.8;
        }

        /// <summary>
        /// 专家任务信息
        /// </summary>
        private class ExpertTaskInfo
        {
            public string ExpertName { get; set; } = "";
            public string TaskDescription { get; set; } = "";
            public int Priority { get; set; } = 1;
            public string EstimatedDuration { get; set; } = "15分钟";
        }

        /// <summary>
        /// Router 决策 DTO（用于 JSON 反序列化）
        /// </summary>
        private class RouterDecisionDto
        {
            public string 需求分析 { get; set; }
            public List<ExpertRecommendation> 推荐专家 { get; set; }
            public string 任务复杂度 { get; set; }
            public double? 信心指数 { get; set; }
        }

        private class ExpertRecommendation
        {
            public string 专家名称 { get; set; }
            public long 专家ID { get; set; }
            public string 任务描述 { get; set; }
            public int? 优先级 { get; set; }
            public string 预估耗时 { get; set; }
        }

        #endregion
    }
}
