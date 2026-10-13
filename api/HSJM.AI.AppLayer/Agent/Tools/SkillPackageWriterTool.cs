using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.Agent.Tools
{
    /// <summary>
    /// Skill 包创建工具 - 供智能体调用以动态创建技能
    /// 
    /// 使用场景：
    /// - ParserCodeGenerator 智能体生成解析代码后调用此工具保存为 Skill
    /// - ParserDeployer 智能体部署解析器时调用此工具
    /// 
    /// 工作流程：
    /// 1. 智能体分析文件结构，生成 Python 解析脚本
    /// 2. 调用此工具，传入脚本代码和元数据
    /// 3. 工具创建 Skill 包目录、写入文件、注册数据库
    /// 4. 返回结果（待审批状态）
    /// </summary>
    public class SkillPackageWriterTool
    {
        private readonly ILogger<SkillPackageWriterTool> _logger;
        private readonly Services.SkillCreatorService _skillCreatorService;

        public SkillPackageWriterTool(
            ILogger<SkillPackageWriterTool> logger,
            Services.SkillCreatorService skillCreatorService)
        {
            _logger = logger;
            _skillCreatorService = skillCreatorService;
        }

        /// <summary>
        /// 创建 PLC 解析器 Skill 包
        /// 将生成的解析脚本保存为可复用的 Agent Skill
        /// </summary>
        [Description(@"创建 PLC 解析器 Skill 包。将生成的 Python 解析脚本保存为可复用技能。

【适用场景】
- 第一层智能体生成解析代码后，调用此工具保存为 Skill
- 解析器验证通过后，通过此工具部署到系统

【输出说明】
- 创建成功后，Skill 处于待审批状态
- 需要管理员审批后才能被第二层智能体使用

【返回值】
- Success: 是否成功
- SkillId: 新创建的技能ID
- ApprovalStatus: 审批状态（Pending）")]
        public async Task<Services.SkillCreationResult> CreateParserSkillAsync(
            [Description("技能唯一编码，建议格式: {brand}-{type}-parser，如 siemens-graph-parser")] string skillCode,
            [Description("技能显示名称，如: 西门子 GRAPH 解析器")] string skillName,
            [Description("技能描述，说明解析器的功能和适用范围")] string description,
            [Description("Python 主解析脚本代码（完整代码，不是文件路径）")] string pythonScript,
            [Description("适用的 PLC 品牌，如: Siemens/Mitsubishi/AB")] string brand,
            [Description("适用的文件格式，如: XML/AWL/SCL")] string fileFormat,
            [Description("可选: 使用说明（Markdown 格式）")] string? usageInstructions = null,
            [Description("可选: 生成此技能的任务ID")] long? generatedByTaskId = null,
            [Description("可选: 生成此技能的智能体角色ID")] long? generatedByAgentId = null)
        {
            _logger.LogInformation("[SkillPackageWriter] 开始创建: SkillCode={SkillCode}, Brand={Brand}",
                skillCode, brand);

            try
            {
                var request = new Services.SkillCreationRequest
                {
                    SkillCode = skillCode,
                    SkillName = skillName,
                    Description = description,
                    Category = "Analysis",
                    ComplexityLevel = "Advanced",
                    ApplicableBrand = brand,
                    ApplicableFileFormat = fileFormat,
                    UsageInstructions = usageInstructions,
                    GeneratedByTaskID = generatedByTaskId,
                    GeneratedByAgentID = generatedByAgentId,
                    AllowedTools = new List<string>
                    {
                        "read_file_lines",
                        "search_file_by_regex",
                        "execute_skill_script"
                    },
                    Scripts = new Dictionary<string, string>
                    {
                        { "main.py", pythonScript }
                    }
                };

                var result = await _skillCreatorService.CreateSkillPackageAsync(request);

                if (result.Success)
                {
                    _logger.LogInformation("[SkillPackageWriter] ✅ 创建成功: SkillId={SkillId}",
                        result.SkillId);
                }
                else
                {
                    _logger.LogWarning("[SkillPackageWriter] ⚠️ 创建失败: {Error}",
                        result.ErrorMessage);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SkillPackageWriter] ❌ 异常: SkillCode={SkillCode}", skillCode);
                return new Services.SkillCreationResult
                {
                    Success = false,
                    SkillCode = skillCode,
                    ErrorMessage = $"创建异常: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// 批量创建多脚本 Skill 包
        /// 支持包含多个脚本文件的复杂技能
        /// </summary>
        [Description(@"批量创建多脚本 Skill 包。支持包含多个 Python 脚本的复杂技能。

【适用场景】
- 复杂解析器需要多个脚本文件配合
- 需要包含辅助脚本（如 utils.py, parsers.py 等）

【scriptsJson 格式】
JSON 对象，key 为文件名，value 为代码内容：
{
  ""main.py"": ""主脚本代码..."",
  ""utils.py"": ""工具函数代码..."",
  ""parsers/graph_parser.py"": ""子目录脚本...""
}")]
        public async Task<Services.SkillCreationResult> CreateMultiScriptSkillAsync(
            [Description("技能唯一编码")] string skillCode,
            [Description("技能显示名称")] string skillName,
            [Description("技能描述")] string description,
            [Description("脚本文件 JSON，格式: {\"main.py\": \"代码\", \"utils.py\": \"代码\"}")] string scriptsJson,
            [Description("适用的 PLC 品牌")] string brand,
            [Description("适用的文件格式")] string fileFormat,
            [Description("可选: 允许使用的工具列表 JSON 数组")] string? allowedToolsJson = null,
            [Description("可选: 使用说明")] string? usageInstructions = null)
        {
            _logger.LogInformation("[SkillPackageWriter-Multi] 开始创建: SkillCode={SkillCode}", skillCode);

            try
            {
                // 解析脚本 JSON
                Dictionary<string, string>? scripts = null;
                if (!string.IsNullOrEmpty(scriptsJson))
                {
                    scripts = JsonSerializer.Deserialize<Dictionary<string, string>>(scriptsJson);
                }

                // 解析允许的工具列表
                List<string>? allowedTools = null;
                if (!string.IsNullOrEmpty(allowedToolsJson))
                {
                    allowedTools = JsonSerializer.Deserialize<List<string>>(allowedToolsJson);
                }

                var request = new Services.SkillCreationRequest
                {
                    SkillCode = skillCode,
                    SkillName = skillName,
                    Description = description,
                    Category = "Analysis",
                    ComplexityLevel = "Advanced",
                    ApplicableBrand = brand,
                    ApplicableFileFormat = fileFormat,
                    UsageInstructions = usageInstructions,
                    AllowedTools = allowedTools ?? new List<string>
                    {
                        "read_file_lines",
                        "execute_skill_script"
                    },
                    Scripts = scripts ?? new Dictionary<string, string>()
                };

                return await _skillCreatorService.CreateSkillPackageAsync(request);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "[SkillPackageWriter-Multi] JSON 解析失败");
                return new Services.SkillCreationResult
                {
                    Success = false,
                    SkillCode = skillCode,
                    ErrorMessage = $"JSON 格式错误: {ex.Message}"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SkillPackageWriter-Multi] 创建失败");
                return new Services.SkillCreationResult
                {
                    Success = false,
                    SkillCode = skillCode,
                    ErrorMessage = $"创建失败: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// 检查是否存在匹配的已批准解析器
        /// </summary>
        [Description(@"检查是否存在匹配的已批准解析器技能。

【使用时机】
- 处理 PLC 文件前，先检查是否有现成解析器
- 如果返回 null，则需要触发第一层智能体生成新解析器")]
        public async Task<SkillMatchResult> FindMatchingSkillAsync(
            [Description("PLC 品牌，如: Siemens/Mitsubishi")] string brand,
            [Description("文件格式，如: XML/AWL")] string fileFormat)
        {
            _logger.LogInformation("[SkillPackageWriter] 查找解析器: Brand={Brand}, Format={Format}",
                brand, fileFormat);

            try
            {
                var skill = await _skillCreatorService.FindApprovedSkillAsync(brand, fileFormat);

                if (skill != null)
                {
                    _logger.LogInformation("[SkillPackageWriter] ✅ 找到解析器: SkillId={SkillId}, Name={Name}",
                        skill.Id, skill.SkillName);

                    return new SkillMatchResult
                    {
                        Found = true,
                        SkillId = skill.Id,
                        SkillCode = skill.SkillCode,
                        SkillName = skill.SkillName,
                        SkillPackagePath = skill.SkillPackagePath
                    };
                }

                _logger.LogInformation("[SkillPackageWriter] 未找到匹配解析器，需要生成新的");
                return new SkillMatchResult
                {
                    Found = false,
                    Message = $"未找到适用于 {brand} {fileFormat} 的解析器，需要触发第一层智能体生成"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SkillPackageWriter] 查找失败");
                return new SkillMatchResult
                {
                    Found = false,
                    Message = $"查找失败: {ex.Message}"
                };
            }
        }
    }

    #region 结果模型

    /// <summary>
    /// Skill 匹配结果
    /// </summary>
    public class SkillMatchResult
    {
        public bool Found { get; set; }
        public long SkillId { get; set; }
        public string? SkillCode { get; set; }
        public string? SkillName { get; set; }
        public string? SkillPackagePath { get; set; }
        public string? Message { get; set; }

        public override string ToString()
        {
            if (!Found)
                return $"未找到匹配解析器: {Message}";

            return $@"找到匹配解析器:
- SkillId: {SkillId}
- SkillCode: {SkillCode}
- SkillName: {SkillName}
- SkillPackagePath: {SkillPackagePath}

可使用 execute_skill_script 执行此技能的脚本";
        }
    }

    #endregion
}
