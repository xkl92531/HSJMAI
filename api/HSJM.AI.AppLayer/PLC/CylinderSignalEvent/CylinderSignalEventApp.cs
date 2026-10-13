using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.PLC.CylinderSignalEvent.DTOS;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using SqlSugar;

namespace HSJM.AI.AppLayer.PLC.CylinderSignalEvent
{
    public class CylinderSignalEventApp : ISelfScopedAutoInject
    {
        private readonly ICylinderSignalEventService _eventService;
        private readonly CylinderSignalEventCollectorService _collectorService;
        private readonly CylinderSignalCollectorJobService _jobService;

        public CylinderSignalEventApp(
            ICylinderSignalEventService eventService,
            CylinderSignalEventCollectorService collectorService,
            CylinderSignalCollectorJobService jobService)
        {
            _eventService = eventService;
            _collectorService = collectorService;
            _jobService = jobService;
        }

        public Task<HttpResponseResultModel<CylinderSignalCollectResultDto>> CollectOnceAsync(CylinderSignalCollectRequestDto request)
        {
            return _collectorService.CollectOnceAsync(request);
        }

        public Task<HttpResponseResultModel<CylinderSignalSimulationGenerateResultDto>> GenerateSimulationValuesAsync(CylinderSignalSimulationGenerateRequestDto request)
        {
            return _collectorService.GenerateSimulationValuesAsync(request);
        }

        public Task<HttpResponseResultModel<CylinderSignalCollectAllResultDto>> CollectAllProjectsOnceAsync(CylinderSignalCollectAllRequestDto request)
        {
            return _jobService.CollectAllProjectsOnceAsync(request);
        }

        public Task<CylinderSignalCollectorJobConfigDto> GetJobConfigAsync()
        {
            return _jobService.GetConfigAsync();
        }

        public Task<HttpResponseResultModel<CylinderSignalCollectorJobConfigDto>> SaveJobConfigAsync(CylinderSignalCollectorJobConfigDto request)
        {
            return _jobService.SaveConfigAsync(request);
        }

        public async Task<HttpResponseResultModel<CylinderSignalCacheClearResultDto>> ClearCacheAsync(
            string projectIdsText,
            List<string> stationCodes,
            bool clearAllStations)
        {
            var response = new HttpResponseResultModel<CylinderSignalCacheClearResultDto>();
            var result = await _collectorService.ClearSignalCacheAsync(ParseProjectIds(projectIdsText), stationCodes, clearAllStations);
            response.IsSuccess = true;
            response.BackResult = result;
            return response;
        }

        public async Task<QueryPagedResponseModel<CylinderSignalEventItemDto>> GetListPagedAsync(
            int page,
            int size,
            CylinderSignalEventQueryDto query)
        {
            page = Math.Max(page, 1);
            size = Math.Clamp(size, 1, 200);
            var expression = Expressionable.Create<CylinderSignalEventEntity>()
                .And(it => it.IsDeleted == false)
                .AndIF(query != null && !string.IsNullOrWhiteSpace(query.PlcId), it => it.PlcId == query.PlcId.Trim())
                .AndIF(query != null && query.ProjectId.HasValue, it => it.PlcId == query.ProjectId.Value.ToString())
                .AndIF(query != null && !string.IsNullOrWhiteSpace(query.StationCode), it => it.StationCode == query.StationCode.Trim())
                .AndIF(query != null && !string.IsNullOrWhiteSpace(query.CylinderId), it => it.CylinderId == query.CylinderId.Trim())
                .AndIF(query != null && !string.IsNullOrWhiteSpace(query.SignalName), it => it.SignalName.Contains(query.SignalName.Trim()))
                .AndIF(query != null && !string.IsNullOrWhiteSpace(query.SignalRole), it => it.SignalRole == query.SignalRole.Trim())
                .AndIF(query != null && query.From.HasValue, it => it.ChangedAt >= query.From.Value)
                .AndIF(query != null && query.To.HasValue, it => it.ChangedAt <= query.To.Value)
                .ToExpression();

            var total = 0;
            var rows = _eventService.GetListPageOrderBy(
                expression,
                page,
                size,
                it => it.ChangedAt,
                OrderByType.Desc,
                ref total);

            return new QueryPagedResponseModel<CylinderSignalEventItemDto>
            {
                Total = total,
                Data = rows.Select(MapEvent).ToList()
            };
        }

        private static CylinderSignalEventItemDto MapEvent(CylinderSignalEventEntity entity)
        {
            return new CylinderSignalEventItemDto
            {
                Id = entity.Id,
                PlcId = entity.PlcId,
                StationCode = entity.StationCode,
                CylinderId = entity.CylinderId,
                SignalName = entity.SignalName,
                SignalRole = entity.SignalRole,
                OldValue = entity.OldValue,
                NewValue = entity.NewValue,
                ChangedAt = entity.ChangedAt,
                StationMode = entity.StationMode,
                CreatedAt = entity.CreateTime ?? entity.ChangedAt
            };
        }

        private static List<long> ParseProjectIds(string projectIdsText)
        {
            return (projectIdsText ?? string.Empty)
                .Split(new[] { ',', '，', ';', '；', '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(it => long.TryParse(it.Trim(), out var projectId) ? projectId : 0)
                .Where(it => it > 0)
                .Distinct()
                .ToList();
        }
    }
}
