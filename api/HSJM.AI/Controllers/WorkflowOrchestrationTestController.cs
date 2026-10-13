using HSJM.AI.AppLayer.Agent.Services;
using HSJM.AI.AppLayer.Agent.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace HSJM.AI.Controllers
{
    /// <summary>
    /// 工作流编排测试控制器
    /// </summary>
    [ApiExplorerSettings(GroupName = "AgentTest")]
    [ApiController]
    [Route("api/WorkflowTest")]
    public class WorkflowOrchestrationTestController : ControllerBase
    {
        private readonly WorkflowOrchestrationService _workflowService;
        private readonly ILogger<WorkflowOrchestrationTestController> _logger;

        public WorkflowOrchestrationTestController(
            WorkflowOrchestrationService workflowService,
            ILogger<WorkflowOrchestrationTestController> logger)
        {
            _workflowService = workflowService;
            _logger = logger;
        }

        /// <summary>
        /// 测试1: 条件路由模式
        /// 示例: Router 决策后调用技术专家、业务专家协作
        /// </summary>
        [HttpPost("conditional-routing")]
        public async Task<IActionResult> TestConditionalRouting([FromBody] ConditionalRoutingRequest request)
        {
            try
            {
                _logger.LogInformation("[测试] 条件路由: {Input}", request.UserInput);

                var result = await _workflowService.RunConditionalRoutingAsync(
                    request.UserInput,
                    request.RouterRoleId,
                    request.ExpertRoleIds
                );

                return Ok(new
                {
                    success = result.Success,
                    workflowId = result.WorkflowId.ToString(), // 转为字符串避免JS精度丢失
                    routerDecision = result.RouterDecision,
                    expertsUsed = result.ExpertsUsed,
                    result = result.Result,
                    executionTimeMs = result.ExecutionTimeMs,
                    errorMessage = result.ErrorMessage
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[测试] 条件路由失败");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// 测试2: FanOut/FanIn 并行处理
        /// 示例: 同时调用多个智能体并行处理，然后聚合结果
        /// </summary>
        [HttpPost("fanout-fanin")]
        public async Task<IActionResult> TestFanOutFanIn([FromBody] FanOutFanInRequest request)
        {
            try
            {
                _logger.LogInformation("[测试] FanOut/FanIn: 并行任务数={Count}", request.ParallelRoleIds.Count);

                var result = await _workflowService.RunFanOutFanInAsync(
                    request.UserInput,
                    request.ParallelRoleIds
                );

                return Ok(new
                {
                    success = result.Success,
                    workflowId = result.WorkflowId.ToString(), // 转为字符串避免JS精度丢失
                    parallelCount = request.ParallelRoleIds.Count,
                    result = result.Result,
                    executionTimeMs = result.ExecutionTimeMs,
                    errorMessage = result.ErrorMessage
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[测试] FanOut/FanIn 失败");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// 测试3: 顺序链模式
        /// 示例: Planner 规划 → Executor 执行 → Reviewer 审核
        /// </summary>
        [HttpPost("sequential-chain")]
        public async Task<IActionResult> TestSequentialChain([FromBody] SequentialChainRequest request)
        {
            try
            {
                _logger.LogInformation("[测试] 顺序链: {Input}", request.UserInput);

                var result = await _workflowService.RunSequentialChainAsync(
                    request.UserInput,
                    request.PlannerRoleId,
                    request.ExecutorRoleId,
                    request.ReviewerRoleId
                );

                return Ok(new
                {
                    success = result.Success,
                    workflowId = result.WorkflowId.ToString(), // 转为字符串避免JS精度丢失
                    stepsCompleted = result.StepsCompleted,
                    result = result.Result,
                    executionTimeMs = result.ExecutionTimeMs,
                    errorMessage = result.ErrorMessage
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[测试] 顺序链失败");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// 🆕 统一入口：智能编排模式（推荐使用）
        /// 用户只需输入需求，AI自动选择Planner并生成执行计划
        /// </summary>
        [HttpPost("simple-run")]
        public async Task<IActionResult> SimpleRun([FromBody] SimpleRunRequest request)
        {
            try
            {
                _logger.LogInformation("[智能编排] 用户需求: {Input}", request.UserInput);

                var result = await _workflowService.RunAgentOrchestrationAsync(
                    request.UserInput,
                    request.PlannerRoleId, // 可为空，系统自动选择
                    null // sessionId
                );

                return Ok(new
                {
                    success = result.Success,
                    workflowId = result.WorkflowId.ToString(),
                    result = result.Result,
                    errorMessage = result.ErrorMessage
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[智能编排] 执行失败");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// 测试4: 完整场景 - 多智能体协作分析 PLC 故障
        /// 流程: Router → 数据采集专家、日志分析专家、故障诊断专家 → 聚合
        /// </summary>
        [HttpPost("plc-diagnosis")]
        public async Task<IActionResult> TestPLCDiagnosis([FromBody] PLCDiagnosisRequest request)
        {
            try
            {
                _logger.LogInformation("[测试] PLC 诊断: {Description}", request.FaultDescription);

                // 假设 RoleId 配置
                var expertRoleIds = new Dictionary< long, string>
                {
                   [request.DataCollectorRoleId]="数据采集专家",
                 [request.LogAnalyzerRoleId]  =  "日志分析专家",
                   [request.DiagnosticianRoleId] = "故障诊断专家"
                };

                var result = await _workflowService.RunConditionalRoutingAsync(
                    $"PLC 故障诊断任务：{request.FaultDescription}\n设备ID：{request.DeviceId}\n时间范围：{request.TimeRange}",
                    request.RouterRoleId,
                    expertRoleIds
                );

                return Ok(new
                {
                    success = result.Success,
                    workflowId = result.WorkflowId.ToString(), // 转为字符串避免JS精度丢失
                    deviceId = request.DeviceId,
                    routerDecision = result.RouterDecision,
                    expertsUsed = result.ExpertsUsed,
                    diagnosisResult = result.Result,
                    executionTimeMs = result.ExecutionTimeMs
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[测试] PLC 诊断失败");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// 上传 PLC 文件（只保存文件，不做检测）
        /// 支持格式：.awl, .xml, .gx3, .gxw, .cxp, .xef, .acd 等
        /// 文件检测由智能体调用 detect_plc_file_type 工具完成
        /// </summary>
        [HttpPost("upload-plc-file")]
        public async Task<IActionResult> UploadPLCFile([FromForm] IFormFile file)
        {
            try
            {
                if (file == null || file.Length == 0)
                {
                    return BadRequest(new { error = "未选择文件或文件为空" });
                }

                _logger.LogInformation("[文件上传] 文件名: {FileName}, 大小: {Size} bytes", file.FileName, file.Length);

                var extension = Path.GetExtension(file.FileName).ToLower();

                // 检查文件大小（限制10MB）
                if (file.Length > 10 * 1024 * 1024)
                {
                    return BadRequest(new { error = "文件大小超过10MB限制" });
                }

                // 保存文件到临时目录
                var uploadFolder = Path.Combine(Directory.GetCurrentDirectory(), "Uploads", "PLC");
                if (!Directory.Exists(uploadFolder))
                {
                    Directory.CreateDirectory(uploadFolder);
                }

                var fileName = $"{DateTime.Now:yyyyMMddHHmmss}_{Guid.NewGuid():N}{extension}";
                var filePath = Path.Combine(uploadFolder, fileName);

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                _logger.LogInformation("[文件上传] 文件已保存: {FilePath}", filePath);

                return Ok(new
                {
                    success = true,
                    message = "文件上传成功，可调用智能体进行分析",
                    file = new
                    {
                        fileName = file.FileName,
                        savedFileName = fileName,
                        filePath = filePath,
                        size = file.Length,
                        uploadedAt = DateTime.Now,
                        extension = extension
                    },
                    tip = "请使用 upload-and-diagnose 接口让智能体自动分析，或在任务描述中引用此文件路径"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[文件上传] 上传失败");
                return StatusCode(500, new { error = $"文件上传失败: {ex.Message}" });
            }
        }

        /// <summary>
        /// 上传 PLC 文件并自动创建分析工作流（智能体自动调用检测工具）
        /// </summary>
        [HttpPost("upload-and-diagnose")]
        public async Task<IActionResult> UploadAndDiagnose([FromForm] IFormFile file, [FromQuery] string task = "detect")
        {
            try
            {
                if (file == null || file.Length == 0)
                {
                    return BadRequest(new { error = "未选择文件或文件为空" });
                }

                // 只上传文件，不做检测
                var uploadResult = await UploadPLCFile(file);
                var uploadData = (uploadResult as OkObjectResult)?.Value as dynamic;

                if (uploadData?.success != true)
                {
                    return uploadResult;
                }

                var fileInfo = uploadData.file;

                // 构建任务描述，让智能体自己调用工具检测文件类型
                string userInput = task.ToLower() switch
                {
                    "detect" => $"我上传了一个PLC程序文件（{fileInfo.fileName}），文件路径是 {fileInfo.filePath}。\n" +
                                $"请先调用 detect_plc_file_type 工具检测文件类型（品牌、型号、语言），然后分析文件内容并提取关键信息。",
                    
                    "parse" => $"我上传了一个PLC程序文件（{fileInfo.fileName}），文件路径是 {fileInfo.filePath}。\n" +
                               $"请先检测文件类型。随后优先调用 extract_structure_info_incremental 获取结构信息（如失败再用 extract_structure_info），" +
                               $"然后调用 generate_parser_code 生成Python解析器脚本并输出 code_file_path，" +
                               $"再调用 format_parser_code 进行格式化，并按需执行 validate_parser_code/execute_parser_sandbox 验证。" +
                               $"输出必须包含 file_path 与 code_file_path，不要重复调用结构提取工具。",
                    
                    "diagnose" => $"我上传了一个PLC程序文件（{fileInfo.fileName}），文件路径是 {fileInfo.filePath}。\n" +
                                  $"请先识别PLC品牌和型号，然后诊断程序中的潜在问题，包括逻辑错误、性能瓶颈和安全隐患。",
                    
                    _ => $"我上传了一个PLC程序文件（{fileInfo.fileName}），文件路径是 {fileInfo.filePath}。\n" +
                         $"请检测文件类型并进行分析。"
                };

                _logger.LogInformation($"[文件分析] 任务类型: {task}, 文件: {fileInfo.fileName}");

                // 创建工作流（智能体会自动调用 detect_plc_file_type 工具）
                var result = await _workflowService.RunAgentOrchestrationAsync(userInput, null, null);

                return Ok(new
                {
                    success = result.Success,
                    workflowId = result.WorkflowId.ToString(),
                    file = fileInfo,
                    task = userInput,
                    message = "工作流已创建，智能体将自动检测文件类型并进行分析..."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[文件分析] 失败");
                return StatusCode(500, new { error = $"文件分析失败: {ex.Message}" });
            }
        }
    }

    #region 请求模型

    public class SimpleRunRequest
    {
        public string UserInput { get; set; } = "";
        public long? PlannerRoleId { get; set; } // 可为空，系统自动选择Planner
        /// <summary>
        /// 系统标识（用于多系统记忆隔离）
        /// </summary>
        public string SystemId { get; set; } = "default";
    }

    public class ConditionalRoutingRequest
    {
        public string UserInput { get; set; } = "";
        public long? RouterRoleId { get; set; }  // 改为可空，支持智能选择
        public Dictionary<long, string>? ExpertRoleIds { get; set; }  
    }

    public class FanOutFanInRequest
    {
        public string UserInput { get; set; } = "";
        public List<long> ParallelRoleIds { get; set; } = new();
    }

    public class SequentialChainRequest
    {
        public string UserInput { get; set; } = "";
        public long PlannerRoleId { get; set; }
        public long ExecutorRoleId { get; set; }
        public long ReviewerRoleId { get; set; }
    }

    public class PLCDiagnosisRequest
    {
        public string FaultDescription { get; set; } = "";
        public string DeviceId { get; set; } = "";
        public string TimeRange { get; set; } = "";
        public long RouterRoleId { get; set; }
        public long DataCollectorRoleId { get; set; }
        public long LogAnalyzerRoleId { get; set; }
        public long DiagnosticianRoleId { get; set; }
    }

    #endregion
}
