using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.Agent.Services;
using HSJM.AI.Core;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.Agent.Tools
{
    /// <summary>
    /// 技能访问工具 - 实现渐进式披露（Progressive Disclosure）
    /// 
    /// 工作流程：
    /// 1. SystemPrompt 只注入技能摘要（名称+描述）
    /// 2. Agent 决定需要使用某技能时，调用 GetSkillDetails 获取完整说明
    /// 3. Agent 根据完整说明调用工具或执行脚本
    /// 4. 执行后自动进行诊断，返回诊断报告
    /// 
    /// 这种设计避免了 SystemPrompt 过长，按需加载技能详情
    /// </summary>
    public class SkillAccessTool : ISelfScopedAutoInject
    {
        private readonly ILogger<SkillAccessTool> _logger;
        private readonly SkillLoaderService _skillLoaderService;
        private readonly ScriptExecutorTool _scriptExecutorTool;
        private readonly SkillExecutionDiagnosticsService _diagnosticsService;

        /// <summary>
        /// 当前关联的角色ID（用于懒加载）
        /// </summary>
        private long? _roleId;

        /// <summary>
        /// 当前会话加载的技能缓存（避免重复查询）
        /// Key: skillName (小写), Value: SkillContent
        /// </summary>
        private Dictionary<string, SkillContent>? _skillCache;

        public SkillAccessTool(
            ILogger<SkillAccessTool> logger,
            SkillLoaderService skillLoaderService,
            ScriptExecutorTool scriptExecutorTool,
            SkillExecutionDiagnosticsService diagnosticsService)
        {
            _logger = logger;
            _skillLoaderService = skillLoaderService;
            _scriptExecutorTool = scriptExecutorTool;
            _diagnosticsService = diagnosticsService;
        }

        /// <summary>
        /// 设置上下文（角色ID）
        /// </summary>
        public void SetContext(long roleId)
        {
            _roleId = roleId;
        }

        /// <summary>
        /// 初始化技能缓存（在 Agent 创建时调用，优化性能）
        /// </summary>
        public void InitializeSkillCache(List<SkillContent> skills)
        {
            _skillCache = skills.ToDictionary(
                s => s.Name.ToLowerInvariant(),
                s => s,
                StringComparer.OrdinalIgnoreCase);
            
            _logger.LogInformation("[SkillAccess] 初始化技能缓存: {Count} 个技能", _skillCache.Count);
        }

        /// <summary>
        /// 确保缓存已加载（懒加载机制）
        /// </summary>
        private async Task EnsureCacheLoadedAsync()
        {
            if (_skillCache != null) return;

            if (_roleId.HasValue)
            {
                _logger.LogInformation("[SkillAccess] 缓存为空，触发懒加载: RoleId={RoleId}", _roleId);
                var skills = await _skillLoaderService.LoadSkillsForRoleAsync(_roleId.Value);
                InitializeSkillCache(skills);
            }
            else
            {
                _logger.LogWarning("[SkillAccess] 缓存为空且未设置 RoleId，无法加载技能");
            }
        }

        /// <summary>
        /// 列出当前可用的技能（摘要信息）
        /// Agent 应首先调用此方法了解有哪些技能可用
        /// </summary>
        [Description("列出当前可用的技能摘要，包括名称和简短描述")]
        public async Task<SkillListResult> ListAvailableSkills()
        {
            await EnsureCacheLoadedAsync();

            var result = new SkillListResult();

            if (_skillCache == null || _skillCache.Count == 0)
            {
                result.Message = "当前没有加载任何技能";
                _logger.LogWarning("[SkillAccess] 技能缓存为空");
                return result;
            }

            result.Skills = _skillCache.Values.Select(s => new SkillSummary
            {
                Name = s.Name,
                DisplayName = s.DisplayName,
                Description = s.Description,
                Category = s.Category,
                IsAgentSkill = s.IsAgentSkill,
                HasScripts = s.IsAgentSkill // Agent Skill 可能包含可执行脚本
            }).ToList();

            result.Message = $"共 {result.Skills.Count} 个可用技能。使用 get_skill_details(skillName) 获取完整说明。";
            
            _logger.LogInformation("[SkillAccess] 列出技能: {Count} 个", result.Skills.Count);
            return result;
        }

        /// <summary>
        /// 获取技能的完整详情（包括 SKILL.md 的完整内容）
        /// 渐进式披露的核心：Agent 决定需要某技能时才获取完整说明
        /// </summary>
        /// <param name="skillName">技能名称（如：plc-analyzer）</param>
        [Description("获取指定技能的完整详情，包括使用说明、可用工具、执行步骤等")]
        public async Task<SkillDetailsResult> GetSkillDetails(
            [Description("技能名称")] string skillName)
        {
            await EnsureCacheLoadedAsync();

            var result = new SkillDetailsResult { SkillName = skillName };

            if (_skillCache == null)
            {
                result.Found = false;
                result.Message = "技能缓存未初始化";
                _logger.LogWarning("[SkillAccess] 尝试获取技能详情但缓存未初始化");
                return result;
            }

            if (!_skillCache.TryGetValue(skillName.ToLowerInvariant(), out var skill))
            {
                // 尝试模糊匹配或重新加载？暂时只需严格匹配
                result.Found = false;
                result.Message = $"未找到技能: {skillName}。请使用 list_available_skills 查看可用技能。";
                _logger.LogWarning("[SkillAccess] 技能不存在: {Name}", skillName);
                return result;
            }

            result.Found = true;
            result.DisplayName = skill.DisplayName;
            result.Description = skill.Description;
            result.Category = skill.Category;
            result.ComplexityLevel = skill.ComplexityLevel;
            result.IsAgentSkill = skill.IsAgentSkill;
            result.AllowedTools = skill.AllowedTools;
            result.License = skill.License;
            result.Compatibility = skill.Compatibility;

            // 完整的 Markdown 说明（这是渐进式披露的核心内容）
            result.FullInstructions = skill.MarkdownBody;
            
            // 🆕 保存技能包路径供后续执行使用
            result.SkillPackagePath = skill.SkillPackagePath;

            // 如果是 Agent Skill，列出可用脚本
            if (skill.IsAgentSkill && !string.IsNullOrEmpty(skill.SkillPackagePath))
            {
                // 🔧 使用 SkillPackagePath 而不是 skillName
                result.AvailableScripts = _scriptExecutorTool.ListSkillScripts(skill.SkillPackagePath);
                result.Message = $"已加载技能 '{skill.DisplayName}' 的完整说明。" +
                    (result.AvailableScripts?.Count > 0 
                        ? $"包含 {result.AvailableScripts.Count} 个可执行脚本。" 
                        : "");
            }
            else
            {
                result.Message = $"已加载技能 '{skill.DisplayName}' 的完整说明。";
            }

            _logger.LogInformation("[SkillAccess] 获取技能详情: {Name}, 说明长度={Len}",
                skillName, result.FullInstructions?.Length ?? 0);

            return result;
        }

        /// <summary>
        /// 执行技能包中的脚本
        /// 这是 ScriptExecutorTool 的代理方法，提供更友好的接口
        /// </summary>
        /// <param name="skillName">技能名称</param>
        /// <param name="scriptName">脚本文件名（如：main.py）</param>
        /// <param name="inputJson">传递给脚本的 JSON 参数</param>
        [Description("执行指定技能包中的脚本（Python/Shell/PowerShell）")]
        public async Task<ScriptExecutionResult> ExecuteSkillScriptAsync(
            [Description("技能名称")] string skillName,
            [Description("脚本文件名，如：main.py")] string scriptName,
            [Description("传递给脚本的 JSON 参数")] string? inputJson = null)
        {
            _logger.LogInformation("[SkillAccess] 执行技能脚本: {Skill}/{Script}", skillName, scriptName);

            // 验证技能存在并获取实际的包路径
            SkillContent? skill = null;
            if (_skillCache != null)
            {
                _skillCache.TryGetValue(skillName.ToLowerInvariant(), out skill);
            }
            
            if (skill == null)
            {
                return new ScriptExecutionResult
                {
                    Success = false,
                    SkillPackagePath = skillName,
                    ScriptName = scriptName,
                    ErrorMessage = $"未找到技能: {skillName}"
                };
            }

            // 🔧 使用 SkillPackagePath（如 plc-skill-factory）而不是 Name
            var packagePath = skill.SkillPackagePath ?? skill.Name;
            
            // 委托给 ScriptExecutorTool 执行
            return await _scriptExecutorTool.ExecuteSkillScriptAsync(
                packagePath, scriptName, inputJson);
        }

        /// <summary>
        /// 执行技能脚本并进行诊断
        /// 返回执行结果和诊断报告，供 Task 层决策
        /// </summary>
        /// <param name="skillName">技能名称</param>
        /// <param name="scriptName">脚本文件名</param>
        /// <param name="inputJson">传递给脚本的 JSON 参数</param>
        [Description("执行技能脚本并返回诊断报告，包含执行结果、错误分析和重试建议")]
        public async Task<SkillExecutionWithDiagnostics> ExecuteWithDiagnosticsAsync(
            [Description("技能名称")] string skillName,
            [Description("脚本文件名，如：main.py")] string scriptName,
            [Description("传递给脚本的 JSON 参数")] string? inputJson = null)
        {
            _logger.LogInformation("[SkillAccess] 执行技能脚本(带诊断): {Skill}/{Script}", skillName, scriptName);

            var result = new SkillExecutionWithDiagnostics
            {
                SkillName = skillName,
                ScriptName = scriptName
            };

            // 执行脚本
            result.ExecutionResult = await ExecuteSkillScriptAsync(skillName, scriptName, inputJson);

            // 进行诊断
            result.DiagnosticReport = _diagnosticsService.Diagnose(result.ExecutionResult);

            _logger.LogInformation("[SkillAccess] 执行诊断完成: IsHealthy={IsHealthy}, Recommendation={Rec}",
                result.DiagnosticReport.IsHealthy, result.DiagnosticReport.Recommendation);

            return result;
        }

        /// <summary>
        /// 获取上次执行的诊断建议（供 Agent 决策使用）
        /// </summary>
        [Description("根据诊断报告获取建议的下一步操作")]
        public string GetDiagnosticAdvice(SkillDiagnosticReport report)
        {
            return report.Recommendation switch
            {
                RetryRecommendation.OK => 
                    "执行成功，可以继续下一步操作。",
                RetryRecommendation.ShouldRetry => 
                    $"建议重试：{report.DiagnosticSummary}。可以调整参数或增加超时时间后重试。",
                RetryRecommendation.ShouldPivot => 
                    $"建议切换策略：{report.DiagnosticSummary}。当前脚本可能不适合此任务，考虑使用其他技能。",
                RetryRecommendation.ManualReview => 
                    $"需要人工介入：{report.DiagnosticSummary}。请检查脚本配置或联系管理员。",
                _ => report.SuggestedNextAction
            };
        }
    }

    /// <summary>
    /// 技能执行结果（包含诊断报告）
    /// </summary>
    public class SkillExecutionWithDiagnostics
    {
        /// <summary>
        /// 技能名称
        /// </summary>
        public string SkillName { get; set; } = "";

        /// <summary>
        /// 脚本名称
        /// </summary>
        public string ScriptName { get; set; } = "";

        /// <summary>
        /// 执行结果
        /// </summary>
        public ScriptExecutionResult? ExecutionResult { get; set; }

        /// <summary>
        /// 诊断报告
        /// </summary>
        public SkillDiagnosticReport? DiagnosticReport { get; set; }

        /// <summary>
        /// 是否执行成功且诊断健康
        /// </summary>
        public bool IsSuccessful => ExecutionResult?.Success == true && DiagnosticReport?.IsHealthy == true;

        /// <summary>
        /// 获取诊断建议摘要
        /// </summary>
        public string GetAdviceSummary()
        {
            if (DiagnosticReport == null) return "无诊断报告";
            return DiagnosticReport.IsHealthy
                ? "执行成功，可继续"
                : $"{DiagnosticReport.Recommendation}: {DiagnosticReport.DiagnosticSummary}";
        }
    }

    #region Result Models

    /// <summary>
    /// 技能列表结果
    /// </summary>
    public class SkillListResult
    {
        public string Message { get; set; } = "";
        public List<SkillSummary> Skills { get; set; } = new();
    }

    /// <summary>
    /// 技能摘要（用于列表展示）
    /// </summary>
    public class SkillSummary
    {
        public string Name { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Description { get; set; } = "";
        public string? Category { get; set; }
        public bool IsAgentSkill { get; set; }
        public bool HasScripts { get; set; }
    }

    /// <summary>
    /// 技能详情结果（渐进式披露的完整内容）
    /// </summary>
    public class SkillDetailsResult
    {
        public bool Found { get; set; }
        public string SkillName { get; set; } = "";
        public string? DisplayName { get; set; }
        public string? Description { get; set; }
        public string? Category { get; set; }
        public string? ComplexityLevel { get; set; }
        public bool IsAgentSkill { get; set; }
        public List<string>? AllowedTools { get; set; }
        public string? License { get; set; }
        public string? Compatibility { get; set; }
        
        /// <summary>
        /// 技能包目录路径（如 plc-skill-factory）
        /// 用于执行脚本时定位文件
        /// </summary>
        public string? SkillPackagePath { get; set; }
        
        /// <summary>
        /// 完整的使用说明（SKILL.md 的 Markdown 正文）
        /// 这是渐进式披露的核心：只有调用 GetSkillDetails 才会返回
        /// </summary>
        public string? FullInstructions { get; set; }
        
        /// <summary>
        /// 可用的脚本列表（Agent Skill 专用）
        /// </summary>
        public List<string>? AvailableScripts { get; set; }
        
        public string Message { get; set; } = "";
    }

    #endregion
}
