
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/2/7 15:05:38
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
using HSJM.AI.AppLayer.PLC.ViewObject;
using HSJM.AI.AppLayer.PLC.DTOS;
using HSJM.AI.NPOIHelper;
using System.IO;
using Microsoft.AspNetCore.Http;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Services;
namespace HSJM.AI.AppLayer
{
    public class PLC_LogicUnitApp:ISelfScopedAutoInject
    {
        private readonly IPLC_LogicUnitService pLC_LogicUnitService;
        private readonly IPLC_BlockService _pLC_BlockService;


         public PLC_LogicUnitApp(IPLC_LogicUnitService pLC_LogicUnitService, IPLC_BlockService pLC_BlockService)
        {
            this.pLC_LogicUnitService = pLC_LogicUnitService;
            _pLC_BlockService = pLC_BlockService;
        }

        /// <summary>
        /// 根据主键获取单个实体
        /// </summary>
        /// <param name="id">主键</param>
        /// <returns></returns>
        public async Task<PLC_LogicUnitEntity> GetAsync(long id)
        {
		   return   await pLC_LogicUnitService.GetByIdAsync(id).ConfigureAwait(false);
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
        public async Task<QueryPagedResponseModel<PLC_LogicUnitEntity>> GetListPagedAsync(int page, int size, PLC_LogicUnitConditionDTO conditionDTO=null, string orderByField = "Id", bool isDesc=true)
        {
            var expression = PackageExpression(conditionDTO);
              
            int total = 0;
            var result =  pLC_LogicUnitService.GetListPageOrderBy(expression,page,size, orderByField.CreateOrderByExpression<PLC_LogicUnitEntity>(), isDesc?OrderByType.Desc: OrderByType.Asc, ref total);
            return new QueryPagedResponseModel<PLC_LogicUnitEntity>()
            {
                Total = total,
                Data = result
            };
        }

        public async Task<QueryPagedResponseModel<PLC_WriterFragmentDTO>> GetWriterFragmentsPagedAsync(
            int page,
            int size,
            PLC_WriterFragmentQueryDTO queryDto = null,
            string orderByField = "WriteVarsCount",
            bool isDesc = true)
        {
            var fragments = await GetWriterFragmentsInternalAsync(queryDto, orderByField, isDesc);
            var total = fragments.Count;
            var data = fragments
                .Skip(Math.Max(page - 1, 0) * size)
                .Take(size)
                .ToList();

            return new QueryPagedResponseModel<PLC_WriterFragmentDTO>
            {
                Total = total,
                Data = data
            };
        }

        public async Task<List<PLC_WriterFragmentDTO>> GetWriterFragmentsAsync(
            PLC_WriterFragmentQueryDTO queryDto,
            string orderByField = "WriteVarsCount",
            bool isDesc = true)
        {
            return await GetWriterFragmentsInternalAsync(queryDto, orderByField, isDesc);
        }

        /// <summary>
        /// 查询数据(不分页) 返回指定实体T
        /// </summary>
        /// <param name="conditionDTO">查询条件类</param>
        /// <param name="orderByField">排序字段</param>
        /// <param name="isDesc">Desc</param>
        /// <returns></returns>
        public async Task<List<PLC_LogicUnitEntity>> GetListAsync(PLC_LogicUnitConditionDTO  conditionDTO, string orderByField = "Id", bool isDesc = true)
        {
            var expression = PackageExpression(conditionDTO);
            var result = await pLC_LogicUnitService.GetListAsync(expression, isDesc ? OrderByType.Desc : OrderByType.Asc, orderByField.CreateOrderByExpression<PLC_LogicUnitEntity>());
            return result;
        }

        /// <summary>
        /// 根据id集合获取多条数据
        /// </summary>
        /// <param name="ids"></param>
        /// <returns></returns>
        public async Task<List<PLC_LogicUnitEntity>> GetListByIdsAsync(List<long> ids)
        {
            if (ids.IsListNullOrEmpty()) {
                return new List<PLC_LogicUnitEntity>();
            }
            return await pLC_LogicUnitService.GetListByIdsAsync(ids);
        }

        /// <summary>
        /// 获取最后一条
        /// </summary>
        /// <returns></returns>
        public async Task<PLC_LogicUnitEntity> GetFirstAsync(PLC_LogicUnitConditionDTO conditionDTO)
        {
            var expression = PackageExpression(conditionDTO);
            return await pLC_LogicUnitService.GetFirstAsync(expression, x => x.CreateTime, OrderByType.Desc);
        }

	    /// <summary>
        /// 插入单个实体
        /// </summary>
        /// <param name="insertDto">对应实体数据传输对象</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<long>> InsertAsync(PLC_LogicUnitInsertDTO insertDto)
        { 
            HttpResponseResultModel<long> httpResponseResultModel = new HttpResponseResultModel<long>();
            var entity = insertDto.MapTo<PLC_LogicUnitEntity>();
            httpResponseResultModel.IsSuccess= await pLC_LogicUnitService.InsertAsync(entity).ConfigureAwait(false);
            httpResponseResultModel.BackResult = entity.Id;
            return httpResponseResultModel;
        }

		 

		/// <summary>
        /// 批量插入实体
        /// </summary>
        /// <param name="insertDtoList">对应实体数据传输对象集合</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> InsertBatchAsync(List<PLC_LogicUnitInsertDTO> insertDtoList)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();
            var entityList = insertDtoList.MapToList<PLC_LogicUnitEntity>();
            httpResponseResultModel.IsSuccess = await pLC_LogicUnitService.InsertRangeAsync(entityList);
            httpResponseResultModel.BackResult = httpResponseResultModel.IsSuccess;
            return httpResponseResultModel;
        }
       
		  
        /// <summary>
        /// 根据主键更新实体
        /// </summary>
		/// <param name="id">主键</param>
        /// <param name="updateDto">对应实体数据传输对象</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> UpdateAsync(long id,PLC_LogicUnitUpdateDTO updateDto)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();
            var entity = updateDto.MapTo<PLC_LogicUnitEntity>();
            entity.Id = id;
            httpResponseResultModel.IsSuccess = await pLC_LogicUnitService.UpdateAsync(entity);
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
            httpResponseResultModel.IsSuccess = await pLC_LogicUnitService.SoftDeleteByIdAsync(id);
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
            httpResponseResultModel.IsSuccess = await pLC_LogicUnitService.SoftDeleteByIdsAsync(idList);
            httpResponseResultModel.BackResult = httpResponseResultModel.IsSuccess;
            return httpResponseResultModel;
        }

