
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/2/7 15:05:43
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using System.Linq.Expressions;
using System;
using System.Threading;
using HSJM.AI.IDbService;
using SqlSugar;
using HSJM.AI.Utility;
using HSJM.AI.Core;
using HSJM.AI.AppLayer;
using HSJM.AI.AppLayer.PLC.ViewObject;
using HSJM.AI.AppLayer.PLC.DTOS;
using HSJM.AI.AppLayer.PLC.PLC_Upload.Igw;
using HSJM.AI.NPOIHelper;
using System.IO;
using Microsoft.AspNetCore.Http;
namespace HSJM.AI.AppLayer
{
    public class PLC_ProjectApp:ISelfScopedAutoInject
    {
        private readonly IPLC_ProjectService pLC_ProjectService;
        private readonly ISqlSugarClient _db;
        private readonly IPlcProjectRemovedHandler _projectRemovedHandler;


         public PLC_ProjectApp(IPLC_ProjectService pLC_ProjectService, ISqlSugarClient db,
            IPlcProjectRemovedHandler projectRemovedHandler)
        {
            this.pLC_ProjectService = pLC_ProjectService;
            this._db = db;
            this._projectRemovedHandler = projectRemovedHandler;
        }

        // NOTE（2026-09-08 回归）：产线/设备数据源统一用基础资料表 "Basic_ProductionLine" / "Basic_Device"。
        // 2026-09-04 曾切到快照表 AI_PLC_*（plc_selfcontained_basic.sql 初始化），快照方案已整体废弃、表已删除。

        /// <summary>
        /// 产线下拉选项（数据源：基础资料表 "Basic_ProductionLine"，只取未删且启用的产线）。
        /// 表不存在等异常场景返回空列表（前端显示"暂无数据"），不抛异常。
        /// </summary>
        public async Task<List<PLC_ProductionLineOptionDTO>> GetProductionLineOptionsAsync()
        {
            try
            {
                const string sql =
                    "SELECT \"Id\", \"Code\", \"Name\" " +
                    "FROM \"Basic_ProductionLine\" " +
                    "WHERE \"IsDeleted\" = false AND (\"IsEnable\" IS NULL OR \"IsEnable\" = true) " +
                    "ORDER BY \"Code\", \"Id\"";
                return await _db.Ado.SqlQueryAsync<PLC_ProductionLineOptionDTO>(sql);
            }
            catch (Exception ex)
            {
                // "Basic_ProductionLine" 表不存在等场景：返回空选项让前端可继续建项目
                Console.WriteLine($"[PLC_Project] 加载产线基础数据失败（返回空列表）: {ex.Message}");
                return new List<PLC_ProductionLineOptionDTO>();
            }
        }

        /// <summary>
        /// PLC 设备下拉选项（数据源：基础资料表 "Basic_Device"）。
        /// productionLineId 非空时只返回该产线下的设备（"Basic_Device".lineid），否则返回全部。
        /// </summary>
        public async Task<List<PLC_PlcDeviceOptionDTO>> GetPlcDeviceOptionsAsync(long? productionLineId = null)
        {
            try
            {
                var lineFilter = productionLineId.HasValue && productionLineId.Value > 0
                    ? " AND \"LineID\" = @lineId"
                    : string.Empty;
                var sql =
                    "SELECT \"Id\", \"DeviceName\", \"IpAddress\", " +
                    "\"DeviceType\", \"LineID\" AS \"LineId\", \"ProjectName\" " +
                    "FROM \"Basic_Device\" WHERE \"IsDeleted\" = false" + lineFilter +
                    " ORDER BY \"DeviceName\", \"Id\"";
                if (lineFilter.Length == 0)
                {
                    return await _db.Ado.SqlQueryAsync<PLC_PlcDeviceOptionDTO>(sql);
                }
                return await _db.Ado.SqlQueryAsync<PLC_PlcDeviceOptionDTO>(sql, new { lineId = productionLineId.Value });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PLC_Project] 加载PLC设备基础数据失败（返回空列表）: {ex.Message}");
                return new List<PLC_PlcDeviceOptionDTO>();
            }
        }

