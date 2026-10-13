

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/02/29 16:36:10
using HSJM.AI.AppLayer;
using HSJM.AI.Utility;
using HSJM.AI.Filters;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.AppLayer.SYS.ViewObject;
using HSJM.AI.AppLayer.SYS.DTOS;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using HSJM.AI.AppLayer.SYS.SysMenu.ViewObject;

namespace HSJM.AI.Controllers
{

    /// <summary>
    /// 系统菜单接口
    /// </summary>
    [ApiExplorerSettings(GroupName = "System")]
    [Route("SysMenu")]
    [ApiController]
    [ModelValidation]
    [Authorize]
    public class SysMenuController : ControllerBase
    {
        private readonly SysMenuApp sysMenuApp;

        public SysMenuController(SysMenuApp sysMenuApp)
        {
            this.sysMenuApp = sysMenuApp;
        }

        /// <summary>
        /// 根据主键获取
        /// </summary>
        /// <param name="id">主键id</param>
        /// <returns></returns>
        [Route("Get")]
        [ProducesResponseType(typeof(SysMenuEntity), 200)]
        [HttpGet]
        public async Task<ActionResult> GetAsync(long id)
        {
            var entity = await sysMenuApp.GetAsync(id).ConfigureAwait(false);
            return Ok(entity);
        }