        /// <summary>
        /// 条件组装
        /// </summary>
        /// <param name="dto"></param>
        /// <returns></returns>
        private Expression<Func<PLC_LogicUnitEntity, bool>> PackageExpression(PLC_LogicUnitConditionDTO dto) {
            Expression<Func<PLC_LogicUnitEntity, bool>> expression = Expressionable.Create<PLC_LogicUnitEntity>()
                .And(t => t.IsDeleted == false)
                .AndIF(dto != null && dto.BlockId.HasValue, t => t.BlockId == dto.BlockId.Value)
                .AndIF(dto != null && dto.NetworkId.HasValue, t => t.NetworkId == dto.NetworkId.Value)
                .AndIF(dto != null && !string.IsNullOrEmpty(dto.Title), t => t.Title.Contains(dto.Title))
                .ToExpression();
            return expression;
        }

        private async Task<List<PLC_WriterFragmentDTO>> GetWriterFragmentsInternalAsync(
            PLC_WriterFragmentQueryDTO queryDto,
            string orderByField,
            bool isDesc)
        {
            queryDto ??= new PLC_WriterFragmentQueryDTO();

            if (!queryDto.ProjectId.HasValue && !queryDto.BlockId.HasValue)
            {
                return new List<PLC_WriterFragmentDTO>();
            }

            if (queryDto.BlockId.HasValue)
            {
                var block = await _pLC_BlockService.GetByIdAsync(queryDto.BlockId.Value);
                if (block == null || block.IsDeleted)
                {
                    return new List<PLC_WriterFragmentDTO>();
                }

                if (queryDto.ProjectId.HasValue && block.ProjectId != queryDto.ProjectId.Value)
                {
                    return new List<PLC_WriterFragmentDTO>();
                }
            }

            var minWriteVarsCount = Math.Max(queryDto.MinWriteVarsCount, 1);
            var normalizedUnitType = queryDto.UnitType?.Trim();

            var fragments = await _pLC_BlockService.GetDb().AsQueryable()
                .InnerJoin<PLC_LogicUnitEntity>((block, logicUnit) => block.Id == logicUnit.BlockId)
                .Where((block, logicUnit) => block.IsDeleted == false && logicUnit.IsDeleted == false)
                .WhereIF(queryDto.ProjectId.HasValue, (block, logicUnit) => block.ProjectId == queryDto.ProjectId.Value)
                .WhereIF(queryDto.BlockId.HasValue, (block, logicUnit) => block.Id == queryDto.BlockId.Value)
                .WhereIF(!string.IsNullOrWhiteSpace(queryDto.BlockName), (block, logicUnit) => block.BlockName.Contains(queryDto.BlockName.Trim()))
                .WhereIF(!string.IsNullOrWhiteSpace(queryDto.StationCode), (block, logicUnit) => block.StationCode == queryDto.StationCode)
                .WhereIF(queryDto.NetworkId.HasValue, (block, logicUnit) => logicUnit.NetworkId == queryDto.NetworkId.Value)
                .WhereIF(!string.IsNullOrWhiteSpace(queryDto.Title), (block, logicUnit) => logicUnit.Title.Contains(queryDto.Title.Trim()))
                .WhereIF(!string.IsNullOrWhiteSpace(normalizedUnitType), (block, logicUnit) => logicUnit.UnitType == normalizedUnitType)
                .Where((block, logicUnit) => SqlFunc.IsNull(logicUnit.WriteVarsCount, 0) >= minWriteVarsCount)
                .Select((block, logicUnit) => new PLC_WriterFragmentDTO
                {
                    LogicUnitId = logicUnit.Id,
                    ProjectId = block.ProjectId,
                    BlockId = logicUnit.BlockId,
                    BlockName = block.BlockName,
                    BlockType = block.BlockType,
                    StationCode = block.StationCode,
                    UnitType = logicUnit.UnitType,
                    NetworkId = logicUnit.NetworkId,
                    Title = logicUnit.Title,
                    Comment = logicUnit.Comment,
                    ProgrammingLanguage = logicUnit.ProgrammingLanguage,
                    ReadVarsCount = logicUnit.ReadVarsCount,
                    WriteVarsCount = logicUnit.WriteVarsCount,
                    CallsCount = logicUnit.CallsCount,
                    UidSymbolMapJson = logicUnit.UidSymbolMapJson,
                    RawContent = logicUnit.RawContent,
                })
                .ToListAsync();

            fragments = fragments
                .Where(it => !queryDto.EligibleOnly || LogicUnitAnalysisPolicy.IsEligible(new PLC_LogicUnitEntity
                {
                    Id = it.LogicUnitId,
                    BlockId = it.BlockId,
                    UnitType = it.UnitType,
                    NetworkId = it.NetworkId,
                    Title = it.Title,
                    Comment = it.Comment,
                    ProgrammingLanguage = it.ProgrammingLanguage,
                    RawContent = it.RawContent,
                    UidSymbolMapJson = it.UidSymbolMapJson,
                    ReadVarsCount = it.ReadVarsCount,
                    WriteVarsCount = it.WriteVarsCount,
                    CallsCount = it.CallsCount,
                }))
                .Select(it =>
                {
                    it.RawContent = TrimRawContent(it.RawContent, queryDto.RawContentMaxLength);
                    return it;
                })
                .ToList();

            return OrderWriterFragments(fragments, orderByField, isDesc);
        }

