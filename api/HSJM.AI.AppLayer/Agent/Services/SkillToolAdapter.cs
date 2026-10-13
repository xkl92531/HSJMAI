using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using HSJM.AI.Utility;

namespace HSJM.AI.AppLayer.Agent.Services
{
    /// <summary>
    /// 技能工具适配器 - Skill-as-Tool 架构核心组件
    /// 
    /// 职责：
    /// 1. 将数据库中的 Skill 动态转换为 AIFunction
    /// 2. 技能名称标准化（plc-skill-factory → plc_skill_factory）
    /// 3. 技能调用路由到 SkillExecutionEngine
    /// 
    /// LLM 视角：
    /// - 看到的是普通工具（如 analyze_plc_file）
    /// - 调用工具时，适配器将请求转发给 SkillExecutionEngine
    /// </summary>
    public class SkillToolAdapter : ISelfScopedAutoInject
    {
        private readonly ILogger<SkillToolAdapter> _logger;
        private readonly SkillLoaderService _skillLoader;
        private readonly SkillExecutionEngine _skillEngine;

        /// <summary>
        /// 当前角色 ID（用于执行时获取模型配置）
        /// </summary>
        private long _currentRoleId;

        /// <summary>
        /// 用户原始请求（ReAct 调用技能时需要传递完整上下文）
        /// 解决问题：LLM 提炼参数后调用技能，可能丢失原始意图
        /// </summary>
        /// <summary>
        /// 当前工作流 ID（用于 SignalR 推送）
        /// </summary>
        private long? _currentWorkflowId;

        /// <summary>
        /// 当前任务 ID（用于 SignalR 推送）
        /// </summary>
        private long? _currentTaskId;

        private string? _originalUserRequest;

        /// <summary>
        /// 技能缓存（SkillId → SkillContent）
        /// </summary>
        private readonly Dictionary<long, SkillContent> _skillCache = new();

        public SkillToolAdapter(
            ILogger<SkillToolAdapter> logger,
            SkillLoaderService skillLoader,
            SkillExecutionEngine skillEngine)
        {
            _logger = logger;
            _skillLoader = skillLoader;
            _skillEngine = skillEngine;
        }

        #region 公开方法

        /// <summary>
        /// 设置当前角色上下文
        /// </summary>
        public void SetRoleContext(long roleId)
        {
            _currentRoleId = roleId;
        }

        /// <summary>
        /// 设置当前工作流上下文
        /// </summary>
        public void SetWorkflowContext(long? workflowId)
        {
            _currentWorkflowId = workflowId;
            _logger.LogDebug("[SkillToolAdapter] 设置工作流上下文: WorkflowId={WorkflowId}", workflowId);
        }

        /// <summary>
        /// 设置当前任务上下文
        /// </summary>
        public void SetTaskContext(long? taskId)
        {
            _currentTaskId = taskId;
            _logger.LogDebug("[SkillToolAdapter] 设置任务上下文: TaskId={TaskId}", taskId);
        }

        /// <summary>
        /// 设置用户原始请求上下文
        /// 必须在 ReAct 调用技能工具前设置，确保 Skill 执行能看到完整上下文
        /// </summary>
        /// <param name="originalRequest">用户的原始输入/需求</param>
        public void SetOriginalUserRequest(string? originalRequest)
        {
            _originalUserRequest = originalRequest;
            _logger.LogDebug("[SkillToolAdapter] 设置原始用户请求: {Request}", 
                originalRequest?.Length > 100 ? originalRequest[..100] + "..." : originalRequest);
        }