        /// <summary>
        /// 根据主键获取
        /// </summary>
        /// <param name="id">主键id</param>
        /// <returns></returns>
        [Route("GetEntity")]
        [ProducesResponseType(typeof(SysMenuView), 200)]
        [HttpGet]
        public async Task<ActionResult> GetEntity(long id)
        {
            var entity = await sysMenuApp.GetEntity(id).ConfigureAwait(false);
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
        public async Task<ActionResult> InsertAsync([FromBody] SysMenuInsertDTO insertDto)
        {
            if (insertDto == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }
            var result = await sysMenuApp.InsertAsync(insertDto).ConfigureAwait(false);

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
        private async Task<ActionResult> InsertBatchAsync([FromBody] List<SysMenuInsertDTO> insertDtoList)
        {
            if (insertDtoList == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }

            var result = await sysMenuApp.InsertBatchAsync(insertDtoList).ConfigureAwait(false);

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
        public async Task<ActionResult> UpdateByIdAsync(long id, [FromBody] SysMenuUpdateDTO updateDto)
        {
            if (updateDto == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }
            var result = await sysMenuApp.UpdateAsync(id, updateDto).ConfigureAwait(false);

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

            var result = await sysMenuApp.DeleteAsync(id).ConfigureAwait(false);
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

            var result = await sysMenuApp.DeleteBatchAsync(idList).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 查询数据(分页) 返回系统菜单表记录
        /// </summary>
        /// <param name="page">页码</param>
        /// <param name="limit">每页数量</param>
        /// <param name="conditionDTO">查询条件类</param>
        /// <returns></returns>
        [Route("GetListPaged")]
        [ProducesResponseType(typeof(QueryPagedResponseModel<SysMenuEntity>), 200)]
        [HttpGet]
        public async Task<ActionResult> GetListPagedAsync(int page = 1, int limit = 10, [FromQuery] SysMenuConditionDTO conditionDTO = null)
        {

            var queryPagedResponse = await sysMenuApp.GetListPagedAsync(page, limit, conditionDTO, "Id", true).ConfigureAwait(false);
            return Ok(queryPagedResponse);
        }

        /// <summary>
        /// 查询数据(不分页) 返回系统菜单表记录
        /// </summary>
        /// <param name="conditionDTO">查询条件类</param>
        /// <returns></returns>
        [Route("GetList")]
        [ProducesResponseType(typeof(List<SysMenuEntity>), 200)]
        [HttpGet]
        public async Task<ActionResult> GetListAsync([FromQuery] SysMenuConditionDTO conditionDTO)
        {
            var list = await sysMenuApp.GetListAsync(conditionDTO).ConfigureAwait(false);
            return Ok(list);
        }

        /// <summary>
        /// 获取第一条
        /// </summary>
        /// <param name="conditionDTO">查询条件类</param>
        /// <returns></returns>
        [Route("GetFirst")]
        [ProducesResponseType(typeof(SysMenuEntity), 200)]
        [HttpGet]
        public async Task<ActionResult> GetFirstAsync([FromQuery] SysMenuConditionDTO conditionDTO)
        {
            var first = await sysMenuApp.GetFirstAsync(conditionDTO).ConfigureAwait(false);
            return Ok(first);
        }

        /// <summary>
        /// 根据id集合获取多条数据
        /// </summary>
        /// <param name="ids"></param>
        /// <returns></returns>
        [Route("GetListByIds")]
        [ProducesResponseType(typeof(List<SysMenuEntity>), 200)]
        [HttpPost]
        public async Task<ActionResult> GetListByIdsAsync([FromBody] List<long> ids)
        {
            var list = await sysMenuApp.GetListByIdsAsync(ids).ConfigureAwait(false);
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
        public async Task<ActionResult> ExportExcelAsync([FromQuery] SysMenuConditionDTO conditionDTO)
        {

            var result = await sysMenuApp.ExportToExcelAsync(conditionDTO).ConfigureAwait(false);
            return Ok(result);
        }


        /// <summary>
        /// 查找所有下级数据
        /// </summary>
        /// <returns></returns>
        [Route("GetTreeChildList")]
        [ProducesResponseType(typeof(List<SysMenuTreeEntity>), 200)]
        [HttpGet]
        public async Task<ActionResult> GetTreeChildList()
        {
            var result = await sysMenuApp.GetTreeChildList().ConfigureAwait(false);

            return Ok(result);
        }

        /// <summary>
        /// 查找所有下级数据
        /// </summary>
        /// <returns></returns>
        [Route("GetTreetList")]
        [ProducesResponseType(typeof(List<SysMenuTreeEntity>), 200)]
        [HttpPost]
        public async Task<ActionResult> GetTreetList(SysMenuConditionDTO conditionDTO)
        {
            var result = await sysMenuApp.GetTreetList(conditionDTO).ConfigureAwait(false);
            return Ok(result);
        }


        /// <summary>
        /// 查找所有下级数据
        /// </summary>
        /// <returns></returns>
        [Route("GetSort")]
        [ProducesResponseType(typeof(int), 200)]
        [HttpPost]
        public async Task<ActionResult> GetSort([FromQuery]   long systemId, long menuId)
        {
            var result = await sysMenuApp.GetSort(menuId, systemId).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 根据用户获取路由
        /// </summary>
        /// <returns></returns>
        [Route("GetRouteList")]
        [ProducesResponseType(typeof(List<RouteMenuViewModel>), 200)]
        [HttpPost]
        public async Task<ActionResult> GetRouteList()
        {
            var result = await sysMenuApp.GetRouteList().ConfigureAwait(false);
            HttpResponseResultModel<List<RouteMenuViewModel>> responseModel = new HttpResponseResultModel<List<RouteMenuViewModel>>();
            responseModel.IsSuccess = true;
            responseModel.BackResult = result;
            return Ok(responseModel);
        }

        /// <summary>
        /// 根据用户获取路由
        /// </summary>
        /// <returns></returns>
        [Route("GetTreeMenuListByRoleId")]
        [ProducesResponseType(typeof(HttpResponseResultModel<List<MenuTreeViewModel>>), 200)]
        [HttpPost]
        public async Task<ActionResult> GetTreeMenuListByRoleId(SysRoleMenuConditionDTO dto)
        {
            var result = await sysMenuApp.GetTreeMenuListByRoleId(dto).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 根据角色获取菜单
        /// </summary>
        /// <returns></returns>
        [Route("GetMenuListByRole")]
        [ProducesResponseType(typeof(Object), 200)]
        [HttpPost]
        public async Task<ActionResult> GetMenuListByRole(SysRoleMenuConditionDTO dto)
        {
            var result = await sysMenuApp.GetMenuListByRole(dto).ConfigureAwait(false);
            return Ok(result);
        }
    }
}