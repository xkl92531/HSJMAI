//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V1  描述:V1_约束长期记忆API  添加时间:2026/4/1
using HSJM.AI.AppLayer.Agent.Services;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace HSJM.AI.Controllers.Agent
{
    /// <summary>
    /// AI约束分析长期记忆API控制器（多场景版）
    /// 支持：constraint=约束生成，stuck_point=卡滞诊断，energy_flow=能量流分析
    /// </summary>
    [ApiExplorerSettings(GroupName = "Agent")]
    [ApiController]
    [Route("api/io-constraint")]
    public class IOConstraintController : ControllerBase
    {
        private readonly DiagnosticMemoryService _memoryService;
        private readonly DiagnosticCorrectionSkill _correctionSkill;
        private readonly DiagnosticCompactionService _compactionService;
        private readonly ILogger<IOConstraintController> _logger;

        public IOConstraintController(
            DiagnosticMemoryService memoryService,
            DiagnosticCorrectionSkill correctionSkill,
            DiagnosticCompactionService compactionService,
            ILogger<IOConstraintController> logger)
        {
            _memoryService = memoryService;
            _correctionSkill = correctionSkill;
            _compactionService = compactionService;
            _logger = logger;
        }

        #region 记忆检索

        /// <summary>
        /// 检索相关记忆（多场景支持）
        /// </summary>
        [HttpGet("memories/search")]
        public async Task<IActionResult> SearchMemories(
            [FromQuery] string sceneType,
            [FromQuery] long? productionLineId,
            [FromQuery] string signalContext,
            [FromQuery] int topK = 5)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(signalContext))
                {
                    return BadRequest(new { success = false, error = "signalContext不能为空" });
                }

                if (string.IsNullOrWhiteSpace(sceneType))
                {
                    sceneType = DiagnosticMemoryService.SceneConstraint;
                }

                var memories = await _memoryService.SearchMemoriesAsync(sceneType, productionLineId, signalContext, topK);
                
                return Ok(new
                {
                    success = true,
                    data = memories,
                    count = memories.Count
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[记忆检索] 失败");
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        /// <summary>
        /// 获取所有活跃记忆（多场景支持）
        /// </summary>
        [HttpGet("memories")]
        public async Task<IActionResult> GetMemories(
            [FromQuery] string sceneType,
            [FromQuery] long? productionLineId,
            [FromQuery] int? layerLevel,
            [FromQuery] string signalType)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sceneType))
                {
                    sceneType = DiagnosticMemoryService.SceneConstraint;
                }

                List<DiagnosticMemoryEntity> memories;
                
                if (layerLevel.HasValue)
                {
                    var allMemories = await _memoryService.GetActiveMemoriesAsync(sceneType, productionLineId);
                    memories = allMemories.Where(m => m.LayerLevel == layerLevel.Value).ToList();
                }
                else
                {
                    memories = await _memoryService.GetActiveMemoriesAsync(sceneType, productionLineId);
                }

                if (!string.IsNullOrWhiteSpace(signalType))
                {
                    memories = memories.Where(m => m.SignalType == signalType).ToList();
                }

                return Ok(new
                {
                    success = true,
                    data = memories,
                    count = memories.Count
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[获取记忆] 失败");
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        /// <summary>
        /// 删除记忆（软删除）
        /// </summary>
        [HttpDelete("memories/{id}")]
        public async Task<IActionResult> DeleteMemory(long id)
        {
            try
            {
                await _memoryService.DeleteMemoryAsync(id);
                return Ok(new { success = true, message = "记忆删除成功" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[删除记忆] 失败: Id={Id}", id);
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        #endregion

        #region 校正处理

        /// <summary>
        /// 确认校正（用户确认AI归纳的pattern）
        /// </summary>
        [HttpPost("correction/confirm")]
        public async Task<IActionResult> ConfirmCorrection([FromBody] ConfirmCorrectionRequest request)
        {
            try
            {
                if (request.CorrectionLogId <= 0)
                {
                    return BadRequest(new { success = false, error = "CorrectionLogId无效" });
                }

                var result = await _correctionSkill.ConfirmCorrectionAsync(request.CorrectionLogId);
                
                if (!result.Success)
                {
                    return Ok(new { success = false, error = result.ErrorMessage });
                }

                return Ok(new
                {
                    success = true,
                    message = "校正确认成功，已学习到新经验",
                    memoryId = result.MemoryId,
                    learnedContent = result.LearnedContent
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[确认校正] 失败");
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        /// <summary>
        /// 补充校正（用户需要补充更多信息）
        /// </summary>
        [HttpPost("correction/supplement")]
        public async Task<IActionResult> SupplementCorrection([FromBody] SupplementCorrectionRequest request)
        {
            try
            {
                if (request.CorrectionLogId <= 0)
                {
                    return BadRequest(new { success = false, error = "CorrectionLogId无效" });
                }

                if (string.IsNullOrWhiteSpace(request.AdditionalInput))
                {
                    return BadRequest(new { success = false, error = "AdditionalInput不能为空" });
                }

                var result = await _correctionSkill.SupplementCorrectionAsync(
                    request.CorrectionLogId, 
                    request.AdditionalInput);

                if (!result.Success)
                {
                    return Ok(new { success = false, error = result.ErrorMessage });
                }

                return Ok(new
                {
                    success = true,
                    correctionLogId = result.CorrectionLogId,
                    patternSummary = result.PatternSummary,
                    status = result.Status
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[补充校正] 失败");
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        /// <summary>
        /// 拒绝校正
        /// </summary>
        [HttpPost("correction/reject/{correctionLogId}")]
        public async Task<IActionResult> RejectCorrection(long correctionLogId)
        {
            try
            {
                var success = await _correctionSkill.RejectCorrectionAsync(correctionLogId);
                
                return Ok(new
                {
                    success = success,
                    message = success ? "已拒绝校正" : "校正记录不存在"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[拒绝校正] 失败");
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        #endregion

        #region 压缩管理

        /// <summary>
        /// 获取压缩历史
        /// </summary>
        [HttpGet("compaction/history")]
        public async Task<IActionResult> GetCompactionHistory([FromQuery] string sceneType, [FromQuery] int limit = 10)
        {
            try
            {
                var history = await _compactionService.GetCompactionHistoryAsync(sceneType, limit);
                
                return Ok(new
                {
                    success = true,
                    data = history,
                    count = history.Count
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[获取压缩历史] 失败");
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        /// <summary>
        /// 手动触发压缩
        /// </summary>
        [HttpPost("compaction/execute")]
        public async Task<IActionResult> ExecuteCompaction([FromQuery] string sceneType, [FromQuery] long? productionLineId)
        {
            try
            {
                _logger.LogInformation("[手动压缩] 开始执行: Scene={Scene}, ProductionLineId={ProductionLineId}", sceneType, productionLineId);
                
                var result = await _compactionService.ExecuteMonthlyCompactionAsync(sceneType, productionLineId);
                
                return Ok(new
                {
                    success = result.Success,
                    skipped = result.Skipped,
                    skippedReason = result.SkippedReason,
                    snapshotPath = result.SnapshotPath,
                    memoriesBefore = result.MemoriesBeforeCount,
                    memoriesAfter = result.MemoriesAfterCount,
                    errorMessage = result.ErrorMessage,
                    startTime = result.StartTime,
                    endTime = result.EndTime
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[手动压缩] 失败");
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        /// <summary>
        /// 回退到指定快照
        /// </summary>
        [HttpPost("compaction/rollback/{snapshotId}")]
        public async Task<IActionResult> RollbackCompaction(long snapshotId)
        {
            try
            {
                _logger.LogInformation("[回退压缩] 开始回退: SnapshotId={SnapshotId}", snapshotId);
                
                var success = await _compactionService.RollbackAsync(snapshotId);
                
                return Ok(new
                {
                    success = success,
                    message = success ? "回退成功" : "回退失败"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[回退压缩] 失败: SnapshotId={SnapshotId}", snapshotId);
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        /// <summary>
        /// 清理过期快照
        /// </summary>
        [HttpPost("compaction/cleanup")]
        public async Task<IActionResult> CleanupOldSnapshots()
        {
            try
            {
                await _compactionService.CleanupOldSnapshotsAsync();
                return Ok(new { success = true, message = "清理完成" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[清理快照] 失败");
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        #endregion
    }

    #region 请求模型

    public class ConfirmCorrectionRequest
    {
        public long CorrectionLogId { get; set; }
    }

    public class SupplementCorrectionRequest
    {
        public long CorrectionLogId { get; set; }
        public string AdditionalInput { get; set; }
    }

    #endregion
}