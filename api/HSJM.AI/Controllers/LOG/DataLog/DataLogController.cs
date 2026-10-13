

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2025/3/31 9:31:07
using  HSJM.AI.AppLayer;
using  HSJM.AI.Utility;
using HSJM.AI.Filters;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.AppLayer.LOG.ViewObject;
using HSJM.AI.AppLayer.LOG.DTOS;
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
    /// 操作日志接口
    /// </summary>
     	[ApiExplorerSettings(GroupName = "LOG")] 
    [Route("DataLog")]
    [ApiController]
    [ModelValidation]
    [Authorize]
    [DisplayName("操作日志")]
    public class  DataLogController : ControllerBase
    {
        private readonly DataLogApp dataLogApp;
        
        public DataLogController(DataLogApp dataLogApp)
        {
            this.dataLogApp = dataLogApp;
        }

          /// <summary>
        /// 根据主键获取
        /// </summary>
        /// <param name="id">主键id</param>
        /// <returns></returns>
        [Route("Get")]
        [DisplayName("根据主键获取")]
        [ProducesResponseType(typeof(DataLogEntity), 200)]
        [HttpGet]
        public async Task<ActionResult> GetAsync(long  id)
        {
            var entity = await dataLogApp.GetAsync(id).ConfigureAwait(false);
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
        public async Task<ActionResult> InsertAsync([FromBody]DataLogInsertDTO insertDto)
        {
            if (insertDto == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }
            var result = await dataLogApp.InsertAsync(insertDto).ConfigureAwait(false);

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
        private async Task<ActionResult> InsertBatchAsync([FromBody]List<DataLogInsertDTO > insertDtoList)
        {
            if (insertDtoList == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }

            var result = await dataLogApp.InsertBatchAsync(insertDtoList).ConfigureAwait(false);

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
        public async Task<ActionResult> UpdateByIdAsync(long id, [FromBody]DataLogUpdateDTO  updateDto)
        {
            if (updateDto == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }
            var result = await dataLogApp.UpdateAsync(id,updateDto).ConfigureAwait(false);

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

            var result = await dataLogApp.DeleteAsync(id).ConfigureAwait(false);
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

            var result = await dataLogApp.DeleteBatchAsync(idList).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 查询数据(分页) 返回操作日志表记录
        /// </summary>
        /// <param name="page">页码</param>
        /// <param name="limit">每页数量</param>
        /// <param name="conditionDTO">查询条件类</param>
        /// <param name="sortField">排序字段</param>
        /// <param name="sortType">desc/asc</param>
        /// <returns></returns>
        [Route("GetListPaged")]
        [ProducesResponseType(typeof(QueryPagedResponseModel<DataLogEntity>), 200)]
        [HttpGet]
        [DisplayName("查询数据(分页)")]
        public async Task<ActionResult> GetListPagedAsync(int page = 1, int limit = 10,string sortField= "Id", string sortType="desc",[FromQuery]DataLogConditionDTO conditionDTO = null)
        {
           
            var queryPagedResponse = await dataLogApp.GetListPagedAsync(page, limit, conditionDTO, sortField,sortType.ToLower()=="desc").ConfigureAwait(false);
            return Ok(queryPagedResponse);
        }

        /// <summary>
        /// 查询数据(不分页) 返回操作日志表记录
        /// </summary>
        /// <param name="conditionDTO">查询条件类</param>
        /// <returns></returns>
        [Route("GetList")]
        [ProducesResponseType(typeof(List<DataLogEntity>), 200)]
        [HttpGet]
         [DisplayName("查询数据(不分页)")]
        public async Task<ActionResult> GetListAsync([FromQuery]DataLogConditionDTO conditionDTO)
        {
            var list = await dataLogApp.GetListAsync(conditionDTO).ConfigureAwait(false);
            return Ok(list);
        }

         /// <summary>
        /// 获取第一条
        /// </summary>
        /// <param name="conditionDTO">查询条件类</param>
        /// <returns></returns>
        [Route("GetFirst")]
        [ProducesResponseType(typeof(DataLogEntity), 200)]
        [HttpGet]
         [DisplayName("获取第一条")]
        public async Task<ActionResult> GetFirstAsync([FromQuery]DataLogConditionDTO conditionDTO)
        {
            var first = await dataLogApp.GetFirstAsync(conditionDTO).ConfigureAwait(false);
            return Ok(first);
        }
        
		 /// <summary>
        /// 根据id集合获取多条数据
        /// </summary>
        /// <param name="ids"></param>
        /// <returns></returns>
        [Route("GetListByIds")]
        [ProducesResponseType(typeof(List<DataLogEntity>), 200)]
        [HttpPost]
         [DisplayName("根据id集合获取多条数据")]
        public async Task<ActionResult> GetListByIdsAsync([FromBody]List<long> ids)
        {
            var list = await dataLogApp.GetListByIdsAsync(ids).ConfigureAwait(false);
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
        public async Task<ActionResult> ExportExcelAsync([FromQuery] DataLogConditionDTO conditionDTO)
        {

            var result = await dataLogApp.ExportToExcelAsync(conditionDTO).ConfigureAwait(false);
            return Ok(result);
        }

    }
}