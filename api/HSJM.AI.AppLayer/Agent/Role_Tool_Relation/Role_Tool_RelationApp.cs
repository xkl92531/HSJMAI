
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:35:38
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
    public class Role_Tool_RelationApp:ISelfScopedAutoInject
    {
        private readonly IRole_Tool_RelationService role_Tool_RelationService;


         public Role_Tool_RelationApp(IRole_Tool_RelationService role_Tool_RelationService)
        {
            this.role_Tool_RelationService = role_Tool_RelationService;
        }

        /// <summary>
        /// 根据主键获取单个实体
        /// </summary>
        /// <param name="id">主键</param>
        /// <returns></returns>
        public async Task<Role_Tool_RelationEntity> GetAsync(long id)
        {
		   return   await role_Tool_RelationService.GetByIdAsync(id).ConfigureAwait(false);
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
        public async Task<QueryPagedResponseModel<Role_Tool_RelationEntity>> GetListPagedAsync(int page, int size, Role_Tool_RelationConditionDTO conditionDTO=null, string orderByField = "Id", bool isDesc=true)
        {
            var expression = PackageExpression(conditionDTO);
              
            int total = 0;
            var result =  role_Tool_RelationService.GetListPageOrderBy(expression,page,size, orderByField.CreateOrderByExpression<Role_Tool_RelationEntity>(), isDesc?OrderByType.Desc: OrderByType.Asc, ref total);
            return new QueryPagedResponseModel<Role_Tool_RelationEntity>()
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
        public async Task<List<Role_Tool_RelationEntity>> GetListAsync(Role_Tool_RelationConditionDTO  conditionDTO, string orderByField = "Id", bool isDesc = true)
        {
            var expression = PackageExpression(conditionDTO);
            var result = await role_Tool_RelationService.GetListAsync(expression, isDesc ? OrderByType.Desc : OrderByType.Asc, orderByField.CreateOrderByExpression<Role_Tool_RelationEntity>());
            return result;
        }

        /// <summary>
        /// 根据id集合获取多条数据
        /// </summary>
        /// <param name="ids"></param>
        /// <returns></returns>
        public async Task<List<Role_Tool_RelationEntity>> GetListByIdsAsync(List<long> ids)
        {
            if (ids.IsListNullOrEmpty()) {
                return new List<Role_Tool_RelationEntity>();
            }
            return await role_Tool_RelationService.GetListByIdsAsync(ids);
        }

        /// <summary>
        /// 获取最后一条
        /// </summary>
        /// <returns></returns>
        public async Task<Role_Tool_RelationEntity> GetFirstAsync(Role_Tool_RelationConditionDTO conditionDTO)
        {
            var expression = PackageExpression(conditionDTO);
            return await role_Tool_RelationService.GetFirstAsync(expression, x => x.CreateTime, OrderByType.Desc);
        }

	    /// <summary>
        /// 插入单个实体
        /// </summary>
        /// <param name="insertDto">对应实体数据传输对象</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<long>> InsertAsync(Role_Tool_RelationInsertDTO insertDto)
        { 
            HttpResponseResultModel<long> httpResponseResultModel = new HttpResponseResultModel<long>();
            var entity = insertDto.MapTo<Role_Tool_RelationEntity>();
            httpResponseResultModel.IsSuccess= await role_Tool_RelationService.InsertAsync(entity).ConfigureAwait(false);
            httpResponseResultModel.BackResult = entity.Id;
            return httpResponseResultModel;
        }

		 

		/// <summary>
        /// 批量插入实体
        /// </summary>
        /// <param name="insertDtoList">对应实体数据传输对象集合</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> InsertBatchAsync(List<Role_Tool_RelationInsertDTO> insertDtoList)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();
            var entityList = insertDtoList.MapToList<Role_Tool_RelationEntity>();
            httpResponseResultModel.IsSuccess = await role_Tool_RelationService.InsertRangeAsync(entityList);
            httpResponseResultModel.BackResult = httpResponseResultModel.IsSuccess;
            return httpResponseResultModel;
        }
       
		  
        /// <summary>
        /// 根据主键更新实体
        /// </summary>
		/// <param name="id">主键</param>
        /// <param name="updateDto">对应实体数据传输对象</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> UpdateAsync(long id,Role_Tool_RelationUpdateDTO updateDto)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();
            var entity = updateDto.MapTo<Role_Tool_RelationEntity>();
            entity.Id = id;
            httpResponseResultModel.IsSuccess = await role_Tool_RelationService.UpdateAsync(entity);
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
            httpResponseResultModel.IsSuccess = await role_Tool_RelationService.SoftDeleteByIdAsync(id);
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
            httpResponseResultModel.IsSuccess = await role_Tool_RelationService.SoftDeleteByIdsAsync(idList);
            httpResponseResultModel.BackResult = httpResponseResultModel.IsSuccess;
            return httpResponseResultModel;
        }

        /// <summary>
        /// 批量更新角色工具授权（一次性替换）
        /// </summary>
        /// <param name="roleId">角色ID</param>
        /// <param name="toolIds">工具ID列表</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> SaveToolAuthAsync(long roleId, List<long> toolIds)
        {
            HttpResponseResultModel<bool> result = new HttpResponseResultModel<bool>();
            
            toolIds = toolIds ?? new List<long>();
            
            // 获取当前已授权的工具关联
            var existingRels = await role_Tool_RelationService.GetListAsync(
                r => r.RoleID == roleId && r.IsDeleted == false);
            var existingToolIds = existingRels.Where(r => r.ToolID.HasValue).Select(r => r.ToolID.Value).ToHashSet();
            var existingRelMap = existingRels.Where(r => r.ToolID.HasValue).ToDictionary(r => r.ToolID.Value, r => r.Id);
            
            var newToolIds = toolIds.ToHashSet();
            
            // 需要新增的
            var toAdd = newToolIds.Except(existingToolIds).ToList();
            // 需要删除的
            var toRemove = existingToolIds.Except(newToolIds).ToList();
            
            using (var tran = role_Tool_RelationService.BeginTran())
            {
                // 批量软删除
                if (toRemove.Any())
                {
                    var idsToDelete = toRemove.Select(toolId => existingRelMap[toolId]).ToList();
                    await role_Tool_RelationService.SoftDeleteByIdsAsync(idsToDelete);
                }
                
                // 批量新增
                if (toAdd.Any())
                {
                    var newEntities = toAdd.Select(toolId => new Role_Tool_RelationEntity
                    {
                        RoleID = roleId,
                        ToolID = toolId
                    }).ToList();
                    await role_Tool_RelationService.InsertRangeAsync(newEntities);
                }
                
                tran.Complete();
            }
            
            result.IsSuccess = true;
            result.BackResult = true;
            return result;
        }

        /// <summary>
        /// 获取角色已授权的工具ID列表
        /// </summary>
        /// <param name="roleId">角色ID</param>
        /// <returns></returns>
        public async Task<List<long>> GetToolIdsByRoleIdAsync(long roleId)
        {
            var rels = await role_Tool_RelationService.GetListAsync(
                r => r.RoleID == roleId && r.IsDeleted == false);
            return rels.Where(r => r.ToolID.HasValue).Select(r => r.ToolID.Value).ToList();
        }

        /// <summary>
        /// 条件组装
        /// </summary>
        /// <param name="dto"></param>
        /// <returns></returns>
        private Expression<Func<Role_Tool_RelationEntity, bool>> PackageExpression(Role_Tool_RelationConditionDTO dto) {
            Expression<Func<Role_Tool_RelationEntity, bool>> expression = Expressionable.Create<Role_Tool_RelationEntity>()
                .And(t => t.IsDeleted == false)
            
                .ToExpression();
            return expression;
        }

       
        /// <summary>
        /// 导出Excel
        /// </summary>
        /// <param name="conditionDTO"></param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<string>> ExportToExcelAsync(Role_Tool_RelationConditionDTO conditionDTO)
        {
            HttpResponseResultModel<string> httpResponseResultModel = new HttpResponseResultModel<string>();
            var exportCondition = new ExportExcel<Role_Tool_RelationView>();
            var tags = await GetListAsync(conditionDTO);
            exportCondition.Data = tags.MapToList<Role_Tool_RelationView>().ToList();
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
            var importResult = await MyExportImportExcelHelper.ImportExcelAsync<Role_Tool_RelationView>(new FileStream(drictory, FileMode.Open));

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
            var insertList = new List<Role_Tool_RelationEntity>();
            importDatas.ForEach(s =>
            {
                s.ID = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                insertList.Add(s.MapTo<Role_Tool_RelationEntity>());
            });

            var insertResult = await role_Tool_RelationService.InsertRangeAsync(insertList);

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
