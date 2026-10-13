
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:24:25
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using System.Linq.Expressions;
using System;
using HSJM.AI.IDbService;
using SqlSugar;
using HSJM.AI.Utility;
using HSJM.AI.Core;
using HSJM.AI.AppLayer;
using HSJM.AI.AppLayer.Agent.ViewObject;
using HSJM.AI.AppLayer.Agent.DTOS;
using HSJM.AI.NPOIHelper;
using System.IO;
using Microsoft.AspNetCore.Http;
namespace HSJM.AI.AppLayer
{
    public class Agent_RelationApp:ISelfScopedAutoInject
    {
        private readonly IAgent_RelationService agent_RelationService;


         public Agent_RelationApp(IAgent_RelationService agent_RelationService)
        {
            this.agent_RelationService = agent_RelationService;
        }

        /// <summary>
        /// 根据主键获取单个实体
        /// </summary>
        /// <param name="id">主键</param>
        /// <returns></returns>
        public async Task<Agent_RelationEntity> GetAsync(long id)
        {
		   return   await agent_RelationService.GetByIdAsync(id).ConfigureAwait(false);
		}


        /// <summary>
        /// 查询数据(分页) 返回指定实体T
        /// </summary>
        /// <param name="page">页码</param>
        /// <param name="size">每页数量</param>
        /// <param name="conditionDTO">查询条件类</param>
        /// <param name="orderByField">排序字段</param>
        /// <param name="isDesc">Desc</param>
        /// <returns></returns>
        public async Task<QueryPagedResponseModel<Agent_RelationEntity>> GetListPagedAsync(int page, int size, Agent_RelationConditionDTO conditionDTO=null, string orderByField = "Id", bool isDesc=true)
        {
            var expression = PackageExpression(conditionDTO);
              
            int total = 0;
            var result =  agent_RelationService.GetListPageOrderBy(expression,page,size, orderByField.CreateOrderByExpression<Agent_RelationEntity>(), isDesc?OrderByType.Desc: OrderByType.Asc, ref total);
            return new QueryPagedResponseModel<Agent_RelationEntity>()
            {
                Total = total,
                Data = result
            };
        }

        /// <summary>
        /// 查询数据(不分页) 返回指定实体T
        /// </summary>
        /// <param name="conditionDTO">查询条件类</param>
        /// <param name="orderByField">排序字段</param>
        /// <param name="isDesc">Desc</param>
        /// <returns></returns>
        public async Task<List<Agent_RelationEntity>> GetListAsync(Agent_RelationConditionDTO  conditionDTO, string orderByField = "Id", bool isDesc = true)
        {
            var expression = PackageExpression(conditionDTO);
            var result = await agent_RelationService.GetListAsync(expression, isDesc ? OrderByType.Desc : OrderByType.Asc, orderByField.CreateOrderByExpression<Agent_RelationEntity>());
            return result;
        }

        /// <summary>
        /// 根据id集合获取多条数据
        /// </summary>
        /// <param name="ids"></param>
        /// <returns></returns>
        public async Task<List<Agent_RelationEntity>> GetListByIdsAsync(List<long> ids)
        {
            if (ids.IsListNullOrEmpty()) {
                return new List<Agent_RelationEntity>();
            }
            return await agent_RelationService.GetListByIdsAsync(ids);
        }

        /// <summary>
        /// 获取最后一条
        /// </summary>
        /// <returns></returns>
        public async Task<Agent_RelationEntity> GetFirstAsync(Agent_RelationConditionDTO conditionDTO)
        {
            var expression = PackageExpression(conditionDTO);
            return await agent_RelationService.GetFirstAsync(expression, x => x.CreateTime, OrderByType.Desc);
        }

	    /// <summary>
        /// 插入单个实体
        /// </summary>
        /// <param name="insertDto">对应实体数据传输对象</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<long>> InsertAsync(Agent_RelationInsertDTO insertDto)
        { 
            HttpResponseResultModel<long> httpResponseResultModel = new HttpResponseResultModel<long>();
            var entity = insertDto.MapTo<Agent_RelationEntity>();
            httpResponseResultModel.IsSuccess= await agent_RelationService.InsertAsync(entity).ConfigureAwait(false);
            httpResponseResultModel.BackResult = entity.Id;
            return httpResponseResultModel;
        }

		 

