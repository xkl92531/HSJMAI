using HSJM.AI.AppLayer.Agent.Services;
using HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.DiagnosticStrategyFactory.Services
{
    /// <summary>
    /// 诊断策略决策服务
    /// 负责分析用户请求，让AI判断需要执行哪些检查步骤
    /// </summary>
    public class DiagnosticStrategyDecider : ISelfScopedAutoInject
    {
        private readonly ILogger<DiagnosticStrategyDecider> _logger;
        private readonly AgentRuntimeService _agentRuntimeService;
        private readonly PromptTemplateService _promptTemplateService;
        private readonly IPLC_ProjectService _plcProjectService;

        /// <summary>
        /// 诊断策略决策服务构造函数
        /// </summary>
        /// <param name="logger">日志记录器</param>
        /// <param name="agentRuntimeService">AI Agent运行时服务</param>
        /// <param name="promptTemplateService">提示词模板服务</param>
        /// <param name="plcProjectService">PLC项目服务</param>
        public DiagnosticStrategyDecider(
            ILogger<DiagnosticStrategyDecider> logger,
            AgentRuntimeService agentRuntimeService,
            PromptTemplateService promptTemplateService,
            IPLC_ProjectService plcProjectService)
        {
            _logger = logger;
            _agentRuntimeService = agentRuntimeService;
            _promptTemplateService = promptTemplateService;
            _plcProjectService = plcProjectService;
        }

        /// <summary>
        /// 根据用户请求判断需要执行哪些检查步骤
        /// </summary>
        /// <param name="productionLineId">产线ID</param>
        /// <param name="userDescription">用户描述的问题</param>
        /// <returns>诊断策略决策结果</returns>
        public async Task<DiagnosticStrategyDecision> DecideStrategyAsync(
            long productionLineId,
            string userDescription = "")
        {
            try
            {
                _logger.LogInformation("[策略决策] ========== 开始 ==========");
                _logger.LogInformation("[策略决策] 开始分析用户请求 - 产线: {ProductionLineId}, 描述: {Description}",
                    productionLineId, userDescription);

                // 1. 构建提示词（先不让AI知道PLC列表，让AI只解析用户描述中的PlcName）
                var prompt = await _promptTemplateService.RenderTemplateAsync(
                    PromptTemplateType.StrategyDecision,
                    new System.Collections.Generic.Dictionary<string, string>
                    {
                        { "PRODUCTION_LINE_ID", productionLineId.ToString() },
                        { "USER_DESCRIPTION", userDescription ?? "未提供" }
                    });

                // 2. 调用AI判断
                var agent = await _agentRuntimeService.CreateAgentByRoleAsync(
                    AppsettingsConfig.GetDiagnosticAgentRoleId());

                var response = await _agentRuntimeService.RunAgentAsync(agent, prompt);

                if (!response.Success || string.IsNullOrWhiteSpace(response.Content))
                {
                    _logger.LogWarning("[策略决策] AI调用失败，使用默认策略");
                    return GetDefaultDecision();
                }

                // 3. 解析AI返回的JSON
                _logger.LogInformation("[策略决策] AI原始返回: {Content}", response.Content);
                var decision = ParseDecision(response.Content);
                _logger.LogInformation("[策略决策] 解析后结果: Safety={Safety}, Constraint={Constraint}, ProblemType={ProblemType}", decision.SafetyCheck, decision.ConstraintCheck, decision.ProblemType);

                // 4. 如果AI解析出了PlcName，则根据PlcName查询对应的PLC ID；否则查询所有PLC ID
                if (!string.IsNullOrEmpty(decision.ParsedPlcName))
                {
                    // 根据PlcName精确查询对应的PLC
                    var matchedPlcs = await _plcProjectService.GetListAsync(p =>
                        p.IsDeleted == false &&
                        (p.StationId.Contains(decision.ParsedPlcName) ||
                         p.ProjectName.Contains(decision.ParsedPlcName)));

                    if (matchedPlcs != null && matchedPlcs.Count > 0)
                    {
                        decision.ParsedPlcIds = matchedPlcs.Select(p => p.Id).ToList();
                        _logger.LogInformation("[策略决策] 匹配到 {Count} 个PLC - StationIds: {StationIds}",
                            matchedPlcs.Count, string.Join(", ", matchedPlcs.Select(p => p.StationId)));
                    }
                    else
                    {
                        // 未匹配到，查询所有PLC
                        var allPlcs = await _plcProjectService.GetListAsync(p => p.IsDeleted == false);
                        decision.ParsedPlcIds = allPlcs?.Select(p => p.Id).ToList() ?? new List<long>();
                        _logger.LogWarning("[策略决策] 未找到匹配的PLC: {PlcName}，使用所有PLC，共 {Count} 个",
                            decision.ParsedPlcName, decision.ParsedPlcIds.Count);
                    }
                }
                else
                {
                    // 没有解析出PlcName，查询所有PLC ID
                    var allPlcs = await _plcProjectService.GetListAsync(p => p.IsDeleted == false);
                    decision.ParsedPlcIds = allPlcs?.Select(p => p.Id).ToList() ?? new List<long>();
                    _logger.LogInformation("[策略决策] 未解析出PlcName，使用所有PLC，共 {Count} 个", decision.ParsedPlcIds.Count);
                }

                _logger.LogInformation("[策略决策] AI判断结果 - 安全检查: {Safety}, 约束: {Constraint}, IO: {IO}, 卡滞点: {Stuck}, 能量流: {EnergyFlow}, 解析的PLC: {PlcName}, 原因: {Reason}",
                    decision.SafetyCheck, decision.ConstraintCheck, decision.IoCheck,
                    decision.StuckPointLocate, decision.EnergyFlowAnalysis, decision.ParsedPlcName ?? "无", decision.Reason);

                return decision;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[策略决策] 决策过程异常，使用默认策略");
                return GetDefaultDecision();
            }
        }

        /// <summary>
        /// 解析AI返回的决策JSON
        /// </summary>
        private DiagnosticStrategyDecision ParseDecision(string content)
        {
            try
            {
                // 尝试提取JSON部分
                var jsonStart = content.IndexOf('{');
                var jsonEnd = content.LastIndexOf('}');

                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    var jsonStr = content.Substring(jsonStart, jsonEnd - jsonStart + 1);
                    return JsonSerializer.Deserialize<DiagnosticStrategyDecision>(jsonStr, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    }) ?? GetDefaultDecision();
                }

                return GetDefaultDecision();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[策略决策] 解析JSON失败，使用默认策略");
                return GetDefaultDecision();
            }
        }

        /// <summary>
        /// 获取默认决策（全面检查）
        /// </summary>
        private DiagnosticStrategyDecision GetDefaultDecision()
        {
            return new DiagnosticStrategyDecision
            {
                SafetyCheck = true,
                ConstraintCheck = true,
                IoCheck = false,
                StuckPointLocate = false,
                EnergyFlowAnalysis = false,
                Reason = "使用默认策略（全面检查）",
                ProblemType = "未知",
                Priority = "高",
                ParsedPlcName = ""
            };
        }
    }
}
