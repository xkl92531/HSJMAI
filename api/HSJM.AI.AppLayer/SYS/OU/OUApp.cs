
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/03/05 13:43:57
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
using HSJM.AI.AppLayer.SYS.ViewObject;
using HSJM.AI.AppLayer.SYS.DTOS;
using HSJM.AI.NPOIHelper;
using System.IO;
using Microsoft.AspNetCore.Http;
using HSJM.AI.AppLayer.SYS.OU.ViewObject;
namespace HSJM.AI.AppLayer
{
    public class OUApp : ISelfScopedAutoInject
    {
        private readonly IOUService oUService;


        public OUApp(IOUService oUService)
        {
            this.oUService = oUService;
        }

        /// <summary>
        /// 根据主键获取单个实体
        /// </summary>
        /// <param name="id">主键</param>
        /// <returns></returns>
        public async Task<OUEntity> GetAsync(long id)
        {
            return await oUService.GetByIdAsync(id).ConfigureAwait(false);
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
        public async Task<QueryPagedResponseModel<OUEntity>> GetListPagedAsync(int page, int size, OUConditionDTO conditionDTO = null, string orderByField = "Id", bool isDesc = true)
        {
            var expression = PackageExpression(conditionDTO);

            int total = 0;
            var result = oUService.GetListPageOrderBy(expression, page, size, orderByField.CreateOrderByExpression<OUEntity>(), isDesc ? OrderByType.Desc : OrderByType.Asc, ref total);
            return new QueryPagedResponseModel<OUEntity>()
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
        public async Task<List<OUEntity>> GetListAsync(OUConditionDTO conditionDTO, string orderByField = "Id", bool isDesc = true)
        {
            var expression = PackageExpression(conditionDTO);
            var result = oUService.GetList(expression, orderByField.CreateOrderByExpression<OUEntity>(), isDesc ? OrderByType.Desc : OrderByType.Asc);
            return result;
        }

        /// <summary>
        /// 根据id集合获取多条数据
        /// </summary>
        /// <param name="ids"></param>
        /// <returns></returns>
        public async Task<List<OUEntity>> GetListByIdsAsync(List<long> ids)
        {
            if (ids.IsListNullOrEmpty())
            {
                return new List<OUEntity>();
            }
            return await oUService.GetListByIdsAsync(ids);
        }

        /// <summary>
        /// 获取最后一条
        /// </summary>
        /// <returns></returns>
        public async Task<OUEntity> GetFirstAsync(OUConditionDTO conditionDTO)
        {
            var expression = PackageExpression(conditionDTO);
            return oUService.GetFirst(expression, x => x.CreateTime, OrderByType.Desc);
        }

        /// <summary>
        /// 插入单个实体
        /// </summary>
        /// <param name="insertDto">对应实体数据传输对象</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<long>> InsertAsync(OUInsertDTO insertDto)
        {
            HttpResponseResultModel<long> httpResponseResultModel = new HttpResponseResultModel<long>();
            var entity = insertDto.MapTo<OUEntity>();
            if (entity.ParentID == 0 || entity.ParentID == null)
            {
                entity.ParentID = 0;
                entity.Type = DataDictionary.Code.OUType.公司;
            }
            if (entity.ParentID != null && entity.ParentID > 0)
            {
                entity.Type = DataDictionary.Code.OUType.部门;
            }
            entity.CreaterId = CurrentUserInfo.Instance.UserID;
            entity.CreaterName = CurrentUserInfo.Instance.UserName;
            entity.CreateTime = DateTime.Now;
            entity.ModifierId = CurrentUserInfo.Instance.UserID;
            entity.ModifierName = CurrentUserInfo.Instance.UserName;
            entity.ModifyTime = DateTime.Now;
            httpResponseResultModel.IsSuccess = await oUService.InsertAsync(entity).ConfigureAwait(false);
            httpResponseResultModel.BackResult = entity.Id;
            return httpResponseResultModel;
        }



        /// <summary>
        /// 批量插入实体
        /// </summary>
        /// <param name="insertDtoList">对应实体数据传输对象集合</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> InsertBatchAsync(List<OUInsertDTO> insertDtoList)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();
            var entityList = insertDtoList.MapToList<OUEntity>();
            httpResponseResultModel.IsSuccess = await oUService.InsertRangeAsync(entityList);
            httpResponseResultModel.BackResult = httpResponseResultModel.IsSuccess;
            return httpResponseResultModel;
        }


        /// <summary>
        /// 根据主键更新实体
        /// </summary>
        /// <param name="id">主键</param>
        /// <param name="updateDto">对应实体数据传输对象</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> UpdateAsync(long id, OUUpdateDTO updateDto)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();
            var entity = updateDto.MapTo<OUEntity>();
            entity.Id = id;
            entity.ModifierId = CurrentUserInfo.Instance.UserID;
            entity.ModifierName = CurrentUserInfo.Instance.UserName;
            entity.ModifyTime = DateTime.Now;
            httpResponseResultModel.IsSuccess = await oUService.UpdateAsync(entity);
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
            httpResponseResultModel.IsSuccess = await oUService.SoftDeleteByIdAsync(id);
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
            httpResponseResultModel.IsSuccess = await oUService.SoftDeleteByIdsAsync(idList);
            httpResponseResultModel.BackResult = httpResponseResultModel.IsSuccess;
            return httpResponseResultModel;
        }

