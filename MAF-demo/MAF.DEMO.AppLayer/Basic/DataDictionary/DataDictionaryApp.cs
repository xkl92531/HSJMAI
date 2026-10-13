
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/02/29 10:03:23
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
using MAF.DEMO.AppLayer.SYS.SysUser.DTOS;
namespace MAF.DEMO.AppLayer
{
    public class DataDictionaryApp : ISelfScopedAutoInject
    {
        private readonly IDataDictionaryService dataDictionaryService;


        public DataDictionaryApp(IDataDictionaryService dataDictionaryService)
        {
            this.dataDictionaryService = dataDictionaryService;
        }

        /// <summary>
        /// 根据主键获取单个实体
        /// </summary>
        /// <param name="id">主键</param>
        /// <returns></returns>
        public async Task<DataDictionaryEntity> GetAsync(long id)
        {
            return await dataDictionaryService.GetByIdAsync(id).ConfigureAwait(false);
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
        public async Task<QueryPagedResponseModel<DataDictionaryEntity>> GetListPagedAsync(int page, int size, DataDictionaryConditionDTO conditionDTO = null, string orderByField = "Id", bool isDesc = true)
        {
            var expression = PackageExpression(conditionDTO);

            int total = 0;
            var result = dataDictionaryService.GetListPageOrderBy(expression, page, size, orderByField.CreateOrderByExpression<DataDictionaryEntity>(), isDesc ? OrderByType.Desc : OrderByType.Asc, ref total);
            return new QueryPagedResponseModel<DataDictionaryEntity>()
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
        public async Task<List<DataDictionaryEntity>> GetListAsync(DataDictionaryConditionDTO conditionDTO, string orderByField = "Id", bool isDesc = true)
        {
            var expression = PackageExpression(conditionDTO);
            var result = dataDictionaryService.GetList(expression, orderByField.CreateOrderByExpression<DataDictionaryEntity>(), isDesc ? OrderByType.Desc : OrderByType.Asc);
            return result;
        }


        /// <summary>
        /// 根据id集合获取多条数据
        /// </summary>
        /// <param name="ids"></param>
        /// <returns></returns>
        public async Task<List<DataDictionaryEntity>> GetListByIdsAsync(List<long> ids)
        {
            if (ids.IsListNullOrEmpty())
            {
                return new List<DataDictionaryEntity>();
            }
            return await dataDictionaryService.GetListByIdsAsync(ids);
        }

        /// <summary>
        /// 获取最后一条
        /// </summary>
        /// <returns></returns>
        public async Task<DataDictionaryEntity> GetFirstAsync(DataDictionaryConditionDTO conditionDTO)
        {
            var expression = PackageExpression(conditionDTO);
            return dataDictionaryService.GetFirst(expression, x => x.CreateTime, OrderByType.Desc);
        }

        /// <summary>
        /// 插入单个实体
        /// </summary>
        /// <param name="insertDto">对应实体数据传输对象</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<long>> InsertAsync(DataDictionaryInsertDTO insertDto)
        {
            HttpResponseResultModel<long> httpResponseResultModel = new HttpResponseResultModel<long>() { IsSuccess = false };
            var entity = insertDto.MapTo<DataDictionaryEntity>();
            if (string.IsNullOrEmpty(insertDto.ParentCode))
            {
                entity.ParentCode = "0";
                entity.ParentName = "顶级";
                if (dataDictionaryService.IsAny(it => it.Code == insertDto.Code && it.IsDeleted == false))
                {
                    httpResponseResultModel.ErrorMessage = "数据代码已存在";
                    return httpResponseResultModel;
                }
            }
            else
            {
                var dataLs = await dataDictionaryService.GetListAsync(it => it.Code == insertDto.ParentCode && it.IsDeleted == false);
                if (dataLs.Count <= 0)
                {
                    httpResponseResultModel.ErrorMessage = "父级数据信息不存在";
                    return httpResponseResultModel;
                }
                if (dataDictionaryService.IsAny(it => it.ParentCode == insertDto.ParentCode && it.IsDeleted == false && it.Code == insertDto.Code))
                {
                    httpResponseResultModel.ErrorMessage = "数据代码已存在";
                    return httpResponseResultModel;
                }

                entity.ParentCode = dataLs.First().Code;
                entity.ParentName = dataLs.First().Name;
            }


            httpResponseResultModel.IsSuccess = await dataDictionaryService.InsertAsync(entity).ConfigureAwait(false);
            httpResponseResultModel.BackResult = entity.Id;
            return httpResponseResultModel;
        }



        /// <summary>
        /// 批量插入实体
        /// </summary>
        /// <param name="insertDtoList">对应实体数据传输对象集合</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> InsertBatchAsync(List<DataDictionaryInsertDTO> insertDtoList)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();
            var entityList = insertDtoList.MapToList<DataDictionaryEntity>();
            httpResponseResultModel.IsSuccess = await dataDictionaryService.InsertRangeAsync(entityList);
            httpResponseResultModel.BackResult = httpResponseResultModel.IsSuccess;
            return httpResponseResultModel;
        }


        /// <summary>
        /// 根据主键更新实体
        /// </summary>
        /// <param name="id">主键</param>
        /// <param name="updateDto">对应实体数据传输对象</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> UpdateAsync(long id, DataDictionaryUpdateDTO updateDto)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();
            var entity = updateDto.MapTo<DataDictionaryEntity>();
            entity.Id = id;

            var oldEntity = await dataDictionaryService.GetByIdAsync(id);
            //如果编辑是顶级节点，则修改子节点的父级信息
            if (oldEntity != null && oldEntity.ParentCode == "0" && oldEntity.Code != entity.Code.Trim())
            {
                await dataDictionaryService.UpdateAsync(it => new DataDictionaryEntity() { ParentCode = entity.Code, ParentName = entity.Name }, it => it.IsDeleted == false && it.ParentCode == oldEntity.Code);
            }

            httpResponseResultModel.IsSuccess = await dataDictionaryService.UpdateAsync(entity);
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
            httpResponseResultModel.IsSuccess = await dataDictionaryService.SoftDeleteByIdAsync(id);
            httpResponseResultModel.BackResult = httpResponseResultModel.IsSuccess;
            return httpResponseResultModel;
        }


