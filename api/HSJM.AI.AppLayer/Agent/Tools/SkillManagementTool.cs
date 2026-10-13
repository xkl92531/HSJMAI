using System;
using System.ComponentModel;
using System.Text.Json;
using System.Threading.Tasks;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.Utility;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.Agent.Tools
{
    /// <summary>
    /// 技能管理工具
    /// 用于创建和管理AI_Skill_Definition
    /// </summary>
    public class SkillManagementTool : ISelfScopedAutoInject
    {
        private readonly IAgent_Skill_RelationService _relationService;
        ISkill_DefinitionService _skillService;
        ILogger<SkillManagementTool> _logger;
        public SkillManagementTool(
            ISkill_DefinitionService skillService,
            IAgent_Skill_RelationService relationService,
            ILogger<SkillManagementTool> logger)
        {
            _skillService = skillService;
            _relationService = relationService;
            _logger = logger;
        }

        /// <summary>
        /// 保存技能定义及关联角色
        /// </summary>
        [Description("保存技能定义到数据库，并自动关联到指定角色(默认通用助手)。如果已存在则更新。")]
        public async Task<string> SaveSkillDefinitionAsync(
            [Description("技能代码（唯一标识，如：pdf-reader）")] string skillCode,
            [Description("技能显示名称")] string skillName,
            [Description("技能描述")] string description,
            [Description("技能包目录路径（如：pdf-reader/）")] string skillPackagePath,
            [Description("角色ID，默认2012(通用助手)")] long roleId = 2012)
        {
            try
            {
                _logger.LogInformation("[SkillManagement] 保存技能定义: {SkillCode}, RoleId={RoleId}", skillCode, roleId);

                // 1. 查找或创建技能定义
                var skill = await _skillService.GetFirstAsync(x => x.SkillCode == skillCode && !x.IsDeleted);
                bool isNew = false;
                
                if (skill == null)
                {
                    isNew = true;
                    skill = new Skill_DefinitionEntity
                    {
                        SkillCode = skillCode,
                        IsAgentSkill = true,
                        Category="Custom",
                        ApprovalStatus = "Approved", // 自动批准
                        IsEnabled = true,
                        Version = 1,
                        CreateTime = DateTime.Now
                    };
                }

                // 更新字段
                skill.SkillName = skillName.Length > 100 ? skillName.Substring(0, 100) : skillName;
                skill.Description = description;
                skill.SkillPackagePath = skillPackagePath;
                skill.ImplementationRef = $"{skillPackagePath}main.py";
                
                // 确保有默认Schema避免报错
                if (string.IsNullOrEmpty(skill.InputSchema)) 
                    skill.InputSchema = "{\"type\":\"object\",\"properties\":{}}";
                if (string.IsNullOrEmpty(skill.OutputSchema)) 
                    skill.OutputSchema = "{\"type\":\"object\",\"properties\":{\"success\":{\"type\":\"boolean\"}}}";

                if (isNew)
                    await _skillService.InsertAsync(skill);
                else
                    await _skillService.UpdateAsync(skill);

                // 2. 建立角色关联 (Upsert)
                var relation = await _relationService.GetFirstAsync(x => x.AgentRoleID == roleId && x.SkillID == skill.Id && !x.IsDeleted);
                if (relation == null)
                {
                    relation = new Agent_Skill_RelationEntity
                    {
                        AgentRoleID = roleId,
                        SkillID = skill.Id,
                        IsEnabled = true,
                        ProficiencyLevel = "Competent",
                        CreateTime = DateTime.Now
                    };
                    await _relationService.InsertAsync(relation);
                    _logger.LogInformation("[SkillManagement] 建立关联: Role={Role}, Skill={Skill}", roleId, skill.Id);
                }

                return JsonHelper.SerializeObject(new
                {
                    success = true,
                    skill_id = skill.Id,
                    skill_code = skill.SkillCode,
                    role_id = roleId,
                    is_new = isNew,
                    message = $"技能 '{skillName}' 已{(isNew ? "创建" : "更新")}并通过角色 {roleId} 关联"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SkillManagement] 保存技能失败: {SkillCode}", skillCode);
                return JsonHelper.SerializeObject(new { success = false, error = ex.Message });
            }
        }

        /// <summary>
        /// 检查技能代码是否已存在
        /// </summary>
        /// <param name="skillCode">技能代码（如：siemens-fb461-parser）</param>
        /// <returns>JSON格式结果：{exists: bool, skill_id?: long, skill_name?: string}</returns>
        [Description("检查技能代码是否已存在于数据库中")]
        public async Task<string> CheckSkillExistsAsync(
            [Description("技能代码（如：siemens-fb461-parser）")] string skillCode)
        {
            try
            {
                _logger.LogInformation("[SkillManagement] 检查技能是否存在: {SkillCode}", skillCode);

                var existing = await _skillService.GetFirstAsync(
                    x => x.SkillCode == skillCode && !x.IsDeleted);

                var result = new
                {
                    success = true,
                    exists = existing != null,
                    skill_id = existing?.Id,
                    skill_name = existing?.SkillName,
                    approval_status = existing?.ApprovalStatus
                };

                return JsonHelper.SerializeObject(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SkillManagement] 检查技能失败: {SkillCode}", skillCode);
                return JsonHelper.SerializeObject(new { success = false, error = ex.Message });
            }
        }

        /// <summary>
        /// 插入技能定义到数据库
        /// </summary>
        /// <param name="skillCode">技能代码（唯一标识）</param>
        /// <param name="skillName">技能显示名称</param>
        /// <param name="description">技能描述</param>
        /// <param name="skillPackagePath">技能包目录路径（如：siemens-fb461-parser/）</param>
        /// <param name="category">分类（Analysis/Execution）</param>
        /// <param name="applicableBrand">适用品牌（可选，如：Siemens）</param>
        /// <param name="applicableFileFormat">适用文件格式（可选，如：XML）</param>
        /// <returns>JSON格式结果：{success: bool, skill_id?: long, message?: string}</returns>
        [Description("将技能定义插入到数据库，状态为Pending等待用户确认")]
        public async Task<string> InsertSkillDefinitionAsync(
            [Description("技能代码（唯一标识，如：siemens-fb461-parser）")] string skillCode,
            [Description("技能显示名称")] string skillName,
            [Description("技能描述")] string description,
            [Description("技能包目录路径（如：siemens-fb461-parser/）")] string skillPackagePath,
            [Description("分类：Analysis 或 Execution")] string category = "Analysis",
            [Description("适用品牌（可选，如：Siemens）")] string? applicableBrand = null,
            [Description("适用文件格式（可选，如：XML）")] string? applicableFileFormat = null)
        {
            try
            {
                _logger.LogInformation("[SkillManagement] 插入技能定义: {SkillCode}", skillCode);

                // 检查是否已存在
                var existing = await _skillService.GetFirstAsync(
                    x => x.SkillCode == skillCode && !x.IsDeleted);

                if (existing != null)
                {
                    return JsonHelper.SerializeObject(new
                    {
                        success = false,
                        error = $"技能代码 {skillCode} 已存在 (ID={existing.Id})",
                        existing_id = existing.Id
                    });
                }

                // 创建实体
                var entity = new Skill_DefinitionEntity
                {
                    SkillCode = skillCode,
                    SkillName = skillName.Length > 100 ? skillName.Substring(0, 100) : skillName,
                    Description = description,
                    Category = category,
                    ComplexityLevel = "Intermediate",
                    ImplementationType = "Local",
                    ImplementationRef = $"{skillPackagePath}main.py",
                    IsAgentSkill = true,
                    SkillPackagePath = skillPackagePath,
                    ApprovalStatus = "Pending", // 待确认
                    ApplicableBrand = applicableBrand,
                    ApplicableFileFormat = applicableFileFormat,
                    IsEnabled = true,
                    Version = 1,
                    InputSchema = "{\"type\":\"object\",\"properties\":{\"file_path\":{\"type\":\"string\",\"description\":\"文件路径\"}},\"required\":[\"file_path\"]}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"success\":{\"type\":\"boolean\"}}}"
                };

                // 插入
                var newId = await _skillService.InsertAsync(entity);

                _logger.LogInformation("[SkillManagement] 技能定义已创建: ID={Id}, Code={Code}", newId, skillCode);

                return JsonHelper.SerializeObject(new
                {
                    success = true,
                    skill_id = entity.Id,
                    skill_code = skillCode,
                    approval_status = "Pending",
                    message = $"技能 '{skillName}' 已创建，状态为 Pending，等待用户确认"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SkillManagement] 插入技能定义失败: {SkillCode}", skillCode);
                return JsonHelper.SerializeObject(new { success = false, error = ex.Message });
            }
        }

        /// <summary>
        /// 批准技能定义
        /// </summary>
        /// <param name="skillId">技能ID</param>
        /// <returns>JSON格式结果</returns>
        [Description("批准技能定义，将状态从Pending改为Approved")]
        public async Task<string> ApproveSkillAsync(
            [Description("技能ID")] long skillId)
        {
            try
            {
                _logger.LogInformation("[SkillManagement] 批准技能: ID={Id}", skillId);

                var entity = await _skillService.GetFirstAsync(x => x.Id == skillId && !x.IsDeleted);
                if (entity == null)
                {
                    return JsonHelper.SerializeObject(new { success = false, error = $"技能ID {skillId} 不存在" });
                }

                entity.ApprovalStatus = "Approved";
                await _skillService.UpdateAsync(entity);

                return JsonHelper.SerializeObject(new
                {
                    success = true,
                    skill_id = skillId,
                    skill_code = entity.SkillCode,
                    approval_status = "Approved",
                    message = $"技能 '{entity.SkillName}' 已批准"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SkillManagement] 批准技能失败: ID={Id}", skillId);
                return JsonHelper.SerializeObject(new { success = false, error = ex.Message });
            }
        }
    }
}
