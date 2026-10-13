using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace HSJM.AI.AppLayer.Agent.Services
{
    /// <summary>
    /// 角色配置管理服务
    /// 职责：角色创建、配置管理、权限授予、协作关系
    /// 
    /// 说明：配置信息存储在 AI_Agent_Role.AgentConfig 字段（JSON格式），
    ///       不存在独立的 AI_Agent_Config 表
    /// </summary>
    public class AgentRoleConfigService : ISelfScopedAutoInject
    {
        private readonly IAgent_RoleService _roleService;
        private readonly IAgent_RelationService _relationService;
        private readonly IRole_Tool_RelationService _roleToolService;
        private readonly IAgent_Skill_RelationService _roleSkillService;
        private readonly IPrompt_VersionService _promptVersionService;
        private readonly ILogger<AgentRoleConfigService> _logger;

        public AgentRoleConfigService(
            IAgent_RoleService roleService,
            IAgent_RelationService relationService,
            IRole_Tool_RelationService roleToolService,
            IAgent_Skill_RelationService roleSkillService,
            IPrompt_VersionService promptVersionService,
            ILogger<AgentRoleConfigService> logger)
        {
            _roleService = roleService;
            _relationService = relationService;
            _roleToolService = roleToolService;
            _roleSkillService = roleSkillService;
            _promptVersionService = promptVersionService;
            _logger = logger;
        }

        #region 角色创建（完整流程）

        /// <summary>
        /// 创建角色（一站式流程）
        /// 
        /// 实现思路：
        /// 1. 创建 AI_Agent_Role 主记录（RoleName, RoleCode, AgentType, SystemPrompt 等）
        /// 2. AgentConfig 字段存储 JSON 配置
        /// 3. 自动创建 AI_Prompt_Version（v1.0.0，100% 流量）
        /// 4. 事务保证两表同时成功
        /// 
        /// 注意：配置不单独存表，而是以 JSON 存在 AI_Agent_Role.AgentConfig 字段
        /// 对应表：AI_Agent_Role, AI_Prompt_Version
        /// </summary>
        public async Task<long> CreateRoleWithConfigAsync(
            string roleName,
            string roleCode,
            string agentType = "Standard",
            string? systemPrompt = null,
            AgentConfigModel? agentConfig = null)
        {
            _logger.LogInformation("[角色创建] 开始: RoleName={RoleName}, Code={Code}, Type={Type}", 
                roleName, roleCode, agentType);

            var startTime = DateTime.Now;

            // 参数校验
            if (string.IsNullOrWhiteSpace(roleName))
                throw new ArgumentException("角色名称不能为空", nameof(roleName));
            if (string.IsNullOrWhiteSpace(roleCode))
                throw new ArgumentException("角色编码不能为空", nameof(roleCode));

            // 检查编码是否重复
            var existingRole = await _roleService.GetFirstAsync(r => r.RoleCode == roleCode && r.IsDeleted == false);
            if (existingRole != null)
            {
                _logger.LogWarning("[角色创建] 编码冲突: {Code} 已存在", roleCode);
                throw new InvalidOperationException($"角色编码 {roleCode} 已存在");
            }

            // 准备默认配置
            var config = agentConfig ?? new AgentConfigModel();
            var configJson = JsonSerializer.Serialize(config, new JsonSerializerOptions 
            { 
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            long roleId = 0;

            // 开启事务
            using (var tran = _roleService.BeginTran())
            {
                try
                {
                    // Step 1: 创建角色记录
                    var role = new Agent_RoleEntity
                    {
                        RoleName = roleName,
                        RoleCode = roleCode,
                        Description = $"{roleName} - 自动生成于 {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                        AgentType = agentType,
                        SystemPrompt = systemPrompt ?? $"你是 {roleName}，负责协助用户完成任务。",
                        AgentConfig = configJson, // ✅ 核心：JSON配置存储在此字段
                        IsEnabled = true,
                        IsPublic = false,
                        Version = 1,
                        CreateTime = DateTime.Now
                    };

                    await _roleService.InsertAsync(role); // ✅ Entity已有雪花ID
                    roleId = role.Id; // ✅ 从Entity获取ID
                    _logger.LogInformation("[角色创建] Step 1/2: 角色记录已创建, ID={RoleId}", roleId);

                    // Step 2: 创建默认 Prompt 版本（v1.0.0）
                    var promptVersion = new Prompt_VersionEntity
                    {
                        RoleID = roleId,
                        VersionNumber = "1.0.0",
                        SystemPromptContent = role.SystemPrompt ?? "",
                        ChangeLog = "初始版本",
                        IsActive = true,      // ✅ 激活状态
                        IsABTest = false,
                        TrafficRatio = 100,   // ✅ 100% 流量
                        AverageScore = 0,
                        UsageCount = 0,
                        CreateTime = DateTime.Now
                    };

                    await _promptVersionService.InsertAsync(promptVersion);
                    _logger.LogInformation("[角色创建] Step 2/2: Prompt版本已创建, Version=1.0.0");

                    // 提交事务
                    tran.Complete();

                    var duration = (int)(DateTime.Now - startTime).TotalMilliseconds;
                    _logger.LogInformation("[角色创建] ✅ 成功: RoleId={RoleId}, 耗时={Duration}ms", 
                        roleId, duration);

                    return roleId;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[角色创建] ❌ 失败: {Message}", ex.Message);
                    throw;
                }
            }
        }

        #endregion

        #region 权限授予

        /// <summary>
        /// 批量授权工具
        /// 
        /// 实现思路：
        /// 1. 检查角色是否存在
        /// 2. 遍历 toolIds，写入 Role_Tool_Relation
        /// 3. 去重检查（避免重复授权）
        /// 
        /// 对应表：Role_Tool_Relation
        /// 
        /// 实现逻辑：
        /// 1. 检查角色存在性
        /// 2. 查询已授权工具（去重）
        /// 3. 批量插入新授权关系
        /// 4. 事务保证
        /// </summary>
        public async Task<int> GrantToolsAsync(long roleId, List<long> toolIds)
        {
            _logger.LogInformation("[工具授权] 开始: RoleId={RoleId}, Count={Count}", roleId, toolIds.Count);
            var startTime = DateTime.Now;

            // 参数校验
            if (toolIds == null || !toolIds.Any())
                throw new ArgumentException("工具列表不能为空", nameof(toolIds));

            // 检查角色存在性
            var role = await _roleService.GetByIdAsync(roleId);
            if (role == null || role.IsDeleted)
                throw new InvalidOperationException($"角色不存在: RoleId={roleId}");

            // 查询已授权工具（去重）
            var existingRelations = await _roleToolService.GetListAsync(
                r => r.RoleID == roleId && toolIds.Contains(r.ToolID.Value) && r.IsDeleted == false);
            var existingToolIds = existingRelations.Select(r => r.ToolID.Value).ToHashSet();

            // 过滤出待新增的工具
            var newToolIds = toolIds.Where(tid => !existingToolIds.Contains(tid)).ToList();

            if (!newToolIds.Any())
            {
                _logger.LogWarning("[工具授权] 所有工具已授权，跳过");
                return 0;
            }

            int insertedCount = 0;

            // 批量插入
            using (var tran = _roleToolService.BeginTran())
            {    
                foreach (var toolId in newToolIds)
                {
                    var relation = new Role_Tool_RelationEntity
                    {
                        RoleID = roleId,
                        ToolID = toolId,
                        OverrideConfig = null
                        // CreatedBy/UpdatedBy 由 ORM 底层自动处理
                    };

                    await _roleToolService.InsertAsync(relation);
                    insertedCount++;
                }

                tran.Complete();
            }

            var elapsed = (DateTime.Now - startTime).TotalMilliseconds;
            _logger.LogInformation("[工具授权] 完成: 新增 {Count} 个工具，耗时 {Elapsed}ms", insertedCount, elapsed);

            return insertedCount;
        }

        /// <summary>
        /// 批量授权技能（带熟练度）
        /// 
        /// 实现思路：
        /// 1. 检查角色是否存在
        /// 2. 写入 AI_Agent_Skill_Relation（ProficiencyLevel 初始值）
        /// 3. 支持更新已有授权的熟练度
        /// 
        /// 对应表：AI_Agent_Skill_Relation
        /// 字段说明：ProficiencyLevel（字符串）Novice/Competent/Proficient/Expert
        ///         ProficiencyScore（整数）0-100
        /// </summary>
        public async Task<int> GrantSkillsAsync(long roleId, Dictionary<long, string> skillsWithProficiency)
        {
            _logger.LogInformation("[技能授权] 开始: RoleId={RoleId}, SkillCount={Count}", 
                roleId, skillsWithProficiency.Count);

            var startTime = DateTime.Now;

            // 参数校验
            if (skillsWithProficiency == null || skillsWithProficiency.Count == 0)
            {
                _logger.LogWarning("[技能授权] 技能列表为空");
                return 0;
            }

            // 检查角色是否存在
            var role = await _roleService.GetByIdAsync(roleId);
            if (role == null || role.IsDeleted)
            {
                throw new InvalidOperationException($"角色不存在: RoleId={roleId}");
            }

            // 查询已有授权（去重用）
            var existingRelations = await _roleSkillService.GetListAsync(
                r => r.AgentRoleID == roleId && r.IsDeleted == false);
            var existingSkillIds = existingRelations.Select(r => r.SkillID).ToHashSet();

            int insertCount = 0;
            int updateCount = 0;

            using (var tran = _roleSkillService.BeginTran())
            {
                try
                {
                    foreach (var (skillId, proficiencyLevel) in skillsWithProficiency)
                    {
                        // 计算熟练度积分
                        int proficiencyScore = proficiencyLevel switch
                        {
                            "Novice" => 25,
                            "Competent" => 50,
                            "Proficient" => 75,
                            "Expert" => 100,
                            _ => 0
                        };

                        if (existingSkillIds.Contains(skillId))
                        {
                            // 更新已有授权
                            var existing = existingRelations.First(r => r.SkillID == skillId);
                            existing.ProficiencyLevel = proficiencyLevel;
                            existing.ProficiencyScore = proficiencyScore;
                            existing.ModifyTime = DateTime.Now;

                            await _roleSkillService.UpdateAsync(existing);
                            updateCount++;
                            _logger.LogDebug("[技能授权] 更新: SkillId={SkillId}, Level={Level}", 
                                skillId, proficiencyLevel);
                        }
                        else
                        {
                            // 新增授权
                            var relation = new Agent_Skill_RelationEntity
                            {
                                AgentRoleID = roleId,
                                SkillID = skillId,
                                ProficiencyLevel = proficiencyLevel,
                                ProficiencyScore = proficiencyScore,
                                AcquiredDate = DateTime.Now,
                                ExecutionCount = 0,
                                SuccessCount = 0,
                                FailureCount = 0,
                                TotalExecutionTime = 0,
                                IsEnabled = true,
                                CreateTime = DateTime.Now
                            };

                            await _roleSkillService.InsertAsync(relation);
                            insertCount++;
                            _logger.LogDebug("[技能授权] 新增: SkillId={SkillId}, Level={Level}", 
                                skillId, proficiencyLevel);
                        }
                    }

                    tran.Complete();

                    var duration = (int)(DateTime.Now - startTime).TotalMilliseconds;
                    _logger.LogInformation("[技能授权] ✅ 成功: Insert={Insert}, Update={Update}, 耗时={Duration}ms", 
                        insertCount, updateCount, duration);

                    return insertCount + updateCount;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[技能授权] ❌ 失败: {Message}", ex.Message);
                    throw;
                }
            }
        }

        /// <summary>
        /// 撤销工具授权
        /// </summary>
        public async Task<bool> RevokeToolAsync(long roleId, long toolId)
        {
            // TODO: 软删除或删除 Role_Tool_Relation 记录
            
            _logger.LogInformation("撤销工具: RoleId={RoleId}, ToolId={ToolId} (占位实现)", 
                roleId, toolId);
            
            throw new NotImplementedException("撤销工具逻辑待实现");
        }

        #endregion

        #region 协作关系

        /// <summary>
        /// 创建角色协作关系（Planner → Worker, Supervisor → Executor 等）
        /// 
        /// 实现思路：
        /// 1. 检查父子角色是否存在
        /// 2. 写入 AI_Agent_Relation（RelationType: Planner-Worker/Supervisor-Executor）
        /// 3. 防止重复关系
        /// 
        /// 对应表：AI_Agent_Relation
        /// 参考：WorkflowOrchestrationApp 中的协作模式
        /// </summary>
        public async Task<long> CreateRelationAsync(
            long parentRoleId, 
            long childRoleId, 
            string relationType)
        {
            // TODO: 创建协作关系
            
            _logger.LogInformation("创建协作关系: {Parent} -> {Child} ({Type}) (占位实现)", 
                parentRoleId, childRoleId, relationType);
            
            throw new NotImplementedException("协作关系创建待实现");
        }

        /// <summary>
        /// 获取角色的下游协作者（子角色列表）
        /// </summary>
        public async Task<List<Agent_RoleEntity>> GetChildRolesAsync(
            long parentRoleId, 
            string? relationType = null)
        {
            // TODO: 查询 AI_Agent_Relation 和 AI_Agent_Role
            
            _logger.LogInformation("查询子角色: ParentId={ParentId} (占位实现)", parentRoleId);
            
            throw new NotImplementedException("子角色查询待实现");
        }

        #endregion

        #region 配置管理

        /// <summary>
        /// 更新角色配置（ThinkingMode, MaxRetryCount, TimeoutSeconds 等）
        /// 
        /// 对应表：AI_Agent_Config
        /// </summary>
        public async Task<bool> UpdateConfigAsync(long roleId, Dictionary<string, object> configUpdates)
        {
            // TODO: 更新 AI_Agent_Config
            
            _logger.LogInformation("更新配置: RoleId={RoleId} (占位实现)", roleId);
            
            throw new NotImplementedException("配置更新待实现");
        }

        /// <summary>
        /// 获取角色完整配置（包含角色信息、配置、权限统计）
        /// 
        /// 返回：角色信息 + 配置 + 工具数量 + 技能数量 + 当前 Prompt 版本
        /// 
        /// 实现逻辑：
        /// 1. 查询角色基本信息 + AgentConfig 反序列化
        /// 2. 并行查询技能、工具、协作关系
        /// 3. 统计执行成功率（基于 Agent_Skill_Relation）
        /// 4. 组装完整配置对象
        /// </summary>
        public async Task<object> GetFullConfigAsync(long roleId)
        {
            _logger.LogInformation("[完整配置] 查询: RoleId={RoleId}", roleId);

            // 1. 查询角色基本信息
            var role = await _roleService.GetByIdAsync(roleId);
            if (role == null || role.IsDeleted)
                throw new InvalidOperationException($"角色不存在: RoleId={roleId}");

            // 2. 反序列化配置
            AgentConfigModel? config = null;
            if (!string.IsNullOrWhiteSpace(role.AgentConfig))
            {
                try
                {
                    config = JsonSerializer.Deserialize<AgentConfigModel>(role.AgentConfig);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("[完整配置] JSON 反序列化失败: {Error}", ex.Message);
                }
            }

            // 3. 并行查询关联数据（过滤逻辑删除）
            var skillRelationsTask = _roleSkillService.GetListAsync(
                r => r.AgentRoleID == roleId && r.IsEnabled == true && r.IsDeleted == false);
            var toolRelationsTask = _roleToolService.GetListAsync(
                r => r.RoleID == roleId && r.IsDeleted == false);
            var childRelationsTask = _relationService.GetListAsync(
                r => r.ParentRoleID == roleId && r.IsDeleted == false);

            await Task.WhenAll(skillRelationsTask, toolRelationsTask, childRelationsTask);

            var skillRelations = await skillRelationsTask;
            var toolRelations = await toolRelationsTask;
            var childRelations = await childRelationsTask;

            // 4. 计算执行统计
            var totalExecutions = skillRelations.Sum(s => s.ExecutionCount ?? 0);
            var successExecutions = skillRelations.Sum(s => s.SuccessCount ?? 0);
            var failedExecutions = skillRelations.Sum(s => s.FailureCount ?? 0);
            var successRate = totalExecutions > 0 ? (double)successExecutions / totalExecutions * 100 : 0;

            // 5. 组装完整配置
            var fullConfig = new
            {
                // 基本信息
                RoleId = role.Id,
                RoleName = role.RoleName,
                RoleCode = role.RoleCode,
                AgentType = role.AgentType,
                SystemPrompt = role.SystemPrompt,
                IsEnabled = role.IsEnabled,
                CreateTime = role.CreateTime,

                // 配置详情
                Config = config,

                // 技能列表
                Skills = skillRelations.Select(s => new
                {
                    SkillId = s.SkillID,
                    ProficiencyLevel = s.ProficiencyLevel,
                    ProficiencyScore = s.ProficiencyScore,
                    ExecutionCount = s.ExecutionCount,
                    SuccessCount = s.SuccessCount,
                    FailureCount = s.FailureCount,
                    SuccessRate = (s.ExecutionCount ?? 0) > 0 
                        ? (double)(s.SuccessCount ?? 0) / (s.ExecutionCount.Value) * 100 
                        : 0
                }).ToList(),

                // 工具列表
                Tools = toolRelations.Select(t => new
                {
                    ToolId = t.ToolID,
                    OverrideConfig = t.OverrideConfig
                }).ToList(),

                // 子角色
                ChildRoles = childRelations.Select(c => new
                {
                    ChildRoleId = c.ChildRoleID,
                    RelationType = c.RelationType,
                    Description = c.Description
                }).ToList(),

                // 执行统计
                Statistics = new
                {
                    TotalExecutions = totalExecutions,
                    SuccessExecutions = successExecutions,
                    FailedExecutions = failedExecutions,
                    SuccessRate = Math.Round(successRate, 2),
                    SkillCount = skillRelations.Count,
                    ToolCount = toolRelations.Count,
                    ChildRoleCount = childRelations.Count
                }
            };

            _logger.LogInformation("[完整配置] 查询完成: Skills={SkillCount}, Tools={ToolCount}", 
                skillRelations.Count, toolRelations.Count);

            return fullConfig;
        }

        #endregion
    }

    /// <summary>
    /// Agent 配置模型（对应 AI_Agent_Role.AgentConfig JSON 字段）
    /// 参考: WorkflowOrchestrationApp 中的智能体配置
    /// </summary>
    public class AgentConfigModel
    {
        /// <summary>
        /// 思考模式: ReAct / PlanAndExecute / Sequential
        /// </summary>
        public string ThinkingMode { get; set; } = "ReAct";

        /// <summary>
        /// 最大思考循环次数（防止无限循环）
        /// </summary>
        public int MaxThinkingLoop { get; set; } = 5;

        /// <summary>
        /// 执行超时时间（秒）
        /// </summary>
        public int TimeoutSeconds { get; set; } = 300;

        /// <summary>
        /// 失败重试次数
        /// </summary>
        public int MaxRetryCount { get; set; } = 3;

        /// <summary>
        /// 记忆策略: ShortTerm / LongTerm / Hybrid
        /// </summary>
        public string MemoryStrategy { get; set; } = "ShortTerm";

        /// <summary>
        /// 规划方法: Sequential / Parallel / Hybrid
        /// </summary>
        public string PlanningMethod { get; set; } = "Sequential";

        /// <summary>
        /// 是否允许并行执行子任务
        /// </summary>
        public bool AllowParallel { get; set; } = false;

        /// <summary>
        /// 模型参数覆盖（Temperature, TopP 等）
        /// </summary>
        public Dictionary<string, object>? ModelParams { get; set; }

        /// <summary>
        /// 扩展配置（预留字段）
        /// </summary>
        public Dictionary<string, object>? ExtendedConfig { get; set; }
    }
}
