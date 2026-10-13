using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.PLC.PLC_Upload;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace HSJM.AI.AppLayer
{
    /// <summary>
    /// 报警变量台账应用服务：页面 CRUD、人工确认锁、批量启停监控/推送、手动重跑识别。
    /// 台账数据源：AlarmVariableRecognitionService 三通道识别 upsert + 本类手工维护。
    /// </summary>
    public class PLC_AlarmVariableApp : ISelfScopedAutoInject
    {
        private readonly IPLC_AlarmVariableService _alarmVariableService;
        private readonly IPLC_IOTagService _tagService;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<PLC_AlarmVariableApp> _logger;

        public PLC_AlarmVariableApp(
            IPLC_AlarmVariableService alarmVariableService,
            IPLC_IOTagService tagService,
            IServiceProvider serviceProvider,
            ILogger<PLC_AlarmVariableApp> logger)
        {
            _alarmVariableService = alarmVariableService;
            _tagService = tagService;
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        /// <summary>
        /// 分页查询台账
        /// </summary>
        public async Task<QueryPagedResponseModel<PLC_AlarmVariableEntity>> GetListPagedAsync(
            int page, int size, long? projectId = null, string keyword = null, string category = null,
            string level = null, bool? monitorEnabled = null, bool? manuallyConfirmed = null, bool? collectable = null)
        {
            var exp = Expressionable.Create<PLC_AlarmVariableEntity>()
                .And(it => it.IsDeleted == false);

            if (projectId.HasValue && projectId.Value > 0)
            {
                exp.And(it => it.ProjectId == projectId.Value);
            }
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                var kw = keyword.Trim();
                exp.And(it => it.VariableName.Contains(kw) || it.Description.Contains(kw) || it.Address.Contains(kw));
            }
            if (!string.IsNullOrWhiteSpace(category))
            {
                exp.And(it => it.AlarmCategory == category);
            }
            if (!string.IsNullOrWhiteSpace(level))
            {
                exp.And(it => it.AlarmLevel == level);
            }
            if (monitorEnabled.HasValue)
            {
                exp.And(it => it.MonitorEnabled == monitorEnabled.Value);
            }
            if (manuallyConfirmed.HasValue)
            {
                exp.And(it => it.ManuallyConfirmed == manuallyConfirmed.Value);
            }
            if (collectable.HasValue)
            {
                exp.And(it => it.Collectable == collectable.Value);
            }

            var expression = exp.ToExpression();
            int total = 0;
            var result = _alarmVariableService.GetListPageOrderBy(expression, page, size,
                "Id".CreateOrderByExpression<PLC_AlarmVariableEntity>(), OrderByType.Desc, ref total);
            return new QueryPagedResponseModel<PLC_AlarmVariableEntity>()
            {
                Total = total,
                Data = result
            };
        }

        /// <summary>
        /// 手工新增报警变量（source=Manual，自动人工确认锁，自动判定可采集性）
        /// </summary>
        public async Task<HttpResponseResultModel<long>> InsertAsync(PLC_AlarmVariableEntity entity)
        {
            var response = new HttpResponseResultModel<long>();
            if (entity == null || string.IsNullOrWhiteSpace(entity.VariableName) || entity.ProjectId == null || entity.ProjectId <= 0)
            {
                response.IsSuccess = false;
                response.ErrorMessage = "ProjectId 与 VariableName 不能为空";
                return response;
            }

            var name = entity.VariableName.Trim();
            var duplicate = await _alarmVariableService.GetFirstAsync(
                it => it.ProjectId == entity.ProjectId && it.VariableName == name && it.IsDeleted == false);
            if (duplicate != null)
            {
                response.IsSuccess = false;
                response.ErrorMessage = $"变量 {name} 已存在于台账（id={duplicate.Id}）";
                return response;
            }

            var ioTag = await _tagService.GetFirstAsync(
                it => it.IsDeleted == false && it.ProjectId == entity.ProjectId && it.TagName == name);

            entity.VariableName = name;
            entity.Source = "Manual";
            entity.ManuallyConfirmed = true;
            entity.Collectable = ioTag != null;
            entity.Address ??= ioTag?.Address;
            entity.DataType ??= ioTag?.DataType;
            entity.AlarmLevel ??= "Warn";
            entity.PushEnabled ??= true;
            entity.AlarmCount ??= 0;
            if (entity.Collectable != true)
            {
                entity.MonitorEnabled = false; // 不可采集硬禁监控
            }
            entity.CreateTime = DateTime.Now;
            entity.IsDeleted = false;

            var id = await _alarmVariableService.InsertSnowflakeIdAsync(entity);
            response.IsSuccess = true;
            response.BackResult = id;
            response.ErrorMessage = entity.Collectable == true ? null : "注意：该变量不在 IO Tag 表，无法采集实时值，已禁用监控";
            return response;
        }

        /// <summary>
        /// 编辑台账行（仅允许改业务字段；名称/来源/统计字段由系统管理）
        /// </summary>
        public async Task<HttpResponseResultModel<bool>> UpdateAsync(long id, PLC_AlarmVariableEntity dto)
        {
            var response = new HttpResponseResultModel<bool>();
            var row = await _alarmVariableService.GetByIdAsync(id);
            if (row == null || row.IsDeleted)
            {
                response.IsSuccess = false;
                response.ErrorMessage = "台账行不存在或已删除";
                return response;
            }

            if (dto == null)
            {
                response.IsSuccess = false;
                response.ErrorMessage = "参数不能为空";
                return response;
            }

            row.StationCode = dto.StationCode;
            row.AlarmCategory = dto.AlarmCategory;
            row.AlarmLevel = dto.AlarmLevel;
            row.Description = dto.Description;
            row.Remark = dto.Remark;
            row.ModifyTime = DateTime.Now;
            // 手工编辑视为人工确认（LLM 重跑不再覆盖）
            row.ManuallyConfirmed = true;

            await _alarmVariableService.UpdateAsync(row);
            response.IsSuccess = true;
            response.BackResult = true;
            return response;
        }

        /// <summary>
        /// 人工确认锁开关。confirmed=true 后 LLM 重跑永不覆盖本行。
        /// </summary>
        public async Task<HttpResponseResultModel<bool>> ConfirmAsync(long id, bool confirmed)
        {
            var response = new HttpResponseResultModel<bool>();
            var row = await _alarmVariableService.GetByIdAsync(id);
            if (row == null || row.IsDeleted)
            {
                response.IsSuccess = false;
                response.ErrorMessage = "台账行不存在或已删除";
                return response;
            }

            await _alarmVariableService.UpdateAsync(
                it => new PLC_AlarmVariableEntity { ManuallyConfirmed = confirmed, ModifyTime = DateTime.Now },
                it => it.Id == id);
            response.IsSuccess = true;
            response.BackResult = true;
            return response;
        }

        /// <summary>
        /// 批量启停监控。collectable=false 的行拒绝开启并返回名单。
        /// </summary>
        public async Task<HttpResponseResultModel<bool>> SetMonitorAsync(List<long> ids, bool enabled)
        {
            var response = new HttpResponseResultModel<bool>();
            if (ids == null || ids.Count == 0)
            {
                response.IsSuccess = false;
                response.ErrorMessage = "ids 不能为空";
                return response;
            }

            var rows = await _alarmVariableService.GetListByIdsAsync(ids);
            rows = rows.Where(r => !r.IsDeleted).ToList();

            var rejected = rows.Where(r => enabled && r.Collectable != true).Select(r => r.VariableName).ToList();
            if (rejected.Count > 0)
            {
                response.IsSuccess = false;
                response.ErrorMessage = $"以下变量不在 IO Tag 表、无法采集实时值，不能开启监控：{string.Join("、", rejected.Take(5))}{(rejected.Count > 5 ? " 等" : "")}";
                return response;
            }

            await _alarmVariableService.UpdateAsync(
                it => new PLC_AlarmVariableEntity { MonitorEnabled = enabled, ModifyTime = DateTime.Now },
                it => ids.Contains(it.Id) && it.IsDeleted == false);
            response.IsSuccess = true;
            response.BackResult = true;
            return response;
        }

        /// <summary>
        /// 批量启停小锦推送
        /// </summary>
        public async Task<HttpResponseResultModel<bool>> SetPushAsync(List<long> ids, bool enabled)
        {
            var response = new HttpResponseResultModel<bool>();
            if (ids == null || ids.Count == 0)
            {
                response.IsSuccess = false;
                response.ErrorMessage = "ids 不能为空";
                return response;
            }

            await _alarmVariableService.UpdateAsync(
                it => new PLC_AlarmVariableEntity { PushEnabled = enabled, ModifyTime = DateTime.Now },
                it => ids.Contains(it.Id) && it.IsDeleted == false);
            response.IsSuccess = true;
            response.BackResult = true;
            return response;
        }

        /// <summary>
        /// 软删除
        /// </summary>
        public Task<bool> DeleteAsync(long id)
        {
            return _alarmVariableService.SoftDeleteByIdAsync(id);
        }

        public Task<bool> DeleteBatchAsync(List<long> ids)
        {
            return _alarmVariableService.SoftDeleteByIdsAsync(ids);
        }

        /// <summary>
        /// 手动重跑识别（带进度任务，前端轮询 UploadAPI.getProgress(taskId)）
        /// </summary>
        public async Task<HttpResponseResultModel<UploadTaskStartResult>> RerunRecognitionAsync(long projectId)
        {
            var response = new HttpResponseResultModel<UploadTaskStartResult>();
            var progressService = _serviceProvider.GetRequiredService<PLC_UploadProgressService>();

            if (PLC.PLC_AlarmVariable.AlarmVariableRecognitionService.IsProjectRunning(projectId))
            {
                response.IsSuccess = false;
                response.ErrorMessage = $"项目 {projectId} 的识别任务正在运行，请稍后";
                return response;
            }

            var taskId = $"alarm_var_{projectId}_{DateTime.Now:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}";
            progressService.CreateProgress(taskId, projectId, "识别报警变量");

            var enqueued = PLC.PLC_AlarmVariable.AlarmVariableRecognitionService.EnqueueWithProgressTaskId(taskId, projectId);
            if (!enqueued)
            {
                response.IsSuccess = false;
                response.ErrorMessage = "识别任务已在运行，未能重复入队";
                return response;
            }

            response.IsSuccess = true;
            response.BackResult = new UploadTaskStartResult
            {
                TaskId = taskId,
                Message = "报警变量识别任务已启动，正在后台处理..."
            };
            _logger.LogInformation("[AlarmVar] 手动重跑识别已入队: ProjectId={ProjectId}, TaskId={TaskId}", projectId, taskId);
            return response;
        }
    }
}
