
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/3/7 11:29:06
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using System.Linq.Expressions;
using System;
using MAF.DEMO.IDbService;
using SqlSugar;
using MAF.DEMO.Utility;
using MAF.DEMO.Core;
using MAF.DEMO.AppLayer;
using MAF.DEMO.AppLayer.Basic.ViewObject;
using MAF.DEMO.AppLayer.Basic.DTOS;
using MAF.DEMO.NPOIHelper;
using System.IO;
using Microsoft.AspNetCore.Http;
namespace MAF.DEMO.AppLayer
{
    public class CustomFormConfigApp:ISelfScopedAutoInject
    {
        private readonly ICustomFormConfigService customFormConfigService;


         public CustomFormConfigApp(ICustomFormConfigService customFormConfigService)
        {
            this.customFormConfigService = customFormConfigService;
        }

        /// <summary>
        /// 根据主键获取单个实体
        /// </summary>
        /// <param name="id">主键</param>
        /// <returns></returns>
        public async Task<CustomFormConfigEntity> GetAsync(long id)
        {
		   return   await customFormConfigService.GetByIdAsync(id).ConfigureAwait(false);
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
        public async Task<QueryPagedResponseModel<CustomFormConfigEntity>> GetListPagedAsync(int page, int size, CustomFormConfigConditionDTO conditionDTO=null, string orderByField = "Id", bool isDesc=true)
        {
            var expression = PackageExpression(conditionDTO);
              
            int total = 0;
            var result =  customFormConfigService.GetListPageOrderBy(expression,page,size, orderByField.CreateOrderByExpression<CustomFormConfigEntity>(), isDesc?OrderByType.Desc: OrderByType.Asc, ref total);
            return new QueryPagedResponseModel<CustomFormConfigEntity>()
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
        public async Task<List<CustomFormConfigEntity>> GetListAsync(CustomFormConfigConditionDTO  conditionDTO, string orderByField = "Id", bool isDesc = true)
        {
            var expression = PackageExpression(conditionDTO);
            var result = customFormConfigService.GetList(expression, orderByField.CreateOrderByExpression<CustomFormConfigEntity>(), isDesc ? OrderByType.Desc : OrderByType.Asc);
            return result;
        }

        /// <summary>
        /// 根据id集合获取多条数据
        /// </summary>
        /// <param name="ids"></param>
        /// <returns></returns>
        public async Task<List<CustomFormConfigEntity>> GetListByIdsAsync(List<long> ids)
        {
            if (ids.IsListNullOrEmpty()) {
                return new List<CustomFormConfigEntity>();
            }

            return await customFormConfigService.GetListByIdsAsync(ids);
        }

        /// <summary>
        /// 获取最后一条
        /// </summary>
        /// <returns></returns>
        public async Task<CustomFormConfigEntity> GetFirstAsync(CustomFormConfigConditionDTO conditionDTO)
        {
            var expression = PackageExpression(conditionDTO);
            return customFormConfigService.GetFirst(expression, x => x.CreateTime, OrderByType.Desc);
        }

	    /// <summary>
        /// 插入单个实体
        /// </summary>
        /// <param name="insertDto">对应实体数据传输对象</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<long>> InsertAsync(CustomFormConfigInsertDTO insertDto)
        { 
            HttpResponseResultModel<long> httpResponseResultModel = new HttpResponseResultModel<long>();
            var entity = insertDto.MapTo<CustomFormConfigEntity>();
            httpResponseResultModel.IsSuccess= await customFormConfigService.InsertAsync(entity).ConfigureAwait(false);
            httpResponseResultModel.BackResult = entity.Id;
            return httpResponseResultModel;
        }

		 

		/// <summary>
        /// 批量插入实体
        /// </summary>
        /// <param name="insertDtoList">对应实体数据传输对象集合</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> InsertBatchAsync(List<CustomFormConfigInsertDTO> insertDtoList)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();
            var entityList = insertDtoList.MapToList<CustomFormConfigEntity>();
            httpResponseResultModel.IsSuccess = await customFormConfigService.InsertRangeAsync(entityList);
            httpResponseResultModel.BackResult = httpResponseResultModel.IsSuccess;
            return httpResponseResultModel;
        }
       
		  
        /// <summary>
        /// 根据主键更新实体
        /// </summary>
		/// <param name="id">主键</param>
        /// <param name="updateDto">对应实体数据传输对象</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> UpdateAsync(long id,CustomFormConfigUpdateDTO updateDto)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();
            var entity = updateDto.MapTo<CustomFormConfigEntity>();
            entity.Id = id;
            httpResponseResultModel.IsSuccess = await customFormConfigService.UpdateAsync(entity);
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
            httpResponseResultModel.IsSuccess = await customFormConfigService.SoftDeleteByIdAsync(id);
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
            httpResponseResultModel.IsSuccess = await customFormConfigService.SoftDeleteByIdsAsync(idList);
            httpResponseResultModel.BackResult = httpResponseResultModel.IsSuccess;
            return httpResponseResultModel;
        }

        /// <summary>
        /// 条件组装
        /// </summary>
        /// <param name="dto"></param>
        /// <returns></returns>
        private Expression<Func<CustomFormConfigEntity, bool>> PackageExpression(CustomFormConfigConditionDTO dto) {
            Expression<Func<CustomFormConfigEntity, bool>> expression = t => t.IsDeleted == false;

            //if (dto.Id.HasValue && dto.Id.Value > 0)
            //{
            //    expression = expression.And(x => x.Id == dto.Id.Value);
            //}
                if (!string.IsNullOrEmpty(dto.FormCode))
    {
    expression = expression.And(x => x.FormCode == dto.FormCode);
    }
    if (!string.IsNullOrEmpty(dto.Attr1))
    {
    expression = expression.And(x => x.Attr1 == dto.Attr1);
    }
    if (!string.IsNullOrEmpty(dto.Attr2))
    {
    expression = expression.And(x => x.Attr2 == dto.Attr2);
    }
    if(dto.UserId.HasValue)
    {
     if (dto.UserId > 0)
    {
    expression = expression.And(x => x.UserId == dto.UserId.Value);
    }
    }
    if (!string.IsNullOrEmpty(dto.Attr3))
    {
    expression = expression.And(x => x.Attr3 == dto.Attr3);
    }

            return expression;
        }

       
        /// <summary>
        /// 导出Excel
        /// </summary>
        /// <param name="conditionDTO"></param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<string>> ExportToExcelAsync(CustomFormConfigConditionDTO conditionDTO)
        {
            HttpResponseResultModel<string> httpResponseResultModel = new HttpResponseResultModel<string>();
            var exportCondition = new ExportExcel<CustomFormConfigView>();
            var tags = await GetListAsync(conditionDTO);
            exportCondition.Data = tags.MapToList<CustomFormConfigView>().ToList();
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
            var importResult = await MyExportImportExcelHelper.ImportExcelAsync<CustomFormConfigView>(new FileStream(drictory, FileMode.Open));

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
            var insertList = new List<CustomFormConfigEntity>();
            importDatas.ForEach(s =>
            {
                s.ID = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                insertList.Add(s.MapTo<CustomFormConfigEntity>());
            });

            var insertResult = await customFormConfigService.InsertRangeAsync(insertList);

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
