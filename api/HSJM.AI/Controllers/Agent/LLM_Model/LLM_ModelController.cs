

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:34:10
using  HSJM.AI.AppLayer;
using  HSJM.AI.Utility;
using HSJM.AI.Filters;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.AppLayer.Agent.ViewObject;
using HSJM.AI.AppLayer.Agent.DTOS;
using HSJM.AI.AppLayer.Agent.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using System.ComponentModel;

namespace  HSJM.AI.Controllers
{
    
    /// <summary>
    /// 大模型配置表接口
    /// </summary>
     	[ApiExplorerSettings(GroupName = "Agent")] 
    [Route("LLM_Model")]
    [ApiController]
    [ModelValidation]
    [Authorize]
    [DisplayName("大模型配置表")]
    public class  LLM_ModelController : ControllerBase
    {
        private readonly LLM_ModelApp lLM_ModelApp;
        private readonly LLMModelService _llmModelService;
        
        public LLM_ModelController(LLM_ModelApp lLM_ModelApp, LLMModelService llmModelService)
        {
            this.lLM_ModelApp = lLM_ModelApp;
            _llmModelService = llmModelService;
        }

          /// <summary>
        /// 根据主键获取
        /// </summary>
        /// <param name="id">主键id</param>
        /// <returns></returns>
        [Route("Get")]
        [DisplayName("根据主键获取")]
        [ProducesResponseType(typeof(LLM_ModelEntity), 200)]
        [HttpGet]
        public async Task<ActionResult> GetAsync(long  id)
        {
            var entity = await lLM_ModelApp.GetAsync(id).ConfigureAwait(false);
            return Ok(entity);
        }

        /// <summary>
        /// 插入新实体
        /// </summary>
        /// <param name="insertDto">对应实体数据传输对象</param>
        /// <returns></returns>
        [Route("Insert")]
        [HttpPost]
        [DisplayName("插入新实体")]
        [ProducesResponseType(typeof(HttpResponseResultModel<long>), 200)]
        public async Task<ActionResult> InsertAsync([FromBody]LLM_ModelInsertDTO insertDto)
        {
            if (insertDto == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }
            var result = await lLM_ModelApp.InsertAsync(insertDto).ConfigureAwait(false);

            return Ok(result);
        }


         /// <summary>
        /// 批量插入新实体
        /// </summary>
        /// <param name="insertEntityList">对应实体数据传输对象集合</param>
        /// <returns></returns>
        [Route("InsertBatch")]
        [HttpPost]
         [DisplayName("批量插入新实体")]
        [ProducesResponseType(typeof(HttpResponseResultModel<bool>), 200)]
        private async Task<ActionResult> InsertBatchAsync([FromBody]List<LLM_ModelInsertDTO > insertDtoList)
        {
            if (insertDtoList == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }

            var result = await lLM_ModelApp.InsertBatchAsync(insertDtoList).ConfigureAwait(false);

            return Ok(result);
        }


       /// <summary>
        /// 更新实体
        /// </summary>
        /// <param name="id">主键id</param>
        /// <param name="updateDto">对应实体数据传输对象</param>
        /// <returns></returns>
        [Route("UpdateById/{id}")]
        [ProducesResponseType(typeof(HttpResponseResultModel<bool>), 200)]
        [HttpPost]
        [DisplayName("更新实体")]
        public async Task<ActionResult> UpdateByIdAsync(long id, [FromBody]LLM_ModelUpdateDTO  updateDto)
        {
            if (updateDto == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }
            var result = await lLM_ModelApp.UpdateAsync(id,updateDto).ConfigureAwait(false);

            return Ok(result);
        }


         

        /// <summary>
        /// 删除实体
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        [Route("Delete/{id}")]
        [HttpDelete]
         [DisplayName("删除实体")]
        [ProducesResponseType(typeof(HttpResponseResultModel<bool>), 200)]
        public async Task<ActionResult> DeleteAsync(long id)
        {
                       if (id <= 0)
                                  {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }

            var result = await lLM_ModelApp.DeleteAsync(id).ConfigureAwait(false);
            return Ok(result);
        }


		 /// <summary>
        /// 批量删除
        /// </summary>
        /// <param name="idList"></param>
        /// <returns></returns>
        [Route("DeleteBatch")]
        [HttpDelete]
        [DisplayName("批量删除")]
        [ProducesResponseType(typeof(HttpResponseResultModel<bool>), 200)]
        public async Task<ActionResult> DeleteBatchAsync([FromBody]List<long> idList)
        {
            if (idList == null || idList.Count == 0)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }

            var result = await lLM_ModelApp.DeleteBatchAsync(idList).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 查询数据(分页) 返回大模型配置表表记录
        /// </summary>
        /// <param name="page">页码</param>
        /// <param name="limit">每页数量</param>
        /// <param name="conditionDTO">查询条件类</param>
        /// <param name="sortField">排序字段</param>
        /// <param name="sortType">desc/asc</param>
        /// <returns></returns>
        [Route("GetListPaged")]
        [ProducesResponseType(typeof(QueryPagedResponseModel<LLM_ModelEntity>), 200)]
        [HttpGet]
        [DisplayName("查询数据(分页)")]
        public async Task<ActionResult> GetListPagedAsync(int page = 1, int limit = 10,string sortField= "Id", string sortType="desc",[FromQuery]LLM_ModelConditionDTO conditionDTO = null)
        {
           
            var queryPagedResponse = await lLM_ModelApp.GetListPagedAsync(page, limit, conditionDTO, sortField,sortType.ToLower()=="desc").ConfigureAwait(false);
            return Ok(queryPagedResponse);
        }