        /// <summary>
        /// 条件组装
        /// </summary>
        /// <param name="dto"></param>
        /// <returns></returns>
        private Expression<Func<OUEntity, bool>> PackageExpression(OUConditionDTO dto)
        {
            Expression<Func<OUEntity, bool>> expression = t => t.IsDeleted == false;

            //if (dto.Id.HasValue && dto.Id.Value > 0)
            //{
            //    expression = expression.And(x => x.Id == dto.Id.Value);
            //}
            if (dto.FirstID.HasValue)
            {
                if (dto.FirstID > 0)
                {
                    expression = expression.And(x => x.CompanyID == dto.FirstID.Value);
                }
            }
            if (dto.ParentID.HasValue)
            {
                if (dto.ParentID > 0)
                {
                    expression = expression.And(x => x.ParentID == dto.ParentID.Value);
                }
            }
            if (!string.IsNullOrEmpty(dto.Name))
            {
                expression = expression.And(x => x.Name.Contains(dto.Name));
            }
            if (dto.Status.HasValue)
            {
                if (dto.Status > 0)
                {
                    expression = expression.And(x => x.Status == dto.Status.Value);
                }
            }

            return expression;
        }

        /// <summary>
        /// 导出Excel
        /// </summary>
        /// <param name="conditionDTO"></param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<string>> ExportToExcelAsync(OUConditionDTO conditionDTO)
        {
            HttpResponseResultModel<string> httpResponseResultModel = new HttpResponseResultModel<string>();
            var exportCondition = new ExportExcel<OUView>();
            var tags = await GetListAsync(conditionDTO);
            exportCondition.Data = tags.MapToList<OUView>().ToList();
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
            var importResult = await MyExportImportExcelHelper.ImportExcelAsync<OUView>(new FileStream(drictory, FileMode.Open));

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
            var insertList = new List<OUEntity>();
            importDatas.ForEach(s =>
            {
                s.ID = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                insertList.Add(s.MapTo<OUEntity>());
            });

            var insertResult = await oUService.InsertRangeAsync(insertList);

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
        /// 获取机构树列表
        /// </summary>
        /// <param name="conditionDTO"></param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<List<OUTreeView>>> GetTreeList(OUConditionDTO conditionDTO)
        {
            var expression = PackageExpression(conditionDTO);
            var result = await oUService.GetListAsync(expression, OrderByType.Asc, it => it.Sort, it => new OUTreeView());

            var ls = new TreeBuildUtil<OUTreeView>().DoTreeBuild(result);
            return new HttpResponseResultModel<List<OUTreeView>>() { BackResult = ls, IsSuccess = true };
        }
    }
}
