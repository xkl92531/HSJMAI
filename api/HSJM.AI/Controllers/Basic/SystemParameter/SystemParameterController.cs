

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2025/3/28 16:07:46
using  HSJM.AI.AppLayer;
using  HSJM.AI.Utility;
using HSJM.AI.Filters;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.AppLayer.Basic.ViewObject;
using HSJM.AI.AppLayer.Basic.DTOS;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using System.ComponentModel;
using HSJM.AI.AppLayer.SYS.SysUser.DTOS;

namespace  HSJM.AI.Controllers
{
    
    /// <summary>
    /// 系统参数接口
    /// </summary>
     	[ApiExplorerSettings(GroupName = "Basic")] 
    [Route("SystemParameter")]
    [ApiController]
    [ModelValidation]
    [Authorize]
    [DisplayName("系统参数")]
    public class  SystemParameterController : ControllerBase
    {
        private readonly SystemParameterApp systemParameterApp;
        
        public SystemParameterController(SystemParameterApp systemParameterApp)
        {
            this.systemParameterApp = systemParameterApp;
        }

          /// <summary>
        /// 根据主键获取
        /// </summary>
        /// <param name="id">主键id</param>
        /// <returns></returns>
        [Route("Get")]
        [DisplayName("根据主键获取")]
        [ProducesResponseType(typeof(SystemParameterEntity), 200)]
        [HttpGet]
        public async Task<ActionResult> GetAsync(long  id)
        {
            var entity = await systemParameterApp.GetAsync(id).ConfigureAwait(false);
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
        public async Task<ActionResult> InsertAsync([FromBody]SystemParameterInsertDTO insertDto)
        {
            if (insertDto == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }
            var result = await systemParameterApp.InsertAsync(insertDto).ConfigureAwait(false);

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
        private async Task<ActionResult> InsertBatchAsync([FromBody]List<SystemParameterInsertDTO > insertDtoList)
        {
            if (insertDtoList == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }

            var result = await systemParameterApp.InsertBatchAsync(insertDtoList).ConfigureAwait(false);

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
        public async Task<ActionResult> UpdateByIdAsync(long id, [FromBody]SystemParameterUpdateDTO  updateDto)
        {
            if (updateDto == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }
            var result = await systemParameterApp.UpdateAsync(id,updateDto).ConfigureAwait(false);

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

            var result = await systemParameterApp.DeleteAsync(id).ConfigureAwait(false);
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

            var result = await systemParameterApp.DeleteBatchAsync(idList).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 查询数据(分页) 返回系统参数表记录
        /// </summary>
        /// <param name="page">页码</param>
        /// <param name="limit">每页数量</param>
        /// <param name="conditionDTO">查询条件类</param>
        /// <param name="sortField">排序字段</param>
        /// <param name="sortType">desc/asc</param>
        /// <returns></returns>
        [Route("GetListPaged")]
        [ProducesResponseType(typeof(QueryPagedResponseModel<SystemParameterEntity>), 200)]
        [HttpGet]
        [DisplayName("查询数据(分页)")]
        public async Task<ActionResult> GetListPagedAsync(int page = 1, int limit = 10,string sortField= "Id", string sortType="desc",[FromQuery]SystemParameterConditionDTO conditionDTO = null)
        {
           
            var queryPagedResponse = await systemParameterApp.GetListPagedAsync(page, limit, conditionDTO, sortField,sortType.ToLower()=="desc").ConfigureAwait(false);
            return Ok(queryPagedResponse);
        }

        /// <summary>
        /// 查询数据(不分页) 返回系统参数表记录
        /// </summary>
        /// <param name="conditionDTO">查询条件类</param>
        /// <returns></returns>
        [Route("GetList")]
        [ProducesResponseType(typeof(List<SystemParameterEntity>), 200)]
        [HttpGet]
         [DisplayName("查询数据(不分页)")]
        public async Task<ActionResult> GetListAsync([FromQuery]SystemParameterConditionDTO conditionDTO)
        {
            var list = await systemParameterApp.GetListAsync(conditionDTO).ConfigureAwait(false);
            return Ok(list);
        }

         /// <summary>
        /// 获取第一条
        /// </summary>
        /// <param name="conditionDTO">查询条件类</param>
        /// <returns></returns>
        [Route("GetFirst")]
        [ProducesResponseType(typeof(SystemParameterEntity), 200)]
        [HttpGet]
         [DisplayName("获取第一条")]
        public async Task<ActionResult> GetFirstAsync([FromQuery]SystemParameterConditionDTO conditionDTO)
        {
            var first = await systemParameterApp.GetFirstAsync(conditionDTO).ConfigureAwait(false);
            return Ok(first);
        }
        
		 /// <summary>
        /// 根据id集合获取多条数据
        /// </summary>
        /// <param name="ids"></param>
        /// <returns></returns>
        [Route("GetListByIds")]
        [ProducesResponseType(typeof(List<SystemParameterEntity>), 200)]
        [HttpPost]
         [DisplayName("根据id集合获取多条数据")]
        public async Task<ActionResult> GetListByIdsAsync([FromBody]List<long> ids)
        {
            var list = await systemParameterApp.GetListByIdsAsync(ids).ConfigureAwait(false);
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
        public async Task<ActionResult> ExportExcelAsync([FromQuery] SystemParameterConditionDTO conditionDTO)
        {

            var result = await systemParameterApp.ExportToExcelAsync(conditionDTO).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 修改状态
        /// </summary> 
        /// <param name="updateDto">对应实体数据传输对象</param>
        /// <returns></returns>
        [Route("UpdateStatus")]
        [ProducesResponseType(typeof(HttpResponseResultModel<bool>), 200)]
        [HttpPost]
        public async Task<ActionResult> UpdateStatus([FromBody] SystemParameterUpdateDTO updateDto)
        {
            if (updateDto == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }
            var result = await systemParameterApp.UpdateStatus(updateDto).ConfigureAwait(false);

            return Ok(result);
        }

    }
}