		/// <summary>
        /// 批量插入实体
        /// </summary>
        /// <param name="insertDtoList">对应实体数据传输对象集合</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> InsertBatchAsync(List<Agent_RelationInsertDTO> insertDtoList)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();
            var entityList = insertDtoList.MapToList<Agent_RelationEntity>();
            httpResponseResultModel.IsSuccess = await agent_RelationService.InsertRangeAsync(entityList);
            httpResponseResultModel.BackResult = httpResponseResultModel.IsSuccess;
            return httpResponseResultModel;
        }
       
		  
        /// <summary>
        /// 根据主键更新实体
        /// </summary>
		/// <param name="id">主键</param>
        /// <param name="updateDto">对应实体数据传输对象</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> UpdateAsync(long id,Agent_RelationUpdateDTO updateDto)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();
            var entity = updateDto.MapTo<Agent_RelationEntity>();
            entity.Id = id;
            httpResponseResultModel.IsSuccess = await agent_RelationService.UpdateAsync(entity);
            httpResponseResultModel.BackResult = httpResponseResultModel.IsSuccess;
            return httpResponseResultModel;
        }
	  
 
       /// <summary>
        /// 根据根据主键删除
        /// </summary>
        /// <param name="id">主键</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> DeleteAsync(long id)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();
            httpResponseResultModel.IsSuccess = await agent_RelationService.SoftDeleteByIdAsync(id);
            httpResponseResultModel.BackResult = httpResponseResultModel.IsSuccess;
            return httpResponseResultModel;
	   }


	     /// <summary>
        /// 批量删除 根据主键
        /// </summary>
        /// <param name="idList">主键集合</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> DeleteBatchAsync(List<long> idList)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();
            if (idList.IsListNullOrEmpty()) {
                httpResponseResultModel.ErrorMessage = "id is null！";
                return httpResponseResultModel;
            }
            httpResponseResultModel.IsSuccess = await agent_RelationService.SoftDeleteByIdsAsync(idList);
            httpResponseResultModel.BackResult = httpResponseResultModel.IsSuccess;
            return httpResponseResultModel;
        }

        /// <summary>
        /// 条件组装
        /// </summary>
        /// <param name="dto"></param>
        /// <returns></returns>
        private Expression<Func<Agent_RelationEntity, bool>> PackageExpression(Agent_RelationConditionDTO dto) {
            Expression<Func<Agent_RelationEntity, bool>> expression = Expressionable.Create<Agent_RelationEntity>()
                .And(t => t.IsDeleted == false)
                .AndIF(dto.ParentRoleID.HasValue&&dto.ParentRoleID > 0,x => x.ParentRoleID == dto.ParentRoleID.Value)
    .AndIF(dto.ChildRoleID.HasValue&&dto.ChildRoleID > 0,x => x.ChildRoleID == dto.ChildRoleID.Value)
    .AndIF(!string.IsNullOrEmpty(dto.RelationType),x => x.RelationType == dto.RelationType)
    .AndIF(!string.IsNullOrEmpty(dto.Description),x => x.Description.Contains(dto.Description))

                .ToExpression();
            return expression;
        }

       
        /// <summary>
        /// 导出Excel
        /// </summary>
        /// <param name="conditionDTO"></param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<string>> ExportToExcelAsync(Agent_RelationConditionDTO conditionDTO)
        {
            HttpResponseResultModel<string> httpResponseResultModel = new HttpResponseResultModel<string>();
            var exportCondition = new ExportExcel<Agent_RelationView>();
            var tags = await GetListAsync(conditionDTO);
            exportCondition.Data = tags.MapToList<Agent_RelationView>().ToList();
            exportCondition.FileName = $"记录导出{DateTime.Now.ToString("yyMMddHHmmss")}.xlsx";
            string currentDirectory = Path.Combine(Environment.CurrentDirectory, "wwwroot", "导出");

            exportCondition.FilePath = currentDirectory + "/";
            if (!Directory.Exists(currentDirectory))
            {
                Directory.CreateDirectory(currentDirectory);
            }
            // 查询数据导出
            var res = await MyExportImportExcelHelper.ExportExcelAsync(exportCondition);
            if (res.HasError == true)
            {
                httpResponseResultModel.ErrorMessage = res.ErrorMessage;
                return httpResponseResultModel;

            }
            httpResponseResultModel.BackResult = $"/导出/{exportCondition.FileName}";
            httpResponseResultModel.IsSuccess = true;
            return httpResponseResultModel;
        }



        /// <summary>
        /// 导入Excel
        /// </summary>
        public async Task<HttpResponseResultModel<string>> InsertUploadFileAsync(IFormFile file)
        {
            // 根据ID查询现有的标记，进行字段更新
            // 如果不存在，则新增标记
            HttpResponseResultModel<string> httpResponse = new HttpResponseResultModel<string>();
            var drictory = StreamToFiles(file);
            var importResult = await MyExportImportExcelHelper.ImportExcelAsync<Agent_RelationView>(new FileStream(drictory, FileMode.Open));

            if (importResult.HasError)
            {
                httpResponse.ErrorMessage = importResult.ErrorMessage;
                return httpResponse;
            }
            var importDatas = importResult.Data;
            if (importDatas.IsListNullOrEmpty())
            {
                httpResponse.IsSuccess = false;
                httpResponse.BackResult = " 表格里没有符合条件的数据";
                return httpResponse;
            }
            var insertList = new List<Agent_RelationEntity>();
            importDatas.ForEach(s =>
            {
                s.ID = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                insertList.Add(s.MapTo<Agent_RelationEntity>());
            });

            var insertResult = await agent_RelationService.InsertRangeAsync(insertList);

            if (insertResult)
            {
                httpResponse.IsSuccess = true;
                httpResponse.BackResult = $"成功导入数据{insertList.Count}条";

            }
            else
            {
                httpResponse.ErrorMessage = "数据入库失败！";
            }
            return httpResponse;



        }
        private string StreamToFiles(IFormFile file)
        {
            try
            {
                var stream = file.OpenReadStream();
                byte[] bytes = new byte[stream.Length];
                stream.Read(bytes, 0, bytes.Length);
                stream.Seek(0, SeekOrigin.Begin);
                string path = Directory.GetCurrentDirectory() + "\\wwwroot\\" + file.FileName;
                FileStream fs = new FileStream(path, FileMode.Create);
                BinaryWriter bw = new BinaryWriter(fs);
                bw.Write(bytes);
                bw.Close();
                fs.Close();
                return path;
            }
            catch (Exception e)
            {
                throw e;
            }

        }

        /// <summary>
        /// 获取Planner的下属智能体ID列表
        /// </summary>
        /// <param name="plannerRoleId">Planner角色ID</param>
        /// <returns>下属智能体ID列表</returns>
        public async Task<List<long>> GetSubordinateIdsByPlannerRoleIdAsync(long plannerRoleId)
        {
            var relations = await agent_RelationService.GetListAsync(
                x => x.ParentRoleID == plannerRoleId && x.IsDeleted == false);
            return relations.Select(x => x.ChildRoleID ?? 0).Where(id => id > 0).ToList();
        }

        /// <summary>
        /// 批量更新Planner的下属智能体
        /// </summary>
        /// <param name="plannerRoleId">Planner角色ID</param>
        /// <param name="subordinateRoleIds">下属智能体角色ID集合</param>
        /// <returns>操作结果</returns>
        public async Task<HttpResponseResultModel<bool>> SaveSubordinatesAsync(long plannerRoleId, List<long> subordinateRoleIds)
        {
            var result = new HttpResponseResultModel<bool>();
            
            try
            {
                using (var tran = agent_RelationService.BeginTran())
                {
                    // 1. 删除现有下属关系（软删除）
                    var existingRelations = await agent_RelationService.GetListAsync(
                        x => x.ParentRoleID == plannerRoleId && x.IsDeleted == false);
                    
                    if (existingRelations.Count > 0)
                    {
                        var idsToDelete = existingRelations.Select(x => x.Id).ToList();
                        await agent_RelationService.SoftDeleteByIdsAsync(idsToDelete);
                    }

                    // 2. 插入新的下属关系
                    if (subordinateRoleIds != null && subordinateRoleIds.Count > 0)
                    {
                        var newRelations = subordinateRoleIds.Select(childId => new Agent_RelationEntity
                        {
                            ParentRoleID = plannerRoleId,
                            ChildRoleID = childId,
                            RelationType = "Supervisor",
                            Description = "Planner调度下属智能体"
                        }).ToList();

                        await agent_RelationService.InsertRangeAsync(newRelations);
                    }

                    tran.Complete();
                    result.IsSuccess = true;
                    result.BackResult = true;
                }
            }
            catch (Exception ex)
            {
                result.IsSuccess = false;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

    }
}
