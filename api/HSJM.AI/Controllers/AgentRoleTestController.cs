using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.Agent.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.Controllers
{
    /// <summary>
    /// Agent 角色配置测试控制器
    /// 用途: 验证 AgentRoleConfigService 功能
    /// </summary>
    [ApiExplorerSettings(GroupName = "AgentTest")]
    [ApiController]
    [Route("api/[controller]")]
    public class AgentRoleTestController : ControllerBase
    {
        private readonly AgentRoleConfigService _roleConfigService;
        private readonly ILogger<AgentRoleTestController> _logger;

        public AgentRoleTestController(
            AgentRoleConfigService roleConfigService,
            ILogger<AgentRoleTestController> logger)
        {
            _roleConfigService = roleConfigService;
            _logger = logger;
        }

        /// <summary>
        /// 测试1: 创建简单角色（使用默认配置）
        /// </summary>
        [HttpPost("create-simple")]
        public async Task<IActionResult> CreateSimpleRole(
            [FromQuery] string roleName = "数据分析师",
            [FromQuery] string roleCode = "data-analyst")
        {
            try
            {
                var roleId = await _roleConfigService.CreateRoleWithConfigAsync(
                    roleName: roleName,
                    roleCode: roleCode,
                    agentType: "Standard",
                    systemPrompt: $"你是专业的{roleName}，擅长数据分析和可视化。"
                );

                return Ok(new
                {
                    success = true,
                    message = "角色创建成功",
                    roleId = roleId,
                    roleName = roleName,
                    roleCode = roleCode
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建角色失败");
                return StatusCode(500, new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }

        /// <summary>
        /// 测试2: 创建高级角色（自定义配置）
        /// </summary>
        [HttpPost("create-advanced")]
        public async Task<IActionResult> CreateAdvancedRole()
        {
            try
            {
                var config = new AgentConfigModel
                {
                    ThinkingMode = "PlanAndExecute",
                    MaxThinkingLoop = 10,
                    TimeoutSeconds = 600,
                    MaxRetryCount = 5,
                    MemoryStrategy = "LongTerm",
                    PlanningMethod = "Hybrid",
                    AllowParallel = true,
                    ModelParams = new Dictionary<string, object>
                    {
                        { "Temperature", 0.7 },
                        { "TopP", 0.9 },
                        { "MaxTokens", 4000 }
                    }
                };

                var roleId = await _roleConfigService.CreateRoleWithConfigAsync(
                    roleName: "工作流编排大师",
                    roleCode: "workflow-master",
                    agentType: "Workflow",
                    systemPrompt: "你是工作流编排专家，负责分解复杂任务并协调多个子智能体执行。",
                    agentConfig: config
                );

                return Ok(new
                {
                    success = true,
                    message = "高级角色创建成功",
                    roleId = roleId,
                    config = config
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建高级角色失败");
                return StatusCode(500, new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }

        /// <summary>
        /// 测试3: 批量创建测试角色（规划师、执行者、审核者）
        /// </summary>
        [HttpPost("create-team")]
        public async Task<IActionResult> CreateTeamRoles()
        {
            try
            {
                var roles = new List<object>();

                // 角色1: 规划师
                var plannerId = await _roleConfigService.CreateRoleWithConfigAsync(
                    roleName: "任务规划师",
                    roleCode: "planner",
                    agentType: "Standard",
                    systemPrompt: "你是任务规划专家，负责分解用户需求为可执行的子任务。",
                    agentConfig: new AgentConfigModel
                    {
                        ThinkingMode = "PlanAndExecute",
                        MaxThinkingLoop = 3,
                        PlanningMethod = "Sequential"
                    }
                );
                roles.Add(new { roleId = plannerId, roleName = "任务规划师", roleCode = "planner" });

                // 角色2: 执行者
                var executorId = await _roleConfigService.CreateRoleWithConfigAsync(
                    roleName: "任务执行者",
                    roleCode: "executor",
                    agentType: "Standard",
                    systemPrompt: "你是任务执行专家，负责高效完成分配的具体工作。",
                    agentConfig: new AgentConfigModel
                    {
                        ThinkingMode = "ReAct",
                        MaxThinkingLoop = 5,
                        AllowParallel = false
                    }
                );
                roles.Add(new { roleId = executorId, roleName = "任务执行者", roleCode = "executor" });

                // 角色3: 审核者
                var reviewerId = await _roleConfigService.CreateRoleWithConfigAsync(
                    roleName: "结果审核员",
                    roleCode: "reviewer",
                    agentType: "Standard",
                    systemPrompt: "你是质量审核专家，负责检查任务执行结果的正确性和完整性。",
                    agentConfig: new AgentConfigModel
                    {
                        ThinkingMode = "Sequential",
                        MaxThinkingLoop = 2,
                        MemoryStrategy = "ShortTerm"
                    }
                );
                roles.Add(new { roleId = reviewerId, roleName = "结果审核员", roleCode = "reviewer" });

                return Ok(new
                {
                    success = true,
                    message = "团队角色创建成功",
                    count = roles.Count,
                    roles = roles
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建团队角色失败");
                return StatusCode(500, new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }

        /// <summary>
        /// 测试4: 创建 Composite 类型角色
        /// </summary>
        [HttpPost("create-composite")]
        public async Task<IActionResult> CreateCompositeRole()
        {
            try
            {
                var roleId = await _roleConfigService.CreateRoleWithConfigAsync(
                    roleName: "智能调度员",
                    roleCode: "dispatcher",
                    agentType: "Composite",
                    systemPrompt: "你是智能调度员，根据任务类型自动选择合适的专家智能体执行。",
                    agentConfig: new AgentConfigModel
                    {
                        ThinkingMode = "PlanAndExecute",
                        MaxThinkingLoop = 8,
                        PlanningMethod = "Hybrid",
                        AllowParallel = true,
                        ExtendedConfig = new Dictionary<string, object>
                        {
                            { "SupportedExperts", new[] { "DataAnalyst", "TimeExpert", "WeatherAdvisor" } },
                            { "RoutingStrategy", "SmartRouting" }
                        }
                    }
                );

                return Ok(new
                {
                    success = true,
                    message = "Composite 角色创建成功",
                    roleId = roleId,
                    note = "此角色可协调多个子智能体"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建 Composite 角色失败");
                return StatusCode(500, new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }

        /// <summary>
        /// 测试5: 错误处理 - 重复编码
        /// </summary>
        [HttpPost("test-duplicate-code")]
        public async Task<IActionResult> TestDuplicateCode()
        {
            try
            {
                // 第一次创建
                var roleId1 = await _roleConfigService.CreateRoleWithConfigAsync(
                    roleName: "测试角色1",
                    roleCode: "test-duplicate",
                    agentType: "Standard"
                );

                // 第二次创建（应该失败）
                var roleId2 = await _roleConfigService.CreateRoleWithConfigAsync(
                    roleName: "测试角色2",
                    roleCode: "test-duplicate", // 重复编码
                    agentType: "Standard"
                );

                return Ok(new
                {
                    success = false,
                    message = "错误：应该抛出异常但没有"
                });
            }
            catch (InvalidOperationException ex)
            {
                // 预期异常
                return Ok(new
                {
                    success = true,
                    message = "✅ 重复编码检测正常",
                    error = ex.Message
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "测试失败");
                return StatusCode(500, new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }

        /// <summary>
        /// 测试6: 批量授权技能
        /// </summary>
        [HttpPost("grant-skills")]
        public async Task<IActionResult> GrantSkills(
            [FromQuery] long roleId,
            [FromBody] Dictionary<long, string>? skills = null)
        {
            try
            {
                // 默认技能列表（如果未提供）
                var skillsToGrant = skills ?? new Dictionary<long, string>
                {
                    { 1001, "Novice" },      // 假设技能ID 1001，新手级
                    { 1002, "Competent" },   // 技能ID 1002，胜任级
                    { 1003, "Proficient" },  // 技能ID 1003，熟练级
                    { 1004, "Expert" }       // 技能ID 1004，专家级
                };

                var count = await _roleConfigService.GrantSkillsAsync(roleId, skillsToGrant);

                return Ok(new
                {
                    success = true,
                    message = "技能授权成功",
                    roleId = roleId,
                    grantedCount = count,
                    skills = skillsToGrant.Select(kv => new
                    {
                        skillId = kv.Key,
                        proficiencyLevel = kv.Value,
                        proficiencyScore = kv.Value switch
                        {
                            "Novice" => 25,
                            "Competent" => 50,
                            "Proficient" => 75,
                            "Expert" => 100,
                            _ => 0
                        }
                    })
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "授权技能失败");
                return StatusCode(500, new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }

        /// <summary>
        /// 测试7: 创建角色并授权技能（完整流程）
        /// </summary>
        [HttpPost("create-role-with-skills")]
        public async Task<IActionResult> CreateRoleWithSkills()
        {
            try
            {
                // Step 1: 创建角色
                var roleId = await _roleConfigService.CreateRoleWithConfigAsync(
                    roleName: "全能助手",
                    roleCode: "omnipotent-assistant",
                    agentType: "Standard",
                    systemPrompt: "你是全能助手，掌握多项技能，可以处理各种复杂任务。",
                    agentConfig: new AgentConfigModel
                    {
                        ThinkingMode = "ReAct",
                        MaxThinkingLoop = 8,
                        AllowParallel = true
                    }
                );

                // Step 2: 授权技能
                var skills = new Dictionary<long, string>
                {
                    { 1001, "Expert" },      // 数据分析
                    { 1002, "Proficient" },  // 代码生成
                    { 1003, "Competent" },   // 文档撰写
                    { 1004, "Novice" }       // 图像处理
                };

                var grantedCount = await _roleConfigService.GrantSkillsAsync(roleId, skills);

                return Ok(new
                {
                    success = true,
                    message = "角色创建并授权技能成功",
                    roleId = roleId,
                    roleName = "全能助手",
                    skillCount = grantedCount,
                    skills = skills.Select(kv => new
                    {
                        skillId = kv.Key,
                        level = kv.Value
                    })
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建角色并授权技能失败");
                return StatusCode(500, new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }

        /// <summary>
        /// 测试9: 批量授权工具
        /// </summary>
        /// <param name="roleId">角色ID</param>
        [HttpPost("grant-tools")]
        public async Task<IActionResult> Test9_GrantTools(long roleId)
        {
            try
            {
                // 准备工具列表（使用测试数据中的工具ID）
                var toolIds = new List<long>
                {
                    2001,  // HTTP请求工具
                    2002,  // 数据库查询工具
                    2003,  // 文件操作工具
                    2004   // 邮件发送工具
                };

                var grantedCount = await _roleConfigService.GrantToolsAsync(roleId, toolIds);

                return Ok(new
                {
                    success = true,
                    message = $"授权工具成功，新增 {grantedCount} 个工具",
                    roleId = roleId,
                    grantedCount = grantedCount,
                    toolIds = toolIds
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "授权工具失败");
                return StatusCode(500, new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }

        /// <summary>
        /// 测试10: 查询完整角色配置
        /// </summary>
        /// <param name="roleId">角色ID</param>
        [HttpGet("full-config")]
        public async Task<IActionResult> Test10_GetFullConfig(long roleId)
        {
            try
            {
                var fullConfig = await _roleConfigService.GetFullConfigAsync(roleId);

                return Ok(new
                {
                    success = true,
                    message = "查询完整配置成功",
                    data = fullConfig
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查询完整配置失败");
                return StatusCode(500, new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }

        /// <summary>
        /// 测试11: 完整流程测试（创建角色 + 授权技能 + 授权工具 + 查询配置）
        /// </summary>
        [HttpPost("full-workflow")]
        public async Task<IActionResult> Test11_FullWorkflow()
        {
            try
            {
                // Step 1: 创建角色
                var roleId = await _roleConfigService.CreateRoleWithConfigAsync(
                    roleName: "完整流程测试角色",
                    roleCode: "full-workflow-test",
                    agentType: "Composite",
                    systemPrompt: "完整流程测试：创建 + 技能 + 工具 + 查询",
                    agentConfig: new AgentConfigModel
                    {
                        ThinkingMode = "ToT",
                        MaxThinkingLoop = 5,
                        AllowParallel = true,
                        PlanningMethod = "Hierarchical"
                    }
                );

                // Step 2: 授权技能
                var skills = new Dictionary<long, string>
                {
                    { 1001, "Expert" },
                    { 1002, "Proficient" }
                };
                var skillCount = await _roleConfigService.GrantSkillsAsync(roleId, skills);

                // Step 3: 授权工具
                var toolIds = new List<long> { 2001, 2002, 2003 };
                var toolCount = await _roleConfigService.GrantToolsAsync(roleId, toolIds);

                // Step 4: 查询完整配置
                var fullConfig = await _roleConfigService.GetFullConfigAsync(roleId);

                return Ok(new
                {
                    success = true,
                    message = "完整流程测试成功",
                    summary = new
                    {
                        roleId = roleId,
                        skillsGranted = skillCount,
                        toolsGranted = toolCount
                    },
                    fullConfig = fullConfig
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "完整流程测试失败");
                return StatusCode(500, new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }

        /// <summary>
        /// 测试8: 更新已有技能熟练度
        /// </summary>
        [HttpPost("update-skill-proficiency")]
        public async Task<IActionResult> UpdateSkillProficiency(
            [FromQuery] long roleId)
        {
            try
            {
                // 先授权初级技能
                var initialSkills = new Dictionary<long, string>
                {
                    { 1001, "Novice" },
                    { 1002, "Novice" }
                };
                await _roleConfigService.GrantSkillsAsync(roleId, initialSkills);

                // 升级为高级
                var upgradedSkills = new Dictionary<long, string>
                {
                    { 1001, "Expert" },     // 从 Novice 升级到 Expert
                    { 1002, "Proficient" }, // 从 Novice 升级到 Proficient
                    { 1003, "Competent" }   // 新增技能
                };
                var count = await _roleConfigService.GrantSkillsAsync(roleId, upgradedSkills);

                return Ok(new
                {
                    success = true,
                    message = "技能熟练度更新成功",
                    roleId = roleId,
                    operationCount = count,
                    note = "1001和1002已升级，1003为新增"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新技能熟练度失败");
                return StatusCode(500, new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }
    }
}
