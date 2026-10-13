
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:33:05
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
    public class Agent_WorkflowApp:ISelfScopedAutoInject
    {
        private readonly IAgent_WorkflowService agent_WorkflowService;
        private readonly IAgent_TaskService agent_TaskService;


         public Agent_WorkflowApp(IAgent_WorkflowService agent_WorkflowService, IAgent_TaskService agent_TaskService)
        {
            this.agent_WorkflowService = agent_WorkflowService;
            this.agent_TaskService = agent_TaskService;
        }

        /// <summary>
        /// 根据主键获取单个实体
        /// </summary>
        /// <param name="id">主键</param>
        /// <returns></returns>
        public async Task<Agent_WorkflowEntity> GetAsync(long id)
        {
		   return   await agent_WorkflowService.GetByIdAsync(id).ConfigureAwait(false);
		}

        /// <summary>
        /// 获取Workflow详情，包含关联的任务列表
        /// </summary>
        /// <param name="id">Workflow主键</param>
        /// <returns></returns>
        public async Task<WorkflowDetailResult> GetDetailAsync(long id)
        {
            var workflow = await agent_WorkflowService.GetByIdAsync(id).ConfigureAwait(false);
            if (workflow == null || workflow.IsDeleted)
            {
                return null;
            }

            // 获取关联的任务列表
            var tasks = await agent_TaskService.GetListAsync(
                t => t.WorkflowID == id && t.IsDeleted == false,
                OrderByType.Asc,
                t => t.CreateTime
            );

            return new WorkflowDetailResult
            {
                Workflow = workflow,
                Tasks = tasks ?? new List<Agent_TaskEntity>()
            };
        }

        /// <summary>
        /// 获取Workflow下的任务列表
        /// </summary>
        /// <param name="workflowId">Workflow主键</param>
        /// <returns></returns>
        public async Task<List<Agent_TaskEntity>> GetTasksAsync(long workflowId)
        {
            return await agent_TaskService.GetListAsync(
                t => t.WorkflowID == workflowId && t.IsDeleted == false,
                OrderByType.Asc,
                t => t.CreateTime
            );
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
        public async Task<QueryPagedResponseModel<Agent_WorkflowEntity>> GetListPagedAsync(int page, int size, Agent_WorkflowConditionDTO conditionDTO=null, string orderByField = "Id", bool isDesc=true)
        {
            var expression = PackageExpression(conditionDTO);
              
            int total = 0;
            var result =  agent_WorkflowService.GetListPageOrderBy(expression,page,size, orderByField.CreateOrderByExpression<Agent_WorkflowEntity>(), isDesc?OrderByType.Desc: OrderByType.Asc, ref total);
            return new QueryPagedResponseModel<Agent_WorkflowEntity>()
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
        public async Task<List<Agent_WorkflowEntity>> GetListAsync(Agent_WorkflowConditionDTO  conditionDTO, string orderByField = "Id", bool isDesc = true)
        {
            var expression = PackageExpression(conditionDTO);
            var result = await agent_WorkflowService.GetListAsync(expression, isDesc ? OrderByType.Desc : OrderByType.Asc, orderByField.CreateOrderByExpression<Agent_WorkflowEntity>());
            return result;
        }

        /// <summary>
        /// 根据id集合获取多条数据
        /// </summary>
        /// <param name="ids"></param>
        /// <returns></returns>
        public async Task<List<Agent_WorkflowEntity>> GetListByIdsAsync(List<long> ids)
        {
            if (ids.IsListNullOrEmpty()) {
                return new List<Agent_WorkflowEntity>();
            }
            return await agent_WorkflowService.GetListByIdsAsync(ids);
        }

        /// <summary>
        /// 获取最后一条
        /// </summary>
        /// <returns></returns>
        public async Task<Agent_WorkflowEntity> GetFirstAsync(Agent_WorkflowConditionDTO conditionDTO)
        {
            var expression = PackageExpression(conditionDTO);
            return await agent_WorkflowService.GetFirstAsync(expression, x => x.CreateTime, OrderByType.Desc);
        }

	    /// <summary>
        /// 插入单个实体
        /// </summary>
        /// <param name="insertDto">对应实体数据传输对象</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<long>> InsertAsync(Agent_WorkflowInsertDTO insertDto)
        { 
            HttpResponseResultModel<long> httpResponseResultModel = new HttpResponseResultModel<long>();
            var entity = insertDto.MapTo<Agent_WorkflowEntity>();
            httpResponseResultModel.IsSuccess= await agent_WorkflowService.InsertAsync(entity).ConfigureAwait(false);
            httpResponseResultModel.BackResult = entity.Id;
            return httpResponseResultModel;
        }

		 

		/// <summary>
        /// 批量插入实体
        /// </summary>
        /// <param name="insertDtoList">对应实体数据传输对象集合</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> InsertBatchAsync(List<Agent_WorkflowInsertDTO> insertDtoList)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();
            var entityList = insertDtoList.MapToList<Agent_WorkflowEntity>();
            httpResponseResultModel.IsSuccess = await agent_WorkflowService.InsertRangeAsync(entityList);
            httpResponseResultModel.BackResult = httpResponseResultModel.IsSuccess;
            return httpResponseResultModel;
        }
       
		  
        /// <summary>
        /// 根据主键更新实体
        /// </summary>
		/// <param name="id">主键</param>
        /// <param name="updateDto">对应实体数据传输对象</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> UpdateAsync(long id,Agent_WorkflowUpdateDTO updateDto)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();
            var entity = updateDto.MapTo<Agent_WorkflowEntity>();
            entity.Id = id;
            httpResponseResultModel.IsSuccess = await agent_WorkflowService.UpdateAsync(entity);
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
            httpResponseResultModel.IsSuccess = await agent_WorkflowService.SoftDeleteByIdAsync(id);
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
            httpResponseResultModel.IsSuccess = await agent_WorkflowService.SoftDeleteByIdsAsync(idList);
            httpResponseResultModel.BackResult = httpResponseResultModel.IsSuccess;
            return httpResponseResultModel;
        }

        /// <summary>
        /// 条件组装
        /// </summary>
        /// <param name="dto"></param>
        /// <returns></returns>
        private Expression<Func<Agent_WorkflowEntity, bool>> PackageExpression(Agent_WorkflowConditionDTO dto) {
            Expression<Func<Agent_WorkflowEntity, bool>> expression = Expressionable.Create<Agent_WorkflowEntity>()
                .And(t => t.IsDeleted == false)
                .AndIF(dto.UsesMAFWorkflow.HasValue,x => x.UsesMAFWorkflow == dto.UsesMAFWorkflow.Value)
    .AndIF(!string.IsNullOrEmpty(dto.MAFCheckpointId),x => x.MAFCheckpointId == dto.MAFCheckpointId)
    .AndIF(!string.IsNullOrEmpty(dto.MAFWorkflowId),x => x.MAFWorkflowId == dto.MAFWorkflowId)
    .AndIF(!string.IsNullOrEmpty(dto.PlanStatus),x => x.PlanStatus == dto.PlanStatus)
    .AndIF(!string.IsNullOrEmpty(dto.TriggerType),x => x.TriggerType == dto.TriggerType)
    .AndIF(dto.PlannerAgentID.HasValue&&dto.PlannerAgentID > 0,x => x.PlannerAgentID == dto.PlannerAgentID.Value)
    .AndIF(!string.IsNullOrEmpty(dto.TraceId),x => x.TraceId == dto.TraceId)

                .ToExpression();
            return expression;
        }

       
        /// <summary>
        /// 导出Excel
        /// </summary>
        /// <param name="conditionDTO"></param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<string>> ExportToExcelAsync(Agent_WorkflowConditionDTO conditionDTO)
        {
            HttpResponseResultModel<string> httpResponseResultModel = new HttpResponseResultModel<string>();
            var exportCondition = new ExportExcel<Agent_WorkflowView>();
            var tags = await GetListAsync(conditionDTO);
            exportCondition.Data = tags.MapToList<Agent_WorkflowView>().ToList();
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
            var importResult = await MyExportImportExcelHelper.ImportExcelAsync<Agent_WorkflowView>(new FileStream(drictory, FileMode.Open));

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
            var insertList = new List<Agent_WorkflowEntity>();
            importDatas.ForEach(s =>
            {
                s.ID = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                insertList.Add(s.MapTo<Agent_WorkflowEntity>());
            });

            var insertResult = await agent_WorkflowService.InsertRangeAsync(insertList);

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

    }
}
