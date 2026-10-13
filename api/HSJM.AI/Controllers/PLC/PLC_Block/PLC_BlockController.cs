

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/2/7 15:05:03
using  HSJM.AI.AppLayer;
using  HSJM.AI.Utility;
using HSJM.AI.Filters;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.AppLayer.PLC.ViewObject;
using HSJM.AI.AppLayer.PLC.DTOS;
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
    /// 程序块表接口
    /// </summary>
     	[ApiExplorerSettings(GroupName = "PLC")] 
    [Route("PLC_Block")]
    [ApiController]
    [ModelValidation]
    [Authorize]
    [DisplayName("程序块表")]
    public class  PLC_BlockController : ControllerBase
    {
        private readonly PLC_BlockApp pLC_BlockApp;
        
        public PLC_BlockController(PLC_BlockApp pLC_BlockApp)
        {
            this.pLC_BlockApp = pLC_BlockApp;
        }

          /// <summary>
        /// 根据主键获取
        /// </summary>
        /// <param name="id">主键id</param>
        /// <returns></returns>
        [Route("Get")]
        [DisplayName("根据主键获取")]
        [ProducesResponseType(typeof(PLC_BlockEntity), 200)]
        [HttpGet]
        public async Task<ActionResult> GetAsync(long  id)
        {
            var entity = await pLC_BlockApp.GetAsync(id).ConfigureAwait(false);
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
        public async Task<ActionResult> InsertAsync([FromBody]PLC_BlockInsertDTO insertDto)
        {
            if (insertDto == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }
            var result = await pLC_BlockApp.InsertAsync(insertDto).ConfigureAwait(false);

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
        private async Task<ActionResult> InsertBatchAsync([FromBody]List<PLC_BlockInsertDTO > insertDtoList)
        {
            if (insertDtoList == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }

            var result = await pLC_BlockApp.InsertBatchAsync(insertDtoList).ConfigureAwait(false);

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
        public async Task<ActionResult> UpdateByIdAsync(long id, [FromBody]PLC_BlockUpdateDTO  updateDto)
        {
            if (updateDto == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }
            var result = await pLC_BlockApp.UpdateAsync(id,updateDto).ConfigureAwait(false);

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

            var result = await pLC_BlockApp.DeleteAsync(id).ConfigureAwait(false);
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

            var result = await pLC_BlockApp.DeleteBatchAsync(idList).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 查询数据(分页) 返回程序块表表记录
        /// </summary>
        /// <param name="page">页码</param>
        /// <param name="limit">每页数量</param>
        /// <param name="conditionDTO">查询条件类</param>
        /// <param name="sortField">排序字段</param>
        /// <param name="sortType">desc/asc</param>
        /// <returns></returns>
        [Route("GetListPaged")]
        [ProducesResponseType(typeof(QueryPagedResponseModel<PLC_BlockEntity>), 200)]
        [HttpGet]
        [DisplayName("查询数据(分页)")]
        public async Task<ActionResult> GetListPagedAsync(int page = 1, int limit = 10,string sortField= "Id", string sortType="desc",[FromQuery]PLC_BlockConditionDTO conditionDTO = null)
        {
           
            var queryPagedResponse = await pLC_BlockApp.GetListPagedAsync(page, limit, conditionDTO, sortField,sortType.ToLower()=="desc").ConfigureAwait(false);
            return Ok(queryPagedResponse);
        }

        /// <summary>
        /// 查询数据(不分页) 返回程序块表表记录
        /// </summary>
        /// <param name="conditionDTO">查询条件类</param>
        /// <returns></returns>
        [Route("GetList")]
        [ProducesResponseType(typeof(List<PLC_BlockEntity>), 200)]
        [HttpGet]
         [DisplayName("查询数据(不分页)")]
        public async Task<ActionResult> GetListAsync([FromQuery]PLC_BlockConditionDTO conditionDTO)
        {
            var list = await pLC_BlockApp.GetListAsync(conditionDTO).ConfigureAwait(false);
            return Ok(list);
        }

         /// <summary>
        /// 获取第一条
        /// </summary>
        /// <param name="conditionDTO">查询条件类</param>
        /// <returns></returns>
        [Route("GetFirst")]
        [ProducesResponseType(typeof(PLC_BlockEntity), 200)]
        [HttpGet]
         [DisplayName("获取第一条")]
        public async Task<ActionResult> GetFirstAsync([FromQuery]PLC_BlockConditionDTO conditionDTO)
        {
            var first = await pLC_BlockApp.GetFirstAsync(conditionDTO).ConfigureAwait(false);
            return Ok(first);
        }
        
		 /// <summary>
        /// 根据id集合获取多条数据
        /// </summary>
        /// <param name="ids"></param>
        /// <returns></returns>
        [Route("GetListByIds")]
        [ProducesResponseType(typeof(List<PLC_BlockEntity>), 200)]
        [HttpPost]
         [DisplayName("根据id集合获取多条数据")]
        public async Task<ActionResult> GetListByIdsAsync([FromBody]List<long> ids)
        {
            var list = await pLC_BlockApp.GetListByIdsAsync(ids).ConfigureAwait(false);
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
        public async Task<ActionResult> ExportExcelAsync([FromQuery] PLC_BlockConditionDTO conditionDTO)
        {

            var result = await pLC_BlockApp.ExportToExcelAsync(conditionDTO).ConfigureAwait(false);
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

            var result = await pLC_BlockApp.InsertUploadFileAsync(file).ConfigureAwait(false);
            return Ok(result);
        }
    }
}