        /// <summary>
        /// 判断设备表 IpAddress 是否像真实主机地址（IPv4/IPv6/带点主机名）。
        /// 基础表里存在 "1"/"2" 这类占位值，不能让它们流入 plcip（IGW 注册消费 plcip）。
        /// </summary>
        private static bool LooksLikeHostAddress(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var s = value.Trim();
            if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^\d{1,3}(\.\d{1,3}){3}$")) return true; // IPv4
            if (s.Contains(':')) return true;  // IPv6 或 host:port
            return s.Contains('.') && s.Length > 3; // 带点主机名
        }

        /// <summary>
        /// 绑定信息回填：按 ProductionLineId / PlcDeviceId 从基础表解析名称并冗余到项目实体。
        /// 设备绑定时自动回填 PlcIp（设备 IpAddress，占位值跳过）与 PlcCode（设备 DeviceName，对齐工位归属过滤约定）。
        /// 返回错误消息（基础数据不存在时），null 表示成功。
        /// </summary>
        private async Task<string> ApplyBindingAsync(PLC_ProjectEntity entity)
        {
            if (entity.ProductionLineId.HasValue && entity.ProductionLineId.Value > 0)
            {
                const string lineSql =
                    "SELECT \"Id\", \"Code\", \"Name\" " +
                    "FROM \"Basic_ProductionLine\" " +
                    "WHERE \"IsDeleted\" = false AND \"Id\" = @id LIMIT 1";
                var lines = await _db.Ado.SqlQueryAsync<PLC_ProductionLineOptionDTO>(
                    lineSql, new { id = entity.ProductionLineId.Value });
                if (lines.Count == 0)
                {
                    return $"产线不存在（Id={entity.ProductionLineId}），请刷新下拉后重选";
                }
                var line = lines[0];
                entity.ProductionLineName = !string.IsNullOrWhiteSpace(line.Name) ? line.Name : line.Code;
            }
            else
            {
                entity.ProductionLineName = null;
            }

            if (entity.PlcDeviceId.HasValue && entity.PlcDeviceId.Value > 0)
            {
                const string deviceSql =
                    "SELECT \"Id\", \"DeviceName\", \"IpAddress\", " +
                    "\"DeviceType\", \"LineID\" AS \"LineId\", \"ProjectName\" " +
                    "FROM \"Basic_Device\" WHERE \"IsDeleted\" = false AND \"Id\" = @id LIMIT 1";
                var devices = await _db.Ado.SqlQueryAsync<PLC_PlcDeviceOptionDTO>(
                    deviceSql, new { id = entity.PlcDeviceId.Value });
                if (devices.Count == 0)
                {
                    return $"PLC 设备不存在（Id={entity.PlcDeviceId}），请刷新下拉后重选";
                }
                var device = devices[0];
                entity.PlcDeviceName = device.DeviceName;
                if (LooksLikeHostAddress(device.IpAddress))
                {
                    // 设备 IP 以基础表为准回填（IGW 注册消费 PlcIp；"1"/"2" 等占位值跳过不回填）
                    entity.PlcIp = device.IpAddress.Trim();
                }
                if (!string.IsNullOrWhiteSpace(device.DeviceName))
                {
                    // PlcCode 对齐 Basic_SubStation.PlcName 约定（PLC1/PLC2），工位归属过滤消费
                    entity.PlcCode = device.DeviceName;
                }
            }
            else
            {
                entity.PlcDeviceName = null;
            }

            return null;
        }

