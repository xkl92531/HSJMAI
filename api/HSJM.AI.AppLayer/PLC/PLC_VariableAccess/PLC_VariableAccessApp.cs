
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/2/7 15:05:56
using Hangfire;
using HSJM.AI.AppLayer;
using HSJM.AI.AppLayer.Agent.Services.Plc_diagnosis;
using HSJM.AI.AppLayer.PLC.DTOS;
using HSJM.AI.AppLayer.PLC.PLC_Upload;
using HSJM.AI.AppLayer.PLC.ViewObject;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.NPOIHelper;
using HSJM.AI.Utility;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using NPOI.POIFS.Storage;
using OfficeOpenXml.FormulaParsing.Excel.Functions.Text;
using SharpCompress.Common;
using SqlSugar;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
namespace HSJM.AI.AppLayer
{
    public class PLC_VariableAccessApp : ISelfScopedAutoInject
    {
        private readonly IPLC_VariableAccessService pLC_VariableAccessService;
        private readonly IOConstraintPreprocessService _constraintPreprocessService;
        private readonly IPLC_LogicUnitService _logicUnitService;
        private readonly IPLC_BlockService _blockService;
        private readonly IPLC_IOTagService _tagService;
        private readonly IServiceProvider _serviceProvider;
        public PLC_VariableAccessApp(IPLC_VariableAccessService pLC_VariableAccessService, IOConstraintPreprocessService constraintPreprocessService, IPLC_LogicUnitService logicUnitService
            , IPLC_BlockService blockService, IPLC_IOTagService tagService, IServiceProvider serviceProvider)
        {
            this.pLC_VariableAccessService = pLC_VariableAccessService;
            _constraintPreprocessService = constraintPreprocessService;
            _logicUnitService = logicUnitService;
            _blockService = blockService;
            _tagService = tagService;
            _serviceProvider = serviceProvider;
        }

        /// <summary>
        /// 根据主键获取单个实体
        /// </summary>
        /// <param name="id">主键</param>
        /// <returns></returns>
        public async Task<PLC_VariableAccessEntity> GetAsync(long id)
        {
            return await pLC_VariableAccessService.GetByIdAsync(id).ConfigureAwait(false);
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
        public async Task<QueryPagedResponseModel<PLC_VariableAccessEntity>> GetListPagedAsync(int page, int size, PLC_VariableAccessConditionDTO conditionDTO = null, string orderByField = "Id", bool isDesc = true)
        {
            var expression = PackageExpression(conditionDTO);

            int total = 0;
            var result = pLC_VariableAccessService.GetListPageOrderBy(expression, page, size, orderByField.CreateOrderByExpression<PLC_VariableAccessEntity>(), isDesc ? OrderByType.Desc : OrderByType.Asc, ref total);
            return new QueryPagedResponseModel<PLC_VariableAccessEntity>()
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
        public async Task<List<PLC_VariableAccessEntity>> GetListAsync(PLC_VariableAccessConditionDTO conditionDTO, string orderByField = "Id", bool isDesc = true)
        {
            var expression = PackageExpression(conditionDTO);
            var result = await pLC_VariableAccessService.GetListAsync(expression, isDesc ? OrderByType.Desc : OrderByType.Asc, orderByField.CreateOrderByExpression<PLC_VariableAccessEntity>());
            return result;
        }

        /// <summary>
        /// 根据id集合获取多条数据
        /// </summary>
        /// <param name="ids"></param>
        /// <returns></returns>
        public async Task<List<PLC_VariableAccessEntity>> GetListByIdsAsync(List<long> ids)
        {
            if (ids.IsListNullOrEmpty())
            {
                return new List<PLC_VariableAccessEntity>();
            }
            return await pLC_VariableAccessService.GetListByIdsAsync(ids);
        }

        /// <summary>
        /// 获取最后一条
        /// </summary>
        /// <returns></returns>
        public async Task<PLC_VariableAccessEntity> GetFirstAsync(PLC_VariableAccessConditionDTO conditionDTO)
        {
            var expression = PackageExpression(conditionDTO);
            return await pLC_VariableAccessService.GetFirstAsync(expression, x => x.CreateTime, OrderByType.Desc);
        }

        /// <summary>
        /// 插入单个实体
        /// </summary>
        /// <param name="insertDto">对应实体数据传输对象</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<long>> InsertAsync(PLC_VariableAccessInsertDTO insertDto)
        {
            HttpResponseResultModel<long> httpResponseResultModel = new HttpResponseResultModel<long>();
            var entity = insertDto.MapTo<PLC_VariableAccessEntity>();
            httpResponseResultModel.IsSuccess = await pLC_VariableAccessService.InsertAsync(entity).ConfigureAwait(false);
            httpResponseResultModel.BackResult = entity.Id;
            return httpResponseResultModel;
        }



        /// <summary>
        /// 批量插入实体
        /// </summary>
        /// <param name="insertDtoList">对应实体数据传输对象集合</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> InsertBatchAsync(List<PLC_VariableAccessInsertDTO> insertDtoList)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();
            var entityList = insertDtoList.MapToList<PLC_VariableAccessEntity>();
            httpResponseResultModel.IsSuccess = await pLC_VariableAccessService.InsertRangeAsync(entityList);
            httpResponseResultModel.BackResult = httpResponseResultModel.IsSuccess;
            return httpResponseResultModel;
        }


        /// <summary>
        /// 根据主键更新实体
        /// </summary>
        /// <param name="id">主键</param>
        /// <param name="updateDto">对应实体数据传输对象</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> UpdateAsync(long id, PLC_VariableAccessUpdateDTO updateDto)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();
            var entity = updateDto.MapTo<PLC_VariableAccessEntity>();
            entity.Id = id;
            httpResponseResultModel.IsSuccess = await pLC_VariableAccessService.UpdateAsync(entity);
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
            httpResponseResultModel.IsSuccess = await pLC_VariableAccessService.SoftDeleteByIdAsync(id);
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
            httpResponseResultModel.IsSuccess = await pLC_VariableAccessService.SoftDeleteByIdsAsync(idList);
            httpResponseResultModel.BackResult = httpResponseResultModel.IsSuccess;
            return httpResponseResultModel;
        }