        /// <summary>
        /// 查询数据(不分页) 返回大模型配置表表记录
        /// </summary>
        /// <param name="conditionDTO">查询条件类</param>
        /// <returns></returns>
        [Route("GetList")]
        [ProducesResponseType(typeof(List<LLM_ModelEntity>), 200)]
        [HttpGet]
         [DisplayName("查询数据(不分页)")]
        public async Task<ActionResult> GetListAsync([FromQuery]LLM_ModelConditionDTO conditionDTO)
        {
            var list = await lLM_ModelApp.GetListAsync(conditionDTO).ConfigureAwait(false);
            return Ok(list);
        }

         /// <summary>
        /// 获取第一条
        /// </summary>
        /// <param name="conditionDTO">查询条件类</param>
        /// <returns></returns>
        [Route("GetFirst")]
        [ProducesResponseType(typeof(LLM_ModelEntity), 200)]
        [HttpGet]
         [DisplayName("获取第一条")]
        public async Task<ActionResult> GetFirstAsync([FromQuery]LLM_ModelConditionDTO conditionDTO)
        {
            var first = await lLM_ModelApp.GetFirstAsync(conditionDTO).ConfigureAwait(false);
            return Ok(first);
        }
        
		 /// <summary>
        /// 根据id集合获取多条数据
        /// </summary>
        /// <param name="ids"></param>
        /// <returns></returns>
        [Route("GetListByIds")]
        [ProducesResponseType(typeof(List<LLM_ModelEntity>), 200)]
        [HttpPost]
         [DisplayName("根据id集合获取多条数据")]
        public async Task<ActionResult> GetListByIdsAsync([FromBody]List<long> ids)
        {
            var list = await lLM_ModelApp.GetListByIdsAsync(ids).ConfigureAwait(false);
            return Ok(list);
        }

        /// <summary>
        /// 导出excel
        /// </summary>
        /// <param name="conditionDTO"></param>
        /// <returns></returns>
        [Route("ExportToExcel")]
        [ProducesResponseType(typeof(HttpResponseResultModel<string>), 200)]
        [HttpGet]
         [DisplayName("导出excel")]
        public async Task<ActionResult> ExportExcelAsync([FromQuery] LLM_ModelConditionDTO conditionDTO)
        {

            var result = await lLM_ModelApp.ExportToExcelAsync(conditionDTO).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 导入
        /// </summary>
        /// <returns></returns>
        [Route("import")]
        [ProducesResponseType(typeof(HttpResponseResultModel<string>), 200)]
        [HttpPost]
         [DisplayName("导入excel")]
        public async Task<ActionResult> ImportAsync([FromForm] IFormFile file)
        {

            var result = await lLM_ModelApp.InsertUploadFileAsync(file).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 快速测试模型对话能力
        /// </summary>
        /// <param name="modelId">模型ID</param>
        /// <param name="message">测试消息</param>
        /// <param name="systemPrompt">系统提示（可选）</param>
        /// <returns></returns>
        [Route("QuickTest")]
        [HttpPost]
        [DisplayName("快速测试模型")]
        [ProducesResponseType(typeof(HttpResponseResultModel<ChatResponse>), 200)]
        public async Task<ActionResult> QuickTestAsync([FromQuery] long modelId, [FromBody] QuickTestRequest request)
        {
            if (modelId <= 0)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }

            try
            {
                var chatClient = await _llmModelService.CreateChatClientByModelIdAsync(modelId).ConfigureAwait(false);
                ChatResponse response;

                // 如果有图片附件，使用多模态聊天
                if (request.ImageUrls != null && request.ImageUrls.Count > 0)
                {
                    response = await _llmModelService.ChatWithImagesAsync(
                        chatClient,
                        request.Message,
                        request.ImageUrls,
                        request.SystemPrompt ?? "你是一个有帮助的AI助手，可以理解和分析图片。"
                    ).ConfigureAwait(false);
                }
                else
                {
                    response = await _llmModelService.ChatAsync(
                        chatClient,
                        request.Message,
                        request.SystemPrompt ?? "你是一个有帮助的AI助手。"
                    ).ConfigureAwait(false);
                }

                return Ok(new HttpResponseResultModel<ChatResponse>
                {
                    IsSuccess = true,
                    BackResult = response
                });
            }
            catch (Exception ex)
            {
                return Ok(new HttpResponseResultModel<ChatResponse>
                {
                    IsSuccess = false,
                    ErrorMessage = ex.Message
                });
            }
        }
    }

    /// <summary>
    /// 快速测试请求
    /// </summary>
    public class QuickTestRequest
    {
        /// <summary>
        /// 测试消息
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// 系统提示（可选）
        /// </summary>
        public string? SystemPrompt { get; set; }

        /// <summary>
        /// 图片URL列表（可选，用于VL多模态模型）
        /// 支持 base64 data URI 格式: data:image/png;base64,xxxxx
        /// 或 HTTP URL 格式: https://example.com/image.jpg
        /// </summary>
        public List<string>? ImageUrls { get; set; }
    }
}