        /// <summary>
        /// 根据主键获取单个实体
        /// </summary>
        /// <param name="id">主键</param>
        /// <returns></returns>
        public async Task<PLC_ProjectEntity> GetAsync(long id)
        {
		   return   await pLC_ProjectService.GetByIdAsync(id).ConfigureAwait(false);
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
        public async Task<QueryPagedResponseModel<PLC_ProjectEntity>> GetListPagedAsync(int page, int size, PLC_ProjectConditionDTO conditionDTO=null, string orderByField = "Id", bool isDesc=true)
        {
            var expression = PackageExpression(conditionDTO);
              
            int total = 0;
            var result =  pLC_ProjectService.GetListPageOrderBy(expression,page,size, orderByField.CreateOrderByExpression<PLC_ProjectEntity>(), isDesc?OrderByType.Desc: OrderByType.Asc, ref total);
            return new QueryPagedResponseModel<PLC_ProjectEntity>()
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
        public async Task<List<PLC_ProjectEntity>> GetListAsync(PLC_ProjectConditionDTO  conditionDTO, string orderByField = "Id", bool isDesc = true)
        {
            var expression = PackageExpression(conditionDTO);
            var result = await pLC_ProjectService.GetListAsync(expression, isDesc ? OrderByType.Desc : OrderByType.Asc, orderByField.CreateOrderByExpression<PLC_ProjectEntity>());
            return result;
        }

        /// <summary>
        /// 根据id集合获取多条数据
        /// </summary>
        /// <param name="ids"></param>
        /// <returns></returns>
        public async Task<List<PLC_ProjectEntity>> GetListByIdsAsync(List<long> ids)
        {
            if (ids.IsListNullOrEmpty()) {
                return new List<PLC_ProjectEntity>();
            }
            return await pLC_ProjectService.GetListByIdsAsync(ids);
        }

        /// <summary>
        /// 获取最后一条
        /// </summary>
        /// <returns></returns>
        public async Task<PLC_ProjectEntity> GetFirstAsync(PLC_ProjectConditionDTO conditionDTO)
        {
            var expression = PackageExpression(conditionDTO);
            return await pLC_ProjectService.GetFirstAsync(expression, x => x.CreateTime, OrderByType.Desc);
        }

	    /// <summary>
        /// 插入单个实体
        /// </summary>
        /// <param name="insertDto">对应实体数据传输对象</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<long>> InsertAsync(PLC_ProjectInsertDTO insertDto)
        { 
            HttpResponseResultModel<long> httpResponseResultModel = new HttpResponseResultModel<long>();
            var entity = insertDto.MapTo<PLC_ProjectEntity>();
            // 按基础表解析产线/PLC设备绑定并冗余名称 + 回填 PlcIp/PlcCode
            var bindingError = await ApplyBindingAsync(entity);
            if (bindingError != null)
            {
                httpResponseResultModel.ErrorMessage = bindingError;
                return httpResponseResultModel;
            }
            httpResponseResultModel.IsSuccess= await pLC_ProjectService.InsertAsync(entity).ConfigureAwait(false);
            httpResponseResultModel.BackResult = entity.Id;
            return httpResponseResultModel;
        }

		 

		/// <summary>
        /// 批量插入实体
        /// </summary>
        /// <param name="insertDtoList">对应实体数据传输对象集合</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> InsertBatchAsync(List<PLC_ProjectInsertDTO> insertDtoList)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();
            var entityList = insertDtoList.MapToList<PLC_ProjectEntity>();
            httpResponseResultModel.IsSuccess = await pLC_ProjectService.InsertRangeAsync(entityList);
            httpResponseResultModel.BackResult = httpResponseResultModel.IsSuccess;
            return httpResponseResultModel;
        }
       
		  
        /// <summary>
        /// 根据主键更新实体
        /// </summary>
		/// <param name="id">主键</param>
        /// <param name="updateDto">对应实体数据传输对象</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> UpdateAsync(long id,PLC_ProjectUpdateDTO updateDto)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();
            var entity = updateDto.MapTo<PLC_ProjectEntity>();
            entity.Id = id;
            // 按基础表解析产线/PLC设备绑定并冗余名称 + 回填 PlcIp/PlcCode
            //（DTO 未传绑定ID时基础表名称置 null，配合 IgnoreColumns(ignoreAllNullColumns) 保持库中已有绑定不变）
            var bindingError = await ApplyBindingAsync(entity);
            if (bindingError != null)
            {
                httpResponseResultModel.ErrorMessage = bindingError;
                return httpResponseResultModel;
            }
            httpResponseResultModel.IsSuccess = await pLC_ProjectService.UpdateAsync(entity);
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
            httpResponseResultModel.IsSuccess = await pLC_ProjectService.SoftDeleteByIdAsync(id);
            if (httpResponseResultModel.IsSuccess)
            {
                await CascadeProjectRemovedAsync(new List<long> { id });
            }
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
            httpResponseResultModel.IsSuccess = await pLC_ProjectService.SoftDeleteByIdsAsync(idList);
            if (httpResponseResultModel.IsSuccess)
            {
                await CascadeProjectRemovedAsync(idList);
            }
            httpResponseResultModel.BackResult = httpResponseResultModel.IsSuccess;
            return httpResponseResultModel;
        }

