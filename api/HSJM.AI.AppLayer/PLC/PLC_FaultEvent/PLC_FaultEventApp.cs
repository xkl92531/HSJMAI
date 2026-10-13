using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.Utility;
using SqlSugar;

namespace HSJM.AI.AppLayer
{
    /// <summary>
    /// 故障事件应用服务：页面查询 + 小锦 Pull 补拉。
    /// 一行 = 一次完整故障（started_at + ended_at + duration_seconds）。
    /// </summary>
    public class PLC_FaultEventApp : ISelfScopedAutoInject
    {
        private readonly IPLC_FaultEventService _faultEventService;

        public PLC_FaultEventApp(IPLC_FaultEventService faultEventService)
        {
            _faultEventService = faultEventService;
        }

        /// <summary>
        /// 分页查询故障事件
        /// </summary>
        public async Task<QueryPagedResponseModel<PLC_FaultEventEntity>> GetListPagedAsync(
            int page, int size, long? projectId = null, string variableName = null,
            bool onlyOpen = false, DateTime? startTime = null, DateTime? endTime = null)
        {
            var exp = Expressionable.Create<PLC_FaultEventEntity>()
                .And(it => it.IsDeleted == false);

            if (projectId.HasValue && projectId.Value > 0)
            {
                exp.And(it => it.ProjectId == projectId.Value);
            }
            if (!string.IsNullOrWhiteSpace(variableName))
            {
                exp.And(it => it.VariableName.Contains(variableName.Trim()));
            }
            if (onlyOpen)
            {
                exp.And(it => it.EndedAt == null);
            }
            if (startTime.HasValue)
            {
                exp.And(it => it.StartedAt >= startTime.Value);
            }
            if (endTime.HasValue)
            {
                exp.And(it => it.StartedAt <= endTime.Value);
            }

            var expression = exp.ToExpression();
            int total = 0;
            var result = _faultEventService.GetListPageOrderBy(expression, page, size,
                "StartedAt".CreateOrderByExpression<PLC_FaultEventEntity>(), OrderByType.Desc, ref total);
            return new QueryPagedResponseModel<PLC_FaultEventEntity>()
            {
                Total = total,
                Data = result
            };
        }

        /// <summary>
        /// 小锦掉线补拉：按 modifytime 游标拉取变化过的故障行。
        /// 协议契约（消费端）：
        ///  1) 每次返回 cursor = 本批最大 rowModifyTime，下次请求带 afterTime=cursor（首次可不带=全量拉一遍存量）；
        ///  2) 每行映射为 1~2 条消息：FaultStarted（必有）+ FaultEnded（若已恢复）；
        ///     同一行结束时 modifytime 更新会再次返回该行 —— 消费端按 (eventId, eventType) 去重后即正确；
        ///  3) 消息字段与 FaultEventHub 实时推送完全一致（eventType/eventId/variableName/startedAt/endedAt/durationSeconds/...）。
        /// </summary>
        public async Task<HttpResponseResultModel<FaultEventPullResult>> PullAsync(long projectId, DateTime? afterTime, int limit = 100)
        {
            var response = new HttpResponseResultModel<FaultEventPullResult>();
            if (projectId <= 0)
            {
                response.IsSuccess = false;
                response.ErrorMessage = "projectId 不能为空";
                return response;
            }

            if (limit <= 0 || limit > 500) limit = 100;

            var exp = Expressionable.Create<PLC_FaultEventEntity>()
                .And(it => it.IsDeleted == false && it.ProjectId == projectId);
            if (afterTime.HasValue)
            {
                var cursor = afterTime.Value;
                exp.And(it => it.ModifyTime > cursor);
            }

            var rows = await _faultEventService.GetListAsync(
                exp.ToExpression(),
                OrderByType.Asc,
                it => it.ModifyTime);
            rows = rows.Take(limit).ToList();

            var messages = new List<FaultEventPullMessage>();
            foreach (var row in rows)
            {
                messages.Add(ToMessage(row, "FaultStarted"));
                if (row.EndedAt.HasValue)
                {
                    messages.Add(ToMessage(row, "FaultEnded"));
                }
            }

            var cursorOut = rows.Count > 0 ? rows.Max(r => r.ModifyTime ?? r.CreateTime ?? DateTime.Now) : afterTime;
            response.IsSuccess = true;
            response.BackResult = new FaultEventPullResult
            {
                ProjectId = projectId,
                Cursor = cursorOut,
                RowCount = rows.Count,
                Messages = messages
            };
            return response;
        }

        private static FaultEventPullMessage ToMessage(PLC_FaultEventEntity row, string eventType)
        {
            return new FaultEventPullMessage
            {
                EventId = row.Id,
                EventType = eventType,
                ProjectId = row.ProjectId,
                StationCode = row.StationCode,
                VariableName = row.VariableName,
                AlarmLevel = row.AlarmLevel,
                Description = row.Description,
                StartedAt = row.StartedAt,
                EndedAt = row.EndedAt,
                DurationSeconds = row.DurationSeconds,
                RowModifyTime = row.ModifyTime ?? row.CreateTime
            };
        }
    }

    /// <summary>Pull 返回结构</summary>
    public class FaultEventPullResult
    {
        public long ProjectId { get; set; }
        public DateTime? Cursor { get; set; }
        public int RowCount { get; set; }
        public List<FaultEventPullMessage> Messages { get; set; } = new List<FaultEventPullMessage>();
    }

    /// <summary>Pull 消息（字段与 FaultEventHub 实时推送一致）</summary>
    public class FaultEventPullMessage
    {
        public long EventId { get; set; }
        public string EventType { get; set; }
        public long ProjectId { get; set; }
        public string StationCode { get; set; }
        public string VariableName { get; set; }
        public string AlarmLevel { get; set; }
        public string Description { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? EndedAt { get; set; }
        public int? DurationSeconds { get; set; }
        public DateTime? RowModifyTime { get; set; }
    }
}
