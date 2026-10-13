using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Models;
using HSJM.AI.Core;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    public class AstPersistenceService : ISelfScopedAutoInject
    {
        private const string CurrentAstVersion = "v1";

        private readonly IPLC_LogicUnitAstService _logicUnitAstService;
        private readonly IPLC_LogicUnitSemanticFindingService _semanticFindingService;
        private readonly IPLC_LogicUnitService _logicUnitService;
        private readonly IPLC_BlockService _blockService;

        public AstPersistenceService(
            IPLC_LogicUnitAstService logicUnitAstService,
            IPLC_LogicUnitSemanticFindingService semanticFindingService,
            IPLC_LogicUnitService logicUnitService,
            IPLC_BlockService blockService)
        {
            _logicUnitAstService = logicUnitAstService;
            _semanticFindingService = semanticFindingService;
            _logicUnitService = logicUnitService;
            _blockService = blockService;
        }

        public async Task PersistAsync(
            long projectId,
            string stationCode,
            long? blockId,
            IReadOnlyList<StopCauseAstCompileResult> compileResults,
            IReadOnlyList<AstSemanticFinding> semanticFindings)
        {
            compileResults ??= Array.Empty<StopCauseAstCompileResult>();
            semanticFindings ??= Array.Empty<AstSemanticFinding>();

            if (blockId.HasValue)
            {
                await _logicUnitAstService.DeleteAsync(it =>
                    it.ProjectId == projectId &&
                    it.BlockId == blockId.Value);

                await _semanticFindingService.DeleteAsync(it =>
                    it.ProjectId == projectId &&
                    it.BlockId == blockId.Value);
            }
            else if (!string.IsNullOrWhiteSpace(stationCode))
            {
                await _logicUnitAstService.DeleteAsync(it =>
                    it.ProjectId == projectId &&
                    it.StationCode == stationCode);

                await _semanticFindingService.DeleteAsync(it =>
                    it.ProjectId == projectId &&
                    it.StationCode == stationCode);
            }
            else
            {
                await _logicUnitAstService.DeleteAsync(it => it.ProjectId == projectId);
                await _semanticFindingService.DeleteAsync(it => it.ProjectId == projectId);
            }

            await PersistRowsAsync(projectId, compileResults, semanticFindings);
        }

        public async Task<int> PersistLogicUnitAsync(
            long projectId,
            long logicUnitId,
            IReadOnlyList<StopCauseAstCompileResult> compileResults,
            IReadOnlyList<AstSemanticFinding> semanticFindings)
        {
            compileResults ??= Array.Empty<StopCauseAstCompileResult>();
            semanticFindings ??= Array.Empty<AstSemanticFinding>();

            await _logicUnitAstService.DeleteAsync(it =>
                it.ProjectId == projectId &&
                it.LogicUnitId == logicUnitId);

            await _semanticFindingService.DeleteAsync(it =>
                it.ProjectId == projectId &&
                it.LogicUnitId == logicUnitId);

            var scopedCompileResults = compileResults
                .Where(result => result?.CoverageReports != null &&
                    result.CoverageReports.Any(report => report.LogicUnitId == logicUnitId))
                .ToList();

            var scopedSemanticFindings = semanticFindings
                .Where(it => it.LogicUnitId == logicUnitId)
                .ToList();

            return await PersistRowsAsync(projectId, scopedCompileResults, scopedSemanticFindings);
        }

        private async Task<int> PersistRowsAsync(
            long projectId,
            IReadOnlyList<StopCauseAstCompileResult> compileResults,
            IReadOnlyList<AstSemanticFinding> semanticFindings)
        {
            compileResults ??= Array.Empty<StopCauseAstCompileResult>();
            semanticFindings ??= Array.Empty<AstSemanticFinding>();

            var logicUnitIds = compileResults
                .SelectMany(it => it.CoverageReports ?? new List<AstCoverageReport>())
                .Where(it => it.LogicUnitId.HasValue)
                .Select(it => it.LogicUnitId.Value)
                .Concat(semanticFindings.Where(it => it.LogicUnitId > 0).Select(it => it.LogicUnitId))
                .Distinct()
                .ToList();

            if (logicUnitIds.Count == 0)
            {
                return 0;
            }

            var logicUnits = await _logicUnitService.GetListAsync(it => logicUnitIds.Contains(it.Id) && it.IsDeleted == false);
            var logicUnitMap = logicUnits.ToDictionary(it => it.Id, it => it);

            var blockIds = logicUnits
                .Where(it => it.BlockId.HasValue)
                .Select(it => it.BlockId.Value)
                .Distinct()
                .ToList();
            var blocks = blockIds.Count == 0
                ? new List<PLC_BlockEntity>()
                : await _blockService.GetListAsync(it => blockIds.Contains(it.Id) && it.IsDeleted == false);
            var blockMap = blocks.ToDictionary(it => it.Id, it => it);

            var astRows = BuildAstRows(projectId, compileResults, logicUnitMap, blockMap);
            if (astRows.Count > 0)
            {
                await _logicUnitAstService.BulkCopyAsync(astRows);
            }

            var sourceHashByLogicUnit = astRows
                .Where(it => it.LogicUnitId.HasValue)
                .GroupBy(it => it.LogicUnitId.Value)
                .ToDictionary(g => g.Key, g => g.First().SourceHash);

            var semanticRows = BuildSemanticRows(projectId, semanticFindings, logicUnitMap, blockMap, sourceHashByLogicUnit);
            if (semanticRows.Count > 0)
            {
                await _semanticFindingService.BulkCopyAsync(semanticRows);
            }

            return astRows.Count;
        }

        public async Task<List<PersistedWriterAstDetailDto>> QueryAsync(PersistedWriterAstQueryDto query)
        {
            query ??= new PersistedWriterAstQueryDto();
            var requestedLogicUnitIds = ParseLogicUnitIds(query.LogicUnitIds);
            var limit = query.Limit <= 0 ? 100 : Math.Min(query.Limit, 500);
            var astRows = await LoadAstRowsAsync(query.ProjectId, query.StationCode, query.BlockId, requestedLogicUnitIds);

            if (requestedLogicUnitIds.Count > 0)
            {
                var orderMap = requestedLogicUnitIds
                    .Select((logicUnitId, index) => new { logicUnitId, index })
                    .ToDictionary(it => it.logicUnitId, it => it.index);

                astRows = astRows
                    .OrderBy(it => it.LogicUnitId.HasValue && orderMap.TryGetValue(it.LogicUnitId.Value, out var index)
                        ? index
                        : int.MaxValue)
                    .Take(limit)
                    .ToList();
            }
            else
            {
                astRows = astRows
                    .OrderBy(it => it.StationCode)
                    .ThenBy(it => it.BlockName)
                    .ThenBy(it => it.NetworkId)
                    .Take(limit)
                    .ToList();
            }

            return await BuildPersistedDetailsAsync(query.ProjectId, astRows);
        }

        public async Task<QueryPagedResponseModel<PersistedWriterAstDetailDto>> QueryPagedAsync(PersistedWriterAstPagedQueryDto query)
        {
            query ??= new PersistedWriterAstPagedQueryDto();

            var requestedLogicUnitIds = ParseLogicUnitIds(query.LogicUnitIds);
            var page = query.Page <= 0 ? 1 : query.Page;
            var limit = query.Limit <= 0 ? 20 : Math.Min(query.Limit, 200);
            var astRows = await LoadAstRowsAsync(query.ProjectId, query.StationCode, query.BlockId, requestedLogicUnitIds);
            var details = await BuildPersistedDetailsAsync(query.ProjectId, astRows);

            if (!string.IsNullOrWhiteSpace(query.BlockName))
            {
                details = details
                    .Where(it => ContainsIgnoreCase(it.BlockName, query.BlockName))
                    .ToList();
            }

            if (!string.IsNullOrWhiteSpace(query.CompileStatus))
            {
                details = details
                    .Where(it => string.Equals(it.CompileStatus, query.CompileStatus, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            if (!string.IsNullOrWhiteSpace(query.VariableName))
            {
                details = details
                    .Where(it => ContainsIgnoreCase(ResolvePrimaryVariableName(it), query.VariableName))
                    .ToList();
            }

            if (requestedLogicUnitIds.Count > 0)
            {
                var orderMap = requestedLogicUnitIds
                    .Select((logicUnitId, index) => new { logicUnitId, index })
                    .ToDictionary(it => it.logicUnitId, it => it.index);

                details = details
                    .OrderBy(it => orderMap.TryGetValue(it.LogicUnitId, out var index) ? index : int.MaxValue)
                    .ToList();
            }
            else
            {
                details = details
                    .OrderBy(it => it.StationCode)
                    .ThenBy(it => it.BlockName)
                    .ThenBy(it => it.NetworkId)
                    .ThenBy(it => it.LogicUnitId)
                    .ToList();
            }

            var total = details.Count;
            var data = details
                .Skip(Math.Max(page - 1, 0) * limit)
                .Take(limit)
                .ToList();

            return new QueryPagedResponseModel<PersistedWriterAstDetailDto>
            {
                Total = total,
                Data = data
            };
        }

        public async Task<QueryPagedResponseModel<PersistedSemanticFindingListItemDto>> QuerySemanticFindingsPagedAsync(PersistedSemanticFindingPagedQueryDto query)
        {
            query ??= new PersistedSemanticFindingPagedQueryDto();

            var page = query.Page <= 0 ? 1 : query.Page;
            var limit = query.Limit <= 0 ? 100 : Math.Min(query.Limit, 500);

            List<PLC_LogicUnitSemanticFindingEntity> rows;
            if (query.BlockId.HasValue && !string.IsNullOrWhiteSpace(query.StationCode))
            {
                rows = await _semanticFindingService.GetListAsync(it =>
                    it.ProjectId == query.ProjectId &&
                    it.IsDeleted == false &&
                    it.BlockId == query.BlockId.Value &&
                    it.StationCode == query.StationCode);
            }
            else if (query.BlockId.HasValue)
            {
                rows = await _semanticFindingService.GetListAsync(it =>
                    it.ProjectId == query.ProjectId &&
                    it.IsDeleted == false &&
                    it.BlockId == query.BlockId.Value);
            }
            else if (!string.IsNullOrWhiteSpace(query.StationCode))
            {
                rows = await _semanticFindingService.GetListAsync(it =>
                    it.ProjectId == query.ProjectId &&
                    it.IsDeleted == false &&
                    it.StationCode == query.StationCode);
            }
            else
            {
                rows = await _semanticFindingService.GetListAsync(it =>
                    it.ProjectId == query.ProjectId &&
                    it.IsDeleted == false);
            }

            if (!string.IsNullOrWhiteSpace(query.Category))
            {
                rows = rows
                    .Where(it => string.Equals(it.Category, query.Category, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            rows = rows
                .OrderByDescending(it => it.Confidence ?? 0m)
                .ThenBy(it => it.StationCode)
                .ThenBy(it => it.BlockName)
                .ThenBy(it => it.VariableName)
                .ToList();

            var total = rows.Count;
            var data = rows
                .Skip(Math.Max(page - 1, 0) * limit)
                .Take(limit)
                .Select(it => new PersistedSemanticFindingListItemDto
                {
                    LogicUnitId = it.LogicUnitId,
                    BlockId = it.BlockId,
                    BlockName = it.BlockName,
                    StationCode = it.StationCode,
                    VariableName = it.VariableName,
                    Category = it.Category,
                    SemanticLabel = it.SemanticLabel,
                    Reason = it.Reason,
                    Confidence = it.Confidence,
                    RelatedIOTags = DeserializeStringList(it.RelatedIoTagsJson),
                    IoMapping = it.IoMapping
                })
                .ToList();

            return new QueryPagedResponseModel<PersistedSemanticFindingListItemDto>
            {
                Total = total,
                Data = data
            };
        }

        private async Task<List<PLC_LogicUnitAstEntity>> LoadAstRowsAsync(
            long projectId,
            string stationCode,
            long? blockId,
            List<long> requestedLogicUnitIds)
        {
            if (blockId.HasValue && !string.IsNullOrEmpty(stationCode) && requestedLogicUnitIds.Count > 0)
            {
                return await _logicUnitAstService.GetListAsync(it =>
                    it.ProjectId == projectId &&
                    it.IsDeleted == false &&
                    it.BlockId == blockId.Value &&
                    it.StationCode == stationCode &&
                    it.LogicUnitId.HasValue &&
                    requestedLogicUnitIds.Contains(it.LogicUnitId.Value));
            }

            if (blockId.HasValue && requestedLogicUnitIds.Count > 0)
            {
                return await _logicUnitAstService.GetListAsync(it =>
                    it.ProjectId == projectId &&
                    it.IsDeleted == false &&
                    it.BlockId == blockId.Value &&
                    it.LogicUnitId.HasValue &&
                    requestedLogicUnitIds.Contains(it.LogicUnitId.Value));
            }

            if (!string.IsNullOrEmpty(stationCode) && requestedLogicUnitIds.Count > 0)
            {
                return await _logicUnitAstService.GetListAsync(it =>
                    it.ProjectId == projectId &&
                    it.IsDeleted == false &&
                    it.StationCode == stationCode &&
                    it.LogicUnitId.HasValue &&
                    requestedLogicUnitIds.Contains(it.LogicUnitId.Value));
            }

            if (requestedLogicUnitIds.Count > 0)
            {
                return await _logicUnitAstService.GetListAsync(it =>
                    it.ProjectId == projectId &&
                    it.IsDeleted == false &&
                    it.LogicUnitId.HasValue &&
                    requestedLogicUnitIds.Contains(it.LogicUnitId.Value));
            }

            if (blockId.HasValue && !string.IsNullOrEmpty(stationCode))
            {
                return await _logicUnitAstService.GetListAsync(it =>
                    it.ProjectId == projectId &&
                    it.IsDeleted == false &&
                    it.BlockId == blockId.Value &&
                    it.StationCode == stationCode);
            }

            if (blockId.HasValue)
            {
                return await _logicUnitAstService.GetListAsync(it =>
                    it.ProjectId == projectId &&
                    it.IsDeleted == false &&
                    it.BlockId == blockId.Value);
            }

            if (!string.IsNullOrEmpty(stationCode))
            {
                return await _logicUnitAstService.GetListAsync(it =>
                    it.ProjectId == projectId &&
                    it.IsDeleted == false &&
                    it.StationCode == stationCode);
            }

            return await _logicUnitAstService.GetListAsync(it =>
                it.ProjectId == projectId &&
                it.IsDeleted == false);
        }

        private async Task<List<PersistedWriterAstDetailDto>> BuildPersistedDetailsAsync(long projectId, List<PLC_LogicUnitAstEntity> astRows)
        {
            astRows ??= new List<PLC_LogicUnitAstEntity>();

            var logicUnitIds = astRows
                .Where(it => it.LogicUnitId.HasValue)
                .Select(it => it.LogicUnitId.Value)
                .Distinct()
                .ToList();

            var semanticRows = logicUnitIds.Count == 0
                ? new List<PLC_LogicUnitSemanticFindingEntity>()
                : await _semanticFindingService.GetListAsync(it =>
                    it.ProjectId == projectId &&
                    it.IsDeleted == false &&
                    it.LogicUnitId.HasValue &&
                    logicUnitIds.Contains(it.LogicUnitId.Value));

            var semanticMap = semanticRows
                .Where(it => it.LogicUnitId.HasValue)
                .GroupBy(it => it.LogicUnitId.Value)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(it => new PersistedSemanticFindingDto
                    {
                        LogicUnitId = it.LogicUnitId,
                        VariableName = it.VariableName,
                        Category = it.Category,
                        SemanticLabel = it.SemanticLabel,
                        Reason = it.Reason,
                        Confidence = it.Confidence,
                        RelatedIOTags = DeserializeStringList(it.RelatedIoTagsJson),
                        IoMapping = it.IoMapping
                    }).OrderByDescending(it => it.Confidence ?? 0m).ToList());

            return astRows.Select(it => new PersistedWriterAstDetailDto
            {
                LogicUnitId = it.LogicUnitId ?? 0,
                BlockId = it.BlockId,
                BlockName = it.BlockName,
                StationCode = it.StationCode,
                UnitType = it.UnitType,
                NetworkId = it.NetworkId,
                Title = it.Title,
                Comment = it.Comment,
                ProgrammingLanguage = it.ProgrammingLanguage,
                ReadVarsCount = it.ReadVarsCount,
                WriteVarsCount = it.WriteVarsCount,
                CallsCount = it.CallsCount,
                CompilerName = it.CompilerName,
                CompileStatus = it.CompileStatus,
                AstVersion = it.AstVersion,
                SourceHash = it.SourceHash,
                RootNodeJson = it.RootNodeJson,
                CoverageReportsJson = it.CoverageReportsJson,
                Messages = DeserializeStringList(it.MessagesJson),
                InputVariables = DeserializeStringList(it.InputVariablesJson),
                OutputVariables = DeserializeStringList(it.OutputVariablesJson),
                RawContentExcerpt = it.RawContentExcerpt,
                SemanticFindings = it.LogicUnitId.HasValue && semanticMap.TryGetValue(it.LogicUnitId.Value, out var findings)
                    ? findings
                    : new List<PersistedSemanticFindingDto>()
            }).ToList();
        }

        private static bool ContainsIgnoreCase(string source, string keyword)
        {
            return !string.IsNullOrWhiteSpace(source) &&
                   !string.IsNullOrWhiteSpace(keyword) &&
                   source.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ResolvePrimaryVariableName(PersistedWriterAstDetailDto dto)
        {
            if (dto == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(dto.RootNodeJson))
            {
                try
                {
                    using var document = JsonDocument.Parse(dto.RootNodeJson);
                    if (document.RootElement.TryGetProperty("variableName", out var variableNameElement) && variableNameElement.ValueKind == JsonValueKind.String)
                    {
                        return variableNameElement.GetString() ?? string.Empty;
                    }

                    if (document.RootElement.TryGetProperty("VariableName", out variableNameElement) && variableNameElement.ValueKind == JsonValueKind.String)
                    {
                        return variableNameElement.GetString() ?? string.Empty;
                    }
                }
                catch
                {
                }
            }

            return dto.OutputVariables?.FirstOrDefault(it => !string.IsNullOrWhiteSpace(it))
                ?? dto.InputVariables?.FirstOrDefault(it => !string.IsNullOrWhiteSpace(it))
                ?? string.Empty;
        }

        private static List<long> ParseLogicUnitIds(string logicUnitIds)
        {
            if (string.IsNullOrWhiteSpace(logicUnitIds))
            {
                return new List<long>();
            }

            return logicUnitIds
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(it => long.TryParse(it.Trim(), out var id) ? id : 0)
                .Where(it => it > 0)
                .Distinct()
                .ToList();
        }

        private static List<PLC_LogicUnitAstEntity> BuildAstRows(
            long projectId,
            IReadOnlyList<StopCauseAstCompileResult> compileResults,
            IReadOnlyDictionary<long, PLC_LogicUnitEntity> logicUnitMap,
            IReadOnlyDictionary<long, PLC_BlockEntity> blockMap)
        {
            return compileResults
                .Select(result => BuildAstRow(projectId, result, logicUnitMap, blockMap))
                .Where(it => it != null)
                .GroupBy(it => it.LogicUnitId.Value)
                .Select(g => g.First())
                .ToList();
        }

        private static PLC_LogicUnitAstEntity BuildAstRow(
            long projectId,
            StopCauseAstCompileResult result,
            IReadOnlyDictionary<long, PLC_LogicUnitEntity> logicUnitMap,
            IReadOnlyDictionary<long, PLC_BlockEntity> blockMap)
        {
            var logicUnitIdOrNull = result?.CoverageReports?
                .Where(it => it.LogicUnitId.HasValue)
                .Select(it => it.LogicUnitId.Value)
                .FirstOrDefault();

            if (!logicUnitIdOrNull.HasValue || logicUnitIdOrNull.Value <= 0)
            {
                return null;
            }

            var logicUnitId = logicUnitIdOrNull.Value;
            if (!logicUnitMap.TryGetValue(logicUnitId, out var logicUnit))
            {
                return null;
            }

            PLC_BlockEntity block = null;
            if (logicUnit.BlockId.HasValue)
            {
                blockMap.TryGetValue(logicUnit.BlockId.Value, out block);
            }

            var inputVariables = result.CoverageReports?
                .SelectMany(it => it.InputVariables ?? new List<string>())
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(60)
                .ToList() ?? new List<string>();

            var outputVariables = result.CoverageReports?
                .SelectMany(it => it.OutputVariables ?? new List<string>())
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(40)
                .ToList() ?? new List<string>();

            var rawContent = logicUnit.RawContent ?? string.Empty;
            var sourceHash = ComputeHash($"{CurrentAstVersion}|{logicUnit.Id}|{logicUnit.UnitType}|{result.CompilerName}|{rawContent}");

            return new PLC_LogicUnitAstEntity
            {
                ProjectId = projectId,
                StationCode = result.Anchor?.StationCode ?? block?.StationCode,
                BlockId = logicUnit.BlockId,
                BlockName = block?.BlockName ?? result.CoverageReports?.FirstOrDefault()?.BlockName,
                LogicUnitId = logicUnit.Id,
                UnitType = logicUnit.UnitType,
                NetworkId = logicUnit.NetworkId,
                Title = logicUnit.Title,
                Comment = logicUnit.Comment,
                ProgrammingLanguage = logicUnit.ProgrammingLanguage,
                ReadVarsCount = logicUnit.ReadVarsCount,
                WriteVarsCount = logicUnit.WriteVarsCount,
                CallsCount = logicUnit.CallsCount,
                CompilerName = result.CompilerName,
                CompileStatus = result.CompileStatus,
                AstVersion = CurrentAstVersion,
                SourceHash = sourceHash,
                RootNodeJson = Serialize(result.RootNode),
                CoverageReportsJson = Serialize(result.CoverageReports),
                MessagesJson = Serialize(result.Messages),
                InputVariablesJson = Serialize(inputVariables),
                OutputVariablesJson = Serialize(outputVariables),
                RawContentExcerpt = result.CoverageReports?.FirstOrDefault()?.RawContentExcerpt ?? logicUnit.RawContent,
                CreateTime = DateTime.Now,
                ModifyTime = DateTime.Now
            };
        }

        private static List<PLC_LogicUnitSemanticFindingEntity> BuildSemanticRows(
            long projectId,
            IReadOnlyList<AstSemanticFinding> semanticFindings,
            IReadOnlyDictionary<long, PLC_LogicUnitEntity> logicUnitMap,
            IReadOnlyDictionary<long, PLC_BlockEntity> blockMap,
            IReadOnlyDictionary<long, string> sourceHashByLogicUnit)
        {
            return semanticFindings
                .Where(it => it.LogicUnitId > 0 && logicUnitMap.ContainsKey(it.LogicUnitId))
                .Select(it =>
                {
                    var logicUnit = logicUnitMap[it.LogicUnitId];
                    PLC_BlockEntity block = null;
                    if (logicUnit.BlockId.HasValue)
                    {
                        blockMap.TryGetValue(logicUnit.BlockId.Value, out block);
                    }

                    return new PLC_LogicUnitSemanticFindingEntity
                    {
                        ProjectId = projectId,
                        StationCode = it.StationCode ?? block?.StationCode,
                        BlockId = it.BlockId ?? logicUnit.BlockId,
                        BlockName = it.BlockName ?? block?.BlockName,
                        LogicUnitId = it.LogicUnitId,
                        VariableName = it.VariableName,
                        Category = it.Category,
                        SemanticLabel = it.SemanticLabel,
                        Reason = it.Reason,
                        Confidence = it.Confidence,
                        RelatedIoTagsJson = Serialize(it.RelatedIOTags),
                        IoMapping = it.IoMapping,
                        AstVersion = CurrentAstVersion,
                        SourceHash = sourceHashByLogicUnit.TryGetValue(it.LogicUnitId, out var sourceHash) ? sourceHash : string.Empty,
                        CreateTime = DateTime.Now,
                        ModifyTime = DateTime.Now
                    };
                })
                .ToList();
        }

        private static string Serialize<T>(T value)
        {
            return value == null ? string.Empty : JsonSerializer.Serialize(value);
        }

        private static List<string> DeserializeStringList(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<string>();
            }

            try
            {
                return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        private static string ComputeHash(string text)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty));
            return Convert.ToHexString(bytes);
        }
    }
}
