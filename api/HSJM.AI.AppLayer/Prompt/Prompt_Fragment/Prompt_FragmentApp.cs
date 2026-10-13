
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:35:04
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
using HSJM.AI.AppLayer.Prompt.ViewObject;
using HSJM.AI.AppLayer.Prompt.DTOS;
using HSJM.AI.NPOIHelper;
using System.IO;
using Microsoft.AspNetCore.Http;
namespace HSJM.AI.AppLayer
{
    public class Prompt_FragmentApp:ISelfScopedAutoInject
    {
        private readonly IPrompt_FragmentService prompt_FragmentService;


         public Prompt_FragmentApp(IPrompt_FragmentService prompt_FragmentService)
        {
            this.prompt_FragmentService = prompt_FragmentService;
        }

        /// <summary>
        /// 根据主键获取单个实体
        /// </summary>
        /// <param name="id">主键</param>
        /// <returns></returns>
        public async Task<Prompt_FragmentEntity> GetAsync(long id)
        {
		   return   await prompt_FragmentService.GetByIdAsync(id).ConfigureAwait(false);
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
        public async Task<QueryPagedResponseModel<Prompt_FragmentEntity>> GetListPagedAsync(int page, int size, Prompt_FragmentConditionDTO conditionDTO=null, string orderByField = "Id", bool isDesc=true)
        {
            var expression = PackageExpression(conditionDTO);
              
            int total = 0;
            var result =  prompt_FragmentService.GetListPageOrderBy(expression,page,size, orderByField.CreateOrderByExpression<Prompt_FragmentEntity>(), isDesc?OrderByType.Desc: OrderByType.Asc, ref total);
            return new QueryPagedResponseModel<Prompt_FragmentEntity>()
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
        public async Task<List<Prompt_FragmentEntity>> GetListAsync(Prompt_FragmentConditionDTO  conditionDTO, string orderByField = "Id", bool isDesc = true)
        {
            var expression = PackageExpression(conditionDTO);
            var result = await prompt_FragmentService.GetListAsync(expression, isDesc ? OrderByType.Desc : OrderByType.Asc, orderByField.CreateOrderByExpression<Prompt_FragmentEntity>());
            return result;
        }

        /// <summary>
        /// 根据id集合获取多条数据
        /// </summary>
        /// <param name="ids"></param>
        /// <returns></returns>
        public async Task<List<Prompt_FragmentEntity>> GetListByIdsAsync(List<long> ids)
        {
            if (ids.IsListNullOrEmpty()) {
                return new List<Prompt_FragmentEntity>();
            }
            return await prompt_FragmentService.GetListByIdsAsync(ids);
        }

        /// <summary>
        /// 获取最后一条
        /// </summary>
        /// <returns></returns>
        public async Task<Prompt_FragmentEntity> GetFirstAsync(Prompt_FragmentConditionDTO conditionDTO)
        {
            var expression = PackageExpression(conditionDTO);
            return await prompt_FragmentService.GetFirstAsync(expression, x => x.CreateTime, OrderByType.Desc);
        }

	    /// <summary>
        /// 插入单个实体
        /// </summary>
        /// <param name="insertDto">对应实体数据传输对象</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<long>> InsertAsync(Prompt_FragmentInsertDTO insertDto)
        { 
            HttpResponseResultModel<long> httpResponseResultModel = new HttpResponseResultModel<long>();
            var entity = insertDto.MapTo<Prompt_FragmentEntity>();
            httpResponseResultModel.IsSuccess= await prompt_FragmentService.InsertAsync(entity).ConfigureAwait(false);
            httpResponseResultModel.BackResult = entity.Id;
            return httpResponseResultModel;
        }

		 

		/// <summary>
        /// 批量插入实体
        /// </summary>
        /// <param name="insertDtoList">对应实体数据传输对象集合</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> InsertBatchAsync(List<Prompt_FragmentInsertDTO> insertDtoList)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();
            var entityList = insertDtoList.MapToList<Prompt_FragmentEntity>();
            httpResponseResultModel.IsSuccess = await prompt_FragmentService.InsertRangeAsync(entityList);
            httpResponseResultModel.BackResult = httpResponseResultModel.IsSuccess;
            return httpResponseResultModel;
        }
       
		  
        /// <summary>
        /// 根据主键更新实体
        /// </summary>
		/// <param name="id">主键</param>
        /// <param name="updateDto">对应实体数据传输对象</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> UpdateAsync(long id,Prompt_FragmentUpdateDTO updateDto)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();
            var entity = updateDto.MapTo<Prompt_FragmentEntity>();
            entity.Id = id;
            httpResponseResultModel.IsSuccess = await prompt_FragmentService.UpdateAsync(entity);
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
            httpResponseResultModel.IsSuccess = await prompt_FragmentService.SoftDeleteByIdAsync(id);
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
            httpResponseResultModel.IsSuccess = await prompt_FragmentService.SoftDeleteByIdsAsync(idList);
            httpResponseResultModel.BackResult = httpResponseResultModel.IsSuccess;
            return httpResponseResultModel;
        }

        /// <summary>
        /// 条件组装
        /// </summary>
        /// <param name="dto"></param>
        /// <returns></returns>
        private Expression<Func<Prompt_FragmentEntity, bool>> PackageExpression(Prompt_FragmentConditionDTO dto) {
            Expression<Func<Prompt_FragmentEntity, bool>> expression = Expressionable.Create<Prompt_FragmentEntity>()
                .And(t => t.IsDeleted == false)
            
                .ToExpression();
            return expression;
        }

       
        /// <summary>
        /// 导出Excel
        /// </summary>
        /// <param name="conditionDTO"></param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<string>> ExportToExcelAsync(Prompt_FragmentConditionDTO conditionDTO)
        {
            HttpResponseResultModel<string> httpResponseResultModel = new HttpResponseResultModel<string>();
            var exportCondition = new ExportExcel<Prompt_FragmentView>();
            var tags = await GetListAsync(conditionDTO);
            exportCondition.Data = tags.MapToList<Prompt_FragmentView>().ToList();
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
            var importResult = await MyExportImportExcelHelper.ImportExcelAsync<Prompt_FragmentView>(new FileStream(drictory, FileMode.Open));

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
            var insertList = new List<Prompt_FragmentEntity>();
            importDatas.ForEach(s =>
            {
                s.ID = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                insertList.Add(s.MapTo<Prompt_FragmentEntity>());
            });

            var insertResult = await prompt_FragmentService.InsertRangeAsync(insertList);

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
