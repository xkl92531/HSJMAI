
using MAF.DEMO.AppLayer;
using MAF.DEMO.AppLayer.SYS.SysUser;
using MAF.DEMO.Core;
using MAF.DEMO.Filters;
using MAF.DEMO.Utility;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.Threading.Tasks;
using MAF.DEMO.AppLayer.SYS.SysUser.DTOS;
using Microsoft.AspNetCore.Authorization;
using Org.BouncyCastle.Crypto;
using System.ComponentModel;
using MAF.DEMO.AppLayer.SYS.DTOS;

namespace MAF.DEMO.Controllers.System.SysUser
{
    [ApiExplorerSettings(GroupName = "System")]
    [Route("SysUser")]
    [ApiController]
    [ModelValidation]
    [Authorize]
    public class SysUserController : ControllerBase
    {
        SysUserApp userApp;
        public SysUserController(SysUserApp userApp)
        {
            this.userApp = userApp;
        }

        /// <summary>
        /// 根据主键获取
        /// </summary>
        /// <param name="id">主键id</param>
        /// <returns></returns>
        [Route("Get")]
        [ProducesResponseType(typeof(SysUserView), 200)]
        [HttpGet]
        public async Task<ActionResult> GetAsync(long id)
        {
            var entity = await userApp.GetAsync(id).ConfigureAwait(false);
            return Ok(entity);
        }

        /// <summary>
        /// 根据主键获取
        /// </summary>
        /// <param name="id">主键id</param>
        /// <returns></returns>
        [Route("GetEntity/{id}")]
        [ProducesResponseType(typeof(SysUserView), 200)]
        [HttpGet]
        public async Task<ActionResult> GetEntity(long id)
        {
            var entity = await userApp.GetEntity(id).ConfigureAwait(false);
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
        public async Task<ActionResult> InsertAsync([FromBody] SysUserInsertDTO insertDto)
        {
            if (insertDto == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }
            var result = await userApp.InsertAsync(insertDto).ConfigureAwait(false);

            return Ok(result);
        }


        /// <summary>
        /// 批量插入新实体
        /// </summary>
        /// <param name="insertDtoList">对应实体数据传输对象集合</param>
        /// <returns></returns>
        [Route("InsertBatch")]
        [HttpPost]
        [ProducesResponseType(typeof(HttpResponseResultModel<bool>), 200)]
        private async Task<ActionResult> InsertBatchAsync([FromBody] List<SysUserInsertDTO> insertDtoList)
        {
            if (insertDtoList == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }

            var result = await userApp.InsertBatchAsync(insertDtoList).ConfigureAwait(false);

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
        public async Task<ActionResult> UpdateByIdAsync(long id, [FromBody] SysUserUpdateDTO updateDto)
        {
            if (updateDto == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }
            var result = await userApp.UpdateAsync(id, updateDto).ConfigureAwait(false);

            return Ok(result);
        }

        /// <summary>
        /// 重置密码
        /// </summary> 
        /// <param name="updateDto">对应实体数据传输对象</param>
        /// <returns></returns>
        [Route("PasswordReset")]
        [ProducesResponseType(typeof(HttpResponseResultModel<bool>), 200)]
        [HttpPost]
        public async Task<ActionResult> PasswordReset([FromBody] SysUserUpdateDTO updateDto)
        {
            if (updateDto == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }
            var result = await userApp.PasswordReset(updateDto).ConfigureAwait(false);

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
        public async Task<ActionResult> UpdateStatus([FromBody] SysUserUpdateDTO updateDto)
        {
            if (updateDto == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }
            var result = await userApp.UpdateStatus(updateDto).ConfigureAwait(false);

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

            var result = await userApp.DeleteAsync(id).ConfigureAwait(false);
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

            var result = await userApp.DeleteBatchAsync(idList).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 查询数据(分页) 返回表记录
        /// </summary>
        /// <param name="page">页码</param>
        /// <param name="limit">每页数量</param>
        /// <param name="conditionDTO">查询条件类</param>
        /// <returns></returns>
        [Route("GetListPaged")]
        [ProducesResponseType(typeof(QueryPagedResponseModel<SysUserView>), 200)]
        [HttpGet]
        public async Task<ActionResult> GetListPagedAsync(int page = 1, int limit = 10, [FromQuery] SysUserConditionDTO conditionDTO = null)
        {

            var queryPagedResponse = await userApp.GetListPagedAsync(page, limit, conditionDTO, "Id", true).ConfigureAwait(false);
            return Ok(queryPagedResponse);
        }

        /// <summary>
        /// 获取第一条
        /// </summary>
        /// <param name="conditionDTO">查询条件类</param>
        /// <returns></returns>
        [Route("GetFirst")]
        [ProducesResponseType(typeof(List<SysUserView>), 200)]
        [HttpGet]
        public async Task<ActionResult> GetFirstAsync([FromQuery] SysUserConditionDTO conditionDTO)
        {
            var list = await userApp.GetFirstAsync(conditionDTO).ConfigureAwait(false);
            return Ok(list);
        }

        /// <summary>
        /// 查询数据(不分页) 返回表记录
        /// </summary>
        /// <param name="conditionDTO">查询条件类</param>
        /// <returns></returns>
        [Route("GetList")]
        [ProducesResponseType(typeof(List<SysUserView>), 200)]
        [HttpGet]
        public async Task<ActionResult> GetListAsync([FromQuery] SysUserConditionDTO conditionDTO)
        {
            var list = await userApp.GetListAsync(conditionDTO).ConfigureAwait(false);
            return Ok(list);
        }


        /// <summary>
        /// 根据id集合获取多条数据
        /// </summary>
        /// <param name="ids"></param>
        /// <returns></returns>
        [Route("GetListByIds")]
        [ProducesResponseType(typeof(List<SysUserView>), 200)]
        [HttpPost]
        public async Task<ActionResult> GetListByIdsAsync([FromBody] List<long> ids)
        {
            var list = await userApp.GetListByIdsAsync(ids).ConfigureAwait(false);
            return Ok(list);
        }
        /// <summary>
        /// 获取权限
        /// </summary>
        /// <returns></returns>
        [Route("GetPermissions")]
        [ProducesResponseType(typeof(HttpResponseResultModel<List<string>>), 200)]
        [HttpGet]
        public async Task<ActionResult> GetPermissionsAsync()
        {
            var list = await userApp.GetPermissions().ConfigureAwait(false);
            HttpResponseResultModel<List<string>> responseResultModel = new HttpResponseResultModel<List<string>>();
            responseResultModel.IsSuccess = true;
            responseResultModel.BackResult = list;
            return Ok(responseResultModel);
        }


        /// <summary>
        /// 修改密码
        /// </summary>
        /// <param name="dto"></param>
        /// <returns></returns>
        [Route("ChangePassword")]
        [ProducesResponseType(typeof(HttpResponseResultModel<bool>), 200)]
        [HttpPost]
        [DisplayName("修改密码")]
        public async Task<ActionResult> ChangePassword(UserChangePwdDTO dto)
        {
            if (dto == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }

            var result = await userApp.ChangePassword(dto).ConfigureAwait(false);
            return Ok(result);
        }


        /// <summary>
        /// 角色批量分配
        /// </summary>
        /// <param name="dto"></param>
        /// <returns></returns>
        [Route("SavePur")]
        [ProducesResponseType(typeof(HttpResponseResultModel<bool>), 200)]
        [HttpPost]
        public async Task<ActionResult> SavePur(PermissionAllocationDTO dto)
        {
            if (dto == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }

            var result = await userApp.SavePur(dto).ConfigureAwait(false);
            return Ok(result);
        }


    }
}
