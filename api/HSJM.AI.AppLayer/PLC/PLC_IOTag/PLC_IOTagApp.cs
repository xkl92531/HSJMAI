
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/2/7 15:05:32
using HSJM.AI.AppLayer;
using HSJM.AI.AppLayer.Agent.Services;
using HSJM.AI.AppLayer.PLC.DTOS;
using HSJM.AI.AppLayer.PLC.ViewObject;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.NPOIHelper;
using HSJM.AI.Utility;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Newtonsoft.Json;
using SqlSugar;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Text.Json;
using System.Threading.Tasks;
namespace HSJM.AI.AppLayer
{
    public class PLC_IOTagApp : ISelfScopedAutoInject
    {
        private readonly IPLC_IOTagService pLC_IOTagService;
        private readonly IPLC_VariableAccessService pLC_VariableAccessService;
        private readonly IPLC_LogicUnitService pLC_LogicUnitService;
        private readonly LLMModelService _llmModelService;

        public PLC_IOTagApp(
            IPLC_IOTagService pLC_IOTagService,
            IPLC_VariableAccessService pLC_VariableAccessService,
            IPLC_LogicUnitService pLC_LogicUnitService,
            LLMModelService _llmModelService)
        {
            this.pLC_IOTagService = pLC_IOTagService;
            this.pLC_VariableAccessService = pLC_VariableAccessService;
            this.pLC_LogicUnitService = pLC_LogicUnitService;
            this._llmModelService = _llmModelService;
        }

        /// <summary>
        /// 根据主键获取单个实体
        /// </summary>
        /// <param name="id">主键</param>
        /// <returns></returns>
        public async Task<PLC_IOTagEntity> GetAsync(long id)
        {
            return await pLC_IOTagService.GetByIdAsync(id).ConfigureAwait(false);
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
        public async Task<QueryPagedResponseModel<PLC_IOTagEntity>> GetListPagedAsync(int page, int size, PLC_IOTagConditionDTO conditionDTO = null, string orderByField = "Id", bool isDesc = true)
        {
            var expression = PackageExpression(conditionDTO);

            int total = 0;
            var result = pLC_IOTagService.GetListPageOrderBy(expression, page, size, orderByField.CreateOrderByExpression<PLC_IOTagEntity>(), isDesc ? OrderByType.Desc : OrderByType.Asc, ref total);
            return new QueryPagedResponseModel<PLC_IOTagEntity>()
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
        public async Task<List<PLC_IOTagEntity>> GetListAsync(PLC_IOTagConditionDTO conditionDTO, string orderByField = "Id", bool isDesc = true)
        {
            var expression = PackageExpression(conditionDTO);
            var result = await pLC_IOTagService.GetListAsync(expression, isDesc ? OrderByType.Desc : OrderByType.Asc, orderByField.CreateOrderByExpression<PLC_IOTagEntity>());
            return result;
        }

        /// <summary>
        /// 根据id集合获取多条数据
        /// </summary>
        /// <param name="ids"></param>
        /// <returns></returns>
        public async Task<List<PLC_IOTagEntity>> GetListByIdsAsync(List<long> ids)
        {
            if (ids.IsListNullOrEmpty())
            {
                return new List<PLC_IOTagEntity>();
            }
            return await pLC_IOTagService.GetListByIdsAsync(ids);
        }

        /// <summary>
        /// 获取最后一条
        /// </summary>
        /// <returns></returns>
        public async Task<PLC_IOTagEntity> GetFirstAsync(PLC_IOTagConditionDTO conditionDTO)
        {
            var expression = PackageExpression(conditionDTO);
            return await pLC_IOTagService.GetFirstAsync(expression, x => x.CreateTime, OrderByType.Desc);
        }

        /// <summary>
        /// 插入单个实体
        /// </summary>
        /// <param name="insertDto">对应实体数据传输对象</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<long>> InsertAsync(PLC_IOTagInsertDTO insertDto)
        {
            HttpResponseResultModel<long> httpResponseResultModel = new HttpResponseResultModel<long>();
            var entity = insertDto.MapTo<PLC_IOTagEntity>();
            httpResponseResultModel.IsSuccess = await pLC_IOTagService.InsertAsync(entity).ConfigureAwait(false);
            httpResponseResultModel.BackResult = entity.Id;
            return httpResponseResultModel;
        }



        /// <summary>
        /// 批量插入实体
        /// </summary>
        /// <param name="insertDtoList">对应实体数据传输对象集合</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> InsertBatchAsync(List<PLC_IOTagInsertDTO> insertDtoList)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();
            var entityList = insertDtoList.MapToList<PLC_IOTagEntity>();
            httpResponseResultModel.IsSuccess = await pLC_IOTagService.InsertRangeAsync(entityList);
            httpResponseResultModel.BackResult = httpResponseResultModel.IsSuccess;
            return httpResponseResultModel;
        }


        /// <summary>
        /// 根据主键更新实体
        /// </summary>
        /// <param name="id">主键</param>
        /// <param name="updateDto">对应实体数据传输对象</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> UpdateAsync(long id, PLC_IOTagUpdateDTO updateDto)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();
            var entity = updateDto.MapTo<PLC_IOTagEntity>();
            entity.Id = id;
            httpResponseResultModel.IsSuccess = await pLC_IOTagService.UpdateAsync(entity);
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
            httpResponseResultModel.IsSuccess = await pLC_IOTagService.SoftDeleteByIdAsync(id);
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
            if (idList.IsListNullOrEmpty())
            {
                httpResponseResultModel.ErrorMessage = "id is null！";
                return httpResponseResultModel;
            }
            httpResponseResultModel.IsSuccess = await pLC_IOTagService.SoftDeleteByIdsAsync(idList);
            httpResponseResultModel.BackResult = httpResponseResultModel.IsSuccess;
            return httpResponseResultModel;
        }