        /// <summary>
        /// 条件组装
        /// </summary>
        /// <param name="dto"></param>
        /// <returns></returns>
        private Expression<Func<PLC_VariableAccessEntity, bool>> PackageExpression(PLC_VariableAccessConditionDTO dto)
        {
            Expression<Func<PLC_VariableAccessEntity, bool>> expression = Expressionable.Create<PLC_VariableAccessEntity>()
                .And(t => t.IsDeleted == false)
                .AndIF(dto != null && !string.IsNullOrEmpty(dto.VariableName), t => t.VariableName == dto.VariableName)
                .ToExpression();
            return expression;
        }


        /// <summary>
        /// 导出Excel
        /// </summary>
        /// <param name="conditionDTO"></param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<string>> ExportToExcelAsync(PLC_VariableAccessConditionDTO conditionDTO)
        {
            HttpResponseResultModel<string> httpResponseResultModel = new HttpResponseResultModel<string>();
            var exportCondition = new ExportExcel<PLC_VariableAccessView>();
            var tags = await GetListAsync(conditionDTO);
            exportCondition.Data = tags.MapToList<PLC_VariableAccessView>().ToList();
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
            var importResult = await MyExportImportExcelHelper.ImportExcelAsync<PLC_VariableAccessView>(new FileStream(drictory, FileMode.Open));

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
            var insertList = new List<PLC_VariableAccessEntity>();
            importDatas.ForEach(s =>
            {
                s.Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                insertList.Add(s.MapTo<PLC_VariableAccessEntity>());
            });

            var insertResult = await pLC_VariableAccessService.InsertRangeAsync(insertList);

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
        /// 分析标签
        /// </summary>
        /// <param name="projectId"></param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<UploadTaskStartResult>> AnalyzeTagAttributes(long projectId)
        {
            // Generate task ID
            var taskId = $"plc_var_access_{projectId}_{DateTime.Now:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}";
            var progressService = _serviceProvider.GetRequiredService<PLC_UploadProgressService>();
            progressService.CreateProgress(taskId, projectId, "分析IO标签");

            // Enqueue background job
            var jobId = BackgroundJob.Enqueue<PLC_VariableAccessApp>(app =>
                app.ExceBackAnalyzeTagAttributes(taskId, projectId));
            return new HttpResponseResultModel<UploadTaskStartResult>()
            {
                IsSuccess = true,
                BackResult = new UploadTaskStartResult
                {
                    TaskId = taskId,
                    Message = "分析任务已启动，正在后台处理...",
                    HangfireJobId = jobId
                }
            };

        }


        /// <summary>
        /// 分析任务
        /// </summary>
        /// <param name="taskId"></param>
        /// <param name="projectId"></param>
        /// <returns></returns>
        [Hangfire.JobDisplayName("变量访问分析 · 项目{1}")]
        public async Task ExceBackAnalyzeTagAttributes(string taskId, long projectId)
        {
            // 并发配置 
            var progressService = _serviceProvider.GetRequiredService<PLC_UploadProgressService>();
            const int MAX_PARALLELISM = 4; // 最大并发数
            var semaphore = new SemaphoreSlim(MAX_PARALLELISM);

            var blockLs = await _blockService.GetListAsync(it => it.IsDeleted == false && it.ProjectId == projectId);

            var blockIds = blockLs.Select(it => it.Id).ToList();

            var logicUnitList = await _logicUnitService.GetListAsync(it => it.IsDeleted == false && blockIds.Contains(it.BlockId.Value)
                                                                    && (it.UnitType == "NETWORK" || it.UnitType == "TRANSITION")
                                                                    && !string.IsNullOrEmpty(it.RawContent) && it.RawContent != null);
            var variableList = await pLC_VariableAccessService.GetListAsync(it => it.IsDeleted == false && blockIds.Contains(it.BlockId.Value) && (it.IsAlarmVariable == false || it.IsAlarmVariable == null || it.IsIOTag == null || it.IsIOTag == false));
            var client = await _constraintPreprocessService.GetChatClientAsync();
            var tagList = await _tagService.GetListAsync(it => it.IsDeleted == false && it.ProjectId == projectId);

            progressService.SetTotalFiles(taskId, logicUnitList.Count, "正在解析IO标签");



            //foreach (var item in logicUnitList)
            //{
            //    progressService.IncrementProcessed(taskId, item.Title, "正在解析IO标签");
            //    var vLs = variableList.Where(it => it.LogicUnitId == item.Id).ToList();
            //    if (vLs != null && vLs.Count > 0)
            //    {
            //        var tagNameLs = vLs.Select(it => it.VariableName).ToList();
            //        var str = await _constraintPreprocessService.CallAIWithRetryAsync(client, GetPrompt(JsonConvert.SerializeObject(tagNameLs)
            //            , item.UidSymbolMapJson
            //            , item.RawContent), ""
            //            , item.Id.ToString() + "-" + item.Title, item.Id.ToString() + "Phase");

            //        try
            //        {
            //            var ls = JsonConvert.DeserializeObject<List<VariableAccessTag>>(FixJsonLooseToStrict(str));

            //            foreach (var t in vLs)
            //            {
            //                var e = ls.Find(it => it.tagName == t.VariableName && it.uid == t.Uid);
            //                if (e != null)
            //                {
            //                    if (t.IsAlarmVariable != e.isAlarm || t.IsStepVariable != e.isStep)
            //                    {
            //                        t.IsAlarmVariable = e.isAlarm;
            //                        t.IsStepVariable = e.isStep;
            //                        t.IsIOTag = tagList.Any(it => it.TagName == t.VariableName);
            //                        updateLs.Add(t);
            //                    }
            //                }
            //            }
            //        }
            //        catch (Exception ex)
            //        {

            //        }
            //    }
            //}
            ConcurrentBag<PLC_VariableAccessEntity> updateLs = new ConcurrentBag<PLC_VariableAccessEntity>();
            var taskList = logicUnitList.Select(async item =>
                {
                    try
                    {
                        await semaphore.WaitAsync();
                        progressService.IncrementProcessed(taskId, item.Title, "正在解析IO标签");
                        var vLs = variableList.Where(it => it.LogicUnitId == item.Id).ToList();
                        if (vLs != null && vLs.Count > 0)
                        {
                            var tagNameLs = vLs.Select(it => it.VariableName).ToList();
                            var str = await _constraintPreprocessService.CallAIWithRetryAsync(client, GetPrompt(JsonConvert.SerializeObject(tagNameLs)
                                , item.UidSymbolMapJson
                                , item.RawContent), ""
                                , item.Id.ToString() + "-" + item.Title, item.Id.ToString() + "Phase");

                            try
                            {
                                var ls = JsonConvert.DeserializeObject<List<VariableAccessTag>>(FixJsonLooseToStrict(str));

                                foreach (var t in vLs)
                                {
                                    var e = ls.Find(it => it.tagName == t.VariableName && it.uid == t.Uid);
                                    if (e != null)
                                    {
                                        if (t.IsAlarmVariable != e.isAlarm || t.IsStepVariable != e.isStep)
                                        {
                                            t.IsAlarmVariable = e.isAlarm;
                                            t.IsStepVariable = e.isStep;
                                            t.IsIOTag = tagList.Any(it => it.TagName == t.VariableName);
                                            updateLs.Add(t);
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                throw ex;
                            }
                        }
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }).ToList();

            await Task.WhenAll(taskList);

            if (updateLs.Count > 0)
            {
                progressService.SetMessage(taskId, "正在保存数据到数据库...");
                await pLC_VariableAccessService.BulkUpdateAsync(updateLs.ToList());
                progressService.MarkCompleted(taskId, projectId.ToString());
            }
        }

        /// <summary>
        /// 获取
        /// </summary>
        /// <param name="tagName"></param>
        /// <param name="xml"></param>
        /// <returns></returns>
        public string GetPrompt(string tagName, string ujson, string xml)
        {

            return $@"请基于 6.XML片段 和 7.UID到变量名的映射字典数据集内容，完成以下操作并输出严格符合格式的 JSON 结果： 
                        1.核心目标标签检索：查找标签名包含「{tagName}」的 IO 标签；
                        2.标签类型自主判定规则：
                            1) 报警标签（isAlarm）」：AI 需基于标签名称、注释语义（如包含 “报警”“Error”“故障”“未到位”“错误”“Alarm” 等报警类关键词）自主判断是否为报警相关标签；
                            2)「步骤标签（isStep）」：AI 需基于标签名称、注释语义（如包含 “步骤”“工序”“阶段”“流程”“Step” 等步骤类关键词）自主判断是否为步骤相关标签；
                            3) 无明确关联语义时，对应布尔值为 false；

                        3.全量 IO 标签提取：遍历文档内所有 IO 标签（<Member Name=""xxx"" Datatype=""Bool"">节点），提取以下信息：
                            1) tagName：标签名称（Member 节点的 Name 属性）；
                            2) path：标签完整路径（按「Type.CylinderOutErrors. 标签名」拼接）；
                            3) isAlarm：AI 自主判定的是否为报警标签（布尔值）；
                            4) isStep：AI 自主判定的是否为步骤标签（布尔值）；
                        4.新增 UID 映射字典：构建uidToTagName字典，key 为标签 UID，value 为对应的 tagName；
                        4.输出格式强制要求：仅输出 JSON 数组，结构如下（无任何外层字段）：
                            [
                              {{""tagName"": ""标签名称"",
                                ""uid"": ""标签唯一标识"",
                                ""path"": ""标签完整路径"",
                                ""isAlarm"": true/false,
                                ""isStep"": true/false
                              }}
                            ]
                        5.执行约束：
                            1) 严格基于文档内实际存在的标签信息，无编造、无遗漏；
                            2) 布尔值仅允许使用小写 true/false；
                            3) 路径拼接需准确反映 XML 内 Struct 层级（Type → CylinderOutErrors → 具体标签名）；
                            4) 若目标标签 OP090_CYL01_FwdlockWarn 不存在，其对应的 isAlarm/isStep 均为 false，且 AllAlarmLs 仍需完整输出文档内所有 IO 标签；
                            5) UID 需提取 Member 节点的 UID 属性；
                            6) 输出结果顺序要以标签输入顺序一致，不要乱序；
                        6.XML片段：
                            {xml}
                        7.UID到变量名的映射字典数据集：
                            {ujson}";
        }

        public class VariableAccessTag
        {
            public string tagName { get; set; }

            public string uid { get; set; }
            public string path { get; set; }
            public bool isAlarm { get; set; }
            public bool isStep { get; set; }
        }


        /// <summary>
        /// 根据PLC项目读取实时数据
        /// </summary>
        /// <param name="projectId"></param>
        /// <returns></returns>
        public Task<List<PLC_ReadTagResult>> GetReadData(long projectId)
        {

            return pLC_VariableAccessService.GetReadData(projectId);
        }


        /// <summary>
        /// JSON 修复器：去除markdown包裹、尝试单引号→双引号、去尾逗号、裁剪前后非JSON字符
        /// </summary>
        /// <param name="raw"></param>
        /// <returns></returns>
        private string FixJsonLooseToStrict(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            var s = raw.Trim();
            s = s.Replace("```json", string.Empty).Replace("```", string.Empty).Trim();
            // 简单裁剪：找到首个 { 或 [ 起始，最后一个 } 或 ] 结束
            int start = Math.Min(
                s.IndexOf('{') >= 0 ? s.IndexOf('{') : int.MaxValue,
                s.IndexOf('[') >= 0 ? s.IndexOf('[') : int.MaxValue);
            int end = Math.Max(s.LastIndexOf('}'), s.LastIndexOf(']'));
            if (start != int.MaxValue && end > start)
            {
                s = s.Substring(start, end - start + 1);
            }
            // 尝试将单引号JSON转双引号（保守处理）
            if (s.Contains("'{") || s.Contains("'}") || s.Contains("':"))
            {
                s = s.Replace("'", "\"");
            }
            // 去除结尾处常见尾逗号（在对象或数组前的逗号）
            s = s.Replace(",}\n", "}\n").Replace(",}\r\n", "}\r\n").Replace(",]", "]");
            return s.Trim();
        }
    }
}
