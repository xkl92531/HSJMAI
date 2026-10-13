using HSJM.AI.AppLayer.PLC;
using HSJM.AI.Core;
using HSJM.AI.Filters;
using HSJM.AI.IDbService;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel;

namespace HSJM.AI.Controllers
{
    /// <summary>
    /// IO 约束规则管理接口
    /// </summary>
    [ApiExplorerSettings(GroupName = "AI_PLC")]
    [Route("PLC_IOConstraint")]
    [ApiController]
    [ModelValidation]
    [Authorize]
    [DisplayName("IO约束规则管理")]
    public class PLC_IOConstraintController : ControllerBase
    {
        private readonly PLC_IOConstraintApp _app;

        public PLC_IOConstraintController(PLC_IOConstraintApp app)
        {
            _app = app;
        }

        /// <summary>
        /// 分页查询约束规则（含信号明细）
        /// </summary>
        /// <param name="projectId">项目ID</param>
        /// <param name="page">页码</param>
        /// <param name="limit">每页数量</param>
        /// <param name="ruleType">约束类型筛选（可选）</param>
        /// <param name="equipmentId">设备ID筛选（可选）</param>
        [Route("GetListPaged")]
        [HttpGet]
        [DisplayName("查询约束规则(分页)")]
        [ProducesResponseType(typeof(QueryPagedResponseModel<object>), 200)]
        public async Task<ActionResult> GetListPagedAsync(
            long projectId, int page = 1, int limit = 20,
            string ruleType = null, string equipmentId = null)
        {
            var result = await _app.GetConstraintRulesPagedAsync(
                projectId, page, limit, ruleType, equipmentId).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 获取项目约束规则统计摘要
        /// </summary>
        /// <param name="projectId">项目ID</param>
        [Route("GetSummary")]
        [HttpGet]
        [DisplayName("约束规则统计")]
        [ProducesResponseType(typeof(object), 200)]
        public async Task<ActionResult> GetSummaryAsync(long projectId)
        {
            var result = await _app.GetConstraintSummaryAsync(projectId).ConfigureAwait(false);
            return Ok(result);
        }

        [Route("GetPreprocessProgress")]
        [HttpGet]
        [DisplayName("获取约束生成进度")]
        [ProducesResponseType(typeof(object), 200)]
        public async Task<ActionResult> GetPreprocessProgressAsync(long projectId)
        {
            var result = await _app.GetPreprocessProgressAsync(projectId).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 单信号约束分析（同步执行，实时返回结果）
        /// </summary>
        /// <param name="ioTagId">IOTag 主键ID</param>
        [Route("AnalyzeSignal/{ioTagId}")]
        [HttpPost]
        [DisplayName("单信号约束分析")]
        [ProducesResponseType(typeof(HttpResponseResultModel<object>), 200)]
        public async Task<ActionResult> AnalyzeSignalAsync(long ioTagId)
        {
            if (ioTagId <= 0)
            {
                return BadRequest("无效的 IOTag ID");
            }

            var result = await _app.AnalyzeSingleSignalAsync(ioTagId).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 删除约束规则
        /// </summary>
        /// <param name="id">规则ID</param>
        [Route("Delete/{id}")]
        [HttpDelete]
        [DisplayName("删除约束规则")]
        [ProducesResponseType(typeof(HttpResponseResultModel<bool>), 200)]
        public async Task<ActionResult> DeleteAsync(long id)
        {
            if (id <= 0)
            {
                return BadRequest("无效的规则ID");
            }

            var result = await _app.DeleteRuleAsync(id).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 从 XML 解析约束并保存（不依赖 AI）
        /// </summary>
        /// <param name="projectId">项目ID</param>
        [Route("ExtractFromXml")]
        [HttpPost]
        [DisplayName("从XML提取约束")]
        [ProducesResponseType(typeof(HttpResponseResultModel<object>), 200)]
        public async Task<ActionResult> ExtractFromXmlAsync(long projectId)
        {
            if (projectId <= 0)
            {
                return BadRequest("无效的项目ID");
            }

            var result = await _app.ExtractConstraintsFromXmlAsync(projectId).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 获取约束规则的 C# 表达式（用于校验）
        /// </summary>
        /// <param name="projectId">项目ID</param>
        /// <param name="signalName">信号名称（可选）</param>
        [Route("GetExpressions")]
        [HttpGet]
        [DisplayName("获取约束C#表达式")]
        [ProducesResponseType(typeof(HttpResponseResultModel<string>), 200)]
        public async Task<ActionResult> GetExpressionsAsync(long projectId, string signalName = null)
        {
            if (projectId <= 0)
            {
                return BadRequest("无效的项目ID");
            }

            var result = await _app.GetConstraintExpressionsAsync(projectId, signalName).ConfigureAwait(false);
            return Ok(result);
        }
        [Route("ValidateAll")]
        [HttpGet]
        [DisplayName("自动校验全部约束")]
        [ProducesResponseType(typeof(HttpResponseResultModel<object>), 200)]
        public async Task<ActionResult> ValidateAllAsync(long projectId, string stationCode = null, bool forceLiveRead = false)
        {
            if (projectId <= 0)
            {
                return BadRequest("无效的项目ID");
            }

            var result = await _app.ValidateConstraintsAutoAsync(projectId, stationCode, forceLiveRead).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 模拟约束违规（基于真实数据智能构造违规场景，用于测试约束校验逻辑）
        /// </summary>
        /// <param name="projectId">项目ID</param>
        /// <param name="stationCode">工位（可选，不填则遍历所有工位）</param>
        /// <param name="maxViolationsPerOperator">每种运算符最多生成几个场景（默认3）</param>
        /// <param name="seed">随机种子（可选，传固定值可复现相同场景）</param>
        [Route("SimulateViolations")]
        [HttpGet]
        [DisplayName("模拟约束违规")]
        [ProducesResponseType(typeof(HttpResponseResultModel<object>), 200)]
        public async Task<ActionResult> SimulateViolationsAsync(
            long projectId,
            string stationCode = null,
            int maxViolationsPerOperator = 3,
            long? seed = null)
        {
            if (projectId <= 0)
            {
                return BadRequest("无效的项目ID");
            }

            var result = await _app.SimulateConstraintViolationsAsync(
                projectId, stationCode, maxViolationsPerOperator, seed).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 获取约束生成系统提示词（含默认值/是否已自定义）
        /// </summary>
        [Route("GetPrompt")]
        [HttpGet]
        [DisplayName("获取约束生成提示词")]
        [ProducesResponseType(typeof(object), 200)]
        public async Task<ActionResult> GetPromptAsync()
        {
            var result = await _app.GetConstraintPromptAsync().ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 保存约束生成系统提示词
        /// </summary>
        [Route("SavePrompt")]
        [HttpPost]
        [DisplayName("保存约束生成提示词")]
        [ProducesResponseType(typeof(HttpResponseResultModel<bool>), 200)]
        public async Task<ActionResult> SavePromptAsync([FromBody] ConstraintPromptSaveDto dto)
        {
            if (dto == null || string.IsNullOrWhiteSpace(dto.Content))
            {
                return BadRequest("提示词内容不能为空");
            }
            var result = await _app.SaveConstraintPromptAsync(dto.Content).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 恢复约束生成系统提示词为默认
        /// </summary>
        [Route("ResetPrompt")]
        [HttpPost]
        [DisplayName("恢复默认约束生成提示词")]
        [ProducesResponseType(typeof(HttpResponseResultModel<bool>), 200)]
        public async Task<ActionResult> ResetPromptAsync()
        {
            var result = await _app.ResetConstraintPromptAsync().ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 手动新增约束规则（含信号）
        /// </summary>
        [Route("Insert")]
        [HttpPost]
        [DisplayName("手动新增约束规则")]
        [ProducesResponseType(typeof(HttpResponseResultModel<long>), 200)]
        public async Task<ActionResult> InsertAsync([FromBody] ConstraintRuleSaveDto dto)
        {
            if (dto == null)
            {
                return BadRequest("参数为空");
            }
            var result = await _app.InsertConstraintRuleAsync(dto).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 编辑约束规则（含信号替换）
        /// </summary>
        [Route("UpdateById/{id}")]
        [HttpPost]
        [DisplayName("编辑约束规则")]
        [ProducesResponseType(typeof(HttpResponseResultModel<bool>), 200)]
        public async Task<ActionResult> UpdateByIdAsync(long id, [FromBody] ConstraintRuleSaveDto dto)
        {
            if (id <= 0 || dto == null)
            {
                return BadRequest("无效的参数");
            }
            var result = await _app.UpdateConstraintRuleAsync(id, dto).ConfigureAwait(false);
            return Ok(result);
        }
    }
}
