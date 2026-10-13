using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.Agent.Services
{
    /// <summary>
    /// Skill 创建服务 - 用于动态创建 Agent Skill 包
    /// 
    /// 主要职责：
    /// 1. 创建技能包目录结构 (wwwroot/skills/{skillCode}/)
    /// 2. 写入 SKILL.md 和 Python 脚本文件
    /// 3. 注册到数据库 (AI_Skill_Definition)
    /// 4. 支持审批工作流 (ApprovalStatus: Pending → Approved/Rejected)
    /// 
    /// 使用场景：
    /// - 第一层智能体动态生成 PLC 解析器后，调用此服务保存为可复用技能
    /// </summary>
    public class SkillCreatorService : ISelfScopedAutoInject
    {
        private readonly ILogger<SkillCreatorService> _logger;
        private readonly ISkill_DefinitionService _skillDefinitionService;
        private readonly IWebHostEnvironment _environment;

        /// <summary>
        /// Skills 根目录（相对于 wwwroot）
        /// </summary>
        private const string SKILLS_ROOT_FOLDER = "skills";

        public SkillCreatorService(
            ILogger<SkillCreatorService> logger,
            ISkill_DefinitionService skillDefinitionService,
            IWebHostEnvironment environment)
        {
            _logger = logger;
            _skillDefinitionService = skillDefinitionService;
            _environment = environment;
        }

        #region 公开方法

        /// <summary>
        /// 创建新的 Agent Skill 包
        /// </summary>
        /// <param name="request">创建请求参数</param>
        /// <returns>创建结果</returns>
        public async Task<SkillCreationResult> CreateSkillPackageAsync(
            SkillCreationRequest request,
            CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("[Skill创建] 开始: SkillCode={SkillCode}, Name={Name}",
                request.SkillCode, request.SkillName);

            var result = new SkillCreationResult { SkillCode = request.SkillCode };

            try
            {
                // 1. 验证参数
                var validation = ValidateRequest(request);
                if (!validation.IsValid)
                {
                    result.Success = false;
                    result.ErrorMessage = validation.ErrorMessage;
                    return result;
                }

                // 2. 检查技能是否已存在
                var existingSkill = await _skillDefinitionService.GetFirstAsync(
                    s => s.SkillCode == request.SkillCode && s.IsDeleted == false);
                if (existingSkill != null)
                {
                    result.Success = false;
                    result.ErrorMessage = $"技能编码 '{request.SkillCode}' 已存在";
                    return result;
                }

                // 3. 创建目录结构
                var skillDir = GetSkillPackageFullPath(request.SkillCode);
                if (!Directory.Exists(skillDir))
                {
                    Directory.CreateDirectory(skillDir);
                    _logger.LogInformation("[Skill创建] 创建目录: {Path}", skillDir);
                }

                // 4. 生成并写入 SKILL.md
                var skillMarkdown = BuildSkillMarkdown(request);
                var skillMdPath = Path.Combine(skillDir, "SKILL.md");
                await File.WriteAllTextAsync(skillMdPath, skillMarkdown, Encoding.UTF8, cancellationToken);
                _logger.LogInformation("[Skill创建] 写入 SKILL.md: {Path}", skillMdPath);

                // 5. 写入脚本文件
                if (request.Scripts != null && request.Scripts.Count > 0)
                {
                    foreach (var script in request.Scripts)
                    {
                        var scriptPath = Path.Combine(skillDir, script.Key);
                        // 确保脚本子目录存在
                        var scriptDir = Path.GetDirectoryName(scriptPath);
                        if (!string.IsNullOrEmpty(scriptDir) && !Directory.Exists(scriptDir))
                        {
                            Directory.CreateDirectory(scriptDir);
                        }
                        await File.WriteAllTextAsync(scriptPath, script.Value, Encoding.UTF8, cancellationToken);
                        _logger.LogInformation("[Skill创建] 写入脚本: {Path}", scriptPath);
                    }
                }

                // 6. 注册到数据库
                var skillEntity = new Skill_DefinitionEntity
                {
                    SkillCode = request.SkillCode,
                    SkillName = request.SkillName,
                    Description = request.Description,
                    Category = request.Category ?? "Analysis",
                    ComplexityLevel = request.ComplexityLevel ?? "Intermediate",
                    ImplementationType = "Script",
                    IsAgentSkill = true,
                    SkillPackagePath = request.SkillCode,
                    IsEnabled = false, // 默认禁用，需要审批后启用
                    ApprovalStatus = "Pending",
                    GeneratedByTaskID = request.GeneratedByTaskID,
                    GeneratedByAgentID = request.GeneratedByAgentID,
                    ApplicableBrand = request.ApplicableBrand,
                    ApplicableFileFormat = request.ApplicableFileFormat,
                    SafetyLevel = 2
                };

                await _skillDefinitionService.InsertAsync(skillEntity);
                _logger.LogInformation("[Skill创建] 数据库注册成功: SkillId={SkillId}", skillEntity.Id);

                result.Success = true;
                result.SkillId = skillEntity.Id;
                result.SkillPackagePath = skillDir;
                result.ApprovalStatus = "Pending";

                _logger.LogInformation("[Skill创建] ✅ 完成: SkillCode={SkillCode}, SkillId={SkillId}",
                    request.SkillCode, skillEntity.Id);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Skill创建] ❌ 失败: SkillCode={SkillCode}", request.SkillCode);
                result.Success = false;
                result.ErrorMessage = $"创建失败: {ex.Message}";
                return result;
            }
        }

        /// <summary>
        /// 更新技能审批状态
        /// </summary>
        public async Task<bool> UpdateApprovalStatusAsync(
            long skillId,
            string status,
            long? approvedByUserId = null)
        {
            var skill = await _skillDefinitionService.GetFirstAsync(
                s => s.Id == skillId && s.IsDeleted == false);

            if (skill == null)
            {
                _logger.LogWarning("[Skill审批] 技能不存在: SkillId={SkillId}", skillId);
                return false;
            }

            skill.ApprovalStatus = status;

            // 审批通过则启用
            if (status == "Approved")
            {
                skill.IsEnabled = true;
            }

            await _skillDefinitionService.UpdateAsync(skill);
            _logger.LogInformation("[Skill审批] 状态更新: SkillId={SkillId}, Status={Status}",
                skillId, status);

            return true;
        }

        /// <summary>
        /// 根据品牌和格式查找已批准的技能
        /// </summary>
        public async Task<Skill_DefinitionEntity?> FindApprovedSkillAsync(
            string brand,
            string fileFormat)
        {
            return await _skillDefinitionService.GetFirstAsync(
                s => s.IsDeleted == false
                    && s.IsEnabled == true
                    && s.ApprovalStatus == "Approved"
                    && s.IsAgentSkill == true
                    && s.ApplicableBrand == brand
                    && s.ApplicableFileFormat == fileFormat);
        }

        #endregion

        #region 私有方法

        /// <summary>
        /// 获取技能包完整路径
        /// </summary>
        private string GetSkillPackageFullPath(string skillCode)
        {
            var webRoot = _environment.WebRootPath
                ?? Path.Combine(_environment.ContentRootPath, "wwwroot");

            // 确保 skills 根目录存在
            var skillsRoot = Path.Combine(webRoot, SKILLS_ROOT_FOLDER);
            if (!Directory.Exists(skillsRoot))
            {
                Directory.CreateDirectory(skillsRoot);
            }

            return Path.Combine(skillsRoot, skillCode);
        }

        /// <summary>
        /// 验证创建请求
        /// </summary>
        private (bool IsValid, string? ErrorMessage) ValidateRequest(SkillCreationRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.SkillCode))
                return (false, "SkillCode 不能为空");

            if (string.IsNullOrWhiteSpace(request.SkillName))
                return (false, "SkillName 不能为空");

            // SkillCode 格式验证：1-64字符，仅允许小写字母、数字、连字符
            if (!System.Text.RegularExpressions.Regex.IsMatch(
                request.SkillCode, @"^[a-z0-9][a-z0-9\-]{0,63}$"))
            {
                return (false, "SkillCode 格式无效，只允许小写字母、数字和连字符，长度1-64");
            }

            return (true, null);
        }

        /// <summary>
        /// 构建 SKILL.md 内容
        /// </summary>
        private string BuildSkillMarkdown(SkillCreationRequest request)
        {
            var sb = new StringBuilder();

            // YAML frontmatter
            sb.AppendLine("---");
            sb.AppendLine($"name: {request.SkillCode}");
            sb.AppendLine($"description: {request.Description ?? request.SkillName}");
            sb.AppendLine("license: MIT");
            sb.AppendLine("compatibility: \">=1.0.0\"");

            if (request.AllowedTools != null && request.AllowedTools.Count > 0)
            {
                sb.AppendLine($"allowed-tools: {string.Join(", ", request.AllowedTools)}");
            }

            sb.AppendLine("---");
            sb.AppendLine();

            // Markdown body
            sb.AppendLine($"# {request.SkillName}");
            sb.AppendLine();

            if (!string.IsNullOrEmpty(request.Description))
            {
                sb.AppendLine(request.Description);
                sb.AppendLine();
            }

            // 适用范围
            if (!string.IsNullOrEmpty(request.ApplicableBrand) ||
                !string.IsNullOrEmpty(request.ApplicableFileFormat))
            {
                sb.AppendLine("## 适用范围");
                if (!string.IsNullOrEmpty(request.ApplicableBrand))
                    sb.AppendLine($"- **PLC 品牌**: {request.ApplicableBrand}");
                if (!string.IsNullOrEmpty(request.ApplicableFileFormat))
                    sb.AppendLine($"- **文件格式**: {request.ApplicableFileFormat}");
                sb.AppendLine();
            }

            // 可用脚本
            if (request.Scripts != null && request.Scripts.Count > 0)
            {
                sb.AppendLine("## 可用脚本");
                foreach (var script in request.Scripts.Keys)
                {
                    sb.AppendLine($"- `{script}`: 执行脚本");
                }
                sb.AppendLine();
            }

            // 使用说明
            if (!string.IsNullOrEmpty(request.UsageInstructions))
            {
                sb.AppendLine("## 使用说明");
                sb.AppendLine(request.UsageInstructions);
                sb.AppendLine();
            }

            // 生成信息
            sb.AppendLine("## 生成信息");
            sb.AppendLine($"- **生成时间**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            if (request.GeneratedByTaskID.HasValue)
                sb.AppendLine($"- **任务ID**: {request.GeneratedByTaskID}");
            if (request.GeneratedByAgentID.HasValue)
                sb.AppendLine($"- **智能体ID**: {request.GeneratedByAgentID}");
            sb.AppendLine("- **状态**: 待审批");

            return sb.ToString();
        }

        #endregion
    }

    #region 数据模型

    /// <summary>
    /// Skill 创建请求
    /// </summary>
    public class SkillCreationRequest
    {
        /// <summary>
        /// 技能编码（唯一标识，如 siemens-graph-parser）
        /// </summary>
        public string SkillCode { get; set; } = "";

        /// <summary>
        /// 技能名称
        /// </summary>
        public string SkillName { get; set; } = "";

        /// <summary>
        /// 技能描述
        /// </summary>
        public string? Description { get; set; }

        /// <summary>
        /// 分类
        /// </summary>
        public string? Category { get; set; }

        /// <summary>
        /// 复杂度
        /// </summary>
        public string? ComplexityLevel { get; set; }

        /// <summary>
        /// 脚本文件 (key=文件名, value=代码内容)
        /// </summary>
        public Dictionary<string, string>? Scripts { get; set; }

        /// <summary>
        /// 允许使用的工具列表
        /// </summary>
        public List<string>? AllowedTools { get; set; }

        /// <summary>
        /// 使用说明
        /// </summary>
        public string? UsageInstructions { get; set; }

        /// <summary>
        /// 适用的 PLC 品牌
        /// </summary>
        public string? ApplicableBrand { get; set; }

        /// <summary>
        /// 适用的文件格式
        /// </summary>
        public string? ApplicableFileFormat { get; set; }

        /// <summary>
        /// 生成此技能的任务ID
        /// </summary>
        public long? GeneratedByTaskID { get; set; }

        /// <summary>
        /// 生成此技能的智能体角色ID
        /// </summary>
        public long? GeneratedByAgentID { get; set; }
    }

    /// <summary>
    /// Skill 创建结果
    /// </summary>
    public class SkillCreationResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public string SkillCode { get; set; } = "";
        public long SkillId { get; set; }
        public string? SkillPackagePath { get; set; }
        public string ApprovalStatus { get; set; } = "Pending";

        public override string ToString()
        {
            if (!Success)
                return $"❌ Skill 创建失败: {ErrorMessage}";

            return $@"✅ Skill 创建成功

技能编码: {SkillCode}
技能ID: {SkillId}
存储路径: {SkillPackagePath}
审批状态: {ApprovalStatus} (待审批后启用)";
        }
    }

    #endregion
}