        /// <summary>
        /// 构建角色的技能工具列表
        /// </summary>
        /// <param name="roleId">角色 ID</param>
        /// <param name="preloadedSkills">预加载的技能列表（可选，避免重复加载）</param>
        /// <returns>AIFunction 工具列表</returns>
        public async Task<List<AIFunction>> BuildSkillToolsAsync(
            long roleId, 
            List<SkillContent>? preloadedSkills = null)
        {
            _currentRoleId = roleId;
            var tools = new List<AIFunction>();

            try
            {
                // 使用预加载的技能或从数据库加载
                var skills = preloadedSkills ?? await _skillLoader.LoadSkillsForRoleAsync(roleId);

                if (skills.Count == 0)
                {
                    _logger.LogInformation("[SkillToolAdapter] 角色 {RoleId} 未配置技能", roleId);
                    return tools;
                }

                _logger.LogInformation("[SkillToolAdapter] 开始转换 {Count} 个技能为工具", skills.Count);

                foreach (var skill in skills)
                {
                    try
                    {
                        var tool = CreateToolFromSkill(skill);
                        if (tool != null)
                        {
                            tools.Add(tool);
                            _skillCache[skill.SkillId] = skill;
                            _logger.LogInformation("[SkillToolAdapter] ✅ 技能转工具成功: {SkillName} → {ToolName}",
                                skill.Name, tool.Name);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[SkillToolAdapter] ❌ 技能转工具失败: {SkillName}", skill.Name);
                    }
                }

                _logger.LogInformation("[SkillToolAdapter] 完成: 成功转换 {Count} 个技能工具", tools.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SkillToolAdapter] 构建技能工具失败: RoleId={RoleId}", roleId);
            }

            return tools;
        }

        /// <summary>
        /// 将单个技能转换为 AIFunction
        /// </summary>
        public AIFunction? CreateToolFromSkill(SkillContent skill)
        {
            if (skill == null) return null;

            var toolName = NormalizeToolName(skill.Name);
            var toolDescription = BuildToolDescription(skill);

            _logger.LogDebug("[SkillToolAdapter] 创建工具: {ToolName}, 描述长度={DescLen}",
                toolName, toolDescription.Length);

            // 缓存技能内容
            _skillCache[skill.SkillId] = skill;

            // 创建动态工具函数
            // 使用闭包捕获 skill 信息
            var skillId = skill.SkillId;
            var skillName = skill.Name;

            Func<object?, CancellationToken, Task<string>> executeFunc = 
                async (input, ct) => await ExecuteSkillInternalAsync(skillId, input, ct);

            // 创建 AIFunction
            // 使用 Description 属性让 LLM 理解这个工具做什么
            var aiFunction = AIFunctionFactory.Create(
                name: toolName,
                description: toolDescription,
                method: executeFunc
            );

            return aiFunction;
        }

        #endregion

        #region 技能执行

        /// <summary>
        /// 技能执行入口（被 AIFunction 调用）
        /// </summary>
        private async Task<string> ExecuteSkillInternalAsync(
            long skillId, 
            object? inputObj, 
            CancellationToken cancellationToken)
        {
            string input;
            if (inputObj is string str)
            {
                input = str;
            }
            else if (inputObj is JsonElement jsonEl)
            {
                input = jsonEl.ValueKind == JsonValueKind.String ? jsonEl.GetString() ?? "" : jsonEl.GetRawText();
            }
            else if (inputObj != null)
            {
                // 尝试 ToString() 先，因为 JsonElement/JsonNode 的 ToString 通常返回 JSON 或 值
                var strVal = inputObj.ToString();
                // 如果 ToString 返回的是类名 (默认行为)，则序列化
                if (strVal != null && !strVal.StartsWith(inputObj.GetType().FullName ?? "System.Object"))
                {
                    input = strVal;
                }
                else
                {
                    input = JsonHelper.SerializeObject(inputObj);
                }
            }
            else
            {
                input = "";
            }

            _logger.LogInformation("[SkillToolAdapter] 执行技能: SkillId={SkillId}, InputType={InputType}, Input={Input}",
                skillId, inputObj?.GetType().FullName ?? "null", input.Length > 100 ? input.Substring(0, 100) + "..." : input);

            // 从缓存获取技能内容
            if (!_skillCache.TryGetValue(skillId, out var skill))
            {
                _logger.LogWarning("[SkillToolAdapter] 技能未在缓存中: SkillId={SkillId}", skillId);
                return $"[错误] 技能未找到: SkillId={skillId}";
            }

            // 委托给 SkillExecutionEngine 执行
            // 🆕 同时传递原始用户请求，让 Skill 能基于完整上下文工作
            var result = await _skillEngine.ExecuteAsync(
                skill, 
                input,                    // LLM 提炼的参数
                _currentRoleId, 
                _originalUserRequest,     // 用户原始请求
                _currentWorkflowId,       // 当前工作流ID
                _currentTaskId,           // 当前任务ID
                cancellationToken);

            return result.ToString();
        }

        #endregion

        #region 辅助方法

        /// <summary>
        /// 标准化工具名称
        /// 规则: 
        /// - 连字符转下划线 (plc-skill-factory → plc_skill_factory)
        /// - 只保留字母数字下划线
        /// - 转小写
        /// </summary>
        public static string NormalizeToolName(string skillCode)
        {
            if (string.IsNullOrEmpty(skillCode))
                return "unknown_skill";

            // 替换连字符为下划线
            var name = skillCode.Replace("-", "_");

            // 只保留字母数字下划线
            name = Regex.Replace(name, @"[^a-zA-Z0-9_]", "");

            // 转小写
            name = name.ToLowerInvariant();

            // 确保不以数字开头
            if (char.IsDigit(name[0]))
            {
                name = "skill_" + name;
            }

            return name;
        }

        /// <summary>
        /// 构建工具描述（供 LLM 理解）
        /// </summary>
        private string BuildToolDescription(SkillContent skill)
        {
            var parts = new List<string>();

            // 主描述
            if (!string.IsNullOrEmpty(skill.Description))
            {
                parts.Add(skill.Description);
            }

            // 显示名称（如果与 Name 不同）
            if (!string.IsNullOrEmpty(skill.DisplayName) && skill.DisplayName != skill.Name)
            {
                parts.Insert(0, $"[{skill.DisplayName}]");
            }

            // 如果有技能包路径，提示是可执行技能
            if (!string.IsNullOrEmpty(skill.SkillPackagePath))
            {
                parts.Add($"(技能包: {skill.SkillPackagePath})");
            }

            // 复杂度提示
            if (!string.IsNullOrEmpty(skill.ComplexityLevel))
            {
                parts.Add($"复杂度: {skill.ComplexityLevel}");
            }

            var description = string.Join(" ", parts);

            // 限制描述长度
            if (description.Length > 500)
            {
                description = description.Substring(0, 497) + "...";
            }

            return string.IsNullOrEmpty(description) ? $"执行技能: {skill.Name}" : description;
        }

        /// <summary>
        /// 解析 InputSchema 生成参数定义（未来扩展）
        /// </summary>
        private List<SkillToolParameter> ParseInputSchema(string? inputSchemaJson)
        {
            var parameters = new List<SkillToolParameter>();

            if (string.IsNullOrEmpty(inputSchemaJson))
            {
                // 默认参数：一个通用输入字符串
                parameters.Add(new SkillToolParameter
                {
                    Name = "input",
                    Type = "string",
                    Description = "任务输入（描述或参数）",
                    Required = true
                });
                return parameters;
            }

            try
            {
                using var doc = JsonDocument.Parse(inputSchemaJson);
                var root = doc.RootElement;

                if (root.TryGetProperty("properties", out var properties))
                {
                    foreach (var prop in properties.EnumerateObject())
                    {
                        var param = new SkillToolParameter
                        {
                            Name = prop.Name,
                            Type = prop.Value.TryGetProperty("type", out var typeEl) 
                                ? typeEl.GetString() ?? "string" 
                                : "string",
                            Description = prop.Value.TryGetProperty("description", out var descEl) 
                                ? descEl.GetString() ?? "" 
                                : ""
                        };

                        // 检查是否必填
                        if (root.TryGetProperty("required", out var requiredEl) 
                            && requiredEl.ValueKind == JsonValueKind.Array)
                        {
                            param.Required = requiredEl.EnumerateArray()
                                .Any(r => r.GetString() == prop.Name);
                        }

                        parameters.Add(param);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SkillToolAdapter] 解析 InputSchema 失败");
                
                // 回退到默认参数
                parameters.Add(new SkillToolParameter
                {
                    Name = "input",
                    Type = "string",
                    Description = "任务输入",
                    Required = true
                });
            }

            return parameters;
        }

        #endregion
    }

    #region Helper Models

    /// <summary>
    /// 技能工具参数定义
    /// </summary>
    internal class SkillToolParameter
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "string";
        public string Description { get; set; } = "";
        public bool Required { get; set; }
    }

    #endregion
}
