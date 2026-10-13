

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/02/29 10:03:23
using HSJM.AI.AppLayer;
using HSJM.AI.Utility;
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
using HSJM.AI.AppLayer.SYS.DTOS;
using HSJM.AI.AppLayer.SYS.SysUser.DTOS;

namespace HSJM.AI.Controllers
{

    /// <summary>
    /// 数据字典接口
    /// </summary>
    [ApiExplorerSettings(GroupName = "Basic")]
    [Route("DataDictionary")]
    [ApiController]
    [ModelValidation]
    public class DataDictionaryController : ControllerBase
    {
        private readonly DataDictionaryApp dataDictionaryApp;

        public DataDictionaryController(DataDictionaryApp dataDictionaryApp)
        {
            this.dataDictionaryApp = dataDictionaryApp;
        }

        /// <summary>
        /// 根据主键获取
        /// </summary>
        /// <param name="id">主键id</param>
        /// <returns></returns>
        [Route("Get")]
        [ProducesResponseType(typeof(DataDictionaryEntity), 200)]
        [HttpGet]
        public async Task<ActionResult> GetAsync(long id)
        {
            var entity = await dataDictionaryApp.GetAsync(id).ConfigureAwait(false);
            return Ok(entity);
        }

        /// <summary>
        /// 插入新实体
        /// </summary>
        /// <param name="insertDto">对应实体数据传输对象</param>
        /// <returns></returns>
        [Route("insert")]
        [HttpPost]
        [ProducesResponseType(typeof(HttpResponseResultModel<long>), 200)]
        public async Task<ActionResult> InsertAsync([FromBody] DataDictionaryInsertDTO insertDto)
        {
            if (insertDto == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }

            var result = await dataDictionaryApp.InsertAsync(insertDto).ConfigureAwait(false);

            return Ok(result);
        }


        /// <summary>
        /// 批量插入新实体
        /// </summary>
        /// <param name="insertEntityList">对应实体数据传输对象集合</param>
        /// <returns></returns>
        [Route("InsertBatch")]
        [HttpPost]
        [ProducesResponseType(typeof(HttpResponseResultModel<bool>), 200)]
        private async Task<ActionResult> InsertBatchAsync([FromBody] List<DataDictionaryInsertDTO> insertDtoList)
        {
            if (insertDtoList == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }

            var result = await dataDictionaryApp.InsertBatchAsync(insertDtoList).ConfigureAwait(false);

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
        public async Task<ActionResult> UpdateByIdAsync(long id, [FromBody] DataDictionaryUpdateDTO updateDto)
        {
            if (updateDto == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }
            var result = await dataDictionaryApp.UpdateAsync(id, updateDto).ConfigureAwait(false);

            return Ok(result);
        }




        /// <summary>
        /// 删除实体
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        [Route("delete/{id}")]
        [HttpDelete]
        [ProducesResponseType(typeof(HttpResponseResultModel<bool>), 200)]
        public async Task<ActionResult> DeleteAsync(long id)
        {
            if (id <= 0)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }

            var result = await dataDictionaryApp.DeleteAsync(id).ConfigureAwait(false);
            return Ok(result);
        }


        /// <summary>
        /// 批量删除
        /// </summary>
        /// <param name="idList"></param>
        /// <returns></returns>
        [Route("DeleteBatch")]
        [HttpDelete]
        [ProducesResponseType(typeof(HttpResponseResultModel<bool>), 200)]
        public async Task<ActionResult> DeleteBatchAsync([FromBody] List<long> idList)
        {
            if (idList == null || idList.Count == 0)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }

            var result = await dataDictionaryApp.DeleteBatchAsync(idList).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 查询数据(分页) 返回数据字典表记录
        /// </summary>
        /// <param name="page">页码</param>
        /// <param name="limit">每页数量</param>
        /// <param name="conditionDTO">查询条件类</param>
        /// <returns></returns>
        [Route("GetListPaged")]
        [ProducesResponseType(typeof(QueryPagedResponseModel<DataDictionaryEntity>), 200)]
        [HttpGet]
        public async Task<ActionResult> GetListPagedAsync(int page = 1, int limit = 10, [FromQuery] DataDictionaryConditionDTO conditionDTO = null)
        {

            var queryPagedResponse = await dataDictionaryApp.GetListPagedAsync(page, limit, conditionDTO, "Sort", false).ConfigureAwait(false);
            return Ok(queryPagedResponse);
        }

        /// <summary>
        /// 查询数据(不分页) 返回数据字典表记录
        /// </summary>
        /// <param name="conditionDTO">查询条件类</param>
        /// <returns></returns>
        [Route("GetList")]
        [ProducesResponseType(typeof(List<DataDictionaryEntity>), 200)]
        [HttpGet]
        public async Task<ActionResult> GetListAsync([FromQuery] DataDictionaryConditionDTO conditionDTO)
        {
            var list = await dataDictionaryApp.GetListAsync(conditionDTO, "Sort", false).ConfigureAwait(false);
            return Ok(list);
        }

        /// <summary>
        /// 获取第一条
        /// </summary>
        /// <param name="conditionDTO">查询条件类</param>
        /// <returns></returns>
        [Route("GetFirst")]
        [ProducesResponseType(typeof(DataDictionaryEntity), 200)]
        [HttpGet]
        public async Task<ActionResult> GetFirstAsync([FromQuery] DataDictionaryConditionDTO conditionDTO)
        {
            var list = await dataDictionaryApp.GetFirstAsync(conditionDTO).ConfigureAwait(false);
            return Ok(list);
        }

        /// <summary>
        /// 根据id集合获取多条数据
        /// </summary>
        /// <param name="ids"></param>
        /// <returns></returns>
        [Route("GetListByIds")]
        [ProducesResponseType(typeof(List<DataDictionaryEntity>), 200)]
        [HttpPost]
        public async Task<ActionResult> GetListByIdsAsync([FromBody] List<long> ids)
        {
            var list = await dataDictionaryApp.GetListByIdsAsync(ids).ConfigureAwait(false);
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
        public async Task<ActionResult> ExportExcelAsync([FromQuery] DataDictionaryConditionDTO conditionDTO)
        {

            var result = await dataDictionaryApp.ExportToExcelAsync(conditionDTO).ConfigureAwait(false);
            return Ok(result);
        }


        /// <summary>
        /// 获取排序
        /// </summary>
        /// <param name="parentCode"></param>
        /// <returns></returns>
        [Route("GetSort")]
        [ProducesResponseType(typeof(int), 200)]
        [HttpGet]
        public async Task<ActionResult> GetSort([FromQuery] string parentCode = "")
        {
            var result = await dataDictionaryApp.GetSort(parentCode).ConfigureAwait(false);
            return Ok(result);

        }

        /// <summary>
        /// 获取数据字典
        /// </summary>
        /// <param name="parentName"></param>
        /// <returns></returns>
        [Route("GetDicionaryList")]
        [HttpGet]
        public async Task<ActionResult> GetDicionaryList(string parentName)
        {

            var result = await dataDictionaryApp.GetDicionaryList(parentName).ConfigureAwait(false);
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
        public async Task<ActionResult> UpdateStatus([FromBody] DataDictionaryUpdateDTO updateDto)
        {
            if (updateDto == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }
            var result = await dataDictionaryApp.UpdateStatus(updateDto).ConfigureAwait(false);

            return Ok(result);
        }

    }
}