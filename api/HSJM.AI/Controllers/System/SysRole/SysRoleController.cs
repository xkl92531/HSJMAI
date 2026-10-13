//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/02/29 09:39:57

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
using System.ComponentModel;

namespace HSJM.AI.Controllers
{
    /// <summary>
    /// 角色表接口
    /// </summary>
    [ApiExplorerSettings(GroupName = "System")]
    [Route("SysRole")]
    [ApiController]
    [ModelValidation]
    [Authorize]
    public class SysRoleController : ControllerBase
    {
        private readonly SysRoleApp sysRoleApp;

        public SysRoleController(SysRoleApp sysRoleApp)
        {
            this.sysRoleApp = sysRoleApp;
        }

        /// <summary>
        /// 根据主键获取
        /// </summary>
        /// <param name="id">主键id</param>
        /// <returns></returns>
        [Route("Get")]
        [ProducesResponseType(typeof(SysRoleEntity), 200)]
        [HttpGet]
        public async Task<ActionResult> GetAsync(long id)
        {
            var entity = await sysRoleApp.GetAsync(id).ConfigureAwait(false);
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
        public async Task<ActionResult> InsertAsync([FromBody] SysRoleInsertDTO insertDto)
        {
            if (insertDto == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }

            var result = await sysRoleApp.InsertAsync(insertDto).ConfigureAwait(false);

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
        private async Task<ActionResult> InsertBatchAsync([FromBody] List<SysRoleInsertDTO> insertDtoList)
        {
            if (insertDtoList == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }

            var result = await sysRoleApp.InsertBatchAsync(insertDtoList).ConfigureAwait(false);

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
        public async Task<ActionResult> UpdateByIdAsync(long id, [FromBody] SysRoleUpdateDTO updateDto)
        {
            if (updateDto == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }

            var result = await sysRoleApp.UpdateAsync(id, updateDto).ConfigureAwait(false);

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
        [DisplayName("删除实体")]
        public async Task<ActionResult> DeleteAsync(long id)
        {
            if (id <= 0)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }

            var result = await sysRoleApp.DeleteAsync(id).ConfigureAwait(false);
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
        [DisplayName("批量删除")]
        public async Task<ActionResult> DeleteBatchAsync([FromBody] List<long> idList)
        {
            if (idList == null || idList.Count == 0)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }

            var result = await sysRoleApp.DeleteBatchAsync(idList).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 查询数据(分页) 返回角色表表记录
        /// </summary>
        /// <param name="page">页码</param>
        /// <param name="limit">每页数量</param>
        /// <param name="conditionDTO">查询条件类</param>
        /// <returns></returns>
        [Route("GetListPaged")]
        [ProducesResponseType(typeof(QueryPagedResponseModel<SysRoleEntity>), 200)]
        [HttpGet]
        [DisplayName("查询数据(分页) 返回角色表表记录")]
        public async Task<ActionResult> GetListPagedAsync(int page = 1, int limit = 10, [FromQuery] SysRoleConditionDTO conditionDTO = null)
        {
            var queryPagedResponse = await sysRoleApp.GetListPagedAsync(page, limit, conditionDTO, "Id", true).ConfigureAwait(false);
            return Ok(queryPagedResponse);
        }

        /// <summary>
        /// 返回用户记录，根据角色ID
        /// </summary>
        /// <param name="page">页码</param>
        /// <param name="limit">每页数量</param>
        /// <param name="conditionDTO">查询条件类</param>
        /// <returns></returns>
        [Route("GetUserListPageByRole")]
        [ProducesResponseType(typeof(QueryPagedResponseModel<SysRoleEntity>), 200)]
        [HttpGet]
        [DisplayName("返回用户记录，根据角色ID")]
        public async Task<ActionResult> GetUserListPageByRole(int page = 1, int limit = 10, [FromQuery] SysRoleConditionDTO conditionDTO = null)
        {
            var queryPagedResponse = await sysRoleApp.GetUserListPageByRole(page, limit, conditionDTO, "Id", true).ConfigureAwait(false);
            return Ok(queryPagedResponse);
        }

        /// <summary>
        /// 查询数据(不分页) 返回角色表表记录
        /// </summary>
        /// <param name="conditionDTO">查询条件类</param>
        /// <returns></returns>
        [Route("GetList")]
        [ProducesResponseType(typeof(List<SysRoleEntity>), 200)]
        [HttpGet]
        [DisplayName("查询数据(不分页) 返回角色表表记录")]

        public async Task<ActionResult> GetListAsync([FromQuery] SysRoleConditionDTO conditionDTO)
        {
            var list = await sysRoleApp.GetListAsync(conditionDTO).ConfigureAwait(false);
            return Ok(list);
        }

        /// <summary>
        /// 获取第一条
        /// </summary>
        /// <param name="conditionDTO">查询条件类</param>
        /// <returns></returns>
        [Route("GetFirst")]
        [ProducesResponseType(typeof(SysRoleEntity), 200)]
        [HttpGet]
        [DisplayName("获取第一条")]
        public async Task<ActionResult> GetFirstAsync([FromQuery] SysRoleConditionDTO conditionDTO)
        {
            var list = await sysRoleApp.GetFirstAsync(conditionDTO).ConfigureAwait(false);
            return Ok(list);
        }

        /// <summary>
        /// 根据id集合获取多条数据
        /// </summary>
        /// <param name="ids"></param>
        /// <returns></returns>
        [Route("GetListByIds")]
        [ProducesResponseType(typeof(List<SysRoleEntity>), 200)]
        [HttpPost]
        [DisplayName("根据id集合获取多条数据")]
        public async Task<ActionResult> GetListByIdsAsync([FromBody] List<long> ids)
        {
            var list = await sysRoleApp.GetListByIdsAsync(ids).ConfigureAwait(false);
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
        public async Task<ActionResult> ExportExcelAsync([FromQuery] SysRoleConditionDTO conditionDTO)
        {
            var result = await sysRoleApp.ExportToExcelAsync(conditionDTO).ConfigureAwait(false);
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
        [DisplayName("修改状态")]
        public async Task<ActionResult> UpdateStatus([FromBody] SysRoleUpdateDTO updateDto)
        {
            if (updateDto == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }
            var result = await sysRoleApp.UpdateStatus(updateDto).ConfigureAwait(false);

            return Ok(result);
        }

        /// <summary>
        /// 保存用户角色关联
        /// </summary> 
        /// <param name="updateDto">对应实体数据传输对象</param>
        /// <returns></returns>
        [Route("SaveUser")]
        [DisplayName("保存用户角色关联")]
        [ProducesResponseType(typeof(HttpResponseResultModel<bool>), 200)]
        [HttpPost]
        public async Task<ActionResult> SaveUser([FromBody] PermissionAllocationDTO updateDto)
        {
            if (updateDto == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }
            var result = await sysRoleApp.SaveUser(updateDto).ConfigureAwait(false);

            return Ok(result);
        }

        /// <summary>
        /// 保存用户角色关联
        /// </summary> 
        /// <param name="updateDto">对应实体数据传输对象</param>
        /// <returns></returns>
        [Route("DeleteUser")]
        [DisplayName("删除用户角色关联")]
        [ProducesResponseType(typeof(HttpResponseResultModel<bool>), 200)]
        [HttpPost]
        public async Task<ActionResult> DeleteUser([FromBody] PermissionAllocationDTO updateDto)
        {
            if (updateDto == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }
            var result = await sysRoleApp.DeleteUser(updateDto).ConfigureAwait(false);

            return Ok(result);
        }
    }
}