        /// <summary>
        /// 条件组装
        /// </summary>
        /// <param name="dto"></param>
        /// <returns></returns>
        private Expression<Func<PLC_IOTagEntity, bool>> PackageExpression(PLC_IOTagConditionDTO dto)
        {
            Expression<Func<PLC_IOTagEntity, bool>> expression = Expressionable.Create<PLC_IOTagEntity>()
                .And(t => t.IsDeleted == false)
                .AndIF(dto != null && dto.ProjectId.HasValue, t => t.ProjectId == dto.ProjectId.Value)
                .AndIF(dto != null && !string.IsNullOrEmpty(dto.TagName), t => t.TagName.Contains(dto.TagName))
                .AndIF(dto != null && !string.IsNullOrEmpty(dto.Address), t => t.Address.Contains(dto.Address))
                .AndIF(dto != null && !string.IsNullOrEmpty(dto.DataType), t => t.DataType == dto.DataType)
                .AndIF(dto != null && !string.IsNullOrEmpty(dto.TableName), t => t.TableName == dto.TableName)
                .AndIF(dto != null && !string.IsNullOrEmpty(dto.Comment), t => t.Comment.Contains(dto.Comment))
                .ToExpression();
            return expression;
        }


        /// <summary>
        /// 导出Excel
        /// </summary>
        /// <param name="conditionDTO"></param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<string>> ExportToExcelAsync(PLC_IOTagConditionDTO conditionDTO)
        {
            HttpResponseResultModel<string> httpResponseResultModel = new HttpResponseResultModel<string>();
            var exportCondition = new ExportExcel<PLC_IOTagView>();
            var tags = await GetListAsync(conditionDTO);
            exportCondition.Data = tags.MapToList<PLC_IOTagView>().ToList();
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
            var importResult = await MyExportImportExcelHelper.ImportExcelAsync<PLC_IOTagView>(new FileStream(drictory, FileMode.Open));

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
            var insertList = new List<PLC_IOTagEntity>();
            importDatas.ForEach(s =>
            {
                s.Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                insertList.Add(s.MapTo<PLC_IOTagEntity>());
            });

            var insertResult = await pLC_IOTagService.InsertRangeAsync(insertList);

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
        /// 通过AI解析IOTag标签的作用
        /// </summary>
        /// <param name="ioTagId">IOTag主键ID</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> ParseIOTagByAIAsync(long ioTagId)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();


            #region 数据查询
            // 1. 根据IOTag ID获取IOTag实体
            var ioTag = await pLC_IOTagService.GetByIdAsync(ioTagId).ConfigureAwait(false);
            if (ioTag == null)
            {
                httpResponseResultModel.ErrorMessage = $"未找到ID为 {ioTagId} 的IOTag记录";
                return httpResponseResultModel;
            }

            // 2. 通过标签名称在 PLC_VariableAccess 表中检索数据
            Expression<Func<PLC_VariableAccessEntity, bool>> variableAccessExpression = Expressionable.Create<PLC_VariableAccessEntity>()
                    .And(t => t.IsDeleted == false)
                    .And(t => t.VariableName == ioTag.TagName)
                    .ToExpression();

            var variableAccess = await pLC_VariableAccessService.GetFirstAsync(variableAccessExpression, x => x.CreateTime, OrderByType.Desc).ConfigureAwait(false);

            if (variableAccess == null)
            {
                httpResponseResultModel.ErrorMessage = $"未找到标签名称为 '{ioTag.TagName}' 的变量访问记录";
                return httpResponseResultModel;
            }

            // 3. 通过 LogicUnitId 查询 PLC_LogicUnit 表获取对应的数据
            var logicUnit = await pLC_LogicUnitService.GetByIdAsync(variableAccess.LogicUnitId.Value).ConfigureAwait(false);

            if (logicUnit == null)
            {
                httpResponseResultModel.ErrorMessage = $"未找到ID为 {variableAccess.LogicUnitId.Value} 的逻辑单元";
                return httpResponseResultModel;
            }

            #endregion

            string systemPrompt = "请你作为PLC程序解析专家，根据以下信息解读文件中的XML标签程序，并按指定格式输出分析结果。回答问题请简洁明了，不要那么多废话";

            string urlContent = "### 基础信息\r\n" +
                                    $"1. 待分析变量：{ioTag.TagName}\r\n" +
                                    $"2. 变量注释：{ioTag.Comment}\r\n" +
                                    $"3. 关联XML标签：{logicUnit.RawContent}" +
                                "### 解析要求\r\n" +
                                "请针对上述变量完成以下3项解析：\r\n" +
                                    "1. 明确说明该变量在程序中起到的具体作用；\r\n" +
                                    "2. 指出程序执行到上述关联的逻辑结构时，该变量在正常工况下的具体数据值，并解释该数值的含义；\r\n" +
                                    "3. 判定该变量所属的控制大类（仅限选项：气缸控制、光电控制、电机控制、伺服控制）。\r\n\r\n" +
                                "### 输出要求\r\n" +
                                    "1. 严格按照以下JSON格式输出结果，不得增加额外内容、修改字段名或格式；\r\n" +
                                    "2. 字段值需基于PLC程序逻辑和工业控制常识填写，确保准确且符合实际工况；\r\n" +
                                    "3. 所有字段值均为字符串类型（数值需用引号包裹），无默认值时不得填写null，需填写具体内容。\r\n\r\n" +
                                "### 输出JSON格式\r\n" +
                                    "{\r\n  " +
                                        "\\\"Normal_Value\\\": \\\"\\\", // 变量正常状态下的数据值\r\n  " +
                                        "\\\"Annotation\\\": \\\"\\\",   // 该数值的注解\r\n  " +
                                        "\\\"TagType\\\": \\\"\\\",         // 变量所属控制大类\r\n  " +
                                        "\\\"TagEffect\\\": \\\"\\\"        // 变量起到的作用\r\n" +
                                    "}";

            var llmModeInfo = await _llmModelService.RecommendModelAsync();

            var chatClient = await _llmModelService.CreateChatClientByModelIdAsync(llmModeInfo?.Id ?? 0);
            var client = chatClient.AsIChatClient(); // 转换为 MEAI Client

            var messages = new List<ChatMessage>
                {
                    new ChatMessage(ChatRole.System, systemPrompt),
                    new ChatMessage(ChatRole.User, urlContent)
                };

            var response = await client.GetResponseAsync(messages);

            var responseContent = response.Text ?? string.Empty;

            responseContent = responseContent.Replace("```json", "").Replace("```", "");

            var isJson = IsValidJson(responseContent);
            if (!isJson)
            {
                httpResponseResultModel.ErrorMessage = $"AI解析返回非JSON格式文件，无法更新至数据库";
                return httpResponseResultModel;
            }
            var analysisInfo = JsonConvert.DeserializeObject<PLC_IOTagEntity>(responseContent);


            ioTag.Normal_Value = analysisInfo.Normal_Value;
            ioTag.Annotation = analysisInfo.Annotation;
            ioTag.TagType = analysisInfo.TagType;
            ioTag.TagEffect = analysisInfo.TagEffect;

            httpResponseResultModel.BackResult = await pLC_IOTagService.UpdateAsync(ioTag);

            // 6. 返回逻辑单元数据,后续逻辑在此处继续编写
            httpResponseResultModel.IsSuccess = httpResponseResultModel.BackResult;

            // TODO: 在此处添加后续的AI分析逻辑

            return httpResponseResultModel;
        }


        /// <summary>
        /// 校验字符串是否为合法的JSON格式（基于System.Text.Json）
        /// </summary>
        /// <param name="input">待校验的字符串</param>
        /// <returns>合法返回true，不合法返回false</returns>
        private bool IsValidJson(string input)
        {
            // 空值/空白字符串直接判定为不合法JSON
            if (string.IsNullOrWhiteSpace(input))
                return false;

            try
            {
                // 尝试反序列化为JsonElement（通用JSON对象，无需指定实体类）
                JsonDocument.Parse(input);
                return true;
            }
            catch (Newtonsoft.Json.JsonException)
            {
                // 反序列化失败，说明JSON格式错误
                return false;
            }
            catch (ArgumentException)
            {
                // 入参为无效格式（如非字符串），判定为不合法
                return false;
            }
            catch (Exception)
            {
                // 入参为无效格式（如非字符串），判定为不合法
                return false;
            }
        }
    }
}