        /// <summary>
        /// 批量删除 根据主键
        /// </summary>
        /// <param name="idList">主键集合</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> DeleteBatchAsync(IList<long> idList)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();
            if (idList.IsListNullOrEmpty())
            {
                httpResponseResultModel.ErrorMessage = "id is null！";
                return httpResponseResultModel;
            }
            httpResponseResultModel.IsSuccess = await dataDictionaryService.SoftDeleteByIdsAsync(idList);
            httpResponseResultModel.BackResult = httpResponseResultModel.IsSuccess;
            return httpResponseResultModel;
        }

        /// <summary>
        /// 条件组装
        /// </summary>
        /// <param name="dto"></param>
        /// <returns></returns>
        private Expression<Func<DataDictionaryEntity, bool>> PackageExpression(DataDictionaryConditionDTO dto)
        {
            Expression<Func<DataDictionaryEntity, bool>> expression = t => t.IsDeleted == false;

            //if (dto.Id.HasValue && dto.Id.Value > 0)
            //{
            //    expression = expression.And(x => x.Id == dto.Id.Value);
            //}
            if (!string.IsNullOrEmpty(dto.ParentCode))
            {
                expression = expression.And(x => x.ParentCode == dto.ParentCode);
            }

            if (!string.IsNullOrEmpty(dto.Keyword))
            {
                expression = expression.And(x => x.Code.Contains(dto.Keyword) || x.Name.Contains(dto.Keyword) || x.ShorterForm.Contains(dto.Keyword));
            }
            if (!string.IsNullOrEmpty(dto.TreeKeyword))
            {
                expression = expression.And(x => x.Code.Contains(dto.TreeKeyword) || x.Name.Contains(dto.TreeKeyword) || x.ShorterForm.Contains(dto.TreeKeyword));
            }

            return expression;
        }

        /// <summary>
        /// 获取排序
        /// </summary>
        /// <param name="parentCode"></param>
        /// <returns></returns>
        public async Task<int> GetSort(string parentCode)
        {
            parentCode = string.IsNullOrEmpty(parentCode) ? "0" : parentCode;

            return await dataDictionaryService.MaxAsync(it => it.ParentCode == parentCode && it.IsDeleted == false, it => it.Sort.Value) + 1;
        }

        /// <summary>
        /// 导出Excel
        /// </summary>
        /// <param name="conditionDTO"></param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<string>> ExportToExcelAsync(DataDictionaryConditionDTO conditionDTO)
        {
            HttpResponseResultModel<string> httpResponseResultModel = new HttpResponseResultModel<string>();
            var exportCondition = new ExportExcel<DataDictionaryView>();
            var tags = await GetListAsync(conditionDTO);
            exportCondition.Data = tags.MapToList<DataDictionaryView>().ToList();
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
            var importResult = await MyExportImportExcelHelper.ImportExcelAsync<DataDictionaryView>(new FileStream(drictory, FileMode.Open));

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
            var insertList = new List<DataDictionaryEntity>();
            importDatas.ForEach(s =>
            {
                s.ID = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                insertList.Add(s.MapTo<DataDictionaryEntity>());
            });

            var insertResult = await dataDictionaryService.InsertRangeAsync(insertList);

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
        /// 获取数据字典
        /// </summary>
        /// <param name="parentName"></param>
        /// <returns></returns>
        public async Task<dynamic> GetDicionaryList(string parentName)
        {
            var ls = await dataDictionaryService.GetListAsync(it => it.ParentCode == parentName && it.IsDeleted == false && it.Status == DataDictionary.Code.Status.正常, OrderByType.Asc, it => it.Sort.Value);
            return ls.Select(it => new { Value = it.Code, Label = it.Name }).ToList();
        }

        /// <summary>
        /// 修改状态
        /// </summary>
        /// <param name="dto"></param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> UpdateStatus(DataDictionaryUpdateDTO dto)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();
            var userEntity = dataDictionaryService.GetById(dto.ID);
            if (userEntity == null)
            {
                httpResponseResultModel.ErrorMessage = "数据不存在！";
                return httpResponseResultModel;
            }
            
            userEntity.Status = dto.Status;
            httpResponseResultModel.IsSuccess = await dataDictionaryService.UpdateAsync(userEntity);
            return httpResponseResultModel;
        }
    }
}
