

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/02/29 09:41:27
using MAF.DEMO.AppLayer;
using MAF.DEMO.Utility;
using MAF.DEMO.Filters;
using MAF.DEMO.Core;
using MAF.DEMO.IDbService;
using MAF.DEMO.AppLayer.SYS.ViewObject;
using MAF.DEMO.AppLayer.SYS.DTOS;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using MAF.DEMO.AppLayer.SYS.SysRoleMenu.DTOS;

namespace MAF.DEMO.Controllers
{

    /// <summary>
    /// 角色菜单表接口
    /// </summary>
    [ApiExplorerSettings(GroupName = "System")]
    [Route("SysRoleMenu")]
    [ApiController]
    [ModelValidation]
    [Authorize]
    public class SysRoleMenuController : ControllerBase
    {
        private readonly SysRoleMenuApp sysRoleMenuApp;

        public SysRoleMenuController(SysRoleMenuApp sysRoleMenuApp)
        {
            this.sysRoleMenuApp = sysRoleMenuApp;
        }

        /// <summary>
        /// 根据主键获取
        /// </summary>
        /// <param name="id">主键id</param>
        /// <returns></returns>
        [Route("Get")]
        [ProducesResponseType(typeof(SysRoleMenuEntity), 200)]
        [HttpGet]
        public async Task<ActionResult> GetAsync(long id)
        {
            var entity = await sysRoleMenuApp.GetAsync(id).ConfigureAwait(false);
            return Ok(entity);
        }

        /// <summary>
        /// 插入新实体
        /// </summary>
        /// <param name="insertDto">对应实体数据传输对象</param>
        /// <returns></returns>
        [Route("Insert")]
        [HttpPost]
        [ProducesResponseType(typeof(HttpResponseResultModel<long>), 200)]
        public async Task<ActionResult> InsertAsync([FromBody] SysRoleMenuInsertDTO insertDto)
        {
            if (insertDto == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }
            var result = await sysRoleMenuApp.InsertAsync(insertDto).ConfigureAwait(false);

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
        private async Task<ActionResult> InsertBatchAsync([FromBody] List<SysRoleMenuInsertDTO> insertDtoList)
        {
            if (insertDtoList == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }

            var result = await sysRoleMenuApp.InsertBatchAsync(insertDtoList).ConfigureAwait(false);

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
        public async Task<ActionResult> UpdateByIdAsync(long id, [FromBody] SysRoleMenuUpdateDTO updateDto)
        {
            if (updateDto == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }
            var result = await sysRoleMenuApp.UpdateAsync(id, updateDto).ConfigureAwait(false);

            return Ok(result);
        }




        /// <summary>
        /// 删除实体
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        [Route("Delete/{id}")]
        [HttpDelete]
        [ProducesResponseType(typeof(HttpResponseResultModel<bool>), 200)]
        public async Task<ActionResult> DeleteAsync(long id)
        {
            if (id <= 0)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }

            var result = await sysRoleMenuApp.DeleteAsync(id).ConfigureAwait(false);
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

            var result = await sysRoleMenuApp.DeleteBatchAsync(idList).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 查询数据(分页) 返回角色菜单表表记录
        /// </summary>
        /// <param name="page">页码</param>
        /// <param name="limit">每页数量</param>
        /// <param name="conditionDTO">查询条件类</param>
        /// <returns></returns>
        [Route("GetListPaged")]
        [ProducesResponseType(typeof(QueryPagedResponseModel<SysRoleMenuEntity>), 200)]
        [HttpGet]
        public async Task<ActionResult> GetListPagedAsync(int page = 1, int limit = 10, [FromQuery] SysRoleMenuConditionDTO conditionDTO = null)
        {

            var queryPagedResponse = await sysRoleMenuApp.GetListPagedAsync(page, limit, conditionDTO, "Id", true).ConfigureAwait(false);
            return Ok(queryPagedResponse);
        }

        /// <summary>
        /// 查询数据(不分页) 返回角色菜单表表记录
        /// </summary>
        /// <param name="conditionDTO">查询条件类</param>
        /// <returns></returns>
        [Route("GetList")]
        [ProducesResponseType(typeof(List<SysRoleMenuEntity>), 200)]
        [HttpGet]
        public async Task<ActionResult> GetListAsync([FromQuery] SysRoleMenuConditionDTO conditionDTO)
        {
            var list = await sysRoleMenuApp.GetListAsync(conditionDTO).ConfigureAwait(false);
            return Ok(list);
        }

        /// <summary>
        /// 获取第一条
        /// </summary>
        /// <param name="conditionDTO">查询条件类</param>
        /// <returns></returns>
        [Route("GetFirst")]
        [ProducesResponseType(typeof(SysRoleMenuEntity), 200)]
        [HttpGet]
        public async Task<ActionResult> GetFirstAsync([FromQuery] SysRoleMenuConditionDTO conditionDTO)
        {
            var list = await sysRoleMenuApp.GetFirstAsync(conditionDTO).ConfigureAwait(false);
            return Ok(list);
        }

        /// <summary>
        /// 根据id集合获取多条数据
        /// </summary>
        /// <param name="ids"></param>
        /// <returns></returns>
        [Route("GetListByIds")]
        [ProducesResponseType(typeof(List<SysRoleMenuEntity>), 200)]
        [HttpPost]
        public async Task<ActionResult> GetListByIdsAsync([FromBody] List<long> ids)
        {
            var list = await sysRoleMenuApp.GetListByIdsAsync(ids).ConfigureAwait(false);
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
        public async Task<ActionResult> ExportExcelAsync([FromQuery] SysRoleMenuConditionDTO conditionDTO)
        {

            var result = await sysRoleMenuApp.ExportToExcelAsync(conditionDTO).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 保存权限
        /// </summary>
        /// <param name="dto"></param>
        /// <returns></returns>
        [Route("SaveRole")]
        [ProducesResponseType(typeof(HttpResponseResultModel<bool>), 200)]
        [HttpPost]
        public async Task<ActionResult> SaveRole([FromBody] SysRoleMenuSaveDTO dto)
        {
            var list = await sysRoleMenuApp.SaveRole(dto).ConfigureAwait(false);
            return Ok(list);
        }

    }
}