        private static string TrimRawContent(string rawContent, int maxLength)
        {
            if (string.IsNullOrEmpty(rawContent))
            {
                return rawContent;
            }

            if (maxLength <= 0 || rawContent.Length <= maxLength)
            {
                return rawContent;
            }

            return rawContent.Substring(0, maxLength) + "...";
        }

        private static List<PLC_WriterFragmentDTO> OrderWriterFragments(
            List<PLC_WriterFragmentDTO> fragments,
            string orderByField,
            bool isDesc)
        {
            Func<PLC_WriterFragmentDTO, object> keySelector = (orderByField ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "blockname" => it => it.BlockName ?? string.Empty,
                "networkid" => it => it.NetworkId ?? int.MinValue,
                "title" => it => it.Title ?? string.Empty,
                "unittype" => it => it.UnitType ?? string.Empty,
                "readvarscount" => it => it.ReadVarsCount ?? 0,
                "callscount" => it => it.CallsCount ?? 0,
                _ => it => it.WriteVarsCount ?? 0,
            };

            return isDesc
                ? fragments.OrderByDescending(keySelector).ThenByDescending(it => it.LogicUnitId).ToList()
                : fragments.OrderBy(keySelector).ThenBy(it => it.LogicUnitId).ToList();
        }

       
        /// <summary>
        /// 导出Excel
        /// </summary>
        /// <param name="conditionDTO"></param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<string>> ExportToExcelAsync(PLC_LogicUnitConditionDTO conditionDTO)
        {
            HttpResponseResultModel<string> httpResponseResultModel = new HttpResponseResultModel<string>();
            var exportCondition = new ExportExcel<PLC_LogicUnitView>();
            var tags = await GetListAsync(conditionDTO);
            exportCondition.Data = tags.MapToList<PLC_LogicUnitView>().ToList();
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
            var importResult = await MyExportImportExcelHelper.ImportExcelAsync<PLC_LogicUnitView>(new FileStream(drictory, FileMode.Open));

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
            var insertList = new List<PLC_LogicUnitEntity>();
            importDatas.ForEach(s =>
            {
                s.Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                insertList.Add(s.MapTo<PLC_LogicUnitEntity>());
            });

            var insertResult = await pLC_LogicUnitService.InsertRangeAsync(insertList);

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
