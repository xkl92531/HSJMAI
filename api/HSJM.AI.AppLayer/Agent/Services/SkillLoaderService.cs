using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.Agent.Services
{
    /// <summary>
    /// Skill 加载服务 - 支持 Agent Skills 规范
    /// 负责加载、解析 SKILL.md 并构建 Prompt 注入内容
    /// 
    /// Agent Skills 设计：
    /// - 技能包存储在 wwwroot/skills/{SkillPackagePath}/ 目录
    /// - 数据库只存基础索引信息（IsAgentSkill、SkillPackagePath）
    /// - 运行时从文件系统读取 SKILL.md 和脚本
    /// 
    /// Agent Skills 与 Tools 的协同：
    /// - Tools: 执行能力，通过 AIFunction 调用
    /// - Skills: 知识增强，注入到 SystemPrompt 教会 Agent 如何使用 Tools
    /// </summary>
    public class SkillLoaderService : ISelfScopedAutoInject
    {
        private readonly ILogger<SkillLoaderService> _logger;
        private readonly ISkill_DefinitionService _skillDefinitionService;
        private readonly IAgent_Skill_RelationService _agentSkillRelationService;
        private readonly IWebHostEnvironment _environment;
        
        /// <summary>
        /// Skills 根目录（相对于 wwwroot）
        /// </summary>
        private const string SKILLS_ROOT_FOLDER = "skills";

        public SkillLoaderService(
            ILogger<SkillLoaderService> logger,
            ISkill_DefinitionService skillDefinitionService,
            IAgent_Skill_RelationService agentSkillRelationService,
            IWebHostEnvironment environment)
        {
            _logger = logger;
            _skillDefinitionService = skillDefinitionService;
            _agentSkillRelationService = agentSkillRelationService;
            _environment = environment;
        }

        #region Skill 加载

        /// <summary>
        /// 加载角色配置的所有 Skills
        /// </summary>
        public async Task<List<SkillContent>> LoadSkillsForRoleAsync(
            long roleId, 
            CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("[Skill加载] 开始加载角色Skills: RoleId={RoleId}", roleId);

            var skills = new List<SkillContent>();

            try
            {
                // 1. 查询角色-技能关联
                var skillRelations = await _agentSkillRelationService.GetListAsync(
                    r => r.AgentRoleID == roleId && r.IsDeleted == false && r.IsEnabled == true);

                if (skillRelations.Count == 0)
                {
                    _logger.LogInformation("[Skill加载] 角色未配置Skills: RoleId={RoleId}", roleId);
                    return skills;
                }

                var skillIds = skillRelations.Select(r => r.SkillID.Value).ToList();

                // 2. 批量加载 Skill 定义
                var skillDefinitions = await _skillDefinitionService.GetListAsync(
                    s => skillIds.Contains(s.Id) && s.IsDeleted == false && s.IsEnabled == true);

                _logger.LogInformation("[Skill加载] 找到{Count}个已启用Skills", skillDefinitions.Count);

                // 3. 解析每个 Skill
                foreach (var skill in skillDefinitions)
                {
                    try
                    {
                        var skillContent = await ParseSkillDefinitionAsync(skill);
                        if (skillContent != null)
                        {
                            skills.Add(skillContent);
                            _logger.LogDebug("[Skill加载] ✅ 解析成功: {SkillName}", skill.SkillName);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Skill加载] ⚠️ 解析失败: {SkillName}", skill.SkillName);
                    }
                }

                _logger.LogInformation("[Skill加载] 完成: RoleId={RoleId}, 加载{Count}个Skills",
                    roleId, skills.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Skill加载] 加载失败: RoleId={RoleId}", roleId);
            }

            return skills;
        }

        /// <summary>
        /// 解析 Skill 定义为 SkillContent
        /// Agent Skill 从文件系统读取 SKILL.md
        /// 传统 Skill 从数据库字段读取
        /// </summary>
        private async Task<SkillContent?> ParseSkillDefinitionAsync(Skill_DefinitionEntity skill)
        {
            var content = new SkillContent
            {
                SkillId = skill.Id,
                Name = skill.SkillCode ?? skill.SkillName ?? $"skill-{skill.Id}",
                DisplayName = skill.SkillName ?? "未命名技能",
                Description = skill.Description ?? "",
                Category = skill.Category,
                ComplexityLevel = skill.ComplexityLevel,
                IsAgentSkill = skill.IsAgentSkill == true,
                // 🆕 保存技能包路径（用于定位可执行脚本）
                SkillPackagePath = skill.SkillPackagePath?.TrimEnd('/')
            };

            // Agent Skill: 从文件系统读取 SKILL.md
            if (skill.IsAgentSkill == true && !string.IsNullOrEmpty(skill.SkillPackagePath))
            {
                var skillMarkdown = await LoadSkillMarkdownFromFileAsync(skill.SkillPackagePath);
                if (!string.IsNullOrEmpty(skillMarkdown))
                {
                    ParseSkillMarkdown(skillMarkdown, content);
                    _logger.LogDebug("[Skill加载] Agent Skill 从文件加载: {Path}", skill.SkillPackagePath);
                }
                else
                {
                    _logger.LogWarning("[Skill加载] Agent Skill 文件未找到: {Path}", skill.SkillPackagePath);
                }
            }
            // 传统 Skill: 使用数据库字段
            else if (!string.IsNullOrEmpty(skill.Description) && skill.Description.Length > 200)
            {
                // 兼容旧逻辑：长描述可能是 SKILL.md 内容
                ParseSkillMarkdown(skill.Description, content);
            }

            // 解析允许的工具列表
            if (!string.IsNullOrEmpty(skill.PrerequisiteSkills))
            {
                try
                {
                    content.AllowedTools = JsonSerializer.Deserialize<List<string>>(skill.PrerequisiteSkills)
                        ?? new List<string>();
                }
                catch
                {
                    content.AllowedTools = new List<string>();
                }
            }

            return content;
        }

        /// <summary>
        /// 重新加载单个 Skill 内容 (用于确保获取最新 SKILL.md)
        /// </summary>
        public async Task<SkillContent> ReloadSkillContentAsync(SkillContent skill)
        {
            if (skill.IsAgentSkill && !string.IsNullOrEmpty(skill.SkillPackagePath))
            {
                var markdown = await LoadSkillMarkdownFromFileAsync(skill.SkillPackagePath);
                if (!string.IsNullOrEmpty(markdown))
                {
                    // 重新解析 Markdown 更新当前对象
                    ParseSkillMarkdown(markdown, skill);
                    _logger.LogInformation("[SkillLoader] 已重新加载 Skill: {Name}", skill.Name);
                }
            }
            return skill;
        }

        /// <summary>
        /// 从文件系统加载 SKILL.md 内容
        /// 路径: wwwroot/skills/{skillPackagePath}/SKILL.md
        /// </summary>
        private async Task<string?> LoadSkillMarkdownFromFileAsync(string skillPackagePath)
        {
            try
            {
                // 构建完整路径: wwwroot/skills/{skillPackagePath}/SKILL.md
                var skillDir = Path.Combine(
                    _environment.WebRootPath ?? Path.Combine(_environment.ContentRootPath, "wwwroot"),
                    SKILLS_ROOT_FOLDER,
                    skillPackagePath.TrimStart('/').TrimEnd('/'));
                
                var skillMdPath = Path.Combine(skillDir, "SKILL.md");

                if (!File.Exists(skillMdPath))
                {
                    _logger.LogWarning("[Skill加载] SKILL.md 不存在: {Path}", skillMdPath);
                    return null;
                }

                var content = await File.ReadAllTextAsync(skillMdPath, Encoding.UTF8);
                _logger.LogDebug("[Skill加载] 成功读取 SKILL.md: {Path}, 长度={Length}", 
                    skillMdPath, content.Length);
                
                return content;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Skill加载] 读取 SKILL.md 失败: {Path}", skillPackagePath);
                return null;
            }
        }

        /// <summary>
        /// 获取技能包目录的完整路径
        /// </summary>
        public string GetSkillPackageFullPath(string skillPackagePath)
        {
            return Path.Combine(
                _environment.WebRootPath ?? Path.Combine(_environment.ContentRootPath, "wwwroot"),
                SKILLS_ROOT_FOLDER,
                skillPackagePath.TrimStart('/').TrimEnd('/'));
        }

        /// <summary>
        /// 检查技能包是否存在
        /// </summary>
        public bool SkillPackageExists(string skillPackagePath)
        {
            var fullPath = GetSkillPackageFullPath(skillPackagePath);
            var skillMdPath = Path.Combine(fullPath, "SKILL.md");
            return File.Exists(skillMdPath);
        }

        /// <summary>
        /// 解析 SKILL.md 格式内容
        /// 格式: YAML frontmatter (---...---) + Markdown body
        /// </summary>
        private void ParseSkillMarkdown(string markdown, SkillContent content)
        {
            // 匹配 YAML frontmatter
            var frontmatterMatch = Regex.Match(markdown, @"^---\s*\n(.*?)\n---\s*\n(.*)$", 
                RegexOptions.Singleline);

            if (frontmatterMatch.Success)
            {
                var yamlContent = frontmatterMatch.Groups[1].Value;
                var markdownBody = frontmatterMatch.Groups[2].Value;

                // 解析 YAML 字段
                ParseYamlFrontmatter(yamlContent, content);
                content.MarkdownBody = markdownBody.Trim();
            }
            else
            {
                // 无 frontmatter，整个内容作为 body
                content.MarkdownBody = markdown.Trim();
            }
        }

        /// <summary>
        /// 解析 YAML frontmatter
        /// </summary>
        private void ParseYamlFrontmatter(string yaml, SkillContent content)
        {
            var lines = yaml.Split('\n');
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                var colonIndex = trimmed.IndexOf(':');
                if (colonIndex <= 0) continue;

                var key = trimmed.Substring(0, colonIndex).Trim().ToLowerInvariant();
                var value = trimmed.Substring(colonIndex + 1).Trim().Trim('"', '\'');

                switch (key)
                {
                    case "name":
                        // 🔧 修复：YAML 的 name 字段用于 DisplayName，不覆盖 Name（SkillCode）
                        // Name 应始终是数据库的 SkillCode（文件夹名，如 plc-skill-factory）
                        if (string.IsNullOrEmpty(content.DisplayName) || content.DisplayName == "未命名技能")
                            content.DisplayName = value;
                        break;
                    case "display_name":
                        // 支持显式的 display_name 字段
                        content.DisplayName = value;
                        break;
                    case "description":
                        if (string.IsNullOrEmpty(content.Description))
                            content.Description = value;
                        break;
                    case "license":
                        content.License = value;
                        break;
                    case "compatibility":
                        content.Compatibility = value;
                        break;
                    case "allowed-tools":
                        // 解析逗号分隔的工具列表
                        content.AllowedTools = value.Split(',')
                            .Select(t => t.Trim())
                            .Where(t => !string.IsNullOrEmpty(t))
                            .ToList();
                        break;
                }
            }
        }

        #endregion

        #region Prompt 构建

        /// <summary>
        /// 构建 Skills 注入的 Prompt 内容
        /// 渐进式披露模式：默认只输出摘要，提示 Agent 按需获取详情
        /// </summary>
        /// <param name="skills">技能列表</param>
        /// <param name="fullContent">是否输出完整内容（默认 false，实现渐进式披露）</param>
        public string BuildSkillsPrompt(List<SkillContent> skills, bool fullContent = false)
        {
            if (skills.Count == 0) return "";

            var sb = new StringBuilder();
            
            if (fullContent)
            {
                // 完整模式：输出所有内容（向后兼容）
                sb.AppendLine("\n\n【已加载的技能】");
                sb.AppendLine("以下技能提供了完成任务的策略指导和最佳实践：\n");

                foreach (var skill in skills)
                {
                    sb.AppendLine($"## {skill.DisplayName}");
                    
                    if (!string.IsNullOrEmpty(skill.Description))
                    {
                        sb.AppendLine($"**描述**: {skill.Description}");
                    }

                    if (skill.AllowedTools?.Count > 0)
                    {
                        sb.AppendLine($"**可用工具**: {string.Join(", ", skill.AllowedTools)}");
                    }

                    if (!string.IsNullOrEmpty(skill.MarkdownBody))
                    {
                        sb.AppendLine();
                        sb.AppendLine(skill.MarkdownBody);
                    }

                    sb.AppendLine();
                    sb.AppendLine("---");
                    sb.AppendLine();
                }
            }
            else
            {
                // 渐进式披露模式：只输出摘要，提示按需加载
                sb.AppendLine("\n\n【可用技能】");
                sb.AppendLine("以下技能可辅助完成任务。如需使用某技能，请先调用 `get_skill_details(skillName)` 获取完整说明。\n");

                foreach (var skill in skills)
                {
                    sb.Append($"- **{skill.Name}**");
                    if (!string.IsNullOrEmpty(skill.DisplayName) && skill.DisplayName != skill.Name)
                    {
                        sb.Append($" ({skill.DisplayName})");
                    }
                    sb.Append(": ");
                    
                    // 只显示简短描述（最多100字符）
                    var shortDesc = skill.Description ?? "";
                    if (shortDesc.Length > 100)
                    {
                        shortDesc = shortDesc.Substring(0, 100) + "...";
                    }
                    sb.AppendLine(shortDesc);

                    // 标记是否为 Agent Skill（可能包含脚本）
                    if (skill.IsAgentSkill)
                    {
                        sb.AppendLine($"  [Agent Skill - 可能包含可执行脚本]");
                    }
                }

                sb.AppendLine();
                sb.AppendLine("**提示**: 调用 `list_available_skills()` 查看所有技能，调用 `get_skill_details(skillName)` 获取详细说明。");
            }

            return sb.ToString();
        }

        /// <summary>
        /// 获取 Skills 允许使用的工具列表（用于过滤）
        /// </summary>
        public HashSet<string> GetAllowedToolsFromSkills(List<SkillContent> skills)
        {
            var allowedTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var skill in skills)
            {
                if (skill.AllowedTools != null)
                {
                    foreach (var tool in skill.AllowedTools)
                    {
                        allowedTools.Add(tool);
                    }
                }
            }

            // 如果没有明确指定，返回空集合（表示不限制）
            return allowedTools;
        }

        #endregion
    }

    /// <summary>
    /// Skill 内容模型
    /// </summary>
    public class SkillContent
    {
        /// <summary>
        /// Skill 数据库 ID
        /// </summary>
        public long SkillId { get; set; }

        /// <summary>
        /// Skill 名称（符合规范：1-64字符，小写字母数字连字符）
        /// </summary>
        public string Name { get; set; } = "";

        /// <summary>
        /// 显示名称
        /// </summary>
        public string DisplayName { get; set; } = "";

        /// <summary>
        /// 描述（1-1024字符）
        /// </summary>
        public string Description { get; set; } = "";

        /// <summary>
        /// 分类
        /// </summary>
        public string? Category { get; set; }

        /// <summary>
        /// 复杂度级别
        /// </summary>
        public string? ComplexityLevel { get; set; }

        /// <summary>
        /// 是否为 AgentSkill 格式
        /// </summary>
        public bool IsAgentSkill { get; set; }

        /// <summary>
        /// 许可证
        /// </summary>
        public string? License { get; set; }

        /// <summary>
        /// 兼容性
        /// </summary>
        public string? Compatibility { get; set; }

        /// <summary>
        /// 允许使用的工具列表
        /// </summary>
        public List<string>? AllowedTools { get; set; }

        /// <summary>
        /// Markdown 正文内容
        /// </summary>
        public string? MarkdownBody { get; set; }

        /// <summary>
        /// 技能包目录路径（如 plc-skill-factory/）
        /// 用于定位可执行脚本
        /// </summary>
        public string? SkillPackagePath { get; set; }
    }
}
