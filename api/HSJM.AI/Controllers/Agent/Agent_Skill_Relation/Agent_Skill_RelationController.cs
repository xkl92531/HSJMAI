

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:30:47
using  HSJM.AI.AppLayer;
using  HSJM.AI.Utility;
using HSJM.AI.Filters;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.AppLayer.Agent.ViewObject;
using HSJM.AI.AppLayer.Agent.DTOS;
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
    /// 智能体-技能关系表 - 管理智能体技能授权与熟练度接口
    /// </summary>
     	[ApiExplorerSettings(GroupName = "Agent")] 
    [Route("Agent_Skill_Relation")]
    [ApiController]
    [ModelValidation]
    [Authorize]
    [DisplayName("智能体-技能关系表 - 管理智能体技能授权与熟练度")]
    public class  Agent_Skill_RelationController : ControllerBase
    {
        private readonly Agent_Skill_RelationApp agent_Skill_RelationApp;
        
        public Agent_Skill_RelationController(Agent_Skill_RelationApp agent_Skill_RelationApp)
        {
            this.agent_Skill_RelationApp = agent_Skill_RelationApp;
        }

          /// <summary>
        /// 根据主键获取
        /// </summary>
        /// <param name="id">主键id</param>
        /// <returns></returns>
        [Route("Get")]
        [DisplayName("根据主键获取")]
        [ProducesResponseType(typeof(Agent_Skill_RelationEntity), 200)]
        [HttpGet]
        public async Task<ActionResult> GetAsync(long  id)
        {
            var entity = await agent_Skill_RelationApp.GetAsync(id).ConfigureAwait(false);
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
        public async Task<ActionResult> InsertAsync([FromBody]Agent_Skill_RelationInsertDTO insertDto)
        {
            if (insertDto == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }
            var result = await agent_Skill_RelationApp.InsertAsync(insertDto).ConfigureAwait(false);

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
        private async Task<ActionResult> InsertBatchAsync([FromBody]List<Agent_Skill_RelationInsertDTO > insertDtoList)
        {
            if (insertDtoList == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }

            var result = await agent_Skill_RelationApp.InsertBatchAsync(insertDtoList).ConfigureAwait(false);

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
        public async Task<ActionResult> UpdateByIdAsync(long id, [FromBody]Agent_Skill_RelationUpdateDTO  updateDto)
        {
            if (updateDto == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }
            var result = await agent_Skill_RelationApp.UpdateAsync(id,updateDto).ConfigureAwait(false);

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

            var result = await agent_Skill_RelationApp.DeleteAsync(id).ConfigureAwait(false);
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

            var result = await agent_Skill_RelationApp.DeleteBatchAsync(idList).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 查询数据(分页) 返回智能体-技能关系表 - 管理智能体技能授权与熟练度表记录
        /// </summary>
        /// <param name="page">页码</param>
        /// <param name="limit">每页数量</param>
        /// <param name="conditionDTO">查询条件类</param>
        /// <param name="sortField">排序字段</param>
        /// <param name="sortType">desc/asc</param>
        /// <returns></returns>
        [Route("GetListPaged")]
        [ProducesResponseType(typeof(QueryPagedResponseModel<Agent_Skill_RelationEntity>), 200)]
        [HttpGet]
        [DisplayName("查询数据(分页)")]
        public async Task<ActionResult> GetListPagedAsync(int page = 1, int limit = 10,string sortField= "Id", string sortType="desc",[FromQuery]Agent_Skill_RelationConditionDTO conditionDTO = null)
        {
           
            var queryPagedResponse = await agent_Skill_RelationApp.GetListPagedAsync(page, limit, conditionDTO, sortField,sortType.ToLower()=="desc").ConfigureAwait(false);
            return Ok(queryPagedResponse);
        }

        /// <summary>
        /// 查询数据(不分页) 返回智能体-技能关系表 - 管理智能体技能授权与熟练度表记录
        /// </summary>
        /// <param name="conditionDTO">查询条件类</param>
        /// <returns></returns>
        [Route("GetList")]
        [ProducesResponseType(typeof(List<Agent_Skill_RelationEntity>), 200)]
        [HttpGet]
         [DisplayName("查询数据(不分页)")]
        public async Task<ActionResult> GetListAsync([FromQuery]Agent_Skill_RelationConditionDTO conditionDTO)
        {
            var list = await agent_Skill_RelationApp.GetListAsync(conditionDTO).ConfigureAwait(false);
            return Ok(list);
        }

         /// <summary>
        /// 获取第一条
        /// </summary>
        /// <param name="conditionDTO">查询条件类</param>
        /// <returns></returns>
        [Route("GetFirst")]
        [ProducesResponseType(typeof(Agent_Skill_RelationEntity), 200)]
        [HttpGet]
         [DisplayName("获取第一条")]
        public async Task<ActionResult> GetFirstAsync([FromQuery]Agent_Skill_RelationConditionDTO conditionDTO)
        {
            var first = await agent_Skill_RelationApp.GetFirstAsync(conditionDTO).ConfigureAwait(false);
            return Ok(first);
        }
        
		 /// <summary>
        /// 根据id集合获取多条数据
        /// </summary>
        /// <param name="ids"></param>
        /// <returns></returns>
        [Route("GetListByIds")]
        [ProducesResponseType(typeof(List<Agent_Skill_RelationEntity>), 200)]
        [HttpPost]
         [DisplayName("根据id集合获取多条数据")]
        public async Task<ActionResult> GetListByIdsAsync([FromBody]List<long> ids)
        {
            var list = await agent_Skill_RelationApp.GetListByIdsAsync(ids).ConfigureAwait(false);
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
        public async Task<ActionResult> ExportExcelAsync([FromQuery] Agent_Skill_RelationConditionDTO conditionDTO)
        {

            var result = await agent_Skill_RelationApp.ExportToExcelAsync(conditionDTO).ConfigureAwait(false);
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

            var result = await agent_Skill_RelationApp.InsertUploadFileAsync(file).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 批量更新角色技能授权
        /// </summary>
        /// <param name="dto">角色ID和技能ID列表</param>
        /// <returns></returns>
        [Route("SaveSkillAuth")]
        [HttpPost]
        [DisplayName("批量更新角色技能授权")]
        [ProducesResponseType(typeof(HttpResponseResultModel<bool>), 200)]
        public async Task<ActionResult> SaveSkillAuthAsync([FromBody] SaveSkillAuthDTO dto)
        {
            if (dto == null || dto.RoleId <= 0)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }
            var result = await agent_Skill_RelationApp.SaveSkillAuthAsync(dto.RoleId, dto.SkillIds).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 获取角色已授权的技能ID列表
        /// </summary>
        /// <param name="roleId">角色ID</param>
        /// <returns></returns>
        [Route("GetSkillIdsByRoleId")]
        [HttpGet]
        [DisplayName("获取角色已授权的技能ID列表")]
        [ProducesResponseType(typeof(List<long>), 200)]
        public async Task<ActionResult> GetSkillIdsByRoleIdAsync(long roleId)
        {
            if (roleId <= 0)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }
            var result = await agent_Skill_RelationApp.GetSkillIdsByRoleIdAsync(roleId).ConfigureAwait(false);
            return Ok(result);
        }
    }
}