        /// <summary>
        /// PLC 项目删除联动（2026-09-04）：经 IPlcProjectRemovedHandler 抽象让各域清理自己的派生数据
        /// （LineUp 实现见 LineUpProjectCascadeApp——图谱资产/序列/修订/步/迁移/动作/绑定/运行事件/导入批次级联软删）。
        /// PLC 模块不触碰任何其它域的表；联动失败不阻断删除，仅记日志。
        /// </summary>
        private async Task CascadeProjectRemovedAsync(List<long> projectIds)
        {
            await CascadePlcDomainAsync(projectIds);
            try
            {
                await _projectRemovedHandler.HandleProjectsRemovedAsync(projectIds, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PLC_Project] 项目删除联动清理失败（不影响删除结果，可重删重试）: {ex.Message}");
            }
            // M-4（2026-09-20）：已删除项目的 IGW 通道清理（后台任务；只删本项目 PLC IP 下带批量标记的设备，通道删空才删通道，
            // 共享通道不动）。入队失败不影响删除结果
            try
            {
                PLC_IgwProjectCleanupJob.EnqueueForRemovedProjects(projectIds);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PLC_Project] 入队 IGW 通道清理失败（不影响删除结果）: {ex.Message}");
            }
        }

        /// <summary>
        /// PLC 域自身子数据级联软删（2026-09-04，补齐「所有数据与 ai_plc_project 联动」的 PLC 侧）。
        /// 删除项目后软删其全部解析产物：块/源文件/逻辑单元/接口/AST/语义发现/IO Tag/约束/变量访问/图边等。
        /// 挂靠：projectid（camel）直挂表 + project_id（snake）直挂表 + blockid 挂块表 + ruleid 挂约束表。
        /// 逐表容错：单表失败（如列缺失/历史表结构差异）仅记日志不中断。
        /// </summary>
        private async Task CascadePlcDomainAsync(List<long> projectIds)
        {
            var idParams = projectIds.Select((value, index) => new SugarParameter($"@p{index}", value)).ToArray();
            var idList = string.Join(", ", idParams.Select(p => p.ParameterName));

            var projectTables = new[]
            {
                "ai_plc_sourcefile", "ai_plc_block_new", "ai_plc_blockcall", "ai_plc_block_edge",
                "ai_plc_connection", "ai_plc_dbfbinding", "ai_plc_diagnosis_log", "ai_plc_entry_station",
                "ai_plc_failed_block", "ai_plc_graphsteptag", "ai_plc_ioconstraintrule", "ai_plc_iotag",
                "ai_plc_logic_expression", "ai_plc_logicexpression", "ai_plc_network", "ai_plc_parse_run",
                "ai_plc_runtime_state", "ai_plc_signal_mapping", "ai_plc_station_device", "ai_plc_block",
                // IGW 注册台账（task-5，列名 projectid 一致；表可能尚未建出——TrySoftDeleteAsync 容错，不影响删除）
                "ai_plc_igwregistration",
            };
            foreach (var table in projectTables)
            {
                await TrySoftDeleteAsync(table, "projectid", idList, idParams);
            }
            // snake_case 直挂表（停机诊断 AST/语义发现）
            await TrySoftDeleteAsync("ai_plc_logicunit_ast", "project_id", idList, idParams);
            await TrySoftDeleteAsync("ai_plc_logicunit_semantic_finding", "project_id", idList, idParams);

            // 经 blockid 挂靠（块接口/逻辑单元）
            var blockIds = await _db.Ado.SqlQueryAsync<long>(
                    $"SELECT id FROM ai_plc_block_new WHERE projectid IN ({idList})", idParams);
            if (blockIds.Count > 0)
            {
                var bParams = blockIds.Select((value, index) => new SugarParameter($"@b{index}", value)).ToArray();
                var bList = string.Join(", ", bParams.Select(p => p.ParameterName));
                await TrySoftDeleteAsync("ai_plc_blockinterface", "blockid", bList, bParams);
                await TrySoftDeleteAsync("ai_plc_logicunit", "blockid", bList, bParams);
            }

            // 经 ruleid 挂靠（约束信号明细）
            var ruleIds = await _db.Ado.SqlQueryAsync<long>(
                    $"SELECT id FROM ai_plc_ioconstraintrule WHERE projectid IN ({idList})", idParams);
            if (ruleIds.Count > 0)
            {
                var rParams = ruleIds.Select((value, index) => new SugarParameter($"@r{index}", value)).ToArray();
                var rList = string.Join(", ", rParams.Select(p => p.ParameterName));
                await TrySoftDeleteAsync("ai_plc_ioconstraintsignal", "ruleid", rList, rParams);
            }
        }

        private async Task TrySoftDeleteAsync(string table, string column, string list, SugarParameter[] parameters)
        {
            try
            {
                await _db.Ado.ExecuteCommandAsync(
                    $"UPDATE {table} SET isdeleted = true WHERE isdeleted = false AND {column} IN ({list})",
                    parameters);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PLC_Project] 级联软删 {table} 失败（跳过，不影响删除）: {ex.Message}");
            }
        }

        /// <summary>
        /// 条件组装
        /// </summary>
        /// <param name="dto"></param>
        /// <returns></returns>
        private Expression<Func<PLC_ProjectEntity, bool>> PackageExpression(PLC_ProjectConditionDTO dto) {
            Expression<Func<PLC_ProjectEntity, bool>> expression = Expressionable.Create<PLC_ProjectEntity>()
                .And(t => t.IsDeleted == false)
            
                .ToExpression();
            return expression;
        }

       
        /// <summary>
        /// 导出Excel
        /// </summary>
        /// <param name="conditionDTO"></param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<string>> ExportToExcelAsync(PLC_ProjectConditionDTO conditionDTO)
        {
            HttpResponseResultModel<string> httpResponseResultModel = new HttpResponseResultModel<string>();
            var exportCondition = new ExportExcel<PLC_ProjectView>();
            var tags = await GetListAsync(conditionDTO);
            exportCondition.Data = tags.MapToList<PLC_ProjectView>().ToList();
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
            var importResult = await MyExportImportExcelHelper.ImportExcelAsync<PLC_ProjectView>(new FileStream(drictory, FileMode.Open));

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
            var insertList = new List<PLC_ProjectEntity>();
            importDatas.ForEach(s =>
            {
                s.Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                insertList.Add(s.MapTo<PLC_ProjectEntity>());
            });

            var insertResult = await pLC_ProjectService.InsertRangeAsync(insertList);

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
