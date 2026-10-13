using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Threading.Tasks;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.Agent.Services.Plc_diagnosis
{
    /// <summary>
    /// PLC 故障诊断服务
    /// 
    /// 提供 9 个诊断工具，供 SkillExecutionEngine 通过 Function Call 调用：
    /// 1. GetMachineStatusAsync          - 获取设备实时状态（IoT 预留）
    /// 2. FindLogicForVariableAsync      - 跨块变量交叉引用查询（基于 VariableAccess 表）
    /// 3. GetNetworkWithValuesAsync      - 获取带实时值的 Network 上下文（静态部分已实现，IoT 预留）
    /// 4. GetVariableHistoryAsync        - 获取变量跳变历史（IoT 预留）
    /// 5. GetIOTagsForAnalysisAsync      - 获取 Bool IOTag 信号列表概览（轻量）
    /// 5b.GetSignalLogicContextAsync     - 获取单个信号的深度逻辑上下文 + 共现信号
    /// 6. SaveIOConstraintsAsync         - 将 AI 推理出的约束规则写入数据库
    /// 7. ValidateIOSignalsAsync         - 用约束规则校验信号组合（支持 N 元约束）
    /// 8. GetIORelationshipAsync         - 查询指定信号的约束关系图
    /// </summary>
    public class PlcDiagnosisService : ISelfScopedAutoInject
    {
        public sealed class SignalLogicContextProjectCache
        {
            public long ProjectId { get; init; }
            public required List<PLC_BlockEntity> ProjectBlocks { get; init; }
            public required Dictionary<long, string> ProjectBlockDict { get; init; }
            public required List<PLC_IOTagEntity> ProjectIOTags { get; init; }
            public required Dictionary<string, PLC_IOTagEntity> ProjectIOTagDict { get; init; }
            public required HashSet<string> ProjectIOTagNames { get; init; }
            public required List<PLC_VariableAccessEntity> VariableAccesses { get; init; }
            public required List<PLC_LogicUnitEntity> LogicUnits { get; init; }
            public required Dictionary<long, PLC_LogicUnitEntity> LogicUnitDict { get; init; }
            public required List<PLC_BlockCallEntity> BlockCalls { get; init; }
            /// <summary>key = logic_unit_id，value = AST 实体（root_node_json）</summary>
            public Dictionary<long, PLC_LogicUnitAstEntity> LogicUnitAstDict { get; init; } = new();
        }

        private readonly ILogger<PlcDiagnosisService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly IPLC_ProjectService _projectService;
        private readonly IPLC_BlockService _blockService;
        private readonly IPLC_LogicUnitService _logicUnitService;
        private readonly IPLC_VariableAccessService _variableAccessService;
        private readonly IPLC_IOTagService _ioTagService;
        private readonly IPLC_BlockInterfaceService _blockInterfaceService;
        private readonly IPLC_BlockCallService _blockCallService;
        private readonly IPLC_IOConstraintRuleService _ioConstraintRuleService;
        private readonly IPLC_IOConstraintSignalService _ioConstraintSignalService;
        private readonly LLMModelService _llmModelService;
        private readonly IAiModelPurposeResolver _purposeResolver;

        // ===== IoT 预留接口 =====
        // 后续对接 IoT 平台时，取消注释并注入实现
        // private readonly IIoTDataService _iotService;

        public PlcDiagnosisService(
            ILogger<PlcDiagnosisService> logger,
            IServiceProvider serviceProvider,
            IPLC_ProjectService projectService,
            IPLC_BlockService blockService,
            IPLC_LogicUnitService logicUnitService,
            IPLC_VariableAccessService variableAccessService,
            IPLC_IOTagService ioTagService,
            IPLC_BlockInterfaceService blockInterfaceService,
            IPLC_BlockCallService blockCallService,
            IPLC_IOConstraintRuleService ioConstraintRuleService,
            IPLC_IOConstraintSignalService ioConstraintSignalService,
            LLMModelService llmModelService,
            IAiModelPurposeResolver purposeResolver)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _projectService = projectService;
            _blockService = blockService;
            _logicUnitService = logicUnitService;
            _variableAccessService = variableAccessService;
            _ioTagService = ioTagService;
            _blockInterfaceService = blockInterfaceService;
            _blockCallService = blockCallService;
            _ioConstraintRuleService = ioConstraintRuleService;
            _ioConstraintSignalService = ioConstraintSignalService;
            _llmModelService = llmModelService;
            _purposeResolver = purposeResolver;
        }

        #region ========== 工具 1：设备状态感知 (IoT 预留) ==========

        /// <summary>
        /// [工具] 获取设备当前整体状态
        /// 
        /// 当前逻辑：
        /// - 从 PLC 数据库拉取项目/工位基础信息、入口块、步序变量、报警变量
        /// - IoT 实时数据（步序、报警、信号值）暂用占位
        /// 
        /// 后续对接 IoT 后：
        /// - 调用 IoT API 获取当前步序号、报警列表、关键信号实时值
        /// - 结合 BlockInterface 中的 OffsetByte/OffsetBit 通过 S7 协议直读 DB 值
        /// </summary>
        /// <param name="stationId">工位标识，如 "OP70"（可选，默认查最新项目）</param>
        public async Task<object> GetMachineStatusAsync(string stationId = "")
        {
            _logger.LogInformation("[Diagnosis] GetMachineStatus: stationId={StationId}", stationId);

            try
            {
                // 1. 查找匹配的项目
                PLC_ProjectEntity? project = null;
                if (!string.IsNullOrEmpty(stationId))
                {
                    project = await _projectService.GetFirstAsync(
                        x => (x.StationId == stationId || x.ProjectName!.Contains(stationId)) && !x.IsDeleted);
                }
                project ??= await _projectService.GetFirstAsync(
                    x => !x.IsDeleted, x => x.CreateTime, SqlSugar.OrderByType.Desc);

                if (project == null)
                {
                    return new { Success = false, Message = "未找到任何 PLC 项目数据，请先上传 PLC 项目文件。" };
                }

                // 2. 获取项目下所有块（概览）
                var blocks = await _blockService.GetListAsync(
                    x => x.ProjectId == project.Id && !x.IsDeleted);

                var entryBlocks = blocks.Where(b => b.IsPotentialEntry == true).Select(b => new
                {
                    b.BlockName,
                    b.BlockType,
                    b.ProgrammingLanguage,
                    b.NetworksCount
                }).ToList();

                // 3. 查找关键步序变量
                var stepVars = await _variableAccessService.GetListAsync(
                    x => x.ProjectId == project.Id && x.IsStepVariable == true && !x.IsDeleted);

                var uniqueStepVars = stepVars
                    .Where(v => !string.IsNullOrEmpty(v.VariableName))
                    .GroupBy(v => v.VariableName!)
                    .Select(g => new { VariableName = g.Key, Comment = g.First().Comment })
                    .Take(20).ToList();

                // 4. 查找报警变量
                var alarmVars = await _variableAccessService.GetListAsync(
                    x => x.ProjectId == project.Id && x.IsAlarmVariable == true && !x.IsDeleted);

                var uniqueAlarmVars = alarmVars
                    .Where(v => !string.IsNullOrEmpty(v.VariableName))
                    .GroupBy(v => v.VariableName!)
                    .Select(g => new { VariableName = g.Key, Comment = g.First().Comment })
                    .Take(20).ToList();

                // ===== IoT 预留区域 =====
                // TODO: 对接 IoT API 后替换以下占位数据
                // var currentStep = await _iotService.ReadVariableAsync("DB_StepNum_Var");
                // var activeAlarms = await _iotService.GetActiveAlarmsAsync(stationId);
                // var keySignals = await _iotService.ReadMultipleAsync(criticalSignalNames);
                var iotPlaceholder = new
                {
                    Note = "IoT 接口尚未对接，以下为占位。对接后返回真实实时值。",
                    CurrentStep = (int?)null,
                    ActiveAlarms = new string[] { },
                    KeySignals = new Dictionary<string, object>()
                };
                // ===== IoT 预留区域结束 =====

                return new
                {
                    Success = true,
                    Project = new
                    {
                        project.Id,
                        project.ProjectName,
                        project.Brand,
                        project.Product,
                        project.StationId
                    },
                    Summary = new
                    {
                        TotalBlocks = blocks.Count,
                        BlockTypeDistribution = blocks.GroupBy(b => b.BlockType ?? "Unknown")
                            .ToDictionary(g => g.Key, g => g.Count()),
                        EntryBlocks = entryBlocks,
                        StepVariables = uniqueStepVars,
                        AlarmVariables = uniqueAlarmVars
                    },
                    IoTStatus = iotPlaceholder,
                    Hint = "请根据以上信息确定诊断起点。如果有步序卡顿，使用 FindLogicForVariableAsync 查找该步序变量的 WRITE 逻辑。"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Diagnosis] GetMachineStatus 异常");
                return new { Success = false, Message = $"获取设备状态失败: {ex.Message}" };
            }
        }

        private async Task<long> InferProjectIdForSignalAsync(string signalName)
        {
            if (string.IsNullOrWhiteSpace(signalName))
            {
                return 0;
            }

            var ioTag = (await _ioTagService.GetListAsync(x => x.TagName == signalName && !x.IsDeleted))
                .FirstOrDefault();
            if (ioTag?.ProjectId != null)
            {
                return ioTag.ProjectId.Value;
            }

            var fuzzyIoTag = (await _ioTagService.GetListAsync(x => x.TagName!.Contains(signalName) && !x.IsDeleted))
                .FirstOrDefault();
            if (fuzzyIoTag?.ProjectId != null)
            {
                return fuzzyIoTag.ProjectId.Value;
            }

            var rules = await _ioConstraintRuleService.GetListAsync(x => !x.IsDeleted);
            var signals = await _ioConstraintSignalService.GetListAsync(x => x.SignalName == signalName && !x.IsDeleted);
            var matchedRule = rules.FirstOrDefault(r => signals.Any(s => s.RuleId == r.Id));
            return matchedRule?.ProjectId ?? 0;
        }

        private async Task<List<RelationshipConstraintDto>> LoadLegacyRelationshipConstraintsAsync(string signalName)
        {
            var matchedSignals = await _ioConstraintSignalService.GetListAsync(
                x => x.SignalName.Contains(signalName) && !x.IsDeleted);
            var ruleIds = matchedSignals.Select(s => s.RuleId).Distinct().ToList();

            var rules = ruleIds.Count > 0
                ? await _ioConstraintRuleService.GetListByIdsAsync(ruleIds)
                : new List<PLC_IOConstraintRuleEntity>();
            rules = rules.Where(r => !r.IsDeleted).ToList();
            ruleIds = rules.Select(r => r.Id).ToList();

            var allRuleSignals = ruleIds.Count > 0
                ? await _ioConstraintSignalService.GetListAsync(
                    x => ruleIds.Contains(x.RuleId) && !x.IsDeleted)
                : new List<PLC_IOConstraintSignalEntity>();

            return rules.Select(r =>
            {
                var signals = allRuleSignals.Where(s => s.RuleId == r.Id).ToList();
                return new RelationshipConstraintDto
                {
                    RuleName = r.RuleName,
                    RuleType = r.RuleType,
                    Operator = r.Operator,
                    Priority = r.Priority,
                    Description = r.Description,
                    Confidence = Convert.ToDouble(r.Confidence ?? 0m),
                    EquipmentId = r.EquipmentId,
                    Signals = signals.Select(s => new RelationshipConstraintSignalDto
                    {
                        SignalName = s.SignalName,
                        SignalAddress = s.SignalAddress,
                        SignalRole = s.SignalRole,
                        SortOrder = s.SortOrder ?? 0
                    }).ToList()
                };
            }).ToList();
        }

        private static List<RelationshipConstraintDto> DeserializeRelationshipConstraints(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<RelationshipConstraintDto>();
            }

            try
            {
                return JsonSerializer.Deserialize<List<RelationshipConstraintDto>>(
                           json,
                           new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                       ?? new List<RelationshipConstraintDto>();
            }
            catch
            {
                return new List<RelationshipConstraintDto>();
            }
        }

        private static List<RelationshipConstraintDto> FilterRelationshipConstraintsByStation(
            List<RelationshipConstraintDto> constraints,
            string stationCode)
        {
            if (constraints == null || constraints.Count == 0 || string.IsNullOrWhiteSpace(stationCode))
            {
                return constraints ?? new List<RelationshipConstraintDto>();
            }

            var normalizedStationCode = stationCode.Trim().ToUpperInvariant();
            var filtered = constraints
                .Where(c =>
                    (!string.IsNullOrWhiteSpace(c.EquipmentId) && c.EquipmentId.Contains(normalizedStationCode, StringComparison.OrdinalIgnoreCase)) ||
                    c.Signals.Any(s => !string.IsNullOrWhiteSpace(s.SignalName) && s.SignalName.Contains(normalizedStationCode, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            return filtered.Count > 0 ? filtered : constraints;
        }

        private static List<PLC_IOConstraintRuleEntity> FilterConstraintRulesByStation(
            List<PLC_IOConstraintRuleEntity> rules,
            string stationCode)
        {
            if (rules == null || rules.Count == 0 || string.IsNullOrWhiteSpace(stationCode))
            {
                return rules ?? new List<PLC_IOConstraintRuleEntity>();
            }

            var normalizedStationCode = stationCode.Trim().ToUpperInvariant();
            var filtered = rules
                .Where(r =>
                    (!string.IsNullOrWhiteSpace(r.EquipmentId) && r.EquipmentId.Contains(normalizedStationCode, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(r.RuleName) && r.RuleName.Contains(normalizedStationCode, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(r.Description) && r.Description.Contains(normalizedStationCode, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            return filtered.Count > 0 ? filtered : rules;
        }

        private static List<PLC_IOConstraintRuleEntity> FilterConstraintRulesByInstanceAnchors(
            List<PLC_IOConstraintRuleEntity> rules,
            Dictionary<long, List<PLC_IOConstraintSignalEntity>> signalsByRule,
            Dictionary<string, bool> values,
            string stationCode = "")
        {
            if (rules == null || rules.Count == 0 || values == null || values.Count == 0)
            {
                return rules ?? new List<PLC_IOConstraintRuleEntity>();
            }

            var anchors = ExtractInstanceAnchors(values.Keys, stationCode);
            if (anchors.Count == 0)
            {
                return rules;
            }

            var filtered = rules
                .Where(rule =>
                {
                    if (!signalsByRule.TryGetValue(rule.Id, out var ruleSignals) || ruleSignals.Count == 0)
                    {
                        return false;
                    }

                    return RuleMatchesAnyAnchor(rule, ruleSignals, anchors);
                })
                .ToList();

            return filtered.Count > 0 ? filtered : rules;
        }

        private static List<PLC_IOConstraintRuleEntity> FilterConstraintRulesByObservedSignals(
            List<PLC_IOConstraintRuleEntity> rules,
            Dictionary<long, List<PLC_IOConstraintSignalEntity>> signalsByRule,
            Dictionary<string, bool> values)
        {
            if (rules == null || rules.Count == 0 || values == null || values.Count == 0)
            {
                return rules ?? new List<PLC_IOConstraintRuleEntity>();
            }

            var observedSignals = values.Keys
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (observedSignals.Count == 0)
            {
                return rules;
            }

            var filtered = rules
                .Where(rule =>
                {
                    if (!signalsByRule.TryGetValue(rule.Id, out var ruleSignals) || ruleSignals.Count == 0)
                    {
                        return false;
                    }

                    return ruleSignals.Any(signal =>
                        observedSignals.Any(observed => SignalNamesOverlap(observed, signal.SignalName)));
                })
                .ToList();

            return filtered.Count > 0 ? filtered : rules;
        }

        private static HashSet<string> ExtractInstanceAnchors(IEnumerable<string> signalNames, string stationCode = "")
        {
            var anchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (signalNames == null)
            {
                return anchors;
            }

            foreach (var signalName in signalNames)
            {
                if (string.IsNullOrWhiteSpace(signalName))
                {
                    continue;
                }

                foreach (Match match in Regex.Matches(signalName, @"OP\d{3}_CYL\d+", RegexOptions.IgnoreCase))
                {
                    anchors.Add(match.Value.ToUpperInvariant());
                }

                foreach (Match match in Regex.Matches(signalName, @"OP\d{3}_YV\d+", RegexOptions.IgnoreCase))
                {
                    anchors.Add(match.Value.ToUpperInvariant());
                }

                foreach (Match match in Regex.Matches(signalName, @"Valve\[\d+\]", RegexOptions.IgnoreCase))
                {
                    anchors.Add(match.Value);
                }
            }

            ExpandInstanceAnchors(anchors, stationCode);
            return anchors;
        }

        private static void ExpandInstanceAnchors(HashSet<string> anchors, string stationCode)
        {
            if (anchors == null || anchors.Count == 0)
            {
                return;
            }

            var normalizedStationCode = string.IsNullOrWhiteSpace(stationCode)
                ? null
                : stationCode.Trim().ToUpperInvariant();
            var snapshot = anchors.ToList();

            foreach (var anchor in snapshot)
            {
                var cylMatch = Regex.Match(anchor, @"^(?<station>OP\d{3})_CYL(?<index>\d+)$", RegexOptions.IgnoreCase);
                if (cylMatch.Success)
                {
                    var station = cylMatch.Groups["station"].Value.ToUpperInvariant();
                    var index = cylMatch.Groups["index"].Value;
                    anchors.Add($"{station}_YV{index}");
                    if (int.TryParse(index, out var valveIndex))
                    {
                        anchors.Add($"Valve[{valveIndex}]");
                    }
                    continue;
                }

                var yvMatch = Regex.Match(anchor, @"^(?<station>OP\d{3})_YV(?<index>\d+)$", RegexOptions.IgnoreCase);
                if (yvMatch.Success)
                {
                    var station = yvMatch.Groups["station"].Value.ToUpperInvariant();
                    var index = yvMatch.Groups["index"].Value;
                    anchors.Add($"{station}_CYL{index}");
                    if (int.TryParse(index, out var valveIndex))
                    {
                        anchors.Add($"Valve[{valveIndex}]");
                    }
                    continue;
                }

                var valveMatch = Regex.Match(anchor, @"^Valve\[(?<index>\d+)\]$", RegexOptions.IgnoreCase);
                if (valveMatch.Success && !string.IsNullOrWhiteSpace(normalizedStationCode))
                {
                    var index = valveMatch.Groups["index"].Value;
                    anchors.Add($"{normalizedStationCode}_CYL{index}");
                    anchors.Add($"{normalizedStationCode}_YV{index}");
                }
            }
        }

        private static bool RuleMatchesAnyAnchor(
            PLC_IOConstraintRuleEntity rule,
            List<PLC_IOConstraintSignalEntity> signals,
            HashSet<string> anchors)
        {
            if (anchors == null || anchors.Count == 0)
            {
                return true;
            }

            foreach (var anchor in anchors)
            {
                if ((!string.IsNullOrWhiteSpace(rule.RuleName) && rule.RuleName.Contains(anchor, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(rule.Description) && rule.Description.Contains(anchor, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(rule.EquipmentId) && rule.EquipmentId.Contains(anchor, StringComparison.OrdinalIgnoreCase)) ||
                    signals.Any(s => !string.IsNullOrWhiteSpace(s.SignalName) && s.SignalName.Contains(anchor, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool SignalNamesOverlap(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
                || left.Contains(right, StringComparison.OrdinalIgnoreCase)
                || right.Contains(left, StringComparison.OrdinalIgnoreCase);
        }

        private static string? ExtractStationCode(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var match = Regex.Match(text, @"OP\d{3}", RegexOptions.IgnoreCase);
            return match.Success ? match.Value.ToUpperInvariant() : null;
        }

        private sealed class RelationshipConstraintDto
        {
            public string RuleName { get; set; } = string.Empty;
            public string RuleType { get; set; } = string.Empty;
            public string Operator { get; set; } = string.Empty;
            public string Priority { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public double Confidence { get; set; }
            public string EquipmentId { get; set; } = string.Empty;
            public List<RelationshipConstraintSignalDto> Signals { get; set; } = new();
        }

        private sealed class RelationshipConstraintSignalDto
        {
            public string SignalName { get; set; } = string.Empty;
            public string SignalAddress { get; set; } = string.Empty;
            public string SignalRole { get; set; } = string.Empty;
            public int SortOrder { get; set; }
        }

        #endregion

        #region ========== 工具 2：变量交叉引用查询 ==========

        /// <summary>
        /// [工具] 跨块搜索变量的读/写逻辑位置（Cross-Reference）
        /// 
        /// 从 VariableAccess 表查询变量被读取或写入的所有位置，
        /// 返回 Block 名称、Network ID、LogicUnit 标题等。
        /// 
        /// 典型用法：
        /// - FindLogicForVariableAsync("OP070_CYL01_W", "WRITE") → 谁控制了这个气缸
        /// - FindLogicForVariableAsync("Step", "READ") → 谁在读取步序号
        /// </summary>
        /// <param name="variableName">变量名（支持模糊匹配）</param>
        /// <param name="type">查找类型: "WRITE"/"READ"/"ALL"</param>
        public async Task<object> FindLogicForVariableAsync(string variableName, string type = "WRITE")
        {
            _logger.LogInformation("[Diagnosis] FindLogicForVariable: var={Var}, type={Type}", variableName, type);

            if (string.IsNullOrWhiteSpace(variableName))
            {
                return new { Success = false, Message = "变量名不能为空" };
            }

            try
            {
                // 1. 查询 VariableAccess 表
                var accessType = type?.ToUpper();
                List<PLC_VariableAccessEntity> accesses;

                if (accessType == "ALL" || string.IsNullOrEmpty(accessType))
                {
                    accesses = await _variableAccessService.GetListAsync(
                        x => x.VariableName!.Contains(variableName) && !x.IsDeleted);
                }
                else
                {
                    accesses = await _variableAccessService.GetListAsync(
                        x => x.VariableName!.Contains(variableName)
                            && x.AccessType == accessType
                            && !x.IsDeleted);
                }

                // 精确搜索无结果，尝试最后一段名称
                if (accesses.Count == 0)
                {
                    var lastSegment = variableName.Split('.').Last();
                    if (lastSegment != variableName)
                    {
                        if (accessType == "ALL" || string.IsNullOrEmpty(accessType))
                        {
                            accesses = await _variableAccessService.GetListAsync(
                                x => x.VariableName!.Contains(lastSegment) && !x.IsDeleted);
                        }
                        else
                        {
                            accesses = await _variableAccessService.GetListAsync(
                                x => x.VariableName!.Contains(lastSegment)
                                    && x.AccessType == accessType
                                    && !x.IsDeleted);
                        }
                    }
                }

                if (accesses.Count == 0)
                {
                    return new
                    {
                        Success = true,
                        VariableName = variableName,
                        FoundType = type,
                        ResultCount = 0,
                        Message = $"未找到变量 '{variableName}' 的 {type} 引用记录。",
                        Hint = "提示: 可尝试只搜索变量名最后一段，或用 'ALL' 类型搜索全部读写引用。"
                    };
                }

                // 2. 批量获取关联 Block 和 LogicUnit
                var blockIds = accesses.Where(a => a.BlockId.HasValue).Select(a => a.BlockId!.Value).Distinct().ToList();
                var luIds = accesses.Where(a => a.LogicUnitId.HasValue).Select(a => a.LogicUnitId!.Value).Distinct().ToList();

                var blocks = blockIds.Count > 0
                    ? await _blockService.GetListByIdsAsync(blockIds)
                    : new List<PLC_BlockEntity>();
                var logicUnits = luIds.Count > 0
                    ? await _logicUnitService.GetListByIdsAsync(luIds)
                    : new List<PLC_LogicUnitEntity>();

                var blockDict = blocks.ToDictionary(b => b.Id);
                var luDict = logicUnits.ToDictionary(lu => lu.Id);

                // 3. 组装明细结果
                var results = accesses.Select(a =>
                {
                    PLC_BlockEntity? block = a.BlockId.HasValue && blockDict.ContainsKey(a.BlockId.Value)
                        ? blockDict[a.BlockId.Value] : null;
                    PLC_LogicUnitEntity? lu = a.LogicUnitId.HasValue && luDict.ContainsKey(a.LogicUnitId.Value)
                        ? luDict[a.LogicUnitId.Value] : null;

                    return new
                    {
                        a.VariableName,
                        a.AccessType,
                        a.Comment,
                        a.Uid,
                        a.VariableScope,
                        IsIO = a.IsIOTag,
                        BlockName = block?.BlockName,
                        BlockType = block?.BlockType,
                        BlockLanguage = block?.ProgrammingLanguage,
                        NetworkId = lu?.NetworkId,
                        NetworkTitle = lu?.Title,
                        LogicUnitId = a.LogicUnitId,
                        LogicUnitType = lu?.UnitType
                    };
                })
                .OrderBy(r => r.BlockName)
                .ThenBy(r => r.NetworkId)
                .ToList();

                // 4. 按块分组摘要
                var groupedByBlock = results
                    .GroupBy(r => r.BlockName ?? "Unknown")
                    .Select(g => new
                    {
                        Block = g.Key,
                        BlockType = g.First().BlockType,
                        Networks = g.Select(r => new
                        {
                            r.NetworkId,
                            r.NetworkTitle,
                            r.AccessType,
                            r.LogicUnitId
                        }).Distinct().ToList()
                    }).ToList();

                return new
                {
                    Success = true,
                    VariableName = variableName,
                    FoundType = type,
                    ResultCount = results.Count,
                    Results = results,
                    GroupedByBlock = groupedByBlock,
                    Hint = "找到引用后，使用 GetNetworkWithValuesAsync(logicUnitId) 获取该 Network 的完整逻辑和信号值。注意：参数是 LogicUnitId（长整型），不是 NetworkId。"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Diagnosis] FindLogicForVariable 异常");
                return new { Success = false, Message = $"查找变量引用失败: {ex.Message}" };
            }
        }

        #endregion

        #region ========== 工具 3：带实时值的 Network 上下文 ==========

        /// <summary>
        /// [工具] 获取 Network 的完整诊断上下文
        /// 
        /// 返回：
        /// - RawLogicXml:      原始 XML 逻辑代码
        /// - UidSymbolMap:     UID -> 变量名映射表
        /// - VariableDetails:  该 Network 所有变量详情（注释、IO 地址等）
        /// - VariableComments: 变量名 -> 注释字典
        /// - BlockCalls:       该 Network 中的块调用列表
        /// - RealtimeValues:   IoT 实时值（预留）
        /// </summary>
        /// <param name="logicUnitId">逻辑单元 ID（从 FindLogicForVariableAsync 返回的 LogicUnitId）</param>
        public async Task<object> GetNetworkWithValuesAsync(long logicUnitId)
        {
            _logger.LogInformation("[Diagnosis] GetNetworkWithValues: logicUnitId={Id}", logicUnitId);

            try
            {
                // 1. 获取 LogicUnit
                var lu = await _logicUnitService.GetByIdAsync(logicUnitId);
                if (lu == null)
                {
                    return new { Success = false, Message = $"未找到 LogicUnit (ID: {logicUnitId})。请确认参数是 LogicUnitId 而非 NetworkId。" };
                }

                // 2. 获取所属 Block
                PLC_BlockEntity? block = null;
                if (lu.BlockId.HasValue)
                    block = await _blockService.GetByIdAsync(lu.BlockId.Value);

                // 3. 该 Network 的所有变量访问记录
                var varAccesses = await _variableAccessService.GetListAsync(
                    x => x.LogicUnitId == logicUnitId && !x.IsDeleted);

                // 4. 解析 uid_symbol_map
                Dictionary<string, string>? uidSymbolMap = null;
                if (!string.IsNullOrEmpty(lu.UidSymbolMapJson))
                {
                    try
                    {
                        uidSymbolMap = JsonSerializer.Deserialize<Dictionary<string, string>>(lu.UidSymbolMapJson);
                    }
                    catch
                    {
                        _logger.LogWarning("[Diagnosis] UidSymbolMapJson 解析失败, LogicUnitId={Id}", logicUnitId);
                    }
                }

                // 5. 收集所有变量名
                var allVarNames = varAccesses
                    .Where(v => !string.IsNullOrEmpty(v.VariableName))
                    .Select(v => v.VariableName!)
                    .Distinct()
                    .ToList();

                // 6. 查 IOTag 表补全物理地址和注释
                var ioTags = new List<PLC_IOTagEntity>();
                long? projectId = block?.ProjectId;
                if (projectId.HasValue)
                {
                    ioTags = await _ioTagService.GetListAsync(
                        x => x.ProjectId == projectId.Value && !x.IsDeleted);
                }
                var ioTagDict = ioTags.ToDictionary(t => t.TagName ?? "", t => t, StringComparer.OrdinalIgnoreCase);

                // 7. 合并 VariableAccess + IOTag
                var variableDetails = varAccesses.Select(va =>
                {
                    PLC_IOTagEntity? ioTag = va.VariableName != null && ioTagDict.ContainsKey(va.VariableName)
                        ? ioTagDict[va.VariableName] : null;

                    return new
                    {
                        va.VariableName,
                        va.AccessType,
                        va.Uid,
                        Comment = va.Comment ?? ioTag?.Comment,
                        PhysicalAddress = ioTag?.Address,
                        DataType = ioTag?.DataType,
                        va.VariableScope,
                        IsIO = va.IsIOTag ?? (ioTag != null),
                        TagCategory = ioTag?.TagCategory
                    };
                }).ToList();

                // 8. 注释字典
                var commentDict = variableDetails
                    .Where(v => !string.IsNullOrEmpty(v.Comment) && !string.IsNullOrEmpty(v.VariableName))
                    .GroupBy(v => v.VariableName!)
                    .ToDictionary(
                        g => g.Key,
                        g => g.First().Comment + (g.First().PhysicalAddress != null ? $" ({g.First().PhysicalAddress})" : ""));

                // 9. 块调用信息
                var blockCalls = await _blockCallService.GetListAsync(
                    x => x.LogicUnitId == logicUnitId && !x.IsDeleted);

                // ===== IoT 预留区域 =====
                // TODO: 对接 IoT API 后：
                // var realtimeValues = await _iotService.ReadMultipleAsync(allVarNames);
                //
                // 也可用 S7 直读 DB：
                // var dbIfaces = await _blockInterfaceService.GetListAsync(
                //     x => x.BlockId == block.Id && x.OffsetByte != null && !x.IsDeleted);
                // foreach (var iface in dbIfaces)
                //     realtimeValues[iface.VariableName] = await _iotService.ReadS7DBAsync(dbNum, iface.OffsetByte.Value, iface.SizeBits.Value);
                var realtimeValues = new Dictionary<string, object>();
                var iotNote = "IoT 接口尚未对接。对接后此处返回每个变量的实时值（True/False/数值），用于能量流分析。";
                // ===== IoT 预留区域结束 =====

                return new
                {
                    Success = true,
                    NetworkContext = new
                    {
                        LogicUnitId = lu.Id,
                        lu.NetworkId,
                        lu.UnitType,
                        lu.Title,
                        lu.Comment,
                        lu.ProgrammingLanguage,
                        BlockName = block?.BlockName,
                        BlockType = block?.BlockType
                    },
                    RawLogicXml = lu.RawContent,
                    ParsedLogicDescription = lu.ParsedLogicDescription,
                    UidSymbolMap = uidSymbolMap,
                    VariableDetails = variableDetails,
                    VariableComments = commentDict,
                    BlockCalls = blockCalls.Select(c => new
                    {
                        c.CalleeBlockName,
                        c.CalleeBlockType,
                        c.NetworkId
                    }).ToList(),
                    RealtimeValues = realtimeValues,
                    IoTNote = iotNote,
                    AnalysisHint = BuildAnalysisHint(lu)
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Diagnosis] GetNetworkWithValues 异常");
                return new { Success = false, Message = $"获取 Network 上下文失败: {ex.Message}" };
            }
        }

        /// <summary>
        /// 根据逻辑单元类型构建分析提示
        /// </summary>
        private static string BuildAnalysisHint(PLC_LogicUnitEntity lu)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== 分析提示 ===");

            if (lu.UnitType == "NETWORK")
            {
                sb.AppendLine("标准 Network（梯形图/FBD 逻辑段）。");
                sb.AppendLine("1. 解析 RawLogicXml 中的 <Wires> 连接关系");
                sb.AppendLine("2. 用 UidSymbolMap 将 UID 映射到变量名");
                sb.AppendLine("3. 用 RealtimeValues 判断哪个触点导致断路");
                sb.AppendLine("4. 串联(AND): 找第一个 False 的常开触点");
                sb.AppendLine("5. 并联(OR): 分析为何所有支路断开");
                sb.AppendLine("6. Invert=true 的触点: 信号为 1 时才阻断");
            }
            else if (lu.UnitType?.StartsWith("TRANSITION") == true)
            {
                sb.AppendLine("GRAPH 转换条件(Transition)。步序在此卡住说明条件未满足。");
                sb.AppendLine("分析所有条件变量，找出未达预期值的信号。");
            }
            else if (lu.UnitType?.Contains("SUPERVISION") == true)
            {
                sb.AppendLine("GRAPH 监控条件(Supervision)。触发通常意味着步骤执行超时或异常。");
            }
            else if (lu.UnitType?.Contains("INTERLOCK") == true)
            {
                sb.AppendLine("GRAPH 互锁条件(Interlock)。失败会阻止步骤内动作执行。");
            }

            return sb.ToString();
        }

        #endregion

        #region ========== 工具 4：变量历史查询 (IoT 预留) ==========

        /// <summary>
        /// [工具] 获取变量最近的跳变历史
        /// 
        /// 当前：返回变量在 PLC 项目中的静态定义信息（IO 地址、注释、引用位置）
        /// IoT 时序数据（跳变历史）为预留
        /// 
        /// 后续对接 IoT 后：
        /// - 调用时序数据库（QuestDB/InfluxDB）查询跳变时间序列
        /// - 返回最近 N 次状态变化的时间戳和值
        /// </summary>
        /// <param name="variableName">变量名</param>
        /// <param name="count">获取数量（默认 10）</param>
        public async Task<object> GetVariableHistoryAsync(string variableName, int count = 10)
        {
            _logger.LogInformation("[Diagnosis] GetVariableHistory: var={Var}, count={Count}", variableName, count);

            if (string.IsNullOrWhiteSpace(variableName))
            {
                return new { Success = false, Message = "变量名不能为空" };
            }

            try
            {
                // 1. 查 IOTag 表
                var ioTags = await _ioTagService.GetListAsync(
                    x => x.TagName!.Contains(variableName) && !x.IsDeleted);

                var tagInfo = ioTags.Select(t => new
                {
                    t.TagName,
                    t.Address,
                    t.DataType,
                    t.Comment,
                    t.TagCategory,
                    t.IsSafety,
                    t.EquipmentId
                }).ToList();

                // 2. 查该变量的所有逻辑引用
                var varAccesses = await _variableAccessService.GetListAsync(
                    x => x.VariableName!.Contains(variableName) && !x.IsDeleted);

                var uniqueRefs = varAccesses
                    .GroupBy(v => new { v.VariableName, v.AccessType })
                    .Select(g => new
                    {
                        g.Key.VariableName,
                        g.Key.AccessType,
                        Comment = g.First().Comment,
                        ReferenceCount = g.Count()
                    }).ToList();

                // ===== IoT 预留区域 =====
                // TODO: 对接 IoT 时序数据库
                // var history = await _iotService.GetVariableHistoryAsync(variableName, count);
                // 返回格式: [ { Time, Value, Duration }, ... ]
                var historyPlaceholder = new
                {
                    Note = "IoT 时序接口尚未对接。对接后返回变量最近的跳变历史。",
                    ExpectedFormat = "[ { Time, Value, Duration }, ... ]",
                    DataSource = "QuestDB / InfluxDB / IoT 平台 API"
                };
                // ===== IoT 预留区域结束 =====

                return new
                {
                    Success = true,
                    VariableName = variableName,
                    IOTagDefinition = tagInfo,
                    LogicReferences = uniqueRefs,
                    History = historyPlaceholder,
                    Hint = tagInfo.Count > 0
                        ? $"变量物理地址 {tagInfo.First().Address}，类型 {tagInfo.First().DataType}。对接 IoT 后可获取实时跳变历史。"
                        : "未在 IOTag 表找到物理地址，可能是内部中间变量(M/DB)。可通过 FindLogicForVariableAsync 查找逻辑上下文。"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Diagnosis] GetVariableHistory 异常");
                return new { Success = false, Message = $"获取变量历史失败: {ex.Message}" };
            }
        }

        #endregion

        #region ========== 工具 5：IO约束数据馈送（供AI推理） ==========

        /// <summary>
        /// [工具] 获取项目下所有 Bool 类型 IOTag 信号列表（轻量概览）
        /// 
        /// 用途：返回项目所有 Bool IO 信号的基础信息，供 AI 了解"有哪些信号"。
        /// 不包含逻辑片段（数据量过大）。
        /// 
        /// 预处理约束推理的推荐工作流程：
        /// 1. 调用本工具获取信号列表
        /// 2. 按 EquipmentId 对信号分组，逐设备处理
        /// 3. 对每个待分析的信号调用 GetSignalLogicContextAsync 获取逻辑上下文
        /// 4. 基于逻辑上下文推理约束关系
        /// 5. 调用 SaveIOConstraintsAsync 写入
        /// </summary>
        /// <param name="projectId">项目ID</param>
        public async Task<object> GetIOTagsForAnalysisAsync(long projectId)
        {
            _logger.LogInformation("[Diagnosis] GetIOTagsForAnalysis: projectId={ProjectId}", projectId);

            try
            {
                // 1. 获取项目下所有 IOTag
                var ioTags = await _ioTagService.GetListAsync(
                    x => x.ProjectId == projectId && !x.IsDeleted);

                if (ioTags == null || ioTags.Count == 0)
                {
                    return new { Success = false, Message = "该项目下未找到 IOTag 数据，请先上传 PLC 标签表。" };
                }

                var boolTags = ioTags.Where(t => t.DataType?.Equals("Bool", StringComparison.OrdinalIgnoreCase) == true).ToList();

                // 2. 组装信号列表（轻量，不含逻辑片段）
                var tagAnalysis = boolTags.Select(t => new
                {
                    t.TagName,
                    t.Address,
                    t.Comment,
                    t.TagCategory,
                    t.IsSafety,
                    t.EquipmentId,
                    // 根据地址前缀标识 IO 方向
                    IoDirection = GetIoDirection(t.Address)
                }).ToList();

                // 3. 按设备分组统计
                var equipmentGroups = tagAnalysis
                    .Where(t => !string.IsNullOrEmpty(t.EquipmentId))
                    .GroupBy(t => t.EquipmentId)
                    .Select(g => new { EquipmentId = g.Key, SignalCount = g.Count() })
                    .OrderByDescending(g => g.SignalCount)
                    .Take(50)
                    .ToList();

                // 4. 统计已有约束规则数
                var existingRuleCount = (await _ioConstraintRuleService.GetListAsync(
                    x => x.ProjectId == projectId && !x.IsDeleted)).Count;

                return new
                {
                    Success = true,
                    ProjectId = projectId,
                    TotalIOTags = ioTags.Count,
                    BoolTags = boolTags.Count,
                    ExistingConstraintRules = existingRuleCount,
                    EquipmentGroups = equipmentGroups,
                    Tags = tagAnalysis,
                    Hint = $"共 {boolTags.Count} 个 Bool 信号，已有 {existingRuleCount} 条约束规则。\n" +
                           "请逐设备/逐信号调用 GetSignalLogicContextAsync(signalName, projectId) 获取逻辑上下文后推理约束。\n" +
                           "I 点(Input)信号在 PLC 中被读取的 Network 是其参与逻辑的关键位置。\n" +
                           "Q 点(Output)信号在 PLC 中被写入的 Network 是其被驱动的逻辑段。"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Diagnosis] GetIOTagsForAnalysis 异常");
                return new { Success = false, Message = $"获取 IOTag 分析数据失败: {ex.Message}" };
            }
        }

        /// <summary>
        /// [工具] 获取单个信号的深度逻辑上下文，用于约束推理
        /// 
        /// 根据信号类型自动选择查找方向：
        /// - I 点（Input/传感器）→ 查找 READ 访问点（信号在哪些逻辑段被读取使用）
        /// - Q 点（Output/执行器）→ 查找 WRITE 访问点（信号在哪些逻辑段被驱动赋值）
        /// - 其他/无法判断 → 查找所有访问点
        /// 
        /// 返回该信号参与的逻辑片段 + 同一 Network 中共现的其他 IO 信号，
        /// 这些共现信号是推理约束关系的核心线索。
        /// 
        /// RuleType 与 Operator 灵活使用：
        /// - RuleType 是物理语义标签（Mutex/ExactlyOne/AllRequired/Pairing/Safety/Sequence/Custom）
        /// - Operator 是校验逻辑（AT_MOST_ONE/EXACTLY_ONE/ALL_TRUE/IMPLIES/REQUIRES_0/CHAIN_ORDER/CUSTOM）
        /// - 默认映射：Mutex→AT_MOST_ONE, Pairing→IMPLIES, Safety→REQUIRES_0 等，但可灵活组合
        /// - CUSTOM 运算符需在 validStates 中提供合法状态组合的 JSON 真值表
        /// </summary>
        /// <param name="signalName">信号名称（如 OP070_CYL01_FwdOK）</param>
        /// <param name="projectId">项目ID</param>
        public async Task<object> GetSignalLogicContextAsync(string signalName, long projectId)
        {
            _logger.LogInformation("[Diagnosis] GetSignalLogicContext: signal={Signal}, projectId={ProjectId}", signalName, projectId);

            try
            {
                if (string.IsNullOrWhiteSpace(signalName))
                    return new { Success = false, Message = "signalName 不能为空" };

                // 1. 查找该信号的 IOTag 信息
                var projectBlocks = await QueryWithNewScopeAsync<IPLC_BlockService, List<PLC_BlockEntity>>(svc => svc.GetListAsync(
                    x => x.ProjectId == projectId && !x.IsDeleted));
                var projectBlockIds = projectBlocks.Select(b => b.Id).ToList();
                var projectBlockDict = projectBlocks.ToDictionary(b => b.Id, b => b.BlockName);

                var ioTag = (await QueryWithNewScopeAsync<IPLC_IOTagService, List<PLC_IOTagEntity>>(svc => svc.GetListAsync(
                    x => x.ProjectId == projectId && x.TagName == signalName && !x.IsDeleted)))
                    .FirstOrDefault();

                var projectIOTags = await QueryWithNewScopeAsync<IPLC_IOTagService, List<PLC_IOTagEntity>>(svc => svc.GetListAsync(
                    x => x.ProjectId == projectId && !x.IsDeleted));
                var projectIOTagDict = projectIOTags
                    .Where(t => !string.IsNullOrWhiteSpace(t.TagName))
                    .GroupBy(t => t.TagName)
                    .ToDictionary(g => g.Key, g => g.First());
                var projectIOTagNames = new HashSet<string>(projectIOTagDict.Keys, StringComparer.OrdinalIgnoreCase);

                var ioDirection = ioTag != null ? GetIoDirection(ioTag.Address) : "Unknown";

                // 2. 根据IO方向确定查找访问类型
                // I 点(Input/传感器) → 找 READ（信号在哪里被读取使用）
                // Q 点(Output/执行器) → 找 WRITE（信号在哪里被驱动赋值）
                string? targetAccessType = ioDirection switch
                {
                    "Input" => "READ",
                    "Output" => "WRITE",
                    _ => null // 查找所有
                };

                // 3. 查找该信号的 VariableAccess 记录
                var accesses = await QueryWithNewScopeAsync<IPLC_VariableAccessService, List<PLC_VariableAccessEntity>>(svc => svc.GetListAsync(x =>
                    x.VariableName == signalName && !x.IsDeleted &&
                    (targetAccessType == null || x.AccessType == targetAccessType)));

                // 如果按方向没找到，退回查找全部
                if (accesses.Count == 0 && targetAccessType != null)
                {
                    accesses = await QueryWithNewScopeAsync<IPLC_VariableAccessService, List<PLC_VariableAccessEntity>>(svc => svc.GetListAsync(x =>
                        x.VariableName == signalName && !x.IsDeleted));
                }

                var fallbackLogicUnits = new List<PLC_LogicUnitEntity>();
                var fallbackBlockDict = new Dictionary<long, string>();

                if (accesses.Count == 0)
                {
                    fallbackBlockDict = projectBlockDict;

                    if (projectBlockIds.Count > 0)
                    {
                        fallbackLogicUnits = await QueryWithNewScopeAsync<IPLC_LogicUnitService, List<PLC_LogicUnitEntity>>(svc => svc.GetListAsync(x =>
                            x.BlockId.HasValue
                            && projectBlockIds.Contains(x.BlockId.Value)
                            && !x.IsDeleted
                            && x.RawContent != null
                            && x.RawContent.Contains(signalName)));
                    }
                }

                if (accesses.Count == 0 && fallbackLogicUnits.Count == 0)
                {
                    return new
                    {
                        Success = true,
                        Signal = new { signalName, ioDirection, ioTag?.Address, ioTag?.Comment, ioTag?.EquipmentId, ioTag?.IsSafety },
                        Message = "该信号在 PLC 逻辑中未找到访问记录（可能是未使用的点位），无法推理约束。",
                        LogicContexts = new List<object>()
                    };
                }

                // 4. 获取关联的 LogicUnit（限制前20个最重要的）
                var luIds = accesses.Where(a => a.LogicUnitId.HasValue)
                    .Select(a => a.LogicUnitId!.Value).Distinct().Take(20).ToList();

                var logicUnits = luIds.Count > 0
                    ? await QueryWithNewScopeAsync<IPLC_LogicUnitService, List<PLC_LogicUnitEntity>>(svc => svc.GetListAsync(x => luIds.Contains(x.Id) && !x.IsDeleted))
                    : new List<PLC_LogicUnitEntity>();

                // 5. 批量获取 Block 名称
                var blockDict = projectBlockDict;
                var blockCalls = luIds.Count > 0
                    ? await QueryWithNewScopeAsync<IPLC_BlockCallService, List<PLC_BlockCallEntity>>(svc => svc.GetListAsync(x =>
                        x.ProjectId == projectId
                        && x.LogicUnitId.HasValue
                        && luIds.Contains(x.LogicUnitId.Value)
                        && !x.IsDeleted))
                    : new List<PLC_BlockCallEntity>();

                // 6. 查找同一 LogicUnit 中的共现 IO 信号
                var coAccessList = luIds.Count > 0
                    ? await QueryWithNewScopeAsync<IPLC_VariableAccessService, List<PLC_VariableAccessEntity>>(svc => svc.GetListAsync(x =>
                        luIds.Contains(x.LogicUnitId!.Value)
                        && x.VariableName != signalName && !x.IsDeleted))
                    : new List<PLC_VariableAccessEntity>();

                // 7. 组装逻辑上下文
                var logicContexts = logicUnits.Select(lu =>
                {
                    // 当前信号在此 LogicUnit 中的访问信息
                    var myAccesses = accesses.Where(a => a.LogicUnitId == lu.Id).ToList();

                    // 收集该 LU 中关键 FB 引脚绑定的 DB 变量名（如 General.AutoMod, E_StopStatus）
                    var keyPinDbVars = CollectKeyPinDbVarsFromLu(lu.RawContent, lu.UidSymbolMapJson, projectIOTagNames);

                    // 同一 LogicUnit 中的共现信号（IO 标签 + 关键引脚 DB 变量）
                    var coSignals = coAccessList
                        .Where(a => a.LogicUnitId == lu.Id)
                        .Where(a => !string.IsNullOrWhiteSpace(a.VariableName)
                            && (projectIOTagNames.Contains(a.VariableName) || keyPinDbVars.Contains(a.VariableName)))
                        .GroupBy(a => a.VariableName)
                        .Select(sg =>
                        {
                            var addr = projectIOTagDict.TryGetValue(sg.Key, out var coTag) ? coTag.Address : null;
                            return new
                            {
                                SignalName = sg.Key,
                                Address = addr,
                                // 显式标注 IO 方向：Q=物理输出, I=物理输入, DB=内部变量, null=未知
                                IoDirection = ParseIoDirection(addr),
                                Comment = projectIOTagDict.TryGetValue(sg.Key, out coTag) ? coTag.Comment : null,
                                AccessTypes = sg.Select(a => a.AccessType).Distinct().ToList(),
                                Instructions = sg.Where(a => !string.IsNullOrEmpty(a.Instruction))
                                    .Select(a => a.Instruction).Distinct().Take(5).ToList()
                            };
                        }).ToList();

                    return new
                    {
                        LogicUnitId = lu.Id,
                        lu.NetworkId,
                        lu.Title,
                        lu.Comment,
                        lu.ProgrammingLanguage,
                        BlockName = lu.BlockId.HasValue && blockDict.ContainsKey(lu.BlockId.Value)
                            ? blockDict[lu.BlockId.Value] : "Unknown",
                        // 当前信号在此 Network 中的访问方式
                        MyAccessTypes = myAccesses.Select(a => a.AccessType).Distinct().ToList(),
                        MyInstructions = myAccesses.Where(a => !string.IsNullOrEmpty(a.Instruction))
                            .Select(a => a.Instruction).Distinct().Take(5).ToList(),
                        // 简化逻辑表达式（优先紧凑版）
                        CompactLogic = BuildLogicSnippet(lu),
                        RawLogicXml = lu.RawContent,
                        ParsedLogicDescription = lu.ParsedLogicDescription,
                        UidSymbolMapJson = lu.UidSymbolMapJson,
                        // 共现的其他 IO 信号 + 关键引脚 DB 变量
                        CoOccurringSignals = coSignals
                    };
                }).Cast<object>().ToList();

                // 8. 汇总共现信号频率（跨所有Network出现的次数）
                if (logicContexts.Count == 0 && fallbackLogicUnits.Count > 0)
                {
                    logicContexts = fallbackLogicUnits.Select(lu => new
                    {
                        LogicUnitId = lu.Id,
                        lu.NetworkId,
                        lu.Title,
                        lu.Comment,
                        lu.ProgrammingLanguage,
                        BlockName = lu.BlockId.HasValue && fallbackBlockDict.ContainsKey(lu.BlockId.Value)
                            ? fallbackBlockDict[lu.BlockId.Value] : "Unknown",
                        MyAccessTypes = new List<string> { "TEXT_FALLBACK" },
                        MyInstructions = new List<string>(),
                        CompactLogic = BuildLogicSnippet(lu),
                        RawLogicXml = lu.RawContent,
                        ParsedLogicDescription = lu.ParsedLogicDescription,
                        UidSymbolMapJson = lu.UidSymbolMapJson,
                        CoOccurringSignals = ExtractCoOccurringIoSignalsFromRawContent(lu.RawContent, projectIOTagDict, signalName)
                    }).ToList<object>();
                }

                var expandedCallContexts = await BuildExpandedCallContextsAsync(
                    signalName,
                    logicUnits,
                    blockCalls,
                    projectBlocks,
                    projectIOTagDict);

                if (expandedCallContexts.Count > 0)
                {
                    logicContexts.AddRange(expandedCallContexts.Cast<object>());
                }

                var coSignalSummary = coAccessList
                    .GroupBy(a => a.VariableName)
                    .Where(g => !string.IsNullOrWhiteSpace(g.Key) && projectIOTagNames.Contains(g.Key))
                    .Select(g => new CoSignalSummaryItem
                    {
                        SignalName = g.Key,
                        CoOccurrenceCount = g.Select(a => a.LogicUnitId).Distinct().Count()
                    })
                    .OrderByDescending(s => s.CoOccurrenceCount)
                    .Take(30)
                    .ToList();

                if (coSignalSummary.Count == 0 && fallbackLogicUnits.Count > 0)
                {
                    coSignalSummary = fallbackLogicUnits
                        .SelectMany(lu => ExtractCoOccurringIoSignalsFromRawContent(lu.RawContent, projectIOTagDict, signalName))
                        .Select(x => JsonSerializer.Serialize(x))
                        .GroupBy(x => x)
                        .Select(g =>
                        {
                            using var doc = JsonDocument.Parse(g.Key);
                            return new CoSignalSummaryItem
                            {
                                SignalName = doc.RootElement.GetProperty("SignalName").GetString() ?? string.Empty,
                                CoOccurrenceCount = g.Count()
                            };
                        })
                        .OrderByDescending(s => s.CoOccurrenceCount)
                        .Take(30)
                        .ToList();
                }

                if (expandedCallContexts.Count > 0)
                {
                    coSignalSummary = coSignalSummary
                        .Concat(ExtractExpandedCoSignalSummary(expandedCallContexts))
                        .GroupBy(x => x.SignalName)
                        .Select(g => new CoSignalSummaryItem
                        {
                            SignalName = g.Key,
                            CoOccurrenceCount = g.Sum(x => x.CoOccurrenceCount)
                        })
                        .OrderByDescending(x => x.CoOccurrenceCount)
                        .Take(30)
                        .ToList();
                }

                return new
                {
                    Success = true,
                    Signal = new
                    {
                        signalName,
                        ioDirection,
                        Address = ioTag?.Address,
                        Comment = ioTag?.Comment,
                        EquipmentId = ioTag?.EquipmentId,
                        IsSafety = ioTag?.IsSafety,
                        TagCategory = ioTag?.TagCategory,
                        SearchAccessType = accesses.Count > 0 ? (targetAccessType ?? "ALL") : "TEXT_FALLBACK"
                    },
                    LogicContextCount = logicContexts.Count,
                    LogicContexts = logicContexts,
                    CoSignalSummary = coSignalSummary,
                    Hint = $"信号 {signalName}（{ioDirection}）参与 {logicContexts.Count} 个逻辑段，" +
                           $"共现 {coSignalSummary.Count} 个其他 IO 信号。\n" +
                           "=== 约束推理指导 ===\n" +
                           "1. 看 CompactLogic（XML LAD格式）：Negated Contact = 互斥暗示；<Part Name=\"O\"> = 并行条件\n" +
                           "2. 看 CoOccurringSignals 的 AccessTypes：一个WRITE一个READ = 因果配对\n" +
                           "3. 看 FC/FB Call 的 Output 参数绑定 → 多个输出信号可能存在互斥/选一关系\n" +
                           "4. CoSignalSummary 共现频次高的信号优先分析\n" +
                           "5. IsSafety=true → Safety 类型候选\n" +
                           "6. EquipmentId 相同的共现信号 → 属于同一设备，约束概率高\n" +
                           "推理完成后调用 SaveIOConstraintsAsync 写入。"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Diagnosis] GetSignalLogicContext 异常");
                return new { Success = false, Message = $"获取信号逻辑上下文失败: {ex.Message}" };
            }
        }

        public async Task<SignalLogicContextProjectCache> LoadProjectLogicContextCacheAsync(long projectId)
        {
            var projectBlocks = await QueryWithNewScopeAsync<IPLC_BlockService, List<PLC_BlockEntity>>(svc => svc.GetListAsync(
                x => x.ProjectId == projectId && !x.IsDeleted));

            var projectBlockDict = projectBlocks
                .GroupBy(b => b.Id)
                .ToDictionary(g => g.Key, g => g.First().BlockName ?? string.Empty);

            var projectIOTags = await QueryWithNewScopeAsync<IPLC_IOTagService, List<PLC_IOTagEntity>>(svc => svc.GetListAsync(
                x => x.ProjectId == projectId && !x.IsDeleted));

            var projectIOTagDict = projectIOTags
                .Where(t => !string.IsNullOrWhiteSpace(t.TagName))
                .GroupBy(t => t.TagName!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var variableAccesses = await QueryWithNewScopeAsync<IPLC_VariableAccessService, List<PLC_VariableAccessEntity>>(svc => svc.GetListAsync(
                x => x.ProjectId == projectId && !x.IsDeleted));

            var projectBlockIds = projectBlocks.Select(b => b.Id).ToHashSet();
            var logicUnits = await QueryWithNewScopeAsync<IPLC_LogicUnitService, List<PLC_LogicUnitEntity>>(svc => svc.GetListAsync(
                x => x.BlockId.HasValue && projectBlockIds.Contains(x.BlockId.Value) && !x.IsDeleted));

            var logicUnitDict = logicUnits
                .GroupBy(lu => lu.Id)
                .ToDictionary(g => g.Key, g => g.First());

            var blockCalls = await QueryWithNewScopeAsync<IPLC_BlockCallService, List<PLC_BlockCallEntity>>(svc => svc.GetListAsync(
                x => x.ProjectId == projectId && !x.IsDeleted));

            // 加载 ai_plc_logicunit_ast，通过 logic_unit_id 与 ai_plc_logicunit.id 关联
            var logicUnitIds = logicUnits.Select(lu => lu.Id).ToHashSet();
            var logicUnitAstList = logicUnitIds.Count > 0
                ? await QueryWithNewScopeAsync<IPLC_LogicUnitAstService, List<PLC_LogicUnitAstEntity>>(svc => svc.GetListAsync(
                    x => x.LogicUnitId.HasValue && logicUnitIds.Contains(x.LogicUnitId.Value) && !x.IsDeleted))
                : new List<PLC_LogicUnitAstEntity>();

            var logicUnitAstDict = logicUnitAstList
                .Where(ast => ast.LogicUnitId.HasValue && !string.IsNullOrWhiteSpace(ast.RootNodeJson))
                .GroupBy(ast => ast.LogicUnitId!.Value)
                .ToDictionary(g => g.Key, g => g.First());

            return new SignalLogicContextProjectCache
            {
                ProjectId = projectId,
                ProjectBlocks = projectBlocks,
                ProjectBlockDict = projectBlockDict,
                ProjectIOTags = projectIOTags,
                ProjectIOTagDict = projectIOTagDict,
                ProjectIOTagNames = new HashSet<string>(projectIOTagDict.Keys, StringComparer.OrdinalIgnoreCase),
                VariableAccesses = variableAccesses,
                LogicUnits = logicUnits,
                LogicUnitDict = logicUnitDict,
                BlockCalls = blockCalls,
                LogicUnitAstDict = logicUnitAstDict
            };
        }

        public async Task<object> GetSignalLogicContextFromCacheAsync(string signalName, SignalLogicContextProjectCache cache)
        {
            if (cache == null)
            {
                throw new ArgumentNullException(nameof(cache));
            }

            if (string.IsNullOrWhiteSpace(signalName))
            {
                return new { Success = false, Message = "signalName 不能为空" };
            }

            var ioTag = cache.ProjectIOTagDict.TryGetValue(signalName, out var cachedIoTag)
                ? cachedIoTag
                : cache.ProjectIOTags.FirstOrDefault(x => string.Equals(x.TagName, signalName, StringComparison.OrdinalIgnoreCase));

            var ioDirection = ioTag != null ? GetIoDirection(ioTag.Address) : "Unknown";
            string? targetAccessType = ioDirection switch
            {
                "Input" => "READ",
                "Output" => "WRITE",
                _ => null
            };

            var accesses = cache.VariableAccesses
                .Where(x => string.Equals(x.VariableName, signalName, StringComparison.OrdinalIgnoreCase))
                .Where(x => targetAccessType == null || string.Equals(x.AccessType, targetAccessType, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (accesses.Count == 0 && targetAccessType != null)
            {
                accesses = cache.VariableAccesses
                    .Where(x => string.Equals(x.VariableName, signalName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            var fallbackLogicUnits = new List<PLC_LogicUnitEntity>();
            if (accesses.Count == 0)
            {
                fallbackLogicUnits = cache.LogicUnits
                    .Where(x => !string.IsNullOrWhiteSpace(x.RawContent)
                        && x.RawContent.Contains(signalName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            if (accesses.Count == 0 && fallbackLogicUnits.Count == 0)
            {
                return new
                {
                    Success = true,
                    Signal = new { signalName, ioDirection, ioTag?.Address, ioTag?.Comment, ioTag?.EquipmentId, ioTag?.IsSafety },
                    Message = "该信号在 PLC 逻辑中未找到访问记录，无法推理约束。",
                    LogicContexts = new List<object>(),
                    CoSignalSummary = new List<object>()
                };
            }

            var luIds = accesses.Where(a => a.LogicUnitId.HasValue)
                .Select(a => a.LogicUnitId!.Value)
                .Distinct()
                .Take(20)
                .ToList();

            var logicUnits = luIds
                .Where(cache.LogicUnitDict.ContainsKey)
                .Select(id => cache.LogicUnitDict[id])
                .ToList();

            var blockCalls = cache.BlockCalls
                .Where(x => x.LogicUnitId.HasValue && luIds.Contains(x.LogicUnitId.Value))
                .ToList();

            var coAccessList = cache.VariableAccesses
                .Where(x => x.LogicUnitId.HasValue && luIds.Contains(x.LogicUnitId.Value))
                .Where(x => !string.Equals(x.VariableName, signalName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var logicContexts = logicUnits.Select(lu =>
            {
                var myAccesses = accesses.Where(a => a.LogicUnitId == lu.Id).ToList();

                // 收集该 LU 中关键 FB 引脚绑定的 DB 变量名
                var keyPinDbVars = CollectKeyPinDbVarsFromLu(lu.RawContent, lu.UidSymbolMapJson, cache.ProjectIOTagNames);

                var coSignals = coAccessList
                    .Where(a => a.LogicUnitId == lu.Id)
                    .Where(a => !string.IsNullOrWhiteSpace(a.VariableName)
                        && (cache.ProjectIOTagNames.Contains(a.VariableName) || keyPinDbVars.Contains(a.VariableName)))
                    .GroupBy(a => a.VariableName)
                    .Select(sg =>
                    {
                        var addr = cache.ProjectIOTagDict.TryGetValue(sg.Key, out var coTag) ? coTag.Address : null;
                        return new
                        {
                            SignalName = sg.Key,
                            Address = addr,
                            // 显式标注 IO 方向：Q=物理输出, I=物理输入, DB=内部变量, null=未知
                            IoDirection = ParseIoDirection(addr),
                            Comment = cache.ProjectIOTagDict.TryGetValue(sg.Key, out coTag) ? coTag.Comment : null,
                            AccessTypes = sg.Select(a => a.AccessType).Distinct().ToList(),
                            Instructions = sg.Where(a => !string.IsNullOrEmpty(a.Instruction))
                                .Select(a => a.Instruction).Distinct().Take(5).ToList()
                        };
                    }).ToList();

                return new
                {
                    LogicUnitId = lu.Id,
                    lu.NetworkId,
                    lu.Title,
                    lu.Comment,
                    lu.ProgrammingLanguage,
                    BlockName = lu.BlockId.HasValue && cache.ProjectBlockDict.ContainsKey(lu.BlockId.Value)
                        ? cache.ProjectBlockDict[lu.BlockId.Value] : "Unknown",
                    MyAccessTypes = myAccesses.Select(a => a.AccessType).Distinct().ToList(),
                    MyInstructions = myAccesses.Where(a => !string.IsNullOrEmpty(a.Instruction))
                        .Select(a => a.Instruction).Distinct().Take(5).ToList(),
                    CompactLogic = BuildLogicSnippet(lu),
                    RawLogicXml = lu.RawContent,
                    ParsedLogicDescription = lu.ParsedLogicDescription,
                    AstRootNodeJson = cache.LogicUnitAstDict.TryGetValue(lu.Id, out var _luAst1) ? _luAst1.RootNodeJson : null,
                    AstInputVariablesJson = cache.LogicUnitAstDict.TryGetValue(lu.Id, out var _luAst1b) ? _luAst1b.InputVariablesJson : null,
                    UidSymbolMapJson = lu.UidSymbolMapJson,
                    CoOccurringSignals = coSignals
                };
            }).Cast<object>().ToList();

            if (logicContexts.Count == 0 && fallbackLogicUnits.Count > 0)
            {
                logicContexts = fallbackLogicUnits.Select(lu => new
                {
                    LogicUnitId = lu.Id,
                    lu.NetworkId,
                    lu.Title,
                    lu.Comment,
                    lu.ProgrammingLanguage,
                    BlockName = lu.BlockId.HasValue && cache.ProjectBlockDict.ContainsKey(lu.BlockId.Value)
                        ? cache.ProjectBlockDict[lu.BlockId.Value] : "Unknown",
                    MyAccessTypes = new List<string> { "TEXT_FALLBACK" },
                    MyInstructions = new List<string>(),
                    CompactLogic = BuildLogicSnippet(lu),
                    RawLogicXml = lu.RawContent,
                    ParsedLogicDescription = lu.ParsedLogicDescription,
                    AstRootNodeJson = cache.LogicUnitAstDict.TryGetValue(lu.Id, out var _luAst2) ? _luAst2.RootNodeJson : null,
                    AstInputVariablesJson = cache.LogicUnitAstDict.TryGetValue(lu.Id, out var _luAst2b) ? _luAst2b.InputVariablesJson : null,
                    UidSymbolMapJson = lu.UidSymbolMapJson,
                    CoOccurringSignals = ExtractCoOccurringIoSignalsFromRawContent(lu.RawContent, cache.ProjectIOTagDict, signalName)
                }).ToList<object>();
            }

            var expandedCallContexts = await BuildExpandedCallContextsFromCacheAsync(
                signalName,
                logicUnits,
                blockCalls,
                cache.ProjectBlocks,
                cache.ProjectIOTagDict,
                cache.VariableAccesses,
                cache.LogicUnits,
                cache.LogicUnitAstDict);

            if (expandedCallContexts.Count > 0)
            {
                logicContexts.AddRange(expandedCallContexts.Cast<object>());
            }

            var coSignalSummary = coAccessList
                .GroupBy(a => a.VariableName)
                .Where(g => !string.IsNullOrWhiteSpace(g.Key) && cache.ProjectIOTagNames.Contains(g.Key))
                .Select(g => new CoSignalSummaryItem
                {
                    SignalName = g.Key,
                    CoOccurrenceCount = g.Select(a => a.LogicUnitId).Distinct().Count()
                })
                .OrderByDescending(s => s.CoOccurrenceCount)
                .Take(30)
                .ToList();

            if (coSignalSummary.Count == 0 && fallbackLogicUnits.Count > 0)
            {
                coSignalSummary = fallbackLogicUnits
                    .SelectMany(lu => ExtractCoOccurringIoSignalsFromRawContent(lu.RawContent, cache.ProjectIOTagDict, signalName))
                    .Select(x => JsonSerializer.Serialize(x))
                    .GroupBy(x => x)
                    .Select(g =>
                    {
                        using var doc = JsonDocument.Parse(g.Key);
                        return new CoSignalSummaryItem
                        {
                            SignalName = doc.RootElement.GetProperty("SignalName").GetString() ?? string.Empty,
                            CoOccurrenceCount = g.Count()
                        };
                    })
                    .OrderByDescending(s => s.CoOccurrenceCount)
                    .Take(30)
                    .ToList();
            }

            if (expandedCallContexts.Count > 0)
            {
                coSignalSummary = coSignalSummary
                    .Concat(ExtractExpandedCoSignalSummary(expandedCallContexts))
                    .GroupBy(x => x.SignalName)
                    .Select(g => new CoSignalSummaryItem
                    {
                        SignalName = g.Key,
                        CoOccurrenceCount = g.Sum(x => x.CoOccurrenceCount)
                    })
                    .OrderByDescending(x => x.CoOccurrenceCount)
                    .Take(30)
                    .ToList();
            }

            return new
            {
                Success = true,
                Signal = new
                {
                    signalName,
                    ioDirection,
                    Address = ioTag?.Address,
                    Comment = ioTag?.Comment,
                    EquipmentId = ioTag?.EquipmentId,
                    IsSafety = ioTag?.IsSafety,
                    TagCategory = ioTag?.TagCategory,
                    SearchAccessType = accesses.Count > 0 ? (targetAccessType ?? "ALL") : "TEXT_FALLBACK"
                },
                LogicContextCount = logicContexts.Count,
                LogicContexts = logicContexts,
                CoSignalSummary = coSignalSummary
            };
        }

        /// <summary>根据物理地址前缀判断IO方向</summary>
        private static string GetIoDirection(string? address)
        {
            if (string.IsNullOrEmpty(address)) return "Unknown";
            if (address.StartsWith("%I", StringComparison.OrdinalIgnoreCase)) return "Input";
            if (address.StartsWith("%Q", StringComparison.OrdinalIgnoreCase)) return "Output";
            if (address.StartsWith("%M", StringComparison.OrdinalIgnoreCase)) return "Memory";
            return "Unknown";
        }

        /// <summary>截断过长内容</summary>
        private static string? TruncateContent(string? content, int maxLen)
        {
            if (string.IsNullOrEmpty(content)) return null;
            return content.Length <= maxLen ? content : content[..maxLen] + "...(truncated)";
        }

        private static string? BuildLogicSnippet(PLC_LogicUnitEntity logicUnit)
        {
            if (!string.IsNullOrWhiteSpace(logicUnit.Compact_logic_xml))
            {
                return logicUnit.Compact_logic_xml;
            }

            return TruncateContent(logicUnit.RawContent, 12000);
        }

        private static List<object> ExtractCoOccurringIoSignalsFromRawContent(
            string? rawContent,
            Dictionary<string, PLC_IOTagEntity> projectIOTagDict,
            string primarySignal,
            int maxCount = 20)
        {
            if (string.IsNullOrWhiteSpace(rawContent))
            {
                return new List<object>();
            }

            return projectIOTagDict
                .Where(kvp => !kvp.Key.Equals(primarySignal, StringComparison.OrdinalIgnoreCase))
                .Where(kvp => rawContent.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                .Take(maxCount)
                .Select(kvp => (object)new
                {
                    SignalName = kvp.Key,
                    Address = kvp.Value.Address,
                    IoDirection = ParseIoDirection(kvp.Value.Address),
                    Comment = kvp.Value.Comment,
                    AccessTypes = new List<string> { "TEXT_FALLBACK" },
                    Instructions = new List<string>()
                })
                .ToList();
        }

        /// <summary>
        /// 从 IO 标签的物理地址中解析信号方向。
        /// %Q = 物理输出（电磁阀/线圈/指示灯）, %I = 物理输入（传感器/按钮/开关）, 其他 = DB 内部变量。
        /// 这个字段会直接序列化到 CoOccurringSignals 中，让 AI 明确知道每个信号的 IO 方向，
        /// 避免将 Q 输出和 I 传感器混合放入同一条 Mutex 等错误约束。
        /// </summary>
        private static string? ParseIoDirection(string? address)
        {
            if (string.IsNullOrWhiteSpace(address)) return "DB";
            if (address.StartsWith("%Q", StringComparison.OrdinalIgnoreCase)) return "Q_Output";
            if (address.StartsWith("%I", StringComparison.OrdinalIgnoreCase)) return "I_Input";
            if (address.StartsWith("%M", StringComparison.OrdinalIgnoreCase)) return "M_Merker";
            return "DB";
        }

        /// <summary>
        /// 从 LogicUnit 的原始 XML 中提取关键 FB 引脚绑定的 DB 变量名集合。
        /// 用于将 E_StopStatus、ManualMod、AutoRunMod 等语义关键变量加入 CoOccurringSignals。
        /// </summary>
        private static HashSet<string> CollectKeyPinDbVarsFromLu(
            string? rawContent,
            string? uidSymbolMapJson,
            HashSet<string> projectIOTagNames)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(rawContent) || string.IsNullOrWhiteSpace(uidSymbolMapJson))
                return result;

            try
            {
                var allHints = ExtractAllCallPinHints(rawContent, uidSymbolMapJson);
                foreach (var (_, hints) in allHints)
                {
                    foreach (var (pinName, signals) in hints.SignalHints)
                    {
                        if (!KeyFbPinNames.Contains(pinName))
                            continue;
                        foreach (var s in signals)
                        {
                            if (!string.IsNullOrWhiteSpace(s) && !projectIOTagNames.Contains(s))
                                result.Add(s);
                        }
                    }

                    // 也检查 ResolvedPins（直接绑定的信号）
                    foreach (var (pinName, signal) in hints.ResolvedPins)
                    {
                        if (KeyFbPinNames.Contains(pinName)
                            && !string.IsNullOrWhiteSpace(signal)
                            && !projectIOTagNames.Contains(signal))
                        {
                            result.Add(signal);
                        }
                    }
                }
            }
            catch
            {
                // 解析失败不影响主流程
            }
            return result;
        }

        private async Task<List<ExpandedCallContext>> BuildExpandedCallContextsFromCacheAsync(
            string signalName,
            List<PLC_LogicUnitEntity> callerLogicUnits,
            List<PLC_BlockCallEntity> blockCalls,
            List<PLC_BlockEntity> projectBlocks,
            Dictionary<string, PLC_IOTagEntity> projectIOTagDict,
            List<PLC_VariableAccessEntity> variableAccesses,
            List<PLC_LogicUnitEntity> allLogicUnits,
            Dictionary<long, PLC_LogicUnitAstEntity>? astDict = null)
        {
            var result = new List<ExpandedCallContext>();
            if (callerLogicUnits.Count == 0 || blockCalls.Count == 0)
            {
                return result;
            }

            var blockByName = projectBlocks
                .Where(b => !string.IsNullOrWhiteSpace(b.BlockName))
                .GroupBy(b => b.BlockName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var callerLogicUnit in callerLogicUnits)
            {
                var logicUnitCalls = blockCalls
                    .Where(c => c.LogicUnitId == callerLogicUnit.Id)
                    .ToList();
                if (logicUnitCalls.Count == 0)
                {
                    continue;
                }

                var mergedBindings = MergeCallBindings(callerLogicUnit);
                if (mergedBindings.Count == 0)
                {
                    continue;
                }

                foreach (var binding in mergedBindings)
                {
                    var relevantPins = GetRelevantFormalPins(binding, signalName);
                    if (relevantPins.Count == 0)
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(binding.callee_name) || !blockByName.TryGetValue(binding.callee_name, out var calleeBlock))
                    {
                        continue;
                    }

                    var matchedCall = logicUnitCalls.FirstOrDefault(c =>
                        string.Equals(c.CalleeBlockName, binding.callee_name, StringComparison.OrdinalIgnoreCase));
                    if (matchedCall == null)
                    {
                        continue;
                    }

                    var calleeLogicUnits = GetCalleeLogicUnitsByFormalPinsFromCache(
                        calleeBlock.Id,
                        relevantPins,
                        variableAccesses,
                        allLogicUnits);
                    if (calleeLogicUnits.Count == 0)
                    {
                        continue;
                    }

                    var callHints = ExtractCallPinHints(callerLogicUnit.RawContent, callerLogicUnit.UidSymbolMapJson, binding.call_uid);
                    var resolvedPins = BuildResolvedPinMap(binding);
                    var unresolvedPins = callHints.AllPins
                        .Where(pin => !resolvedPins.ContainsKey(pin) && !callHints.SignalHints.ContainsKey(pin))
                        .OrderBy(pin => pin)
                        .ToList();

                    foreach (var calleeLogicUnit in calleeLogicUnits)
                    {
                        var concreteRaw = ApplyCallBindingSubstitution(calleeLogicUnit.RawContent, binding);
                        var concreteCompact = ApplyCallBindingSubstitution(calleeLogicUnit.Compact_logic_xml, binding);
                        var logicText = !string.IsNullOrWhiteSpace(concreteCompact)
                            ? concreteCompact
                            : TruncateContent(concreteRaw, 12000);

                        PLC_LogicUnitAstEntity? calleeAstEntity = null;
                        astDict?.TryGetValue(calleeLogicUnit.Id, out calleeAstEntity);

                        result.Add(new ExpandedCallContext
                        {
                            LogicUnitId = calleeLogicUnit.Id,
                            NetworkId = calleeLogicUnit.NetworkId,
                            Title = calleeLogicUnit.Title,
                            Comment = calleeLogicUnit.Comment,
                            ProgrammingLanguage = calleeLogicUnit.ProgrammingLanguage,
                            BlockName = calleeBlock.BlockName,
                            MyAccessTypes = new List<string> { "CALL_EXPANDED" },
                            MyInstructions = relevantPins.Select(p => $"{p.Direction}:{p.PinName}").Distinct().ToList(),
                            CompactLogic = logicText,
                            RawLogicXml = concreteRaw,
                            AstRootNodeJson = calleeAstEntity?.RootNodeJson,
                            AstInputVariablesJson = calleeAstEntity?.InputVariablesJson,
                            UidSymbolMapJson = calleeLogicUnit.UidSymbolMapJson,
                            CoOccurringSignals = ExtractCoOccurringIoSignalsFromRawContent(
                                concreteCompact ?? concreteRaw,
                                projectIOTagDict,
                                signalName),
                            ContextSource = "CalleeBlock",
                            CallerLogicUnitId = callerLogicUnit.Id,
                            CallerBlockName = matchedCall.CallerBlockName,
                            CalleeBlockName = binding.callee_name,
                            RelevantFormalPins = relevantPins.Select(p => $"{p.Direction}:{p.PinName}").ToList(),
                            CallBindingSummary = resolvedPins,
                            CallPinHints = callHints.ResolvedPins,
                            CallPinSignalHints = callHints.SignalHints,
                            UnresolvedCallPins = unresolvedPins
                        });
                    }
                }
            }

            return result
                .GroupBy(x => $"{x.LogicUnitId}|{x.CallerLogicUnitId}|{x.CalleeBlockName}|{string.Join(",", x.RelevantFormalPins)}")
                .Select(g => g.First())
                .ToList();
        }

        private static List<PLC_LogicUnitEntity> GetCalleeLogicUnitsByFormalPinsFromCache(
            long blockId,
            List<RelevantFormalPin> relevantPins,
            List<PLC_VariableAccessEntity> variableAccesses,
            List<PLC_LogicUnitEntity> allLogicUnits)
        {
            var formalNames = relevantPins
                .SelectMany(pin =>
                {
                    var names = new List<string> { pin.PinName };
                    if (!pin.PinName.StartsWith("#", StringComparison.Ordinal))
                    {
                        names.Add("#" + pin.PinName);
                    }

                    return names;
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var accesses = variableAccesses
                .Where(va => va.BlockId == blockId && formalNames.Contains(va.VariableName))
                .ToList();

            var relevantLogicUnitIds = accesses
                .Where(va =>
                    (relevantPins.Any(pin => pin.Direction == "Output")
                        && (va.AccessType == "WRITE" || va.AccessType == "READWRITE"))
                    || (relevantPins.Any(pin => pin.Direction != "Output")
                        && (va.AccessType == "READ" || va.AccessType == "READWRITE" || va.AccessType == "WRITE")))
                .Where(va => va.LogicUnitId.HasValue)
                .Select(va => va.LogicUnitId!.Value)
                .Distinct()
                .ToList();

            if (relevantLogicUnitIds.Count > 0)
            {
                return allLogicUnits
                    .Where(lu => relevantLogicUnitIds.Contains(lu.Id))
                    .ToList();
            }

            return allLogicUnits
                .Where(lu => lu.BlockId == blockId
                    && formalNames.Any(name =>
                        (!string.IsNullOrEmpty(lu.RawContent) && lu.RawContent.Contains(name))
                        || (!string.IsNullOrEmpty(lu.Compact_logic_xml) && lu.Compact_logic_xml.Contains(name))))
                .ToList();
        }

        private async Task<List<ExpandedCallContext>> BuildExpandedCallContextsAsync(
            string signalName,
            List<PLC_LogicUnitEntity> callerLogicUnits,
            List<PLC_BlockCallEntity> blockCalls,
            List<PLC_BlockEntity> projectBlocks,
            Dictionary<string, PLC_IOTagEntity> projectIOTagDict)
        {
            var result = new List<ExpandedCallContext>();
            if (callerLogicUnits.Count == 0 || blockCalls.Count == 0)
            {
                return result;
            }

            var blockByName = projectBlocks
                .Where(b => !string.IsNullOrWhiteSpace(b.BlockName))
                .GroupBy(b => b.BlockName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var callerLogicUnit in callerLogicUnits)
            {
                var logicUnitCalls = blockCalls
                    .Where(c => c.LogicUnitId == callerLogicUnit.Id)
                    .ToList();
                if (logicUnitCalls.Count == 0)
                {
                    continue;
                }

                var mergedBindings = MergeCallBindings(callerLogicUnit);
                if (mergedBindings.Count == 0)
                {
                    continue;
                }

                foreach (var binding in mergedBindings)
                {
                    var relevantPins = GetRelevantFormalPins(binding, signalName);
                    if (relevantPins.Count == 0)
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(binding.callee_name) || !blockByName.TryGetValue(binding.callee_name, out var calleeBlock))
                    {
                        continue;
                    }

                    var matchedCall = logicUnitCalls.FirstOrDefault(c =>
                        string.Equals(c.CalleeBlockName, binding.callee_name, StringComparison.OrdinalIgnoreCase));
                    if (matchedCall == null)
                    {
                        continue;
                    }

                    var calleeLogicUnits = await GetCalleeLogicUnitsByFormalPinsAsync(calleeBlock.Id, relevantPins);
                    if (calleeLogicUnits.Count == 0)
                    {
                        continue;
                    }

                    var callHints = ExtractCallPinHints(callerLogicUnit.RawContent, callerLogicUnit.UidSymbolMapJson, binding.call_uid);
                    var resolvedPins = BuildResolvedPinMap(binding);
                    var unresolvedPins = callHints.AllPins
                        .Where(pin =>
                            !resolvedPins.ContainsKey(pin)
                            && !callHints.SignalHints.ContainsKey(pin))
                        .OrderBy(pin => pin)
                        .ToList();

                    foreach (var calleeLogicUnit in calleeLogicUnits)
                    {
                        var concreteRaw = ApplyCallBindingSubstitution(calleeLogicUnit.RawContent, binding);
                        var concreteCompact = ApplyCallBindingSubstitution(calleeLogicUnit.Compact_logic_xml, binding);
                        var logicText = !string.IsNullOrWhiteSpace(concreteCompact)
                            ? concreteCompact
                            : TruncateContent(concreteRaw, 12000);

                        result.Add(new ExpandedCallContext
                        {
                            LogicUnitId = calleeLogicUnit.Id,
                            NetworkId = calleeLogicUnit.NetworkId,
                            Title = calleeLogicUnit.Title,
                            Comment = calleeLogicUnit.Comment,
                            ProgrammingLanguage = calleeLogicUnit.ProgrammingLanguage,
                            BlockName = calleeBlock.BlockName,
                            MyAccessTypes = new List<string> { "CALL_EXPANDED" },
                            MyInstructions = relevantPins.Select(p => $"{p.Direction}:{p.PinName}").Distinct().ToList(),
                            CompactLogic = logicText,
                            RawLogicXml = concreteRaw,
                            UidSymbolMapJson = calleeLogicUnit.UidSymbolMapJson,
                            CoOccurringSignals = ExtractCoOccurringIoSignalsFromRawContent(
                                concreteCompact ?? concreteRaw,
                                projectIOTagDict,
                                signalName),
                            ContextSource = "CalleeBlock",
                            CallerLogicUnitId = callerLogicUnit.Id,
                            CallerBlockName = matchedCall.CallerBlockName,
                            CalleeBlockName = binding.callee_name,
                            RelevantFormalPins = relevantPins.Select(p => $"{p.Direction}:{p.PinName}").ToList(),
                            CallBindingSummary = resolvedPins,
                            CallPinHints = callHints.ResolvedPins,
                            CallPinSignalHints = callHints.SignalHints,
                            UnresolvedCallPins = unresolvedPins
                        });
                    }
                }
            }

            return result
                .GroupBy(x => $"{x.LogicUnitId}|{x.CallerLogicUnitId}|{x.CalleeBlockName}|{string.Join(",", x.RelevantFormalPins)}")
                .Select(g => g.First())
                .ToList();
        }

        private async Task<List<PLC_LogicUnitEntity>> GetCalleeLogicUnitsByFormalPinsAsync(
            long blockId,
            List<RelevantFormalPin> relevantPins)
        {
            var formalNames = relevantPins
                .SelectMany(pin =>
                {
                    var names = new List<string> { pin.PinName };
                    if (!pin.PinName.StartsWith("#", StringComparison.Ordinal))
                    {
                        names.Add("#" + pin.PinName);
                    }

                    return names;
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var accesses = await QueryWithNewScopeAsync<IPLC_VariableAccessService, List<PLC_VariableAccessEntity>>(svc => svc.GetListAsync(va =>
                va.BlockId == blockId
                && !va.IsDeleted
                && formalNames.Contains(va.VariableName)));

            var relevantLogicUnitIds = accesses
                .Where(va =>
                    (relevantPins.Any(pin => pin.Direction == "Output")
                        && (va.AccessType == "WRITE" || va.AccessType == "READWRITE"))
                    || (relevantPins.Any(pin => pin.Direction != "Output")
                        && (va.AccessType == "READ" || va.AccessType == "READWRITE" || va.AccessType == "WRITE")))
                .Where(va => va.LogicUnitId.HasValue)
                .Select(va => va.LogicUnitId!.Value)
                .Distinct()
                .ToList();

            if (relevantLogicUnitIds.Count > 0)
            {
                return await QueryWithNewScopeAsync<IPLC_LogicUnitService, List<PLC_LogicUnitEntity>>(svc => svc.GetListAsync(lu =>
                    relevantLogicUnitIds.Contains(lu.Id) && !lu.IsDeleted));
            }

            return await QueryWithNewScopeAsync<IPLC_LogicUnitService, List<PLC_LogicUnitEntity>>(svc => svc.GetListAsync(lu =>
                lu.BlockId == blockId
                && !lu.IsDeleted
                && formalNames.Any(name =>
                    (!string.IsNullOrEmpty(lu.RawContent) && lu.RawContent.Contains(name))
                    || (!string.IsNullOrEmpty(lu.Compact_logic_xml) && lu.Compact_logic_xml.Contains(name)))));
        }

        private async Task<TResult> QueryWithNewScopeAsync<TService, TResult>(Func<TService, Task<TResult>> query)
            where TService : notnull
        {
            using var scope = _serviceProvider.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<TService>();
            return await query(service);
        }

        private static List<BlockCallPinBindingRecord> MergeCallBindings(PLC_LogicUnitEntity logicUnit)
        {
            var bindings = ParseCallPinBindingsJson(logicUnit.CallPinBindingsJson);
            if (bindings.Count == 0)
            {
                return bindings;
            }

            var callHints = ExtractAllCallPinHints(logicUnit.RawContent, logicUnit.UidSymbolMapJson);
            foreach (var binding in bindings)
            {
                if (string.IsNullOrWhiteSpace(binding.call_uid) || !callHints.TryGetValue(binding.call_uid, out var hint))
                {
                    continue;
                }

                binding.input_pins ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                binding.output_pins ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                binding.inout_pins ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var pair in hint.ResolvedPins)
                {
                    if (!binding.input_pins.ContainsKey(pair.Key)
                        && !binding.output_pins.ContainsKey(pair.Key)
                        && !binding.inout_pins.ContainsKey(pair.Key))
                    {
                        binding.input_pins[pair.Key] = pair.Value;
                    }
                }
            }

            return bindings;
        }

        private static List<BlockCallPinBindingRecord> ParseCallPinBindingsJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<BlockCallPinBindingRecord>();
            }

            try
            {
                var bindings = JsonSerializer.Deserialize<List<BlockCallPinBindingRecord>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                return bindings ?? new List<BlockCallPinBindingRecord>();
            }
            catch
            {
                return new List<BlockCallPinBindingRecord>();
            }
        }

        private static List<RelevantFormalPin> GetRelevantFormalPins(BlockCallPinBindingRecord binding, string signalName)
        {
            var result = new List<RelevantFormalPin>();

            void Append(Dictionary<string, string>? pins, string direction)
            {
                if (pins == null)
                {
                    return;
                }

                foreach (var pair in pins)
                {
                    if (string.Equals(pair.Value, signalName, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(new RelevantFormalPin
                        {
                            PinName = pair.Key,
                            ActualSignal = pair.Value,
                            Direction = direction
                        });
                    }
                }
            }

            Append(binding.input_pins, "Input");
            Append(binding.output_pins, "Output");
            Append(binding.inout_pins, "InOut");

            return result;
        }

        private static Dictionary<string, string> BuildResolvedPinMap(BlockCallPinBindingRecord binding)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            void Merge(Dictionary<string, string>? pins)
            {
                if (pins == null)
                {
                    return;
                }

                foreach (var pair in pins)
                {
                    result[pair.Key] = pair.Value;
                }
            }

            Merge(binding.input_pins);
            Merge(binding.output_pins);
            Merge(binding.inout_pins);

            return result;
        }

        internal static Dictionary<string, CallPinHintCollection> ExtractAllCallPinHints(string? rawContent, string? uidSymbolMapJson)
        {
            var result = new Dictionary<string, CallPinHintCollection>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(rawContent))
            {
                return result;
            }

            var uidMap = ParseUidSymbolMap(uidSymbolMapJson);
            var nameConRegex = new Regex(@"<NameCon\s+UId=""(?<uid>\d+)""\s+Name=""(?<name>[^""]+)""\s*/?>", RegexOptions.IgnoreCase);
            var identRegex = new Regex(@"<IdentCon\s+UId=""(?<uid>\d+)""\s*/?>", RegexOptions.IgnoreCase);
            var wireRegex = new Regex(@"<Wire\b[^>]*>(?<content>[\s\S]*?)</Wire>", RegexOptions.IgnoreCase);
            var reverseAdjacency = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var partPorts = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var partTypes = ExtractFlgNetPartTypes(rawContent);
            var callPortDirections = ExtractCallPortDirections(rawContent);

            foreach (Match match in nameConRegex.Matches(rawContent))
            {
                var callUid = match.Groups["uid"].Value;
                var pinName = match.Groups["name"].Value;
                if (!result.TryGetValue(callUid, out var hint))
                {
                    hint = new CallPinHintCollection();
                    result[callUid] = hint;
                }

                hint.AllPins.Add(pinName);
            }

            foreach (Match wireMatch in wireRegex.Matches(rawContent))
            {
                var wireContent = wireMatch.Groups["content"].Value;
                var nameCons = nameConRegex.Matches(wireContent).Cast<Match>().ToList();
                foreach (var nameCon in nameCons)
                {
                    var uid = nameCon.Groups["uid"].Value;
                    var portName = nameCon.Groups["name"].Value;
                    if (!partPorts.TryGetValue(uid, out var ports))
                    {
                        ports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        partPorts[uid] = ports;
                    }

                    ports.Add(portName);
                }

                ConnectWireReverseTraversal(
                    reverseAdjacency,
                    wireContent,
                    nameCons,
                    identRegex,
                    partTypes,
                    callPortDirections);

                if (nameCons.Count == 0)
                {
                    continue;
                }

                var identUid = identRegex.Matches(wireContent).Cast<Match>()
                    .Select(m => m.Groups["uid"].Value)
                    .FirstOrDefault(uid => uidMap.ContainsKey(uid));
                if (string.IsNullOrWhiteSpace(identUid))
                {
                    continue;
                }

                foreach (var nameCon in nameCons)
                {
                    var callUid = nameCon.Groups["uid"].Value;
                    var pinName = nameCon.Groups["name"].Value;
                    if (!result.TryGetValue(callUid, out var hint))
                    {
                        hint = new CallPinHintCollection();
                        result[callUid] = hint;
                    }

                    hint.AllPins.Add(pinName);
                    hint.ResolvedPins[pinName] = uidMap[identUid];
                }
            }

            foreach (var pair in partPorts)
            {
                if (!partTypes.TryGetValue(pair.Key, out var partName))
                {
                    continue;
                }

                ConnectPartInternalEndpoints(reverseAdjacency, pair.Key, partName, pair.Value, callPortDirections);
            }

            foreach (var pair in result)
            {
                foreach (var pin in pair.Value.AllPins.ToList())
                {
                    var endpointKey = BuildNameEndpointKey(pair.Key, pin);
                    var reachableSignals = CollectReachableSignals(endpointKey, reverseAdjacency, uidMap);
                    if (reachableSignals.Count > 0)
                    {
                        pair.Value.SignalHints[pin] = reachableSignals;
                    }
                }
            }

            return result;
        }

        private static CallPinHintCollection ExtractCallPinHints(string? rawContent, string? uidSymbolMapJson, string? callUid)
        {
            if (string.IsNullOrWhiteSpace(callUid))
            {
                return new CallPinHintCollection();
            }

            var allHints = ExtractAllCallPinHints(rawContent, uidSymbolMapJson);
            return allHints.TryGetValue(callUid, out var hint) ? hint : new CallPinHintCollection();
        }

        private static Dictionary<string, string> ParseUidSymbolMap(string? uidSymbolMapJson)
        {
            if (string.IsNullOrWhiteSpace(uidSymbolMapJson))
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            try
            {
                var uidMap = JsonSerializer.Deserialize<Dictionary<string, string>>(uidSymbolMapJson, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                return uidMap != null
                    ? new Dictionary<string, string>(uidMap, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static string? ApplyCallBindingSubstitution(string? content, BlockCallPinBindingRecord binding)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return content;
            }

            var substitutions = BuildResolvedPinMap(binding);
            if (substitutions.Count == 0)
            {
                return content;
            }

            var result = content;
            foreach (var pair in substitutions.OrderByDescending(x => x.Key.Length))
            {
                if (string.IsNullOrWhiteSpace(pair.Value))
                {
                    continue;
                }

                var pinName = pair.Key.TrimStart('#');
                result = Regex.Replace(result, $@"(?<![A-Za-z0-9_]){Regex.Escape(pinName)}(?![A-Za-z0-9_])", pair.Value);
            }

            return result;
        }

        private static Dictionary<string, string> ExtractFlgNetPartTypes(string rawContent)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var flgNetRegex = new Regex(@"<FlgNet\b[^>]*>(?<content>[\s\S]*?)</FlgNet>", RegexOptions.IgnoreCase);

            foreach (Match match in flgNetRegex.Matches(rawContent))
            {
                TryPopulatePartTypes(match.Value, result);
            }

            if (result.Count == 0)
            {
                TryPopulatePartTypes(rawContent, result);
            }

            return result;
        }

        private static void TryPopulatePartTypes(string xmlFragment, Dictionary<string, string> target)
        {
            if (string.IsNullOrWhiteSpace(xmlFragment))
            {
                return;
            }

            try
            {
                var wrapped = $"<Root>{xmlFragment}</Root>";
                var doc = XDocument.Parse(wrapped, LoadOptions.PreserveWhitespace);
                foreach (var element in doc.Descendants())
                {
                    var uid = element.Attribute("UId")?.Value;
                    if (string.IsNullOrWhiteSpace(uid) || target.ContainsKey(uid))
                    {
                        continue;
                    }

                    if (string.Equals(element.Name.LocalName, "Part", StringComparison.OrdinalIgnoreCase))
                    {
                        var partName = element.Attribute("Name")?.Value;
                        if (!string.IsNullOrWhiteSpace(partName))
                        {
                            target[uid] = partName;
                        }
                    }
                    else if (string.Equals(element.Name.LocalName, "Call", StringComparison.OrdinalIgnoreCase))
                    {
                        target[uid] = "Call";
                    }
                }
            }
            catch
            {
            }
        }

        private static void ConnectPartInternalEndpoints(
            Dictionary<string, HashSet<string>> reverseAdjacency,
            string uid,
            string partName,
            IEnumerable<string> portNames,
            Dictionary<string, Dictionary<string, string>> callPortDirections)
        {
            if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(partName))
            {
                return;
            }

            var ports = portNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (ports.Count <= 1)
            {
                return;
            }

            if (string.Equals(partName, "Call", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (ShouldFullyConnectPart(partName))
            {
                var outputPorts = ports
                    .Where(port => GetNamePortDirection(uid, port, partName, callPortDirections) == "Output")
                    .ToList();
                var inputPorts = ports
                    .Where(port => GetNamePortDirection(uid, port, partName, callPortDirections) != "Output")
                    .ToList();
                ConnectSignalFlow(reverseAdjacency, uid, outputPorts, inputPorts);
                return;
            }

            var normalized = partName.Trim();
            if (string.Equals(normalized, "Contact", StringComparison.OrdinalIgnoreCase))
            {
                ConnectPartFlow(reverseAdjacency, uid, "out", new[] { "in", "operand" }, ports);
                return;
            }

            if (string.Equals(normalized, "Coil", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "SCoil", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "RCoil", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "TrCoil", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "IlCoil", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "SvCoil", StringComparison.OrdinalIgnoreCase))
            {
                ConnectPartFlow(reverseAdjacency, uid, "operand", new[] { "in" }, ports);
                return;
            }

            var genericOutputs = ports
                .Where(port => GetNamePortDirection(uid, port, partName, callPortDirections) == "Output")
                .ToList();
            var genericInputs = ports
                .Where(port => GetNamePortDirection(uid, port, partName, callPortDirections) != "Output")
                .ToList();
            ConnectSignalFlow(reverseAdjacency, uid, genericOutputs, genericInputs);
        }

        private static bool ShouldFullyConnectPart(string partName)
        {
            return string.Equals(partName, "O", StringComparison.OrdinalIgnoreCase)
                || string.Equals(partName, "A", StringComparison.OrdinalIgnoreCase)
                || string.Equals(partName, "X", StringComparison.OrdinalIgnoreCase)
                || string.Equals(partName, "AN", StringComparison.OrdinalIgnoreCase)
                || string.Equals(partName, "ON", StringComparison.OrdinalIgnoreCase)
                || string.Equals(partName, "XN", StringComparison.OrdinalIgnoreCase)
                || string.Equals(partName, "Comparator", StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<string, Dictionary<string, string>> ExtractCallPortDirections(string rawContent)
        {
            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(rawContent))
            {
                return result;
            }

            try
            {
                var doc = XDocument.Parse($"<Root>{rawContent}</Root>", LoadOptions.PreserveWhitespace);
                foreach (var callElement in doc.Descendants().Where(x => string.Equals(x.Name.LocalName, "Call", StringComparison.OrdinalIgnoreCase)))
                {
                    var callUid = callElement.Attribute("UId")?.Value;
                    if (string.IsNullOrWhiteSpace(callUid))
                    {
                        continue;
                    }

                    var portDirections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var parameter in callElement.Descendants().Where(x => string.Equals(x.Name.LocalName, "Parameter", StringComparison.OrdinalIgnoreCase)))
                    {
                        var name = parameter.Attribute("Name")?.Value;
                        var section = parameter.Attribute("Section")?.Value;
                        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(section))
                        {
                            portDirections[name] = section;
                        }
                    }

                    if (portDirections.Count > 0)
                    {
                        result[callUid] = portDirections;
                    }
                }
            }
            catch
            {
            }

            return result;
        }

        private static void ConnectWireReverseTraversal(
            Dictionary<string, HashSet<string>> reverseAdjacency,
            string wireContent,
            List<Match> nameCons,
            Regex identRegex,
            Dictionary<string, string> partTypes,
            Dictionary<string, Dictionary<string, string>> callPortDirections)
        {
            var inputEndpoints = new List<string>();
            var outputEndpoints = new List<string>();
            var unknownEndpoints = new List<string>();

            foreach (var nameCon in nameCons)
            {
                var uid = nameCon.Groups["uid"].Value;
                var portName = nameCon.Groups["name"].Value;
                var endpoint = BuildNameEndpointKey(uid, portName);
                var partName = partTypes.TryGetValue(uid, out var value) ? value : string.Empty;
                var direction = GetNamePortDirection(uid, portName, partName, callPortDirections);
                switch (direction)
                {
                    case "Input":
                        inputEndpoints.Add(endpoint);
                        break;
                    case "Output":
                        outputEndpoints.Add(endpoint);
                        break;
                    default:
                        unknownEndpoints.Add(endpoint);
                        break;
                }
            }

            var identEndpoints = identRegex.Matches(wireContent).Cast<Match>()
                .Select(m => BuildIdentEndpointKey(m.Groups["uid"].Value))
                .ToList();
            var hasPowerrail = wireContent.IndexOf("<Powerrail", StringComparison.OrdinalIgnoreCase) >= 0;

            if (hasPowerrail)
            {
                foreach (var input in inputEndpoints)
                {
                    AddReverseEdge(reverseAdjacency, input, "PWR");
                }
            }

            foreach (var output in outputEndpoints)
            {
                foreach (var input in inputEndpoints)
                {
                    AddReverseEdge(reverseAdjacency, input, output);
                }

                foreach (var ident in identEndpoints)
                {
                    AddReverseEdge(reverseAdjacency, ident, output);
                }
            }

            foreach (var ident in identEndpoints)
            {
                foreach (var input in inputEndpoints)
                {
                    AddReverseEdge(reverseAdjacency, input, ident);
                }
            }

            var fallbackTargets = unknownEndpoints.Concat(outputEndpoints).Concat(inputEndpoints).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var unknown in unknownEndpoints)
            {
                foreach (var ident in identEndpoints)
                {
                    AddBidirectionalReverseEdges(reverseAdjacency, unknown, ident);
                }

                foreach (var other in fallbackTargets)
                {
                    if (!string.Equals(unknown, other, StringComparison.OrdinalIgnoreCase))
                    {
                        AddBidirectionalReverseEdges(reverseAdjacency, unknown, other);
                    }
                }
            }
        }

        private static string GetNamePortDirection(
            string uid,
            string portName,
            string? partName,
            Dictionary<string, Dictionary<string, string>> callPortDirections)
        {
            if (string.IsNullOrWhiteSpace(portName))
            {
                return "Unknown";
            }

            if (callPortDirections.TryGetValue(uid, out var callDirections)
                && callDirections.TryGetValue(portName, out var section))
            {
                if (section.Equals("Output", StringComparison.OrdinalIgnoreCase))
                {
                    return "Output";
                }

                if (section.Equals("Input", StringComparison.OrdinalIgnoreCase)
                    || section.Equals("InOut", StringComparison.OrdinalIgnoreCase))
                {
                    return "Input";
                }
            }

            if (string.Equals(partName, "Contact", StringComparison.OrdinalIgnoreCase))
            {
                return portName.Equals("out", StringComparison.OrdinalIgnoreCase) ? "Output" : "Input";
            }

            if (ShouldFullyConnectPart(partName ?? string.Empty))
            {
                return portName.StartsWith("out", StringComparison.OrdinalIgnoreCase) ? "Output" : "Input";
            }

            if (string.Equals(partName, "Coil", StringComparison.OrdinalIgnoreCase)
                || string.Equals(partName, "SCoil", StringComparison.OrdinalIgnoreCase)
                || string.Equals(partName, "RCoil", StringComparison.OrdinalIgnoreCase)
                || string.Equals(partName, "TrCoil", StringComparison.OrdinalIgnoreCase)
                || string.Equals(partName, "IlCoil", StringComparison.OrdinalIgnoreCase)
                || string.Equals(partName, "SvCoil", StringComparison.OrdinalIgnoreCase))
            {
                return portName.Equals("operand", StringComparison.OrdinalIgnoreCase) ? "Output" : "Input";
            }

            return "Unknown";
        }

        private static void ConnectPartFlow(
            Dictionary<string, HashSet<string>> reverseAdjacency,
            string uid,
            string outputPort,
            IEnumerable<string> inputPorts,
            List<string> availablePorts)
        {
            if (!availablePorts.Contains(outputPort, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            var outputEndpoint = BuildNameEndpointKey(uid, outputPort);
            foreach (var inputPort in inputPorts.Where(port => availablePorts.Contains(port, StringComparer.OrdinalIgnoreCase)))
            {
                AddReverseEdge(reverseAdjacency, outputEndpoint, BuildNameEndpointKey(uid, inputPort));
            }
        }

        private static void ConnectSignalFlow(
            Dictionary<string, HashSet<string>> reverseAdjacency,
            string uid,
            List<string> outputPorts,
            List<string> inputPorts)
        {
            foreach (var outputPort in outputPorts)
            {
                var outputEndpoint = BuildNameEndpointKey(uid, outputPort);
                foreach (var inputPort in inputPorts)
                {
                    AddReverseEdge(reverseAdjacency, outputEndpoint, BuildNameEndpointKey(uid, inputPort));
                }
            }
        }

        private static void AddBidirectionalReverseEdges(
            Dictionary<string, HashSet<string>> reverseAdjacency,
            string left,
            string right)
        {
            AddReverseEdge(reverseAdjacency, left, right);
            AddReverseEdge(reverseAdjacency, right, left);
        }

        private static void AddReverseEdge(
            Dictionary<string, HashSet<string>> reverseAdjacency,
            string target,
            string source)
        {
            if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(source))
            {
                return;
            }

            if (!reverseAdjacency.TryGetValue(target, out var sources))
            {
                sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                reverseAdjacency[target] = sources;
            }

            sources.Add(source);
        }

        private static void ConnectEndpoints(
            Dictionary<string, HashSet<string>> adjacency,
            List<string> endpoints)
        {
            var uniqueEndpoints = endpoints
                .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var endpoint in uniqueEndpoints)
            {
                if (!adjacency.ContainsKey(endpoint))
                {
                    adjacency[endpoint] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }
            }

            for (var i = 0; i < uniqueEndpoints.Count; i++)
            {
                for (var j = i + 1; j < uniqueEndpoints.Count; j++)
                {
                    adjacency[uniqueEndpoints[i]].Add(uniqueEndpoints[j]);
                    adjacency[uniqueEndpoints[j]].Add(uniqueEndpoints[i]);
                }
            }
        }

        private static List<string> CollectReachableSignals(
            string startEndpoint,
            Dictionary<string, HashSet<string>> adjacency,
            Dictionary<string, string> uidMap)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!adjacency.ContainsKey(startEndpoint))
            {
                return result.ToList();
            }

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            queue.Enqueue(startEndpoint);
            visited.Add(startEndpoint);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (TryParseIdentEndpointKey(current, out var identUid) && uidMap.TryGetValue(identUid, out var signalName))
                {
                    result.Add(signalName);
                }

                if (!adjacency.TryGetValue(current, out var neighbors))
                {
                    continue;
                }

                foreach (var neighbor in neighbors)
                {
                    if (visited.Add(neighbor))
                    {
                        queue.Enqueue(neighbor);
                    }
                }
            }

            return result.OrderBy(x => x).ToList();
        }

        private static string BuildNameEndpointKey(string uid, string name) => $"N:{uid}:{name}";

        private static string BuildIdentEndpointKey(string uid) => $"I:{uid}";

        private static bool TryParseIdentEndpointKey(string endpoint, out string uid)
        {
            uid = string.Empty;
            if (string.IsNullOrWhiteSpace(endpoint) || !endpoint.StartsWith("I:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            uid = endpoint[2..];
            return !string.IsNullOrWhiteSpace(uid);
        }

        private static List<CoSignalSummaryItem> ExtractExpandedCoSignalSummary(List<ExpandedCallContext> contexts)
        {
            return contexts
                .SelectMany(ctx => ctx.CoOccurringSignals)
                .Select(item => JsonSerializer.Serialize(item))
                .GroupBy(item => item)
                .Select(group =>
                {
                    using var doc = JsonDocument.Parse(group.Key);
                    return new CoSignalSummaryItem
                    {
                        SignalName = doc.RootElement.TryGetProperty("SignalName", out var signalNameElement)
                            ? signalNameElement.GetString() ?? string.Empty
                            : string.Empty,
                        CoOccurrenceCount = group.Count()
                    };
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.SignalName))
                .ToList();
        }

        private sealed class RelevantFormalPin
        {
            public string PinName { get; set; } = string.Empty;
            public string ActualSignal { get; set; } = string.Empty;
            public string Direction { get; set; } = string.Empty;
        }

        private sealed class BlockCallPinBindingRecord
        {
            public string callee_name { get; set; } = string.Empty;
            public string callee_block_type { get; set; } = string.Empty;
            public string instance_db { get; set; } = string.Empty;
            public string call_uid { get; set; } = string.Empty;
            public Dictionary<string, string>? input_pins { get; set; }
            public Dictionary<string, string>? output_pins { get; set; }
            public Dictionary<string, string>? inout_pins { get; set; }
        }

        internal sealed class CallPinHintCollection
        {
            public HashSet<string> AllPins { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> ResolvedPins { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, List<string>> SignalHints { get; set; } = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class ExpandedCallContext
        {
            public long LogicUnitId { get; set; }
            public int? NetworkId { get; set; }
            public string? Title { get; set; }
            public string? Comment { get; set; }
            public string? ProgrammingLanguage { get; set; }
            public string? BlockName { get; set; }
            public List<string> MyAccessTypes { get; set; } = new List<string>();
            public List<string> MyInstructions { get; set; } = new List<string>();
            public string? CompactLogic { get; set; }
            public string? RawLogicXml { get; set; }
            public string? AstRootNodeJson { get; set; }
            public string? AstInputVariablesJson { get; set; }
            public string? UidSymbolMapJson { get; set; }
            public List<object> CoOccurringSignals { get; set; } = new List<object>();
            public string ContextSource { get; set; } = string.Empty;
            public long CallerLogicUnitId { get; set; }
            public string? CallerBlockName { get; set; }
            public string? CalleeBlockName { get; set; }
            public List<string> RelevantFormalPins { get; set; } = new List<string>();
            public Dictionary<string, string> CallBindingSummary { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> CallPinHints { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, List<string>> CallPinSignalHints { get; set; } = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            public List<string> UnresolvedCallPins { get; set; } = new List<string>();
        }

        private sealed class CoSignalSummaryItem
        {
            public string SignalName { get; set; } = string.Empty;
            public int CoOccurrenceCount { get; set; }
        }

        private async Task<string> BuildConstraintDescriptionAsync(ConstraintInput input)
        {
            // ALL_TRUE：无论有无 Source，AI 描述均频繁出现因果倒置（"CYL 全到位时 YV 才允许"）。
            // 全路径短路，始终使用确定性模板，彻底规避 AI 改写带来的方向错误。
            var opUpper = (input.Operator ?? string.Empty).ToUpperInvariant();
            if (opUpper == "ALL_TRUE")
                return BuildTemplateConstraintDescription(input);

            var fallback = BuildTemplateConstraintDescription(input);
            try
            {
                var chatClient = await CreateConstraintDescriptionClientAsync();
                var response = await chatClient.GetResponseAsync(new List<ChatMessage>
                {
                    new ChatMessage(ChatRole.System,
                        """
                        你是PLC约束规则描述生成器。
                        你的任务是把结构化约束改写成准确、完整、自然的中文业务描述，用于直接落库。
                        必须遵守：
                        1. 严格以给定结构为准，不要臆测新的约束。
                        2. IMPLIES 必须写成“当 Source 成立时，Target 必须成立”，绝不能反向。
                        3. REQUIRES_0 必须写成“当 Source 成立时，Target 必须为 false 或不得成立”。
                        4. ALL_TRUE 必须区分两种结构：
                        - 有 Source：必须写成“当 Source 有效（为 true）时，以下成员信号必须全部为 true”，Source 是触发方，Member 是被约束方。绝对不能写反为“Member 全 true 时 Source 才允许成立”。
                        - 无 Source：写“Target 的成立依赖哪些成员信号同时为 true；如果没有 Target，就写成员必须同时成立”。
                        5. AT_MOST_ONE / EXACTLY_ONE 必须体现互斥或唯一有效状态，并点出涉及信号。
                        6. CUSTOM 如果给了 validStates，要表达成“只允许这些合法组合或允许状态”，不要泛泛而谈。
                        7. 描述必须比参考模板更完整，尽量写出动作允许条件、到位条件、互锁关系、合法组合约束等业务语义。
                        8. 只输出 1 到 2 句中文，不要输出 JSON、列表、解释或前缀。
                        """),
                    new ChatMessage(ChatRole.User, BuildConstraintDescriptionPrompt(input, fallback))
                });

                var text = SanitizeConstraintDescription(response?.Text);
                if (!string.IsNullOrWhiteSpace(text) && IsDescriptionConsistent(text, input))
                {
                    return TruncateDescription(EnhanceDescriptionWithComments(text, input));
                }
            }
            catch (AiModelPurposeException ex)
            {
                // 用途未绑定/模型不可用时两次尝试都会失败，直接退回模板描述
                _logger.LogWarning("[约束描述] {Message}，使用模板描述", ex.Message);
                return TruncateDescription(EnhanceDescriptionWithComments(fallback, input));
            }
            catch
            {
            }

            try
            {
                var chatClient = await CreateConstraintDescriptionClientAsync();
                var response = await chatClient.GetResponseAsync(new List<ChatMessage>
                {
                    new ChatMessage(ChatRole.System, "你是PLC约束描述生成器。必须严格依据给定的Operator、Source、Target、Member生成一句准确的中文描述，绝不能说反方向。只输出一句中文，不要解释。"),
                    new ChatMessage(ChatRole.User, BuildConstraintDescriptionPrompt(input, fallback))
                });

                var text = response?.Text?.Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    return fallback;
                }

                // 截断到字段上限避免 PG 22001
                var picked = IsDescriptionConsistent(text, input) ? text : fallback;
                return TruncateDescription(EnhanceDescriptionWithComments(picked, input));
            }
            catch
            {
                return TruncateDescription(fallback);
            }
        }

        /// <summary>约束描述使用网站「I/O 约束说明」用途绑定的模型，不走平台默认聊天模型。</summary>
        private async Task<IChatClient> CreateConstraintDescriptionClientAsync()
        {
            var binding = await _purposeResolver.ResolveAsync(AiModelPurposes.ConstraintDescription);
            return (await _llmModelService.CreateChatClientByModelIdAsync(binding.Model.Id)).AsIChatClient();
        }

        private static string BuildConstraintDescriptionPrompt(ConstraintInput input, string fallback)
        {
            var signals = input.Signals ?? new List<SignalInput>();
            var sourceNames = string.Join(", ", signals.Where(s => string.Equals(s.SignalRole, "Source", StringComparison.OrdinalIgnoreCase)).Select(s => s.SignalName).Where(s => !string.IsNullOrWhiteSpace(s)));
            var targetNames = string.Join(", ", signals.Where(s => string.Equals(s.SignalRole, "Target", StringComparison.OrdinalIgnoreCase)).Select(s => s.SignalName).Where(s => !string.IsNullOrWhiteSpace(s)));
            var memberNames = string.Join(", ", signals.Where(s => string.Equals(s.SignalRole, "Member", StringComparison.OrdinalIgnoreCase)).OrderBy(s => s.SortOrder ?? int.MaxValue).Select(s => s.SignalName).Where(s => !string.IsNullOrWhiteSpace(s)));
            var validStates = string.IsNullOrWhiteSpace(input.ValidStates) ? "(none)" : input.ValidStates;

            return $"""
约束名称: {input.RuleName}
Operator: {input.Operator}
RuleType: {input.RuleType}
SourceSignals: {sourceNames}
TargetSignals: {targetNames}
MemberSignals: {memberNames}
ValidStates: {validStates}
参考模板: {fallback}
输出要求:
1. 用更完整、更像业务语义的中文重写，不要照抄模板。
2. 必须覆盖关键对象：Source / Target / Member / ValidStates 中实际存在的内容。
3. 如果是 ALL_TRUE：
   - 有 Source：必须写成“当 Source 为 true 时，Member 信号必须全部为 true”，Source 是触发方，Member 是被要求方，绝对不能反向写成“Member 全 true 时 Source 才允许”。
   - 无 Source 有 Target：写“Target 的成立依赖 Member 同时为 true”。
   - 无 Source 无 Target：写“Member 信号必须同时为 true”。
4. 如果是 IMPLIES，必须严格按 Source -> Target 描述，不要出现方向误导。
5. 如果是互斥类，要点出“防止同时动作、同时成立或同时通电冲突”等含义。
6. 只输出最终描述正文，不要加“描述：”“说明：”等前缀。
""";

            return $"""
Operator: {input.Operator}
RuleType: {input.RuleType}
Source: {sourceNames}
Target: {targetNames}
Member: {memberNames}
参考模板: {fallback}
要求: 描述必须与 Source/Target 方向一致，不能写反，不能写“指令→输出强配对”这类可能误导的话。
""";
        }

        /// <summary>
        /// ai_plc_ioconstraintrule.description 字段是 varchar(500)。
        /// 合并多 Target 后描述很容易超长，PG 会抛 22001。此函数在末尾统一兜底截断。
        /// </summary>
        private const int DescriptionMaxLength = 480;
        private static string TruncateDescription(string desc)
        {
            if (string.IsNullOrEmpty(desc)) return desc;
            return desc.Length <= DescriptionMaxLength ? desc : desc.Substring(0, DescriptionMaxLength) + "…";
        }

        /// <summary>把 description 字符串里出现的 signal 名追加 (comment) 业务说明。
        /// 仅对在 input.IoTagCommentsByName 字典里找到 comment 的 signal 增强。
        /// 已紧跟 ( 的位置不重复增强,避免 Q102.3(塔灯红)(塔灯红) 嵌套。
        /// 长名优先,避免短名先吃掉长名(如 Q102.3 vs Q102.30)。</summary>
        private static string EnhanceDescriptionWithComments(string desc, ConstraintInput input)
        {
            if (string.IsNullOrWhiteSpace(desc)) return desc;
            if (input.IoTagCommentsByName == null || input.IoTagCommentsByName.Count == 0) return desc;
            if (input.Signals == null || input.Signals.Count == 0) return desc;

            var result = desc;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in input.Signals
                .Where(x => !string.IsNullOrWhiteSpace(x.SignalName))
                .OrderByDescending(x => x.SignalName!.Length))
            {
                var name = s.SignalName!;
                if (!seen.Add(name)) continue;
                if (!input.IoTagCommentsByName.TryGetValue(name, out var comment) || string.IsNullOrWhiteSpace(comment))
                    continue;

                var sb = new System.Text.StringBuilder(result.Length + comment.Length + 4);
                var idx = 0;
                while (idx < result.Length)
                {
                    var found = result.IndexOf(name, idx, StringComparison.Ordinal);
                    if (found < 0) { sb.Append(result, idx, result.Length - idx); break; }
                    sb.Append(result, idx, found - idx);
                    sb.Append(name);
                    var nextPos = found + name.Length;
                    // 已紧跟 ( 的不重复增强
                    if (nextPos >= result.Length || result[nextPos] != '(')
                    {
                        sb.Append('(').Append(comment).Append(')');
                    }
                    idx = nextPos;
                }
                result = sb.ToString();
            }
            return result;
        }

        private static string BuildTemplateConstraintDescription(ConstraintInput input)
        {
            var signals = input.Signals ?? new List<SignalInput>();
            var sourceNames = signals.Where(s => string.Equals(s.SignalRole, "Source", StringComparison.OrdinalIgnoreCase)).Select(s => s.SignalName).Where(s => !string.IsNullOrWhiteSpace(s)).Cast<string>().ToList();
            var targetNames = signals.Where(s => string.Equals(s.SignalRole, "Target", StringComparison.OrdinalIgnoreCase)).Select(s => s.SignalName).Where(s => !string.IsNullOrWhiteSpace(s)).Cast<string>().ToList();
            var memberNames = signals.Where(s => string.Equals(s.SignalRole, "Member", StringComparison.OrdinalIgnoreCase)).OrderBy(s => s.SortOrder ?? int.MaxValue).Select(s => s.SignalName).Where(s => !string.IsNullOrWhiteSpace(s)).Cast<string>().ToList();
            var allSignalNames = signals.Select(s => s.SignalName).Where(s => !string.IsNullOrWhiteSpace(s)).Cast<string>().ToList();
            var memberText = string.Join("、", memberNames);
            var sourceText = string.Join("、", sourceNames);
            var targetText = string.Join("、", targetNames);

            var result = (input.Operator ?? string.Empty).ToUpperInvariant() switch
            {
                "IMPLIES" when sourceNames.Count > 0 && targetNames.Count > 0
                    => $"当 {sourceText} 成立时，{targetText} 也必须成立，这是该动作链路中的前提到结果约束。",
                "REQUIRES_0" when sourceNames.Count > 0 && targetNames.Count > 0
                    => $"当 {sourceText} 成立时，{targetText} 必须保持为 false，用于避免不允许的对立状态同时出现。",
                "AT_MOST_ONE" when memberNames.Count > 0
                    => $"信号 {memberText} 之间互斥，任一时刻最多只允许一个为 true，用于防止同类动作或对立输出同时成立。",
                "SAME_STATE" when memberNames.Count > 0
                    => $"信号 {memberText} 必须状态一致（同时为 true 或同时为 false），用于诊断同步驱动线圈/继电器的接线或硬件故障。",
                "EXACTLY_ONE" when memberNames.Count > 0
                    => $"信号 {memberText} 中必须且仅允许一个为 true，用于保证系统始终处于唯一有效状态。",
                "ALL_TRUE" when sourceNames.Count > 0 && memberNames.Count > 0
                    => $"当 {sourceText} 为 true（已激活）时，{memberText} 必须全部为 true；若有成员信号未到位，则说明执行机构状态异常。",
                "ALL_TRUE" when memberNames.Count > 0 && targetNames.Count > 0
                    => $"{targetText} 的成立依赖以下信号同时为 true：{memberText}；只有这些条件全部满足时，该目标条件才算成立。",
                "ALL_TRUE" when memberNames.Count > 0
                    => $"信号 {memberText} 必须同时为 true，这是一组需要全满足的组合条件。",
                "CHAIN_ORDER" when memberNames.Count > 0
                    => $"信号 {string.Join(" -> ", memberNames)} 必须按既定先后顺序依次满足，不能跳步或乱序。",
                "CUSTOM" when !string.IsNullOrWhiteSpace(input.ValidStates) && allSignalNames.Count > 0
                    => $"信号 {string.Join("、", allSignalNames)} 只允许满足预定义的合法组合状态，超出 validStates 定义的取值组合都应视为异常。",
                "CUSTOM" when targetNames.Count > 0
                    => $"{targetText} 需要满足预定义的合法组合条件，只有处于允许状态时该目标约束才成立。",
                _ => $"该规则涉及信号 {string.Join("、", allSignalNames)}，用于限定它们之间的有效逻辑关系。"
            };
            return TruncateDescription(EnhanceDescriptionWithComments(result, input));
        }

        private static string BuildTemplateConstraintDescription_UnusedFallback(ConstraintInput input)
        {
            var signals = input.Signals ?? new List<SignalInput>();
            var sourceNames = signals.Where(s => string.Equals(s.SignalRole, "Source", StringComparison.OrdinalIgnoreCase)).Select(s => s.SignalName).Where(s => !string.IsNullOrWhiteSpace(s)).Cast<string>().ToList();
            var targetNames = signals.Where(s => string.Equals(s.SignalRole, "Target", StringComparison.OrdinalIgnoreCase)).Select(s => s.SignalName).Where(s => !string.IsNullOrWhiteSpace(s)).Cast<string>().ToList();
            var memberNames = signals.Where(s => string.Equals(s.SignalRole, "Member", StringComparison.OrdinalIgnoreCase)).Select(s => s.SignalName).Where(s => !string.IsNullOrWhiteSpace(s)).Cast<string>().ToList();
            return (input.Operator ?? string.Empty).ToUpperInvariant() switch
            {
                "IMPLIES" when sourceNames.Count > 0 && targetNames.Count > 0
                    => $"当 [{string.Join(", ", sourceNames)}] 为 true 时，[{string.Join(", ", targetNames)}] 应满足对应条件。",
                "REQUIRES_0" when sourceNames.Count > 0 && targetNames.Count > 0
                    => $"当 [{string.Join(", ", sourceNames)}] 生效时，[{string.Join(", ", targetNames)}] 必须为 false。",
                "AT_MOST_ONE" when memberNames.Count > 0
                    => $"信号 [{string.Join(", ", memberNames)}] 互斥，任一时刻最多只允许一个为 true。",
                "EXACTLY_ONE" when memberNames.Count > 0
                    => $"信号 [{string.Join(", ", memberNames)}] 中必须且仅允许一个为 true。",
                "ALL_TRUE" when memberNames.Count > 0 && targetNames.Count > 0
                    => $"目标 [{string.Join(", ", targetNames)}] 对应的成员信号 [{string.Join(", ", memberNames)}] 需要全部为 true。",
                "ALL_TRUE" when memberNames.Count > 0
                    => $"信号 [{string.Join(", ", memberNames)}] 需要全部为 true。",
                "CHAIN_ORDER" when memberNames.Count > 0
                    => $"信号 [{string.Join(" -> ", memberNames)}] 必须按顺序满足。",
                "CUSTOM" when targetNames.Count > 0
                    => $"目标 [{string.Join(", ", targetNames)}] 需要满足预定义的合法组合条件。",
                _ => $"规则涉及信号 [{string.Join(", ", signals.Select(s => s.SignalName).Where(s => !string.IsNullOrWhiteSpace(s)))}]。"
            };
        }

        private static bool IsDescriptionConsistent(string description, ConstraintInput input)
        {
            if (string.IsNullOrWhiteSpace(description))
            {
                return false;
            }

            var signals = input.Signals ?? new List<SignalInput>();
            var sourceNames = signals.Where(s => string.Equals(s.SignalRole, "Source", StringComparison.OrdinalIgnoreCase)).Select(s => s.SignalName).Where(s => !string.IsNullOrWhiteSpace(s)).Cast<string>().ToList();
            var targetNames = signals.Where(s => string.Equals(s.SignalRole, "Target", StringComparison.OrdinalIgnoreCase)).Select(s => s.SignalName).Where(s => !string.IsNullOrWhiteSpace(s)).Cast<string>().ToList();
            var memberNames = signals.Where(s => string.Equals(s.SignalRole, "Member", StringComparison.OrdinalIgnoreCase)).Select(s => s.SignalName).Where(s => !string.IsNullOrWhiteSpace(s)).Cast<string>().ToList();
            var normalizedDescription = description.Trim();
            var op = (input.Operator ?? string.Empty).ToUpperInvariant();

            bool ContainsEnough(IEnumerable<string> names, int minRequired = int.MaxValue)
            {
                var list = names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (list.Count == 0)
                {
                    return true;
                }

                var hitCount = list.Count(name => normalizedDescription.Contains(name, StringComparison.OrdinalIgnoreCase));
                return hitCount >= Math.Min(minRequired, list.Count);
            }

            return op switch
            {
                "IMPLIES" => ContainsEnough(sourceNames) && ContainsEnough(targetNames),
                "REQUIRES_0" => ContainsEnough(sourceNames) && ContainsEnough(targetNames),
                "ALL_TRUE" when sourceNames.Count > 0
                    => ContainsEnough(sourceNames) && ContainsEnough(memberNames, Math.Max(1, Math.Min(2, memberNames.Count))),
                "ALL_TRUE" when targetNames.Count > 0
                    => ContainsEnough(targetNames) && ContainsEnough(memberNames, Math.Max(1, Math.Min(2, memberNames.Count))),
                "ALL_TRUE"
                    => ContainsEnough(memberNames, Math.Max(1, Math.Min(2, memberNames.Count))),
                "AT_MOST_ONE"
                    => ContainsEnough(memberNames, Math.Max(1, Math.Min(2, memberNames.Count))),
                "EXACTLY_ONE"
                    => ContainsEnough(memberNames, Math.Max(1, Math.Min(2, memberNames.Count))),
                "CHAIN_ORDER"
                    => ContainsEnough(memberNames, Math.Max(1, Math.Min(3, memberNames.Count))),
                "CUSTOM" when !string.IsNullOrWhiteSpace(input.ValidStates)
                    => ContainsEnough(targetNames.Concat(memberNames), 1)
                        && (normalizedDescription.Contains("合法组合", StringComparison.OrdinalIgnoreCase)
                            || normalizedDescription.Contains("允许状态", StringComparison.OrdinalIgnoreCase)
                            || normalizedDescription.Contains("有效状态", StringComparison.OrdinalIgnoreCase)),
                "CUSTOM"
                    => ContainsEnough(targetNames.Concat(memberNames), 1),
                _ => true
            };

            if (string.Equals(input.Operator, "IMPLIES", StringComparison.OrdinalIgnoreCase))
            {
                return sourceNames.All(description.Contains) && targetNames.All(description.Contains);
            }

            return true;
        }

        private static string? SanitizeConstraintDescription(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var cleaned = text.Trim();
            cleaned = cleaned.Replace("\r", " ").Replace("\n", " ");
            cleaned = Regex.Replace(cleaned, @"^\s*(描述|说明|结果|输出)\s*[：:]\s*", string.Empty, RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"\s+", " ");

            return cleaned.Trim('\"', '\'', '`', ' ');
        }

        #endregion

        #region ========== 工具 6：AI约束规则写入 ==========

        /// <summary>
        /// [工具] 将 AI 推理出的约束规则批量写入数据库
        /// 
        /// 接收 JSON 数组格式的约束规则，每条包含 ruleName/ruleType/operator/signals 等字段。
        /// 
        /// ruleType（物理语义标签）: Mutex | ExactlyOne | AllRequired | Pairing | Safety | Sequence | Custom
        /// operator（校验逻辑，可与ruleType灵活组合）: AT_MOST_ONE | EXACTLY_ONE | ALL_TRUE | IMPLIES | REQUIRES_0 | CHAIN_ORDER | CUSTOM
        /// signals[].signalRole: Source（驱动端）| Target（反馈端）| Member（对等成员）
        /// 
        /// CUSTOM 运算符使用场景：当一组信号的合法状态不能用上述6种内置运算符描述时（如多种合法组合）,
        /// 使用 CUSTOM 并在 validStates 中提供真值表 JSON:
        /// [{"SignalA":true,"SignalB":false},{"SignalA":false,"SignalB":true},{"SignalA":false,"SignalB":false}]
        /// </summary>
        /// <summary>
        /// 项目级别后处理：合并同组 2 元 Mutex → 1 条 N 元 Mutex（如果它们构成完全图）。
        ///
        /// 背景：LLM 在 Phase2 把 N 个互斥信号拆成 C(N,2) 条成对 2 元 Mutex 是常见误差。
        /// 典型场景：伺服的「负限/原点/正限」3 个位置标志，LLM 输出 3 条 2 元 Mutex
        /// （SQ17↔SQ18、SQ17↔SQ19、SQ18↔SQ19），应合并为 1 条含 3 个 Member 的 Mutex。
        ///
        /// 算法：
        /// 1. 取所有 AT_MOST_ONE + signals.Count==2 的规则
        /// 2. 构建无向图（信号 = 节点，每个 2 元 Mutex = 一条边）
        /// 3. 找连通分量
        /// 4. 对每个 N≥3 的连通分量，检查是不是**完全图**（C(N,2) 条边都齐）
        ///    - 是 → 合并为 1 条 N 元 Mutex（保留最早的规则 ID，删除其它）
        ///    - 否 → 不动（边不齐说明不是真的"全部互斥"，可能是 A↔B + A↔C 但 B/C 不互斥）
        /// </summary>
        /// <summary>
        /// 项目级后处理：清理「IMPLIES Targets 之间存在 Mutex」的物理矛盾约束。
        ///
        /// 背景：SaveIOConstraintsAsync 的 2a-3b 物理矛盾过滤只看**本批 inputs 内**的 Mutex；
        /// 跨 LU 时序下，IMPLIES 可能比对应 Mutex 先入库，本批没看到 Mutex 而漏掉。
        /// 典型场景：LU A 输出 IMPLIES(YV01_W → [CYL01_W, CYL01_H])，
        ///          LU B (Valve[1]) 输出 Mutex(CYL01_W, CYL01_H)，B 比 A 后入库。
        /// 等所有 LU 都跑完后，扫一遍项目级数据清掉这类矛盾。
        ///
        /// 算法：
        /// 1. 取项目级所有 AT_MOST_ONE 规则，构建 Mutex 信号对集合
        /// 2. 遍历所有 IMPLIES 规则，检查其 Target 集合中是否存在两两在 Mutex 集合里的对
        /// 3. 有 → 整条 IMPLIES 软删除（物理矛盾，1 Source 不可能同时驱动两个互斥 Target）
        /// </summary>
        public async Task<object> RemoveContradictoryPairingAsync(long projectId)
        {
            _logger.LogInformation("[Diagnosis] RemoveContradictoryPairing: projectId={ProjectId}", projectId);

            var allRules = await _ioConstraintRuleService.GetListAsync(
                x => x.ProjectId == projectId && !x.IsDeleted);
            var mutexRules = allRules.Where(r => string.Equals(r.Operator, "AT_MOST_ONE", StringComparison.OrdinalIgnoreCase)).ToList();
            var impliesRules = allRules.Where(r => string.Equals(r.Operator, "IMPLIES", StringComparison.OrdinalIgnoreCase)).ToList();
            // ALL_TRUE 也要扫——Member 之间含 Mutex 对的同样物理矛盾（如 Source→[CYL_W, CYL_H]）
            var allTrueRules = allRules.Where(r => string.Equals(r.Operator, "ALL_TRUE", StringComparison.OrdinalIgnoreCase)).ToList();
            if (mutexRules.Count == 0 || (impliesRules.Count == 0 && allTrueRules.Count == 0))
            {
                return new { Success = true, Removed = 0 };
            }

            var ruleIds = mutexRules.Select(r => r.Id)
                .Concat(impliesRules.Select(r => r.Id))
                .Concat(allTrueRules.Select(r => r.Id))
                .ToList();
            var allSigs = await _ioConstraintSignalService.GetListAsync(
                x => ruleIds.Contains(x.RuleId) && !x.IsDeleted);
            var sigByRule = allSigs.GroupBy(s => s.RuleId).ToDictionary(g => g.Key, g => g.ToList());

            // 构建项目级 Mutex 对集合
            var mutexPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in mutexRules)
            {
                if (!sigByRule.TryGetValue(r.Id, out var sigs)) continue;
                var names = sigs.Select(s => s.SignalName?.Trim())
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                for (int i = 0; i < names.Count; i++)
                    for (int j = i + 1; j < names.Count; j++)
                    {
                        var a = names[i]!; var b = names[j]!;
                        var key = string.Compare(a, b, StringComparison.OrdinalIgnoreCase) < 0
                            ? $"{a}|{b}" : $"{b}|{a}";
                        mutexPairs.Add(key);
                    }
            }

            // 找含互斥 Target 对的 IMPLIES 软删
            var toRemove = new List<PLC_IOConstraintRuleEntity>();
            foreach (var r in impliesRules)
            {
                if (!sigByRule.TryGetValue(r.Id, out var sigs)) continue;
                var tgtNames = sigs.Where(s => string.Equals(s.SignalRole, "Target", StringComparison.OrdinalIgnoreCase))
                    .Select(s => s.SignalName?.Trim())
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (tgtNames.Count < 2) continue;
                bool contradiction = false;
                for (int i = 0; i < tgtNames.Count && !contradiction; i++)
                    for (int j = i + 1; j < tgtNames.Count && !contradiction; j++)
                    {
                        var a = tgtNames[i]!; var b = tgtNames[j]!;
                        var key = string.Compare(a, b, StringComparison.OrdinalIgnoreCase) < 0
                            ? $"{a}|{b}" : $"{b}|{a}";
                        if (mutexPairs.Contains(key)) contradiction = true;
                    }
                if (contradiction) toRemove.Add(r);
            }

            foreach (var r in toRemove)
            {
                await _ioConstraintRuleService.UpdateAsync(
                    u => new PLC_IOConstraintRuleEntity { IsDeleted = true },
                    x => x.Id == r.Id);
                await _ioConstraintSignalService.UpdateAsync(
                    u => new PLC_IOConstraintSignalEntity { IsDeleted = true },
                    x => x.RuleId == r.Id);
            }
            if (toRemove.Count > 0)
            {
                _logger.LogInformation("[Diagnosis] RemoveContradictoryPairing: 软删 {N} 条 IMPLIES（Target 间含互斥对，物理矛盾）", toRemove.Count);
            }
            return new { Success = true, Removed = toRemove.Count };
        }

        /// <summary>
        /// 项目级后处理：聚合所有 LU 的 PLD「同步驱动对」集合，扫项目所有 Mutex 软删命中的。
        ///
        /// 背景：LU 级 FilterOutSyncDrivenMutex（IOConstraintPreprocessService）只看本 LU 自己的 PLD；
        /// 但 LLM 可能在另一个不含同步证据的 LU 凭命名启发式（W/H 配对）也产同样的 Mutex(A,B)，
        /// 那条 Mutex 在它的源 LU 看不到同步证据 → LU 级过滤拦不住 → 入库后变成误判。
        /// 等所有 LU 都跑完后在此扫一次项目级数据补拦。
        ///
        /// 算法（基于 PLD 结构证据，遵守 §4.2 铁律）：
        /// 1. 加载本项目所有 LU 的 PLD
        /// 2. 对每个 LU 调 PldStructuralConstraintAugmenter.ExtractSyncDrivenPairs 取同步对
        /// 3. 聚合所有同步对到项目级集合
        /// 4. 扫所有 2 元 Mutex 命中此集合 → 软删
        /// </summary>
        public async Task<object> RemoveSyncDrivenMutexAsync(long projectId)
        {
            _logger.LogInformation("[Diagnosis] RemoveSyncDrivenMutex: projectId={ProjectId}", projectId);

            // 1. 加载本项目所有有 PLD 的 LU
            var blocks = await _blockService.GetListAsync(x => x.ProjectId == projectId && !x.IsDeleted);
            var blockIds = blocks.Select(b => b.Id).ToList();
            if (blockIds.Count == 0) return new { Success = true, Removed = 0 };

            var lus = await _logicUnitService.GetListAsync(x =>
                x.BlockId.HasValue && blockIds.Contains(x.BlockId.Value)
                && !x.IsDeleted
                && x.RawContent != null && x.RawContent != "");

            // 2-3. 聚合项目级同步对集合（直接从 LogicUnit XML 抽锚点结构，跳过 Markdown round-trip）
            var syncPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var lu in lus)
            {
                var pairs = PldStructuralConstraintAugmenter.ExtractSyncDrivenPairs(lu.RawContent);
                foreach (var p in pairs) syncPairs.Add(p);
            }
            if (syncPairs.Count == 0) return new { Success = true, Removed = 0 };

            // 4. 扫项目级 2 元 Mutex，软删命中
            var mutexRules = await _ioConstraintRuleService.GetListAsync(x =>
                x.ProjectId == projectId && !x.IsDeleted
                && x.Operator == "AT_MOST_ONE");
            if (mutexRules.Count == 0) return new { Success = true, Removed = 0 };

            var ruleIds = mutexRules.Select(r => r.Id).ToList();
            var allSigs = await _ioConstraintSignalService.GetListAsync(
                x => ruleIds.Contains(x.RuleId) && !x.IsDeleted);
            var sigByRule = allSigs.GroupBy(s => s.RuleId).ToDictionary(g => g.Key, g => g.ToList());

            var toRemove = new List<PLC_IOConstraintRuleEntity>();
            foreach (var r in mutexRules)
            {
                if (!sigByRule.TryGetValue(r.Id, out var sigs)) continue;
                var names = sigs.Select(s => s.SignalName?.Trim())
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                bool hit = false;
                for (int i = 0; i < names.Count && !hit; i++)
                    for (int j = i + 1; j < names.Count && !hit; j++)
                    {
                        var a = names[i]!; var b = names[j]!;
                        var key = string.Compare(a, b, StringComparison.OrdinalIgnoreCase) < 0
                            ? $"{a}|{b}" : $"{b}|{a}";
                        if (syncPairs.Contains(key)) hit = true;
                    }
                if (hit) toRemove.Add(r);
            }

            foreach (var r in toRemove)
            {
                await _ioConstraintRuleService.UpdateAsync(
                    u => new PLC_IOConstraintRuleEntity { IsDeleted = true },
                    x => x.Id == r.Id);
                await _ioConstraintSignalService.UpdateAsync(
                    u => new PLC_IOConstraintSignalEntity { IsDeleted = true },
                    x => x.RuleId == r.Id);
            }
            if (toRemove.Count > 0)
            {
                _logger.LogInformation(
                    "[Diagnosis] RemoveSyncDrivenMutex: 软删 {N} 条 Mutex（PLD 显示两信号被同步触点表达式同时驱动，不是反向对）",
                    toRemove.Count);
            }
            return new { Success = true, Removed = toRemove.Count };
        }

        public async Task<object> MergePairMutexIntoNAryAsync(long projectId)
        {
            _logger.LogInformation("[Diagnosis] MergePairMutexIntoNAry: projectId={ProjectId}", projectId);

            var rules = await _ioConstraintRuleService.GetListAsync(
                x => x.ProjectId == projectId && !x.IsDeleted
                  && x.Operator == "AT_MOST_ONE");
            if (rules.Count == 0)
            {
                return new { Success = true, Merged = 0, Reason = "无 Mutex 规则" };
            }
            var ruleIds = rules.Select(r => r.Id).ToList();
            var allSignals = await _ioConstraintSignalService.GetListAsync(
                x => ruleIds.Contains(x.RuleId) && !x.IsDeleted);
            var signalsByRule = allSignals
                .GroupBy(s => s.RuleId)
                .ToDictionary(g => g.Key, g => g.Select(s => s.SignalName?.Trim() ?? "").Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList());

            // 收集 2 元 Mutex 边：(signalA, signalB) → ruleId
            var pairEdges = new Dictionary<(string A, string B), long>();
            foreach (var rule in rules)
            {
                if (!signalsByRule.TryGetValue(rule.Id, out var sigs)) continue;
                if (sigs.Count != 2) continue;
                var sorted = sigs.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
                var key = (A: sorted[0], B: sorted[1]);
                if (!pairEdges.ContainsKey(key)) pairEdges[key] = rule.Id;
            }
            if (pairEdges.Count < 3)
            {
                return new { Success = true, Merged = 0, Reason = "2 元 Mutex 不足" };
            }

            // 构图 & 找连通分量
            var adj = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, _) in pairEdges)
            {
                if (!adj.TryGetValue(k.A, out var sa)) { sa = new HashSet<string>(StringComparer.OrdinalIgnoreCase); adj[k.A] = sa; }
                if (!adj.TryGetValue(k.B, out var sb)) { sb = new HashSet<string>(StringComparer.OrdinalIgnoreCase); adj[k.B] = sb; }
                sa.Add(k.B);
                sb.Add(k.A);
            }
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var components = new List<List<string>>();
            foreach (var node in adj.Keys)
            {
                if (visited.Contains(node)) continue;
                var stack = new Stack<string>();
                stack.Push(node);
                var component = new List<string>();
                while (stack.Count > 0)
                {
                    var n = stack.Pop();
                    if (!visited.Add(n)) continue;
                    component.Add(n);
                    foreach (var m in adj[n]) if (!visited.Contains(m)) stack.Push(m);
                }
                if (component.Count >= 3) components.Add(component);
            }

            // 对每个 N≥3 的连通分量，检查是不是完全图
            var ruleService = _ioConstraintRuleService;
            var signalService = _ioConstraintSignalService;
            int mergedGroups = 0;
            int deletedRules = 0;
            foreach (var comp in components)
            {
                var sorted = comp.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
                var n = sorted.Count;
                var expected = n * (n - 1) / 2;
                // 收集这 n 个节点之间的全部边（如果不齐则跳过）
                var edgesInGroup = new List<long>(); // ruleIds
                var foundEdges = 0;
                for (int i = 0; i < n; i++)
                {
                    for (int j = i + 1; j < n; j++)
                    {
                        var k = (A: sorted[i], B: sorted[j]);
                        if (pairEdges.TryGetValue(k, out var rid))
                        {
                            foundEdges++;
                            edgesInGroup.Add(rid);
                        }
                    }
                }
                if (foundEdges != expected) continue; // 非完全图，不合并

                // 选最早创建的 ruleId 保留，扩展为 N 元 Mutex；其它删除
                var groupRules = rules.Where(r => edgesInGroup.Contains(r.Id)).OrderBy(r => r.CreateTime ?? DateTime.MaxValue).ToList();
                if (groupRules.Count == 0) continue;
                var keep = groupRules[0];

                // 给保留的规则换 signals：变成 N 个 Member
                var newSignalEntities = sorted.Select((sig, idx) => new PLC_IOConstraintSignalEntity
                {
                    RuleId = keep.Id,
                    SignalName = sig,
                    SignalRole = "Member",
                    SignalAddress = allSignals.FirstOrDefault(s => string.Equals(s.SignalName, sig, StringComparison.OrdinalIgnoreCase))?.SignalAddress,
                    SortOrder = idx,
                    IsDeleted = false,
                    CreaterId = keep.CreaterId,
                    CreaterName = keep.CreaterName ?? "AI-Merge",
                    CreateTime = DateTime.Now,
                }).ToList();

                // 删旧 signals + 删其它 group rules + 重写 keep 规则的 signals
                // 1) 删本组所有规则的所有 signals
                await signalService.DeleteAsync(s => edgesInGroup.Contains(s.RuleId));
                // 2) 删除非 keep 的规则（软删）
                var toDelete = edgesInGroup.Where(id => id != keep.Id).ToList();
                if (toDelete.Count > 0)
                {
                    await ruleService.UpdateAsync(
                        u => new PLC_IOConstraintRuleEntity { IsDeleted = true, ModifyTime = DateTime.Now },
                        u => toDelete.Contains(u.Id));
                    deletedRules += toDelete.Count;
                }
                // 3) 给 keep 规则插入新 N 个 signals
                foreach (var s in newSignalEntities)
                {
                    await signalService.InsertAsync(s);
                }
                // 4) 更新 keep 规则的描述
                var newDesc = $"信号 {string.Join(", ", sorted)} 之间互斥，同一时刻最多允许其中一个为 true（由 {expected} 条 2 元 Mutex 合并）。";
                var newName = $"{sorted[0]}_等{n}信号_互斥";
                await ruleService.UpdateAsync(
                    u => new PLC_IOConstraintRuleEntity { RuleName = newName, Description = newDesc, ModifyTime = DateTime.Now },
                    u => u.Id == keep.Id);
                mergedGroups++;
                _logger.LogInformation("[Diagnosis] 合并 {N} 元 Mutex 完全图：{Signals}，保留 ruleId={Keep}，删除 {DelN} 条冗余",
                    n, string.Join(",", sorted), keep.Id, toDelete.Count);
            }

            return new { Success = true, MergedGroups = mergedGroups, DeletedRules = deletedRules };
        }

        /// <param name="projectId">项目ID</param>
        /// <param name="constraintsJson">约束规则 JSON 数组</param>
        public async Task<object> SaveIOConstraintsAsync(
            long projectId,
            string constraintsJson,
            long? sourceLuId = null,
            IReadOnlyCollection<string>? extraAllowedSignals = null)
        {
            _logger.LogInformation("[Diagnosis] SaveIOConstraints: projectId={ProjectId}, sourceLuId={LuId}, extraAllowed={ExtraCount}",
                projectId, sourceLuId, extraAllowedSignals?.Count ?? 0);

            // 把 extraAllowedSignals 收成大小写不敏感的 set，下游过滤/方向校验复用。
            // 这里只承载「本 LU SCoil/RCoil/Coil 真实驱动的 DB 内部信号」，
            // 由调用方按 LU 范围收集 → 避免跨 LU 信号串入，结构判定不依赖命名前缀。
            var extraAllowedSet = extraAllowedSignals == null || extraAllowedSignals.Count == 0
                ? null
                : new HashSet<string>(
                    extraAllowedSignals.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()),
                    StringComparer.OrdinalIgnoreCase);

            try
            {
                if (string.IsNullOrWhiteSpace(constraintsJson))
                {
                    return new { Success = false, Message = "constraintsJson 不能为空" };
                }

                // 1. 解析 JSON
                List<ConstraintInput>? inputs;
                try
                {
                    inputs = JsonSerializer.Deserialize<List<ConstraintInput>>(constraintsJson,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (Exception ex)
                {
                    return new { Success = false, Message = $"JSON 解析失败: {ex.Message}" };
                }

                if (inputs == null || inputs.Count == 0)
                {
                    return new { Success = false, Message = "解析后约束列表为空" };
                }

                // 2. 加载已有规则用于去重
                // 注意：MergeExistingDuplicateRulesAsync 不在此处自动调用——
                // 它是 O(N²) 重操作且对每个合并组会触发 AI 描述生成，每个 Tag 调用都执行会导致 LLM 配额耗尽。
                // 如需修复历史重复，请在用户手动"清空重建"入口调用一次。

                var existingRules = await _ioConstraintRuleService.GetListAsync(
                    x => x.ProjectId == projectId && !x.IsDeleted);
                var projectIOTags = await _ioTagService.GetListAsync(
                    x => x.ProjectId == projectId && !x.IsDeleted);
                var ioTagByName = projectIOTags
                    .Where(x => !string.IsNullOrWhiteSpace(x.TagName))
                    .GroupBy(x => x.TagName!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                // 信号名 → IO Tag comment(业务描述) 映射,用于 description 增强:
                // 源 PLC 项目里部分 TagName 是裸地址(如 Q102.3),业务名只在 comment 字段(如"OP1030_TW01塔灯_红")。
                // 把这个字典挂到每个 input,让 description 生成时把信号名后面追加 (comment),提升可读性。
                var ioTagCommentsByName = ioTagByName
                    .Where(kv => !string.IsNullOrWhiteSpace(kv.Value.Comment))
                    .ToDictionary(kv => kv.Key, kv => kv.Value.Comment!, StringComparer.OrdinalIgnoreCase);
                if (ioTagCommentsByName.Count > 0)
                {
                    foreach (var inp in inputs)
                    {
                        inp.IoTagCommentsByName = ioTagCommentsByName;
                    }
                }

                // 一次性扫描：识别 CPU 时钟字节（结构判定：%M 区，同字节 6+ 个 Bool bit + comment 是周期格式）
                var cpuClockBytes = DetectCpuClockBytes(ioTagByName);
                if (cpuClockBytes.Count > 0)
                {
                    _logger.LogInformation("[Diagnosis] 识别 CPU 时钟字节：{Bytes}（这些字节里的所有 Bool bit 都是脉冲位，不参与约束）",
                        string.Join(", ", cpuClockBytes.Select(b => $"%M{b}")));
                }

                var existingSignals = existingRules.Count > 0
                    ? await _ioConstraintSignalService.GetListAsync(
                        x => existingRules.Select(r => r.Id).Contains(x.RuleId) && !x.IsDeleted)
                    : new List<PLC_IOConstraintSignalEntity>();

                // 2a-0. 整条占位符防御：任意 signal 命中 IsPlaceholderSignal (Null/AlwaysTRUE/AlwaysFALSE/Debug_*) 的约束整条丢。
                // 单信号级 IsAllowedConstraintSignal 已会过滤这些占位符，但若一条约束被过滤后剩 ≥2 个合法信号，
                // 会出现「Source=AlwaysFALSE 被丢、剩 Target 一堆」这种角色破损但 count≥2 的约束依然入库的漏洞。
                // 这一层整条扔，是最保险的兜底。
                int placeholderInputsDropped = inputs.RemoveAll(it =>
                    it.Signals != null && it.Signals.Any(s => IsPlaceholderSignal(s.SignalName)));
                if (placeholderInputsDropped > 0)
                {
                    _logger.LogInformation(
                        "[Diagnosis] 丢弃 {Count} 条含占位/常量/调试信号 (Null/AlwaysTRUE/AlwaysFALSE/Debug_*) 的约束",
                        placeholderInputsDropped);
                }

                // 2a. 拒绝已废弃的 AlarmBinding 约束类型。AlarmBinding 描述的是"输出超时→报警"这种带时延的因果，
                // 不能用瞬时布尔约束（IMPLIES）表达。AI 即使生成 ruleType=AlarmBinding 也直接丢弃。
                int alarmBindingDropped = inputs.RemoveAll(it =>
                    string.Equals(it.RuleType, "AlarmBinding", StringComparison.OrdinalIgnoreCase));
                if (alarmBindingDropped > 0)
                {
                    _logger.LogInformation("[Diagnosis] 丢弃 {Count} 条 AlarmBinding 约束（该类型已废弃）", alarmBindingDropped);
                }

                // 2a-2. operator 合法性校验：尝试纠正常见拼写错误（如 AT_MOOT_ONE → AT_MOST_ONE），无法纠正的丢弃
                var validOperators = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "AT_MOST_ONE", "EXACTLY_ONE", "ALL_TRUE", "IMPLIES", "REQUIRES_0", "CHAIN_ORDER", "CUSTOM",
                    "SAME_STATE",  // 同步驱动：全部 Member 状态必须一致（同 true 或同 false）
                    "REQUIRES_0_WHEN_FALSE",  // 反向零态联锁：Source=false（如急停按下）时 Target 必须 false。用于博图 FB E_StopStatus 等「状态有效」语义信号
                    "MUST_BE_FALSE"  // 单信号永禁：单 Target 必须永为 false。专给屏蔽态 SCoil 报警网络识别的传感器（§4.5）。每个传感器各成一条约束，告警粒度独立
                };
                var operatorFixMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["AT_MOOT_ONE"] = "AT_MOST_ONE",
                    ["AT_MOST_ONCE"] = "AT_MOST_ONE",
                    ["AT_LEAST_ONE"] = "EXACTLY_ONE",
                    ["ALL_FALSE"] = "REQUIRES_0",
                };
                int operatorFixed = 0, operatorDropped = 0;
                inputs.RemoveAll(it =>
                {
                    var op = (it.Operator ?? "").Trim();
                    if (validOperators.Contains(op)) return false;
                    if (operatorFixMap.TryGetValue(op, out var fixedOp))
                    {
                        it.Operator = fixedOp;
                        operatorFixed++;
                        return false;
                    }
                    operatorDropped++;
                    return true;
                });
                if (operatorFixed > 0 || operatorDropped > 0)
                {
                    _logger.LogInformation("[Diagnosis] operator 校验：纠正 {Fixed} 条拼错，丢弃 {Dropped} 条无效 operator", operatorFixed, operatorDropped);
                }

                // 2a-3. Pairing (IMPLIES) 方向纠正：
                // 物理铁律：Pairing 必须是 Q(%Q) 输出 → I(%I) 反馈，反方向（I→Q）物理上不可能存在。
                // - 若 Source 地址是 %I、Target 地址是 %Q → 互换 Source/Target
                // - 若两端都是 %Q 或都是 %I → 不能是 Pairing，整条丢弃
                // 地址回填：每个 signal 没填 address 的，从 ioTagByName 反查回填
                int pairingFixedDir = 0, pairingDroppedSameDir = 0, addressBackfilled = 0;
                inputs.RemoveAll(it =>
                {
                    // 先把所有 signals 的 SignalAddress 回填（不论约束类型）
                    if (it.Signals != null)
                    {
                        foreach (var s in it.Signals)
                        {
                            if (string.IsNullOrWhiteSpace(s.SignalAddress)
                                && !string.IsNullOrWhiteSpace(s.SignalName)
                                && ioTagByName.TryGetValue(s.SignalName.Trim(), out var tag))
                            {
                                s.SignalAddress = tag.Address;
                                addressBackfilled++;
                            }
                        }
                    }

                    // 只对 IMPLIES 做方向校验
                    if (!string.Equals(it.Operator, "IMPLIES", StringComparison.OrdinalIgnoreCase)) return false;
                    if (it.Signals == null || it.Signals.Count < 2) return false;

                    var sources = it.Signals.Where(s => string.Equals(s.SignalRole, "Source", StringComparison.OrdinalIgnoreCase)).ToList();
                    var targets = it.Signals.Where(s => string.Equals(s.SignalRole, "Target", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (sources.Count == 0 || targets.Count == 0) return false; // 缺角色，由后续结构校验处理

                    // 判断每个 Source/Target 是 Q 还是 I（基于已回填的 SignalAddress）
                    bool IsQ(SignalInput s) => (s.SignalAddress ?? "").StartsWith("%Q", StringComparison.OrdinalIgnoreCase);
                    bool IsI(SignalInput s) => (s.SignalAddress ?? "").StartsWith("%I", StringComparison.OrdinalIgnoreCase);

                    bool allSourcesQ = sources.All(IsQ);
                    bool allSourcesI = sources.All(IsI);
                    bool allTargetsQ = targets.All(IsQ);
                    bool allTargetsI = targets.All(IsI);

                    // 反向：Source 全是 I、Target 全是 Q → 互换
                    if (allSourcesI && allTargetsQ)
                    {
                        foreach (var s in sources) s.SignalRole = "Target";
                        foreach (var t in targets) t.SignalRole = "Source";
                        pairingFixedDir++;
                        return false; // 纠正后保留
                    }

                    // 同方向：两端都 Q 或都 I → 不能是 Pairing，丢弃
                    if ((allSourcesQ && allTargetsQ) || (allSourcesI && allTargetsI))
                    {
                        pairingDroppedSameDir++;
                        return true;
                    }

                    // 混合情况（部分 Q 部分 I，或含 DB 变量）：保持不变，由后续白名单/结构校验处理
                    return false;
                });
                if (pairingFixedDir > 0 || pairingDroppedSameDir > 0 || addressBackfilled > 0)
                {
                    _logger.LogInformation("[Diagnosis] Pairing 方向校验：纠正 {Fixed} 条反向，丢弃 {Dropped} 条同方向；回填 {Backfilled} 个 SignalAddress",
                        pairingFixedDir, pairingDroppedSameDir, addressBackfilled);
                }

                // 2a-3b. IMPLIES 多 Target 拆分：1 Source + N Target (N>=2) 拆为 N 条 1:1 Pairing。
                // 原因：
                //   - LLM 习惯输出 "OP020_YV04_W → CYL02_1_W + CYL02_2_W + ... + CYL02_4_W" 这种紧凑写法，
                //     但 Pairing 语义是 1:1（Q 驱动 → I 反馈），多 Target 在结构上更像 AllRequired。
                //   - 拆为多条独立 1:1 Pairing 后，每条都能独立做反向校验、互斥校验；
                //     这也直接补上了「Pairing(Q→I) 大量缺失」中由 LLM 紧凑写法造成的那部分。
                // 边界：
                //   - 拆分后 Target 各为 1 个；ruleName / description 沿用原条目，由 BuildCompactConstraintRuleName 重生。
                //   - 拆分前先扫本批 inputs 的 Mutex 对：若多 Target 中含反向互斥对（如 CYL_W ↔ CYL_H），
                //     整条 IMPLIES 丢——一个 Q 不可能同时驱动两个互斥的 I 反馈（物理矛盾）。
                //     典型问题约束 `OP030_YV15_W → [CYL15_W, CYL15_H]` 等都靠这步拦掉。
                var localMutexPairsForSplit = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var mtx in inputs)
                {
                    if (!string.Equals(mtx.Operator, "AT_MOST_ONE", StringComparison.OrdinalIgnoreCase)) continue;
                    if (mtx.Signals == null) continue;
                    var members = mtx.Signals
                        .Select(s => s.SignalName?.Trim())
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .ToList();
                    for (int i = 0; i < members.Count; i++)
                        for (int j = i + 1; j < members.Count; j++)
                        {
                            var a = members[i]; var b = members[j];
                            var key = string.Compare(a, b, StringComparison.OrdinalIgnoreCase) < 0 ? $"{a}|{b}" : $"{b}|{a}";
                            localMutexPairsForSplit.Add(key);
                        }
                }
                int impliesContradictoryDropped = inputs.RemoveAll(it =>
                {
                    if (!string.Equals(it.Operator, "IMPLIES", StringComparison.OrdinalIgnoreCase)) return false;
                    if (it.Signals == null) return false;
                    var tgts = it.Signals.Where(s => string.Equals(s.SignalRole, "Target", StringComparison.OrdinalIgnoreCase))
                        .Select(s => s.SignalName?.Trim()).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
                    if (tgts.Count < 2) return false;
                    for (int i = 0; i < tgts.Count; i++)
                        for (int j = i + 1; j < tgts.Count; j++)
                        {
                            var a = tgts[i]; var b = tgts[j];
                            var key = string.Compare(a, b, StringComparison.OrdinalIgnoreCase) < 0 ? $"{a}|{b}" : $"{b}|{a}";
                            if (localMutexPairsForSplit.Contains(key)) return true;
                        }
                    return false;
                });
                if (impliesContradictoryDropped > 0)
                {
                    _logger.LogInformation("[Diagnosis] IMPLIES 物理矛盾过滤：丢弃 {Count} 条多 Target 中含互斥反向对的约束 (如 Source→[W,H] 反向气缸到位)",
                        impliesContradictoryDropped);
                }

                // 2a-3b-2. ALL_TRUE 物理矛盾过滤：Member 之间含 Mutex 反向对 → 整条丢。
                // 语义：ALL_TRUE Source=YV07_W, Members=[CYL07_W, CYL07_H] 要求 W 阀激活时 CYL07_W + CYL07_H **都**为 True，
                // 但 CYL07_W 与 CYL07_H 是 Mutex 反向对（不可能同时 True），所以这条约束永远会违反，是 LLM 错误识别同步语义。
                // 典型 bug：OP030_YV07_W_前置全满足 Members=[CYL07_W, CYL07_H] 把反向对当多缸同步。
                int allTrueContradictoryDropped = inputs.RemoveAll(it =>
                {
                    if (!string.Equals(it.Operator, "ALL_TRUE", StringComparison.OrdinalIgnoreCase)) return false;
                    if (it.Signals == null) return false;
                    var mems = it.Signals.Where(s => string.Equals(s.SignalRole, "Member", StringComparison.OrdinalIgnoreCase))
                        .Select(s => s.SignalName?.Trim()).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
                    if (mems.Count < 2) return false;
                    for (int i = 0; i < mems.Count; i++)
                        for (int j = i + 1; j < mems.Count; j++)
                        {
                            var a = mems[i]; var b = mems[j];
                            var key = string.Compare(a, b, StringComparison.OrdinalIgnoreCase) < 0 ? $"{a}|{b}" : $"{b}|{a}";
                            if (localMutexPairsForSplit.Contains(key)) return true;
                        }
                    return false;
                });
                if (allTrueContradictoryDropped > 0)
                {
                    _logger.LogInformation("[Diagnosis] ALL_TRUE 物理矛盾过滤：丢弃 {Count} 条 Members 中含互斥反向对的约束 (如 Source→[CYL_W, CYL_H] 反向反馈对同时为 True 物理不可能)",
                        allTrueContradictoryDropped);
                }

                int impliesSplitOriginal = 0, impliesSplitProduced = 0;
                var afterImpliesSplit = new List<ConstraintInput>(inputs.Count);
                foreach (var inp in inputs)
                {
                    if (!string.Equals(inp.Operator, "IMPLIES", StringComparison.OrdinalIgnoreCase)
                        || inp.Signals == null)
                    {
                        afterImpliesSplit.Add(inp);
                        continue;
                    }
                    var sources = inp.Signals.Where(s => string.Equals(s.SignalRole, "Source", StringComparison.OrdinalIgnoreCase)).ToList();
                    var targets = inp.Signals.Where(s => string.Equals(s.SignalRole, "Target", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (sources.Count != 1 || targets.Count <= 1)
                    {
                        afterImpliesSplit.Add(inp);
                        continue;
                    }
                    impliesSplitOriginal++;
                    foreach (var t in targets)
                    {
                        var splitInp = new ConstraintInput
                        {
                            RuleName = inp.RuleName,
                            RuleType = inp.RuleType,
                            Operator = inp.Operator,
                            EquipmentId = inp.EquipmentId,
                            Confidence = inp.Confidence,
                            Source = inp.Source,
                            Priority = inp.Priority,
                            Description = inp.Description,
                            ValidStates = inp.ValidStates,
                            Signals = new List<SignalInput>
                            {
                                new SignalInput
                                {
                                    SignalName = sources[0].SignalName,
                                    SignalAddress = sources[0].SignalAddress,
                                    SignalRole = "Source",
                                    SortOrder = 0
                                },
                                new SignalInput
                                {
                                    SignalName = t.SignalName,
                                    SignalAddress = t.SignalAddress,
                                    SignalRole = "Target",
                                    SortOrder = 1
                                }
                            }
                        };
                        afterImpliesSplit.Add(splitInp);
                        impliesSplitProduced++;
                    }
                }
                if (impliesSplitOriginal > 0)
                {
                    _logger.LogInformation("[Diagnosis] IMPLIES 多 Target 拆分：原 {Orig} 条 1→N IMPLIES 拆为 {Produced} 条 1:1 Pairing",
                        impliesSplitOriginal, impliesSplitProduced);
                }
                inputs = afterImpliesSplit;

                // 2a-3c. IMPLIES 单条方向硬校验（拆分后再扫一遍兜底）：
                // - Source 地址必须是 %Q 或 无地址但末段命中 KeyFbPin（如 General.ManualMod 触发的命令对）
                // - Target 地址必须是 %I 或 无地址但末段命中 KeyFbPin
                // - 任意一端地址为 %Q (Source) / %I (Target) 之外的具体地址 → 丢
                // 修复异常：HMI_Reset_PB(%I) → ResetStatus、AutoRunMod → 启动PLC1/PLC2(%Q) 等
                //
                // SCoil 驱动 sensor→alarm 的例外（结构判定，§4.2 兼容）：
                // 当 IMPLIES 的所有 Target 都是「无 %Q/%I 地址 + 在 extraAllowedSet 里」（= 本 LU SCoil 驱动的 DB 内部信号），
                // 此时 Source 允许是 %I（典型：传感器触发 SCoil 置位报警位）；其它情况仍按 Q→I 物理铁律。
                int impliesHardDirDropped = inputs.RemoveAll(it =>
                {
                    if (!string.Equals(it.Operator, "IMPLIES", StringComparison.OrdinalIgnoreCase)) return false;
                    if (it.Signals == null || it.Signals.Count < 2) return false;
                    var sources = it.Signals.Where(s => string.Equals(s.SignalRole, "Source", StringComparison.OrdinalIgnoreCase)).ToList();
                    var targets = it.Signals.Where(s => string.Equals(s.SignalRole, "Target", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (sources.Count == 0 || targets.Count == 0) return false;

                    // 检测 SCoil sensor→alarm 模式：所有 Target 都是 DB 内部信号（无 %Q/%I/%M 地址）+ 在 extraAllowedSet 里
                    bool IsScoilAlarmTarget(SignalInput s)
                    {
                        var addr = s.SignalAddress ?? string.Empty;
                        if (addr.StartsWith("%Q", StringComparison.OrdinalIgnoreCase)) return false;
                        if (addr.StartsWith("%I", StringComparison.OrdinalIgnoreCase)) return false;
                        if (addr.StartsWith("%M", StringComparison.OrdinalIgnoreCase)) return false;
                        return extraAllowedSet != null
                            && !string.IsNullOrWhiteSpace(s.SignalName)
                            && extraAllowedSet.Contains(s.SignalName.Trim());
                    }
                    bool isScoilAlarmPattern = targets.All(IsScoilAlarmTarget);

                    bool OkAsSource(SignalInput s)
                    {
                        var addr = s.SignalAddress ?? string.Empty;
                        if (addr.StartsWith("%Q", StringComparison.OrdinalIgnoreCase)) return true;
                        // SCoil sensor→alarm 模式：Source 允许 %I（传感器触发置位）
                        if (addr.StartsWith("%I", StringComparison.OrdinalIgnoreCase))
                            return isScoilAlarmPattern;
                        if (addr.StartsWith("%M", StringComparison.OrdinalIgnoreCase)) return false; // M 通常是中间位 / 占位
                        // 无地址：必须末段命中 KeyFbPin（如 General.AutoRunMod / ManualMod）
                        var name = s.SignalName ?? string.Empty;
                        var lastDot = name.LastIndexOf('.');
                        var leaf = lastDot >= 0 ? name[(lastDot + 1)..] : name;
                        return !string.IsNullOrWhiteSpace(leaf) && KeyFbPinNames.Contains(leaf);
                    }
                    bool OkAsTarget(SignalInput s)
                    {
                        var addr = s.SignalAddress ?? string.Empty;
                        if (addr.StartsWith("%I", StringComparison.OrdinalIgnoreCase)) return true;
                        if (addr.StartsWith("%Q", StringComparison.OrdinalIgnoreCase)) return false; // Q 不可作 Target
                        if (addr.StartsWith("%M", StringComparison.OrdinalIgnoreCase)) return false;
                        // SCoil sensor→alarm 模式：Target 允许在 extraAllowedSet 的 DB 内部信号
                        if (isScoilAlarmPattern) return IsScoilAlarmTarget(s);
                        var name = s.SignalName ?? string.Empty;
                        var lastDot = name.LastIndexOf('.');
                        var leaf = lastDot >= 0 ? name[(lastDot + 1)..] : name;
                        return !string.IsNullOrWhiteSpace(leaf) && KeyFbPinNames.Contains(leaf);
                    }

                    return !sources.All(OkAsSource) || !targets.All(OkAsTarget);
                });
                if (impliesHardDirDropped > 0)
                {
                    _logger.LogInformation("[Diagnosis] IMPLIES 方向硬校验：丢弃 {Count} 条 Source/Target 地址类不符 Pairing 方向铁律的约束 (Source 必须 %Q 或 KeyFbPin，Target 必须 %I 或 KeyFbPin)",
                        impliesHardDirDropped);
                }

                // 2a-3d. REQUIRES_0 (Safety) 多 Source 拆分：N Source + M Target → N 条 1 Source + M Target。
                // 现象：LLM 偶尔把多个工位的 E_StopStatus 都列为 Source，使得一条 Safety 含 7 个 Source，
                // 语义模糊（无法用单值表达"任一 Source 触发"还是"所有 Source 同时触发"）。统一拆为单 Source。
                int safetyMultiSrcSplitOriginal = 0, safetyMultiSrcSplitProduced = 0;
                var afterSafetySplit = new List<ConstraintInput>(inputs.Count);
                foreach (var inp in inputs)
                {
                    if (!string.Equals(inp.Operator, "REQUIRES_0", StringComparison.OrdinalIgnoreCase)
                        || inp.Signals == null)
                    {
                        afterSafetySplit.Add(inp);
                        continue;
                    }
                    var sources = inp.Signals.Where(s => string.Equals(s.SignalRole, "Source", StringComparison.OrdinalIgnoreCase)).ToList();
                    var targets = inp.Signals.Where(s => string.Equals(s.SignalRole, "Target", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (sources.Count <= 1 || targets.Count == 0)
                    {
                        afterSafetySplit.Add(inp);
                        continue;
                    }
                    safetyMultiSrcSplitOriginal++;
                    foreach (var src in sources)
                    {
                        var splitInp = new ConstraintInput
                        {
                            RuleName = inp.RuleName,
                            RuleType = inp.RuleType,
                            Operator = inp.Operator,
                            EquipmentId = inp.EquipmentId,
                            Confidence = inp.Confidence,
                            Source = inp.Source,
                            Priority = inp.Priority,
                            Description = inp.Description,
                            ValidStates = inp.ValidStates,
                            Signals = new List<SignalInput>
                            {
                                new SignalInput
                                {
                                    SignalName = src.SignalName,
                                    SignalAddress = src.SignalAddress,
                                    SignalRole = "Source",
                                    SortOrder = 0
                                }
                            }
                        };
                        for (int i = 0; i < targets.Count; i++)
                        {
                            splitInp.Signals.Add(new SignalInput
                            {
                                SignalName = targets[i].SignalName,
                                SignalAddress = targets[i].SignalAddress,
                                SignalRole = "Target",
                                SortOrder = i + 1
                            });
                        }
                        afterSafetySplit.Add(splitInp);
                        safetyMultiSrcSplitProduced++;
                    }
                }
                if (safetyMultiSrcSplitOriginal > 0)
                {
                    _logger.LogInformation("[Diagnosis] REQUIRES_0 多 Source 拆分：原 {Orig} 条 N→M Safety 拆为 {Produced} 条 1→M Safety",
                        safetyMultiSrcSplitOriginal, safetyMultiSrcSplitProduced);
                }
                inputs = afterSafetySplit;

                // 2a-3e. REQUIRES_0 Source 硬校验（2026-05-26 改造 — 语义反转）：
                // **铁律**：博图 FB InputParam E_StopStatus/ResetStatus/SafetyStatus/SafeStop 是「状态有效」语义
                //   - E_StopStatus = true → 急停链路正常（FB 允许动作）
                //   - E_StopStatus = false → 急停按下（FB 强制停止）
                // 而 REQUIRES_0 语义是「Source=true 时 Target 必须为 false」——
                //   如果用这类信号作 Source，等于「急停链路正常时输出必须断电」，方向完全反了！
                // 历史上 LLM 大量误产 `Safety(Source=E_StopStatus, Target=[YV*_H, ...])` 导致诊断引擎在
                //   PLC 正常运行时（E_StopStatus=true）误报"所有气缸输出违规"——已撤掉 CheckConstraintsAsync
                //   的 `RuleType != "Safety"` 过滤后这些误报全部暴露（详见 §六 2026-05-26 修复历史）。
                //
                // 因此**完全禁止** E_StopStatus/ResetStatus/SafetyStatus/SafeStop 作为 REQUIRES_0 Source。
                // 唯一合法的 REQUIRES_0 是「无 Source + ≥2 Target」（§4.5 SCoil 屏蔽态报警网络）。
                var bannedSafetySourcePins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "E_StopStatus", "ResetStatus", "SafetyStatus", "SafeStop"
                };
                int requires0SrcDropped = inputs.RemoveAll(it =>
                {
                    if (!string.Equals(it.Operator, "REQUIRES_0", StringComparison.OrdinalIgnoreCase)) return false;
                    if (it.Signals == null || it.Signals.Count < 2) return false;
                    var sources = it.Signals.Where(s => string.Equals(s.SignalRole, "Source", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (sources.Count == 0) return false;  // 无 Source 的 REQUIRES_0（SCoil 屏蔽态）放行，由 2b-2 单独校验
                    foreach (var s in sources)
                    {
                        var name = s.SignalName ?? string.Empty;
                        var lastDot = name.LastIndexOf('.');
                        var leaf = lastDot >= 0 ? name[(lastDot + 1)..] : name;
                        // Source 末段命中黑名单 (E_Stop/Reset/Safety/SafeStop) → 整条丢
                        // 任何其它信号作 Source 也丢（约束模型不支持任意"Source 触发"语义，避免假因果）
                        if (bannedSafetySourcePins.Contains(leaf)) return true;
                        // 其它 Source 也丢（业务态信号 ManualMod / AutoRunMod / FwdPmt 等不应作零态约束 Source）
                        return true;
                    }
                    return false;
                });
                if (requires0SrcDropped > 0)
                {
                    _logger.LogInformation("[Diagnosis] REQUIRES_0 Source 硬校验：丢弃 {Count} 条带 Source 的 Safety (E_Stop/Reset/SafetyStatus 是「状态有效」语义不能作触发 Source；仅允许「无 Source + ≥2 Target」的 SCoil 屏蔽态约束)",
                        requires0SrcDropped);
                }

                // 2a-3f. REQUIRES_0_WHEN_FALSE Source 硬校验（NEW 2026-05-26）：
                // 反向零态联锁（Source=false 时 Target 必须 false）专门表达急停/复位 FB 「状态有效」语义信号。
                // 因此 Source 末段**必须**是 E_StopStatus / ResetStatus / SafetyStatus / SafeStop 之一，且 Target ≥ 1。
                // 不命中（如 ManualMod 等模式信号）→ 整条丢；无 Source → 整条丢。
                int reqWhenFalseDropped = inputs.RemoveAll(it =>
                {
                    if (!string.Equals(it.Operator, "REQUIRES_0_WHEN_FALSE", StringComparison.OrdinalIgnoreCase)) return false;
                    if (it.Signals == null || it.Signals.Count < 2) return true;
                    var sources = it.Signals.Where(s => string.Equals(s.SignalRole, "Source", StringComparison.OrdinalIgnoreCase)).ToList();
                    var targets = it.Signals.Where(s => string.Equals(s.SignalRole, "Target", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (sources.Count == 0 || targets.Count == 0) return true;
                    foreach (var s in sources)
                    {
                        var name = s.SignalName ?? string.Empty;
                        var lastDot = name.LastIndexOf('.');
                        var leaf = lastDot >= 0 ? name[(lastDot + 1)..] : name;
                        if (!bannedSafetySourcePins.Contains(leaf)) return true;  // 末段必须命中 E_Stop/Reset/Safety/SafeStop
                    }
                    return false;
                });
                if (reqWhenFalseDropped > 0)
                {
                    _logger.LogInformation("[Diagnosis] REQUIRES_0_WHEN_FALSE Source 硬校验：丢弃 {Count} 条 Source 不符规范的反向零态联锁 (Source 末段必须是 E_StopStatus/ResetStatus/SafetyStatus/SafeStop)",
                        reqWhenFalseDropped);
                }

                // 2a-4. FB 实例形参兜底过滤：Valve[N].xxx / Cylinder[N].xxx / Motor[N].xxx 等
                // 是 FB 实例引脚形参名，不是真实信号。Phase2 prompt 已要求 AI 替换为实际 IO Tag，
                // 这里做兜底：若 AI 漏替换，整条约束丢弃（不应有形参进约束表）。
                int fbInstanceParamsDropped = inputs.RemoveAll(it =>
                {
                    if (it.Signals == null) return false;
                    return it.Signals.Any(s =>
                        !string.IsNullOrWhiteSpace(s.SignalName)
                        && System.Text.RegularExpressions.Regex.IsMatch(
                            s.SignalName,
                            @"^[A-Za-z_][A-Za-z0-9_]*\[\d+\]\.",
                            System.Text.RegularExpressions.RegexOptions.None));
                });
                if (fbInstanceParamsDropped > 0)
                {
                    _logger.LogInformation("[Diagnosis] 丢弃 {Count} 条含 FB 实例形参（如 Valve[N].QFwd）的约束", fbInstanceParamsDropped);
                }

                // 2a-5. 同向 I 信号互斥兜底过滤：
                // 通用原则：Mutex 只允许在同一 FB 实例的反向引脚或反向 Q 输出之间。
                // 多个独立的 I 输入传感器（不论命名是 -SQ/-FZ/-LS 还是别的）物理上是独立的，
                // 可以同时为 true，不应该互斥。
                // 判定：Mutex 的 signals 全部是 I 输入（%I 地址），且不属于"同一气缸反向到位"模式
                //      → 视为多个独立传感器互斥，丢弃。
                // "同一气缸反向到位"如何识别？最稳的办法是 Step1 ParsedLogicDescription 第 9 段
                // 已明确写出来的同 FB 反向 I 反馈对；但本兜底层拿不到那段描述，所以采用保守判定：
                // 只在 2 个 I 信号之间允许 Mutex；3 个以上 I 信号互斥一定是误判。
                int multiIMutexDropped = inputs.RemoveAll(it =>
                {
                    if (!string.Equals(it.Operator, "AT_MOST_ONE", StringComparison.OrdinalIgnoreCase)) return false;
                    if (it.Signals == null || it.Signals.Count < 3) return false;
                    bool allInput = it.Signals.All(s =>
                        !string.IsNullOrWhiteSpace(s.SignalAddress)
                        && s.SignalAddress.StartsWith("%I", StringComparison.OrdinalIgnoreCase));
                    if (!allInput) return false;
                    // 例外：Augmenter 产的结构性约束（source=Structural）已经基于「同 FB 实例 + 同字节」严格证据判定，
                    // 不是 LLM 凭命名相似的猜测 — 放行。典型场景：V90/S210 伺服 FB 的位置传感器组 Mutex
                    // （Stop_cam_minus / Servo_HP / Stop_cam_plus 三引脚的 %I 信号互斥，详见 §4.5）
                    if (string.Equals(it.Source, "Structural", StringComparison.OrdinalIgnoreCase)) return false;
                    return true;
                });
                if (multiIMutexDropped > 0)
                {
                    _logger.LogInformation("[Diagnosis] 丢弃 {Count} 条 3+ 个 I 输入信号之间的 Mutex（独立传感器物理上允许多个同时为 true）", multiIMutexDropped);
                }

                // 2a-5b2. Mutex 含 Q+I 混合信号 → 拒绝
                // 同 FB 反向 Q 输出对（QFwd/QBkd）= 全 %Q；同 FB 反向 I 反馈对（IWP/IHP）= 全 %I。
                // Q+I 混在同一条 Mutex 是 LLM 错把 Pairing/AllRequired 的「驱动+反馈」当成互斥的标志。
                int qiMixedMutexDropped = inputs.RemoveAll(it =>
                {
                    if (!string.Equals(it.Operator, "AT_MOST_ONE", StringComparison.OrdinalIgnoreCase)) return false;
                    if (it.Signals == null || it.Signals.Count < 2) return false;
                    bool hasQ = it.Signals.Any(s => (s.SignalAddress ?? "").StartsWith("%Q", StringComparison.OrdinalIgnoreCase));
                    bool hasI = it.Signals.Any(s => (s.SignalAddress ?? "").StartsWith("%I", StringComparison.OrdinalIgnoreCase));
                    return hasQ && hasI;
                });
                if (qiMixedMutexDropped > 0)
                {
                    _logger.LogInformation("[Diagnosis] 丢弃 {Count} 条 Q+I 混合 Mutex（同 FB 反向引脚对必须同方向）", qiMixedMutexDropped);
                }

                // 2a-5b3. 4+ 元同字节 %I Mutex → 拒绝
                // 同字节多个 I 输入信号互斥是典型 LLM 误判：N 个独立机构的传感器在硬件上常被分配到同字节（设计紧凑），
                // 但物理上各自独立，可以同时全为 true（如 4 个夹爪同时张开）。
                // 同 FB 反向 I 反馈对最多 2 元（IWP/IHP），不可能 4+ 元同方向。
                int multiSameByteIMutexDropped = inputs.RemoveAll(it =>
                {
                    if (!string.Equals(it.Operator, "AT_MOST_ONE", StringComparison.OrdinalIgnoreCase)) return false;
                    if (it.Signals == null || it.Signals.Count < 4) return false;
                    if (!it.Signals.All(s => (s.SignalAddress ?? "").StartsWith("%I", StringComparison.OrdinalIgnoreCase))) return false;
                    // 例外：Augmenter 产的结构性约束（source=Structural）已经基于同 FB 实例 + 同字节严格证据判定 — 放行
                    if (string.Equals(it.Source, "Structural", StringComparison.OrdinalIgnoreCase)) return false;
                    // 看是不是同字节
                    var bytes = new HashSet<string>();
                    foreach (var s in it.Signals)
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(s.SignalAddress ?? "", @"^%I(\d+)\.\d+$");
                        if (!m.Success) return false;
                        bytes.Add(m.Groups[1].Value);
                    }
                    return bytes.Count == 1;  // 4+ 个 %I 信号都在同一字节
                });
                if (multiSameByteIMutexDropped > 0)
                {
                    _logger.LogInformation("[Diagnosis] 丢弃 {Count} 条 4+ 元同字节 %I Mutex（N 个独立传感器布在同字节是设计紧凑，不代表互斥）", multiSameByteIMutexDropped);
                }

                // 2a-5c. 2 元 %I/%Q Mutex 跨字节拒绝（保留同字节非相邻位 — 让合并算法识别完全图后再处理）：
                // 跨字节（如 %I1339.0 ↔ %I1342.0 双工位双胞胎信号）几乎肯定不是同 FB 反向引脚对，丢弃。
                // 同字节非相邻（如 %I1006.0 ↔ %I1006.2）可能是 N 元 Mutex 拆出来的边，保留供合并算法识别。
                int crossBytePairMutexDropped = inputs.RemoveAll(it =>
                {
                    if (!string.Equals(it.Operator, "AT_MOST_ONE", StringComparison.OrdinalIgnoreCase)) return false;
                    if (it.Signals == null || it.Signals.Count != 2) return false;
                    var addrs = it.Signals.Select(s => s.SignalAddress ?? string.Empty).ToList();
                    if (!addrs.All(a => System.Text.RegularExpressions.Regex.IsMatch(a, @"^%[IQM]\d+\.\d+$"))) return false;
                    var parsed = addrs.Select(a =>
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(a, @"^%[IQM](\d+)\.(\d+)$");
                        return (Byte: int.Parse(m.Groups[1].Value), Bit: int.Parse(m.Groups[2].Value));
                    }).ToList();
                    var byteDiff = Math.Abs(parsed[0].Byte - parsed[1].Byte);
                    // 只拒绝跨字节（byteDiff > 0）；同字节内任意 bit 差都保留
                    return byteDiff > 0;
                });
                if (crossBytePairMutexDropped > 0)
                {
                    _logger.LogInformation("[Diagnosis] 丢弃 {Count} 条 2 元 Mutex 跨字节（不可能是同 FB 反向引脚对）", crossBytePairMutexDropped);
                }

                // 2a-5b. 报警位/到位反馈位禁止出现在瞬时 IMPLIES/Pairing：
                // 信号名末段含 FwdErr/BkdErr/FwdLockErr/BkdLockErr/FwdWarn/BkdWarn/FwdError/BkdError 等是博图 FB 报警位输出，
                // 它们是 "Q 出来 N 秒后没到位才置位" 的时延型状态——本质是 AlarmBinding 而非瞬时 IMPLIES。
                // 末段含 FwdPos/BkdPos 是 DB 内部到位反馈缓存，也不应在瞬时约束里出现。
                // 这些都是博图标准命名，跨项目通用。
                int alarmPosInPairingDropped = 0, alarmPosSignalsDropped = 0;
                bool IsAlarmOrPosFeedback(string? name)
                {
                    if (string.IsNullOrWhiteSpace(name)) return false;
                    var lastDot = name.LastIndexOf('.');
                    var leaf = lastDot >= 0 ? name[(lastDot + 1)..] : name;
                    return leaf.EndsWith("FwdErr", StringComparison.OrdinalIgnoreCase)
                        || leaf.EndsWith("BkdErr", StringComparison.OrdinalIgnoreCase)
                        || leaf.EndsWith("FwdError", StringComparison.OrdinalIgnoreCase)
                        || leaf.EndsWith("BkdError", StringComparison.OrdinalIgnoreCase)
                        || leaf.EndsWith("FwdLockErr", StringComparison.OrdinalIgnoreCase)
                        || leaf.EndsWith("BkdLockErr", StringComparison.OrdinalIgnoreCase)
                        || leaf.EndsWith("FwdlockWarn", StringComparison.OrdinalIgnoreCase)
                        || leaf.EndsWith("BkdlockWarn", StringComparison.OrdinalIgnoreCase)
                        || leaf.EndsWith("FwdPos", StringComparison.OrdinalIgnoreCase)
                        || leaf.EndsWith("BkdPos", StringComparison.OrdinalIgnoreCase);
                }
                var afterAlarmFilter = new List<ConstraintInput>(inputs.Count);
                foreach (var inp in inputs)
                {
                    if (inp.Signals == null || inp.Signals.Count == 0)
                    {
                        afterAlarmFilter.Add(inp);
                        continue;
                    }
                    // IMPLIES / Pairing：含报警/反馈位 → 整条丢弃（不能拆，因为方向是确定的）
                    if (string.Equals(inp.Operator, "IMPLIES", StringComparison.OrdinalIgnoreCase)
                        && inp.Signals.Any(s => IsAlarmOrPosFeedback(s.SignalName)))
                    {
                        alarmPosInPairingDropped++;
                        continue;
                    }
                    // 其它 operator（Mutex/AllRequired/Safety）：只丢含报警/反馈位的信号
                    var origCnt = inp.Signals.Count;
                    inp.Signals = inp.Signals.Where(s => !IsAlarmOrPosFeedback(s.SignalName)).ToList();
                    alarmPosSignalsDropped += (origCnt - inp.Signals.Count);
                    if (inp.Signals.Count < GetMinSignalsForOperator(inp.Operator)) continue;  // 剩余信号不足，整条丢
                    afterAlarmFilter.Add(inp);
                }
                if (alarmPosInPairingDropped > 0 || alarmPosSignalsDropped > 0)
                {
                    _logger.LogInformation("[Diagnosis] 报警位/反馈位兜底过滤：丢弃 {Pair} 条含报警位的 IMPLIES、{Sig} 个报警/反馈位信号",
                        alarmPosInPairingDropped, alarmPosSignalsDropped);
                }
                inputs = afterAlarmFilter;

                // 2a-6. IoMapping.Category=Ignore 信号兜底过滤：
                // Step1 IoMapping 字段标 category=Ignore 的信号意思是"不参与诊断"——
                // 典型如 Sensor Mapping 块的 -FZ/-SQ/-LS 等汇总映射信号（DI.Sensor[N] <= OP030_x-FZxx），
                // 这些是中转字段，没有控制语义，不应该出现在 Mutex/Pairing/Safety 里。
                // 即使 LLM 漏了 IoMappingHint 的指引产了约束，这一层硬丢弃。
                // 判定：信号 SignalName 命中 ai_plc_iotag.IoMapping 字段且首条 category=Ignore → 该信号丢弃。
                // 诊断：先 audit 字典里有多少 IoMapping 字段非空（验证 SqlSugar 真的读到了）
                var ioMappingPopulated = ioTagByName.Values.Count(t => !string.IsNullOrWhiteSpace(t.IoMapping));
                var ioMappingIgnore = ioTagByName.Values.Count(t => !string.IsNullOrWhiteSpace(t.IoMapping)
                    && t.IoMapping!.Contains("category=Ignore", StringComparison.OrdinalIgnoreCase));
                _logger.LogInformation("[Diagnosis] 2a-6 进入：ioTagByName 共 {Total} 个，含 IoMapping 字段非空 {Mapped} 个，其中 category=Ignore {Ignore} 个",
                    ioTagByName.Count, ioMappingPopulated, ioMappingIgnore);

                int ignoreSignalsDropped = 0, ignoreInputsDropped = 0;
                var afterIgnore = new List<ConstraintInput>(inputs.Count);
                foreach (var inp in inputs)
                {
                    if (inp.Signals == null || inp.Signals.Count == 0)
                    {
                        afterIgnore.Add(inp);
                        continue;
                    }
                    // MUST_BE_FALSE 屏蔽态报警约束豁免 IoMapping=Ignore 过滤（§4.5）：
                    // IoMapping=Ignore 是 Step1 给 Sensor Mapping 块的「中转 DB 信号」打的标记，但实际数据里
                    // Sensor Mapping **源端** %I 物理传感器也常被误标 Ignore（如 OP010-FZ104 在 FC110_OP10Mapping 块里
                    // 作为 sourceVar 被映射到 DB.DI.Sensor[10]，结果 IoMapping 字段也写了 category=Ignore）。
                    // 屏蔽态报警的语义是「物理传感器永不应触发」，与 IoMapping 中转语义无关 — 整条豁免。
                    if (string.Equals(inp.Operator, "MUST_BE_FALSE", StringComparison.OrdinalIgnoreCase))
                    {
                        afterIgnore.Add(inp);
                        continue;
                    }
                    var origCnt = inp.Signals.Count;
                    inp.Signals = inp.Signals
                        .Where(s => !IsIgnoreCategorySignal(s.SignalName, ioTagByName))
                        .ToList();
                    var droppedHere = origCnt - inp.Signals.Count;
                    if (droppedHere > 0)
                    {
                        ignoreSignalsDropped += droppedHere;
                    }
                    if (inp.Signals.Count < GetMinSignalsForOperator(inp.Operator))
                    {
                        ignoreInputsDropped++;
                        continue;
                    }
                    afterIgnore.Add(inp);
                }
                if (ignoreSignalsDropped > 0 || ignoreInputsDropped > 0)
                {
                    _logger.LogInformation("[Diagnosis] IoMapping.Category=Ignore 兜底过滤：丢弃 {SigN} 个 Ignore 信号、{InpN} 条因 Ignore 导致结构破损的约束",
                        ignoreSignalsDropped, ignoreInputsDropped);
                }
                inputs = afterIgnore;

                // 2a-7. CPU 时钟脉冲位兜底过滤（结构判定，跨项目通用，不靠命名前缀）：
                // 西门子博图 CPU 配置「时钟存储字节」自动生成的 8 位方波时钟（如 %M1.0-%M1.7 = 10/5/2.5/2/1.25/1/0.625/0.5 Hz），
                // 这些信号只能作为脉冲驱动源（典型 `Lamp := Clock_1Hz`），不参与任何瞬时布尔约束。
                // 识别方法：同字节 6+ 位都是 Bool + comment 是周期格式 → 字节判定为时钟字节（见 DetectCpuClockBytes）。
                if (cpuClockBytes.Count > 0)
                {
                    int clockSignalsDropped = 0, clockInputsDropped = 0;
                    var afterClock = new List<ConstraintInput>(inputs.Count);
                    foreach (var inp in inputs)
                    {
                        if (inp.Signals == null || inp.Signals.Count == 0)
                        {
                            afterClock.Add(inp);
                            continue;
                        }
                        var origCnt = inp.Signals.Count;
                        inp.Signals = inp.Signals
                            .Where(s => !IsCpuClockPulseSignal(s.SignalName, ioTagByName, cpuClockBytes))
                            .ToList();
                        clockSignalsDropped += (origCnt - inp.Signals.Count);
                        if (inp.Signals.Count < GetMinSignalsForOperator(inp.Operator))
                        {
                            clockInputsDropped++;
                            continue;
                        }
                        afterClock.Add(inp);
                    }
                    if (clockSignalsDropped > 0 || clockInputsDropped > 0)
                    {
                        _logger.LogInformation("[Diagnosis] CPU 时钟脉冲位兜底过滤：丢弃 {SigN} 个时钟信号、{InpN} 条因时钟信号导致结构破损的约束",
                            clockSignalsDropped, clockInputsDropped);
                    }
                    inputs = afterClock;
                }

                // 2b. 入库前硬过滤：信号必须是允许集合中的成员。
                // 允许集合 = ① 本项目登记的 IO Tag，∨ ② 末段名命中 KeyFbPinNames 的全局 DB 变量
                // （如 General.E_StopStatus、General.ManualMod 等，与上游 Prompt 端 KeyFbPin 白名单一致）。
                // 这一层拦截 AI 幻觉产生的 DB 内部变量（典型如 DB390_OP30Alarm.Type.*FwdError 这种报警位），
                // 避免它们进入约束表后无法被实时信号校验，污染诊断链路。
                // 丢弃后若约束 signals 不足 2 个，按记忆约定整条规则跳过。
                int filteredInputCount = 0, filteredSignalCount = 0;
                var filteredInputs = new List<ConstraintInput>(inputs.Count);
                foreach (var inp in inputs)
                {
                    if (inp.Signals == null || inp.Signals.Count == 0)
                    {
                        filteredInputs.Add(inp);
                        continue;
                    }

                    var originalCount = inp.Signals.Count;
                    inp.Signals = inp.Signals
                        .Where(s => IsAllowedConstraintSignal(s.SignalName, ioTagByName, extraAllowedSet))
                        .ToList();
                    var droppedCount = originalCount - inp.Signals.Count;
                    if (droppedCount > 0)
                    {
                        filteredSignalCount += droppedCount;
                    }

                    if (inp.Signals.Count < GetMinSignalsForOperator(inp.Operator))
                    {
                        filteredInputCount++;
                        _logger.LogInformation(
                            "[Diagnosis] 跳过含非允许信号且过滤后结构破损的约束: projectId={ProjectId}, ruleName={RuleName}, operator={Operator}, 丢弃信号数={DroppedCount}",
                            projectId, inp.RuleName, inp.Operator, droppedCount);
                        continue;
                    }

                    filteredInputs.Add(inp);
                }

                if (filteredSignalCount > 0 || filteredInputCount > 0)
                {
                    _logger.LogInformation(
                        "[Diagnosis] 信号白名单硬过滤: projectId={ProjectId}, 丢弃非允许信号 {DroppedSignal} 个, 因结构破损丢弃约束 {DroppedInput} 条",
                        projectId, filteredSignalCount, filteredInputCount);
                }

                inputs = filteredInputs;
                if (inputs.Count == 0)
                {
                    return new
                    {
                        Success = true,
                        ProjectId = projectId,
                        TotalInput = 0,
                        Inserted = 0,
                        SkippedDuplicate = 0,
                        SampleRules = new List<object>(),
                        Hint = $"所有约束的 signals 均含非允许信号（共丢弃 {filteredSignalCount} 个信号、{filteredInputCount} 条约束），无可入库内容。"
                    };
                }

                // 2b-2. 信号过滤后的结构破损二次校验：
                // 上面 2b 走的是 signal-level 过滤，可能出现「单端角色被全部丢掉、另一端剩 ≥2 个」的残缺约束。
                // 典型场景：REQUIRES_0 Source=AlwaysFALSE 被丢 → 剩 3 个 Target；ALL_TRUE 含 AlwaysTRUE 被丢 → 剩 1 个 Member。
                // 这种残缺约束哪怕信号数 ≥2 也不能入库（语义已损坏）。
                int structurallyBrokenDropped = inputs.RemoveAll(it =>
                {
                    var op = (it.Operator ?? string.Empty).Trim();
                    // MUST_BE_FALSE 是单 Target 形态（§4.5 屏蔽态报警单传感器约束），早于通用 ≥2 信号校验放行
                    if (string.Equals(op, "MUST_BE_FALSE", StringComparison.OrdinalIgnoreCase))
                    {
                        if (it.Signals == null || it.Signals.Count == 0) return true;
                        // 不应带 Source；至少 1 个 Target
                        bool mustHasSource = it.Signals.Any(s => string.Equals(s.SignalRole, "Source", StringComparison.OrdinalIgnoreCase));
                        if (mustHasSource) return true;
                        int targetCnt = it.Signals.Count(s => string.Equals(s.SignalRole, "Target", StringComparison.OrdinalIgnoreCase));
                        return targetCnt < 1;
                    }
                    if (it.Signals == null || it.Signals.Count < 2) return true;
                    bool hasSource = it.Signals.Any(s => string.Equals(s.SignalRole, "Source", StringComparison.OrdinalIgnoreCase));
                    bool hasTarget = it.Signals.Any(s => string.Equals(s.SignalRole, "Target", StringComparison.OrdinalIgnoreCase));
                    bool hasMember = it.Signals.Any(s => string.Equals(s.SignalRole, "Member", StringComparison.OrdinalIgnoreCase));
                    if (string.Equals(op, "IMPLIES", StringComparison.OrdinalIgnoreCase))
                    {
                        return !(hasSource && hasTarget);
                    }
                    if (string.Equals(op, "REQUIRES_0_WHEN_FALSE", StringComparison.OrdinalIgnoreCase))
                    {
                        // 反向零态联锁必须 Source + Target 齐全（无 Source 形态由 REQUIRES_0 承担）
                        return !(hasSource && hasTarget);
                    }
                    if (string.Equals(op, "REQUIRES_0", StringComparison.OrdinalIgnoreCase))
                    {
                        // REQUIRES_0 两种合法结构：
                        // 1) 有 Source + 有 Target：条件性禁止（Source 触发时 Target 必为 false）
                        // 2) 无 Source + ≥2 Target：无条件禁止（所有 Target 必须永为 false，ALL_FALSE 语义）
                        //    用途：表达"被 AlwaysTRUE(=false) 等占位常量屏蔽的 SCoil 报警网络永远不应被置位"
                        //    诊断引擎已支持（DiagnosticStrategyFactoryApp REQUIRES_0 case：sources empty 时退化为 forbidden 全部 Target）
                        if (hasSource) return !hasTarget;
                        int targetCnt = it.Signals.Count(s => string.Equals(s.SignalRole, "Target", StringComparison.OrdinalIgnoreCase));
                        return targetCnt < 2;
                    }
                    if (string.Equals(op, "ALL_TRUE", StringComparison.OrdinalIgnoreCase))
                    {
                        // ALL_TRUE 需要 Source + 至少 1 个 Member（共 ≥2 个）或 ≥2 个 Member
                        int memberCnt = it.Signals.Count(s => string.Equals(s.SignalRole, "Member", StringComparison.OrdinalIgnoreCase));
                        return memberCnt < (hasSource ? 1 : 2);
                    }
                    if (string.Equals(op, "AT_MOST_ONE", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(op, "EXACTLY_ONE", StringComparison.OrdinalIgnoreCase))
                    {
                        int memberCnt = it.Signals.Count(s => string.Equals(s.SignalRole, "Member", StringComparison.OrdinalIgnoreCase));
                        return memberCnt < 2;
                    }
                    if (string.Equals(op, "SAME_STATE", StringComparison.OrdinalIgnoreCase))
                    {
                        // SAME_STATE 要求至少 2 个 Member 状态一致
                        int memberCnt = it.Signals.Count(s => string.Equals(s.SignalRole, "Member", StringComparison.OrdinalIgnoreCase));
                        return memberCnt < 2;
                    }
                    return false;
                });
                if (structurallyBrokenDropped > 0)
                {
                    _logger.LogInformation(
                        "[Diagnosis] 信号过滤后结构破损二次校验：丢弃 {Count} 条角色残缺约束 (典型为 Source 被占位符过滤丢失后剩余 Target 一堆)",
                        structurallyBrokenDropped);
                }

                // 2b-3. 入库前最终过滤：signalAddress 必须非空（信号在 IO Tag 表能采集到值）。
                // 即使 KeyFbPin 末段（ManualMod/E_StopStatus/ResetStatus 等）信号在白名单允许通过，
                // 但若该信号在 IO Tag 表里没记录（signalAddress=null），诊断引擎实时采集时拿不到值
                // → 即使产了约束也无法被实时校验 → 等于无效约束污染表。
                // 典型场景：
                //   - LLM 产 ManualMod_互斥(ManualMod, AutoRunMod) — KeyFbPin 末段允许过白名单，但
                //     DB 信号 *.General.ManualMod 在 IO Tag 表里没行，signalAddress=null
                //   - LLM 产 REQUIRES_0_WHEN_FALSE(Source=..., Target=DB***Alarm.Warning1) — Target 是
                //     DB 内部 Warning 位，不在 IO Tag 表，signalAddress=null
                int signalsMissingAddrDropped = 0, inputsMissingAddrDropped = 0;
                var afterAddrFilter = new List<ConstraintInput>(inputs.Count);
                foreach (var inp in inputs)
                {
                    if (inp.Signals == null || inp.Signals.Count == 0)
                    {
                        afterAddrFilter.Add(inp);
                        continue;
                    }
                    var origCnt = inp.Signals.Count;
                    inp.Signals = inp.Signals.Where(s => !string.IsNullOrWhiteSpace(s.SignalAddress)).ToList();
                    var droppedHere = origCnt - inp.Signals.Count;
                    if (droppedHere > 0)
                    {
                        signalsMissingAddrDropped += droppedHere;
                    }
                    if (inp.Signals.Count < GetMinSignalsForOperator(inp.Operator))
                    {
                        inputsMissingAddrDropped++;
                        continue;
                    }
                    afterAddrFilter.Add(inp);
                }
                if (signalsMissingAddrDropped > 0 || inputsMissingAddrDropped > 0)
                {
                    _logger.LogInformation(
                        "[Diagnosis] 入库前 signalAddress 兜底过滤：丢弃 {SigN} 个无地址信号、{InpN} 条因无地址导致结构破损的约束 (DB 内部信号诊断引擎采集不到，约束无效)",
                        signalsMissingAddrDropped, inputsMissingAddrDropped);
                }
                inputs = afterAddrFilter;

                // 2b-4. IMPLIES 方向终检（NEW 2026-05-29）：入库前最后一道无条件方向防线。
                // 背景：全量重跑(16555 LU)实测极偶发(~0.018%)有 PNQ(%I)→Q(%Q) / Q(%Q)→Q(%Q) 方向错误 IMPLIES
                // 漏过前面 FilterPairingsByWhitelist + 2a-3 + 2a-3c 多道过滤入库，污染诊断（误报）。
                // 与 2a-3c 判定逻辑完全一致（纯兜底，不引入新误伤），但位置在最末端：地址已回填、无地址信号已删、
                // 所有按类型/白名单过滤已跑完，是进入主入库循环（含跨调用 Source 合并）前对最终 inputs 的统一方向校验。
                // 规则：Source 必须 %Q 或末段 KeyFbPin；Target 必须 %I 或末段 KeyFbPin；
                //       SCoil sensor→alarm 例外：所有 Target 都是 extraAllowedSet 内 DB 内部信号时 Source 允许 %I。
                int impliesFinalDirDropped = inputs.RemoveAll(it =>
                {
                    if (!string.Equals(it.Operator, "IMPLIES", StringComparison.OrdinalIgnoreCase)) return false;
                    if (it.Signals == null || it.Signals.Count < 2) return false;
                    var sources = it.Signals.Where(s => string.Equals(s.SignalRole, "Source", StringComparison.OrdinalIgnoreCase)).ToList();
                    var targets = it.Signals.Where(s => string.Equals(s.SignalRole, "Target", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (sources.Count == 0 || targets.Count == 0) return false;

                    bool IsScoilAlarmTarget(SignalInput s)
                    {
                        var addr = s.SignalAddress ?? string.Empty;
                        if (addr.StartsWith("%Q", StringComparison.OrdinalIgnoreCase)) return false;
                        if (addr.StartsWith("%I", StringComparison.OrdinalIgnoreCase)) return false;
                        if (addr.StartsWith("%M", StringComparison.OrdinalIgnoreCase)) return false;
                        return extraAllowedSet != null
                            && !string.IsNullOrWhiteSpace(s.SignalName)
                            && extraAllowedSet.Contains(s.SignalName.Trim());
                    }
                    bool isScoilAlarmPattern = targets.All(IsScoilAlarmTarget);

                    bool OkAsSource(SignalInput s)
                    {
                        var addr = s.SignalAddress ?? string.Empty;
                        if (addr.StartsWith("%Q", StringComparison.OrdinalIgnoreCase)) return true;
                        if (addr.StartsWith("%I", StringComparison.OrdinalIgnoreCase)) return isScoilAlarmPattern;
                        if (addr.StartsWith("%M", StringComparison.OrdinalIgnoreCase)) return false;
                        var name = s.SignalName ?? string.Empty;
                        var lastDot = name.LastIndexOf('.');
                        var leaf = lastDot >= 0 ? name[(lastDot + 1)..] : name;
                        return !string.IsNullOrWhiteSpace(leaf) && KeyFbPinNames.Contains(leaf);
                    }
                    bool OkAsTarget(SignalInput s)
                    {
                        var addr = s.SignalAddress ?? string.Empty;
                        if (addr.StartsWith("%I", StringComparison.OrdinalIgnoreCase)) return true;
                        if (addr.StartsWith("%Q", StringComparison.OrdinalIgnoreCase)) return false;
                        if (addr.StartsWith("%M", StringComparison.OrdinalIgnoreCase)) return false;
                        if (isScoilAlarmPattern) return IsScoilAlarmTarget(s);
                        var name = s.SignalName ?? string.Empty;
                        var lastDot = name.LastIndexOf('.');
                        var leaf = lastDot >= 0 ? name[(lastDot + 1)..] : name;
                        return !string.IsNullOrWhiteSpace(leaf) && KeyFbPinNames.Contains(leaf);
                    }

                    return !sources.All(OkAsSource) || !targets.All(OkAsTarget);
                });
                if (impliesFinalDirDropped > 0)
                {
                    _logger.LogWarning(
                        "[Diagnosis] 2b-4 IMPLIES 方向终检：入库前丢弃 {Count} 条方向仍不符的 IMPLIES (Source 非%Q/KeyFbPin 或 Target 非%I/KeyFbPin)，sourceLuId={LuId}",
                        impliesFinalDirDropped, sourceLuId);
                }

                if (inputs.Count == 0)
                {
                    return new
                    {
                        Success = true,
                        ProjectId = projectId,
                        TotalInput = 0,
                        Inserted = 0,
                        SkippedDuplicate = 0,
                        SampleRules = new List<object>(),
                        Hint = $"过滤后所有约束角色残缺，无可入库内容。"
                    };
                }

                // 构建去重键：operator + 带角色的排序信号名（不含 RuleType，防止同一约束因分类不同被重复存储）
                var existingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var rule in existingRules)
                {
                    var ruleSignals = existingSignals
                        .Where(s => s.RuleId == rule.Id)
                        .Select(s => $"{(s.SignalRole ?? "Member").ToUpperInvariant()}:{(s.SignalName ?? "").Trim()}")
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
                    existingKeys.Add($"{(rule.Operator ?? "").Trim().ToUpperInvariant()}|{string.Join(",", ruleSignals)}");
                }

                // 跨调用合并索引：(Operator + Source信号集) → (现有规则, 现有 Target/Member 名集合)
                // 由于 SaveIOConstraintsAsync 会被每个 Tag 单独调用，DB 中可能已存在同 Source 的规则；
                // 新 input 与之 Source 完全一致时，应把新 Target/Member 追加到现有规则，而不是新建一条。
                var sourceBasedRuleIndex = new Dictionary<string, (PLC_IOConstraintRuleEntity Rule, HashSet<string> ChildNames, string ChildRole)>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var rule in existingRules)
                {
                    var ruleOp = (rule.Operator ?? "").Trim().ToUpperInvariant();
                    if (ruleOp != "ALL_TRUE" && ruleOp != "IMPLIES" && ruleOp != "REQUIRES_0") continue;

                    var ruleSignalsList = existingSignals.Where(s => s.RuleId == rule.Id).ToList();
                    var sourceNames = ruleSignalsList
                        .Where(s => string.Equals(s.SignalRole, "Source", StringComparison.OrdinalIgnoreCase)
                                    && !string.IsNullOrWhiteSpace(s.SignalName))
                        .Select(s => s.SignalName!.Trim())
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
                    var sourceKey = string.Join("|", sourceNames);
                    if (string.IsNullOrEmpty(sourceKey)) continue;

                    var indexKey = $"{ruleOp}#{sourceKey}";
                    if (sourceBasedRuleIndex.ContainsKey(indexKey)) continue;

                    var childRole = ruleOp == "ALL_TRUE" ? "Member" : "Target";
                    var childNames = new HashSet<string>(
                        ruleSignalsList
                            .Where(s => string.Equals(s.SignalRole, childRole, StringComparison.OrdinalIgnoreCase)
                                        && !string.IsNullOrWhiteSpace(s.SignalName))
                            .Select(s => s.SignalName!.Trim()),
                        StringComparer.OrdinalIgnoreCase);
                    sourceBasedRuleIndex[indexKey] = (rule, childNames, childRole);
                }

                // 3a. 构建互斥信号地图（AT_MOST_ONE / EXACTLY_ONE 规则中的信号两两互斥）
                // 用途：后续对每条 IMPLIES 约束做交叉校验，防止 "互斥信号对" 被错误地生成 IMPLIES 关系。
                // 数据来源：① 现有 DB 规则；② 当前输入批次中的 AT_MOST_ONE/EXACTLY_ONE 条目。
                var mutexSignalGroups = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

                void AddMutexGroup(IEnumerable<string> names)
                {
                    var nameList = names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    foreach (var name in nameList)
                    {
                        if (!mutexSignalGroups.TryGetValue(name, out var peers))
                            mutexSignalGroups[name] = peers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var other in nameList)
                            if (!string.Equals(name, other, StringComparison.OrdinalIgnoreCase))
                                peers.Add(other);
                    }
                }

                // 从现有 DB 规则收集互斥组
                foreach (var rule in existingRules)
                {
                    var op = (rule.Operator ?? "").Trim().ToUpperInvariant();
                    if (op != "AT_MOST_ONE" && op != "EXACTLY_ONE") continue;
                    AddMutexGroup(existingSignals.Where(s => s.RuleId == rule.Id).Select(s => s.SignalName));
                }

                // 从当前批次收集互斥组（先过一遍，让互斥关系优先于 IMPLIES 入库）
                foreach (var inp in inputs)
                {
                    var op = (inp.Operator ?? "").Trim().ToUpperInvariant();
                    if ((op != "AT_MOST_ONE" && op != "EXACTLY_ONE") || inp.Signals == null) continue;
                    AddMutexGroup(inp.Signals.Select(s => s.SignalName));
                }

                // 2.5 先对所有 input 做角色规范化，再合并同 Source 的 ALL_TRUE 规则
                // （规范化必须在合并前完成，否则 Target 还未转为 Source 导致分组 key 不一致）
                foreach (var inp in inputs)
                {
                    if (inp.Signals == null || inp.Signals.Count == 0) continue;
                    NormalizeSignalInputs(inp);
                    NormalizeConstraintDirections(inp, ioTagByName);
                }
                inputs = MergeAllTrueRulesBySameSource(inputs);
                inputs = MergeSameSourceTargetRulesBySameSource(inputs, "REQUIRES_0");
                // 合并 IMPLIES 之前抓一次 mutex pair 集合（合并完不会再变）。
                // 用于合并后的物理矛盾二次过滤——因为合并可能把两条单 Target Pairing
                // 合成多 Target 包含互斥反向对（YV05_W → CYL05_W + 来自另一条的 CYL05_H），
                // 此时单条来源 Pairing 本来不矛盾，合并后才矛盾。
                var mutexPairsForPostMergeCheck = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var mtx in inputs)
                {
                    if (!string.Equals(mtx.Operator, "AT_MOST_ONE", StringComparison.OrdinalIgnoreCase)) continue;
                    if (mtx.Signals == null) continue;
                    var ms = mtx.Signals.Select(s => s.SignalName?.Trim())
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    for (int i = 0; i < ms.Count; i++)
                        for (int j = i + 1; j < ms.Count; j++)
                        {
                            var a = ms[i]!; var b = ms[j]!;
                            var key = string.Compare(a, b, StringComparison.OrdinalIgnoreCase) < 0 ? $"{a}|{b}" : $"{b}|{a}";
                            mutexPairsForPostMergeCheck.Add(key);
                        }
                }
                inputs = MergeSameSourceTargetRulesBySameSource(inputs, "IMPLIES", mutexPairsForPostMergeCheck);

                // 3. 逐条处理并去重
                int insertedCount = 0, skippedCount = 0;
                var sampleRules = new List<object>();

                foreach (var input in inputs)
                {
                    if (input.Signals == null || input.Signals.Count == 0) continue;
                    // 角色规范化已在 2.5 阶段完成，此处只重建规则名
                    input.RuleName = BuildCompactConstraintRuleName(input);
                    if (ShouldSkipInvalidConstraintInput(input))
                    {
                        skippedCount++;
                        continue;
                    }

                    // IMPLIES 互斥交叉校验：若 Source 与 Target 已在任意互斥规则中共现，则两者不可能存在 IMPLIES 关系
                    if (string.Equals(input.Operator, "IMPLIES", StringComparison.OrdinalIgnoreCase)
                        && IsImpliesConflictingWithMutex(input, mutexSignalGroups))
                    {
                        skippedCount++;
                        _logger.LogInformation(
                            "[Diagnosis] 跳过 IMPLIES（Source 与 Target 互斥，IMPLIES 逻辑矛盾）: {Source} → {Target}",
                            input.Signals?.FirstOrDefault(s => string.Equals(s.SignalRole, "Source", StringComparison.OrdinalIgnoreCase))?.SignalName,
                            input.Signals?.FirstOrDefault(s => string.Equals(s.SignalRole, "Target", StringComparison.OrdinalIgnoreCase))?.SignalName);
                        continue;
                    }

                    if (!TryValidateConstraintStructure(input, out var invalidReason))
                    {
                        skippedCount++;
                        _logger.LogWarning(
                            "[Diagnosis] 跳过结构非法约束: projectId={ProjectId}, ruleName={RuleName}, operator={Operator}, reason={Reason}",
                            projectId,
                            input.RuleName,
                            input.Operator,
                            invalidReason);
                        continue;
                    }

                    input.Description = await BuildConstraintDescriptionAsync(input);

                    // 生成去重键：operator + 带角色的排序信号名（不含 RuleType，防止同一约束因分类不同被重复存储）
                    var inputOpUpper = (input.Operator ?? "").Trim().ToUpperInvariant();
                    var sortedNames = input.Signals
                        .Select(s => $"{(s.SignalRole ?? "Member").ToUpperInvariant()}:{(s.SignalName ?? "").Trim()}")
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
                    var dedupKey = $"{inputOpUpper}|{string.Join(",", sortedNames)}";

                    if (existingKeys.Contains(dedupKey))
                    {
                        skippedCount++;
                        continue;
                    }

                    // 跨调用合并：对 ALL_TRUE / IMPLIES / REQUIRES_0，若 Source 与 DB 现有规则一致，
                    // 则把新 Target/Member 追加到现有规则，避免同 Source 不同 Target 被拆成多条。
                    if (inputOpUpper == "ALL_TRUE" || inputOpUpper == "IMPLIES" || inputOpUpper == "REQUIRES_0")
                    {
                        var inputSourceNames = input.Signals
                            .Where(s => string.Equals(s.SignalRole, "Source", StringComparison.OrdinalIgnoreCase)
                                        && !string.IsNullOrWhiteSpace(s.SignalName))
                            .Select(s => s.SignalName!.Trim())
                            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
                        var inputSourceKey = string.Join("|", inputSourceNames);
                        if (!string.IsNullOrEmpty(inputSourceKey))
                        {
                            var indexKey = $"{inputOpUpper}#{inputSourceKey}";
                            if (sourceBasedRuleIndex.TryGetValue(indexKey, out var existing))
                            {
                                var childRole = existing.ChildRole;
                                var newChildSignals = input.Signals
                                    .Where(s => string.Equals(s.SignalRole, childRole, StringComparison.OrdinalIgnoreCase)
                                                && !string.IsNullOrWhiteSpace(s.SignalName)
                                                && !existing.ChildNames.Contains(s.SignalName!.Trim()))
                                    .ToList();

                                if (newChildSignals.Count == 0)
                                {
                                    skippedCount++;
                                    existingKeys.Add(dedupKey);
                                    continue;
                                }

                                var existingSiblingSignals = await _ioConstraintSignalService.GetListAsync(
                                    s => s.RuleId == existing.Rule.Id && !s.IsDeleted);
                                var maxSortOrder = existingSiblingSignals
                                    .Select(s => s.SortOrder ?? 0)
                                    .DefaultIfEmpty(0)
                                    .Max();

                                var appendedSignalEntities = newChildSignals.Select((s, idx) => new PLC_IOConstraintSignalEntity
                                {
                                    RuleId = existing.Rule.Id,
                                    SignalName = s.SignalName!.Trim(),
                                    SignalAddress = s.SignalAddress,
                                    SignalRole = childRole,
                                    SortOrder = maxSortOrder + 1 + idx
                                }).ToList();
                                await _ioConstraintSignalService.InsertRangeAsync(appendedSignalEntities);

                                foreach (var s in newChildSignals)
                                    existing.ChildNames.Add(s.SignalName!.Trim());

                                // 用合并后的完整信号集合重建 input，重新生成规则名与描述
                                var mergedSignals = existingSiblingSignals
                                    .Concat(appendedSignalEntities)
                                    .Select(s => new SignalInput
                                    {
                                        SignalName = s.SignalName,
                                        SignalAddress = s.SignalAddress,
                                        SignalRole = s.SignalRole,
                                        SortOrder = s.SortOrder
                                    }).ToList();
                                var rebuiltInput = new ConstraintInput
                                {
                                    Operator = inputOpUpper,
                                    RuleType = existing.Rule.RuleType,
                                    Signals = mergedSignals,
                                    IoTagCommentsByName = input.IoTagCommentsByName  // 继承 comment 字典,让模板描述也包含业务名
                                };
                                rebuiltInput.RuleName = BuildCompactConstraintRuleName(rebuiltInput);
                                // 合并命中时使用模板描述，避免每个 Tag 调用都触发 LLM（性能与配额关键）
                                var newDescription = BuildTemplateConstraintDescription(rebuiltInput);

                                await _ioConstraintRuleService.UpdateAsync(
                                    u => new PLC_IOConstraintRuleEntity
                                    {
                                        Description = newDescription,
                                        RuleName = rebuiltInput.RuleName
                                    },
                                    r => r.Id == existing.Rule.Id);

                                existing.Rule.Description = newDescription;
                                existing.Rule.RuleName = rebuiltInput.RuleName;
                                existingKeys.Add(dedupKey);
                                insertedCount++;
                                continue;
                            }
                        }
                    }

                    // 创建 Rule 实体
                    var rule = new PLC_IOConstraintRuleEntity
                    {
                        ProjectId = projectId,
                        RuleName = input.RuleName,
                        RuleType = input.RuleType ?? "Mutex",
                        Operator = input.Operator ?? "AT_MOST_ONE",
                        EquipmentId = input.EquipmentId,
                        Confidence = input.Confidence,
                        Source = string.IsNullOrWhiteSpace(input.Source) ? "AI" : input.Source,
                        Description = input.Description,
                        Priority = input.Priority ?? "Warning",
                        ValidStates = input.Operator == "CUSTOM" ? input.ValidStates : null,
                        // 方案 D（增量约束）：记录生成本条约束的 LU。
                        // 后续上传时按 SourceLuId 软删，实现增量重生。
                        SourceLuId = sourceLuId
                    };
                    await _ioConstraintRuleService.InsertAsync(rule);

                    // 创建 Signal 实体列表
                    var signalEntities = input.Signals.Select((s, idx) => new PLC_IOConstraintSignalEntity
                    {
                        RuleId = rule.Id,
                        SignalName = s.SignalName ?? "",
                        SignalAddress = s.SignalAddress,
                        SignalRole = s.SignalRole ?? "Member",
                        SortOrder = s.SortOrder ?? idx
                    }).ToList();
                    await _ioConstraintSignalService.InsertRangeAsync(signalEntities);

                    // 把新插入规则也加入跨调用合并索引，保证同批后续 input 也能命中合并
                    if (inputOpUpper == "ALL_TRUE" || inputOpUpper == "IMPLIES" || inputOpUpper == "REQUIRES_0")
                    {
                        var newRuleSourceNames = signalEntities
                            .Where(s => string.Equals(s.SignalRole, "Source", StringComparison.OrdinalIgnoreCase)
                                        && !string.IsNullOrWhiteSpace(s.SignalName))
                            .Select(s => s.SignalName!.Trim())
                            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
                        var newRuleSourceKey = string.Join("|", newRuleSourceNames);
                        if (!string.IsNullOrEmpty(newRuleSourceKey))
                        {
                            var newIndexKey = $"{inputOpUpper}#{newRuleSourceKey}";
                            var newChildRole = inputOpUpper == "ALL_TRUE" ? "Member" : "Target";
                            var newChildNames = new HashSet<string>(
                                signalEntities
                                    .Where(s => string.Equals(s.SignalRole, newChildRole, StringComparison.OrdinalIgnoreCase)
                                                && !string.IsNullOrWhiteSpace(s.SignalName))
                                    .Select(s => s.SignalName!.Trim()),
                                StringComparer.OrdinalIgnoreCase);
                            sourceBasedRuleIndex[newIndexKey] = (rule, newChildNames, newChildRole);
                        }
                    }

                    existingKeys.Add(dedupKey);
                    insertedCount++;

                    if (sampleRules.Count < 5)
                    {
                        sampleRules.Add(new
                        {
                            rule.RuleName, rule.RuleType, rule.Operator, rule.Priority,
                            Signals = signalEntities.Select(s => new { s.SignalName, s.SignalRole }).ToList()
                        });
                    }
                }

                // 修正该项目所有 ALL_TRUE 规则的描述：以 ai_plc_ioconstraintsignal 中存储的正确角色为准重新生成
                await FixAllTrueConstraintDescriptionsAsync(projectId);

                return new
                {
                    Success = true,
                    ProjectId = projectId,
                    TotalInput = inputs.Count,
                    Inserted = insertedCount,
                    SkippedDuplicate = skippedCount,
                    SampleRules = sampleRules,
                    Hint = insertedCount > 0
                        ? $"成功写入 {insertedCount} 条约束规则（跳过 {skippedCount} 条重复）。可使用 ValidateIOSignalsAsync 进行校验。"
                        : $"所有 {inputs.Count} 条规则均已存在，无新增。"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Diagnosis] SaveIOConstraints 异常");
                return new { Success = false, Message = $"保存约束规则失败: {ex.Message}" };
            }
        }

        /// <summary>
        /// 合并 DB 中已存在的同 Operator 同 Source 信号集的重复规则（修正历史/上一次运行残留的重复数据）。
        /// 适用于 ALL_TRUE / IMPLIES / REQUIRES_0：保留最早的规则，把其他同组规则的 Target/Member 信号迁移过来后软删除。
        /// </summary>
        private async Task MergeExistingDuplicateRulesAsync(long projectId)
        {
            try
            {
                var allRules = await _ioConstraintRuleService.GetListAsync(r =>
                    r.ProjectId == projectId && !r.IsDeleted);
                if (allRules == null || allRules.Count < 2) return;

                var mergeableRules = allRules
                    .Where(r =>
                    {
                        var op = (r.Operator ?? "").Trim().ToUpperInvariant();
                        return op == "ALL_TRUE" || op == "IMPLIES" || op == "REQUIRES_0";
                    })
                    .ToList();
                if (mergeableRules.Count < 2) return;

                // 信号名 → comment 字典,让重写描述时能带业务名(见 SaveIOConstraintsAsync 同名构造点)
                var projectIOTagsForMerge = await _ioTagService.GetListAsync(
                    x => x.ProjectId == projectId && !x.IsDeleted);
                var ioTagCommentsByName = projectIOTagsForMerge
                    .Where(t => !string.IsNullOrWhiteSpace(t.TagName) && !string.IsNullOrWhiteSpace(t.Comment))
                    .GroupBy(t => t.TagName!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().Comment!, StringComparer.OrdinalIgnoreCase);

                var ruleIds = mergeableRules.Select(r => r.Id).ToList();
                var allSignals = await _ioConstraintSignalService.GetListAsync(s =>
                    ruleIds.Contains(s.RuleId) && !s.IsDeleted);

                var signalsByRule = allSignals
                    .GroupBy(s => s.RuleId)
                    .ToDictionary(g => g.Key, g => g.ToList());

                // 按 (Operator, Source信号集) 分组
                var groups = mergeableRules
                    .Select(rule =>
                    {
                        var op = (rule.Operator ?? "").Trim().ToUpperInvariant();
                        if (!signalsByRule.TryGetValue(rule.Id, out var sigs))
                            sigs = new List<PLC_IOConstraintSignalEntity>();
                        var sourceKey = string.Join("|", sigs
                            .Where(s => string.Equals(s.SignalRole, "Source", StringComparison.OrdinalIgnoreCase)
                                        && !string.IsNullOrWhiteSpace(s.SignalName))
                            .Select(s => s.SignalName!.Trim())
                            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
                        return new { Rule = rule, Op = op, SourceKey = sourceKey, Signals = sigs };
                    })
                    .Where(x => !string.IsNullOrEmpty(x.SourceKey))
                    .GroupBy(x => $"{x.Op}#{x.SourceKey}")
                    .Where(g => g.Count() > 1)
                    .ToList();

                if (groups.Count == 0) return;

                int mergedCount = 0;
                foreach (var group in groups)
                {
                    var ordered = group.OrderBy(x => x.Rule.Id).ToList();
                    var keeper = ordered[0];
                    var keeperOp = keeper.Op;
                    var childRole = keeperOp == "ALL_TRUE" ? "Member" : "Target";

                    var keeperChildNames = new HashSet<string>(
                        keeper.Signals
                            .Where(s => string.Equals(s.SignalRole, childRole, StringComparison.OrdinalIgnoreCase)
                                        && !string.IsNullOrWhiteSpace(s.SignalName))
                            .Select(s => s.SignalName!.Trim()),
                        StringComparer.OrdinalIgnoreCase);

                    var keeperMaxSortOrder = keeper.Signals
                        .Select(s => s.SortOrder ?? 0)
                        .DefaultIfEmpty(0)
                        .Max();

                    var newSignalEntitiesToAdd = new List<PLC_IOConstraintSignalEntity>();
                    foreach (var dup in ordered.Skip(1))
                    {
                        foreach (var sig in dup.Signals
                                     .Where(s => string.Equals(s.SignalRole, childRole, StringComparison.OrdinalIgnoreCase)
                                                 && !string.IsNullOrWhiteSpace(s.SignalName)))
                        {
                            var trimmedName = sig.SignalName!.Trim();
                            if (keeperChildNames.Add(trimmedName))
                            {
                                keeperMaxSortOrder++;
                                newSignalEntitiesToAdd.Add(new PLC_IOConstraintSignalEntity
                                {
                                    RuleId = keeper.Rule.Id,
                                    SignalName = trimmedName,
                                    SignalAddress = sig.SignalAddress,
                                    SignalRole = childRole,
                                    SortOrder = keeperMaxSortOrder
                                });
                            }
                        }

                        // 软删除重复规则及其所有信号
                        await _ioConstraintRuleService.UpdateAsync(
                            u => new PLC_IOConstraintRuleEntity { IsDeleted = true },
                            r => r.Id == dup.Rule.Id);
                        await _ioConstraintSignalService.UpdateAsync(
                            u => new PLC_IOConstraintSignalEntity { IsDeleted = true },
                            s => s.RuleId == dup.Rule.Id);
                        mergedCount++;
                    }

                    if (newSignalEntitiesToAdd.Count > 0)
                    {
                        await _ioConstraintSignalService.InsertRangeAsync(newSignalEntitiesToAdd);

                        // 用合并后的完整信号集合重新生成保留规则的描述与名称
                        var refreshedSignals = await _ioConstraintSignalService.GetListAsync(
                            s => s.RuleId == keeper.Rule.Id && !s.IsDeleted);
                        var rebuiltInput = new ConstraintInput
                        {
                            Operator = keeperOp,
                            RuleType = keeper.Rule.RuleType,
                            Signals = refreshedSignals.Select(s => new SignalInput
                            {
                                SignalName = s.SignalName,
                                SignalAddress = s.SignalAddress,
                                SignalRole = s.SignalRole,
                                SortOrder = s.SortOrder
                            }).ToList(),
                            IoTagCommentsByName = ioTagCommentsByName.Count > 0 ? ioTagCommentsByName : null
                        };
                        rebuiltInput.RuleName = BuildCompactConstraintRuleName(rebuiltInput);
                        // 用模板生成描述，避免合并大量历史规则时触发海量 LLM 调用
                        var newDescription = BuildTemplateConstraintDescription(rebuiltInput);

                        await _ioConstraintRuleService.UpdateAsync(
                            u => new PLC_IOConstraintRuleEntity
                            {
                                Description = newDescription,
                                RuleName = rebuiltInput.RuleName
                            },
                            r => r.Id == keeper.Rule.Id);
                    }
                }

                if (mergedCount > 0)
                {
                    _logger.LogInformation(
                        "[Diagnosis] MergeExistingDuplicateRules: projectId={ProjectId}, 合并并软删除重复规则 {Count} 条",
                        projectId, mergedCount);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Diagnosis] MergeExistingDuplicateRules 合并历史重复规则时发生异常，跳过");
            }
        }

        /// <summary>
        /// 修正指定项目所有 ALL_TRUE 规则的 SignalRole 与 Description 字段。
        /// 1. 若 DB 中 Source 数量 > Member 数量（角色倒置），同时修正信号角色和描述。
        /// 2. 始终使用确定性模板重新生成描述，避免 AI 生成的反向描述残留。
        /// </summary>
        private async Task FixAllTrueConstraintDescriptionsAsync(long projectId)
        {
            try
            {
                var allTrueRules = await _ioConstraintRuleService.GetListAsync(r =>
                    r.ProjectId == projectId
                    && r.Operator == "ALL_TRUE"
                    && !r.IsDeleted);

                if (allTrueRules == null || allTrueRules.Count == 0) return;

                var ruleIds = allTrueRules.Select(r => r.Id).ToList();
                var allSignals = await _ioConstraintSignalService.GetListAsync(s =>
                    ruleIds.Contains(s.RuleId) && !s.IsDeleted);

                var signalsByRule = allSignals
                    .GroupBy(s => s.RuleId)
                    .ToDictionary(g => g.Key, g => g.ToList());

                // 信号名 → comment 字典(用于重写描述时带业务名)
                var projectIOTagsForFix = await _ioTagService.GetListAsync(
                    x => x.ProjectId == projectId && !x.IsDeleted);
                var ioTagCommentsByName = projectIOTagsForFix
                    .Where(t => !string.IsNullOrWhiteSpace(t.TagName) && !string.IsNullOrWhiteSpace(t.Comment))
                    .GroupBy(t => t.TagName!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().Comment!, StringComparer.OrdinalIgnoreCase);

                foreach (var rule in allTrueRules)
                {
                    if (!signalsByRule.TryGetValue(rule.Id, out var signals)) continue;

                    var sourceSignals = signals
                        .Where(s => string.Equals(s.SignalRole, "Source", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    var memberSignals = signals
                        .Where(s => string.Equals(s.SignalRole, "Member", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(s => s.SortOrder ?? 0)
                        .ToList();

                    // 若 DB 中角色倒置（Source 数量 > Member 数量 且 Member 仅 1 个），修正 DB 角色
                    if (sourceSignals.Count > 1 && memberSignals.Count == 1)
                    {
                        foreach (var sig in sourceSignals)
                            await _ioConstraintSignalService.UpdateAsync(
                                u => new PLC_IOConstraintSignalEntity { SignalRole = "Member" },
                                s => s.Id == sig.Id);

                        await _ioConstraintSignalService.UpdateAsync(
                            u => new PLC_IOConstraintSignalEntity { SignalRole = "Source" },
                            s => s.Id == memberSignals[0].Id);

                        // 交换引用以便后续生成正确描述
                        (sourceSignals, memberSignals) = (memberSignals, sourceSignals);
                    }

                    // 无 Source 的规则暂不处理（描述不会出现方向错误）
                    if (sourceSignals.Count == 0) continue;

                    var input = new ConstraintInput
                    {
                        RuleName = rule.RuleName,
                        Operator = "ALL_TRUE",
                        RuleType = rule.RuleType,
                        Signals = sourceSignals
                            .Select(s => new SignalInput { SignalName = s.SignalName, SignalRole = "Source", SortOrder = s.SortOrder })
                            .Concat(memberSignals.Select((s, i) => new SignalInput
                            {
                                SignalName = s.SignalName,
                                SignalRole = "Member",
                                SortOrder = s.SortOrder ?? i
                            }))
                            .ToList(),
                        IoTagCommentsByName = ioTagCommentsByName.Count > 0 ? ioTagCommentsByName : null
                    };

                    var correctDescription = BuildTemplateConstraintDescription(input);
                    if (string.Equals(rule.Description, correctDescription, StringComparison.Ordinal)) continue;

                    await _ioConstraintRuleService.UpdateAsync(
                        u => new PLC_IOConstraintRuleEntity { Description = correctDescription },
                        r => r.Id == rule.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Diagnosis] FixAllTrueConstraintDescriptions 修正描述时发生异常，跳过");
            }
        }

        /// <summary>SaveIOConstraintsAsync 的输入模型</summary>
        private class ConstraintInput
        {
            public string? RuleName { get; set; }
            public string? RuleType { get; set; }
            public string? Operator { get; set; }
            public string? Source { get; set; }
            public string? EquipmentId { get; set; }
            public decimal? Confidence { get; set; }
            public string? Description { get; set; }
            public string? Priority { get; set; }
            /// <summary>CUSTOM 运算符时的合法状态组合 JSON</summary>
            public string? ValidStates { get; set; }
            public List<SignalInput>? Signals { get; set; }

            /// <summary>运行时附加：信号名（TagName）→ IO Tag comment（业务描述）映射。
            /// 不参与 JSON 反序列化。SaveIOConstraintsAsync 入口构造后挂到每个 input,
            /// 让 description 生成器把信号名后追加 (comment) 提升可读性。
            /// </summary>
            [System.Text.Json.Serialization.JsonIgnore]
            public Dictionary<string, string>? IoTagCommentsByName { get; set; }
        }

        private class SignalInput
        {
            public string? SignalName { get; set; }
            public string? SignalAddress { get; set; }
            public string? SignalRole { get; set; }
            public int? SortOrder { get; set; }
        }

        /// <summary>
        /// FB 关键引脚白名单：末段命中此集合的 DB 变量（如 General.E_StopStatus / ManualMod / AutoRunMod）
        /// 允许进入约束表，与 IO Tag 名单并列作为放行集合。
        /// </summary>
        private static readonly HashSet<string> KeyFbPinNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "E_StopStatus", "ManualMod", "AutoRunMod", "FwdPmt", "BkdPmt",
            "ResetStatus", "Model", "PmtDebug", "NeedSafeAckFwd", "NeedSafeAckBkd"
        };

        /// <summary>
        /// 占位 / 常量 / 调试类信号黑名单：即使在 IO Tag 字典里也不能进约束表。
        /// - `Null`：PLC 工程师在 Graph 顺控/Lamp FC 输出引脚没绑实际信号时填的占位 M 位（永远 false）
        /// - `AlwaysTRUE` / `AlwaysFALSE`：常量信号
        /// - `Debug_*` / `Debug.*`：调试模式信号（不参与运行约束）
        /// 注：这里只匹配末段名，跨项目通用（任何项目里这种命名都是占位）。
        /// </summary>
        private static readonly HashSet<string> PlaceholderSignalNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Null", "AlwaysTRUE", "AlwaysFALSE", "True", "False"
        };

        /// <summary>
        /// 判断信号 IoMapping.Category 是否标了 Ignore（不参与诊断）。
        /// Step1 在 ai_plc_iotag.iomapping 字段标 `category=Ignore` 的信号是中转映射类，
        /// 不应该作为约束的 signalName（典型如 Sensor Mapping 块汇总的 -FZ/-SQ 等）。
        /// </summary>
        private static bool IsIgnoreCategorySignal(
            string? signalName,
            Dictionary<string, PLC_IOTagEntity> ioTagByName)
        {
            if (string.IsNullOrWhiteSpace(signalName)) return false;
            var trimmed = signalName.Trim();
            if (!ioTagByName.TryGetValue(trimmed, out var tag)) return false;
            var mapping = tag?.IoMapping;
            if (string.IsNullOrWhiteSpace(mapping)) return false;
            // 取首条 entry（pipe 分隔）的 category 字段
            var firstEntry = mapping.Split('|', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(firstEntry)) return false;
            foreach (var part in firstEntry.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0) continue;
                var k = part[..eq].Trim();
                var v = part[(eq + 1)..].Trim();
                if (string.Equals(k, "category", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(v, "Ignore", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 判断信号名是否是占位/常量信号（不应进约束表）。
        /// 检查：① 末段名命中 PlaceholderSignalNames（Null/AlwaysTRUE 等纯命名占位）
        ///       ② 整名含 Debug_Test/Debug. 等调试前缀（这两个判定保留：业界通用调试命名）
        ///
        /// 注意：不再用「Clock_*Hz」前缀过滤。CPU 时钟脉冲位的识别由结构判定承担
        /// （见 IsCpuClockPulseSignal），靠地址+表名/分组判定，跨项目通用。
        /// </summary>
        private static bool IsPlaceholderSignal(string? signalName)
        {
            if (string.IsNullOrWhiteSpace(signalName)) return true;
            var trimmed = signalName.Trim();
            var lastDot = trimmed.LastIndexOf('.');
            var leaf = lastDot >= 0 ? trimmed[(lastDot + 1)..] : trimmed;
            if (PlaceholderSignalNames.Contains(leaf)) return true;
            if (PlaceholderSignalNames.Contains(trimmed)) return true;
            // Debug_xxx / Debug.xxx 等调试信号（不放进约束）
            if (trimmed.StartsWith("Debug_", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("Debug.", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        /// <summary>
        /// 判断信号是否是 CPU 时钟脉冲位（结构判定，不靠命名前缀）。
        /// 西门子博图 CPU 配置「时钟存储字节」（Clock Memory Byte）会自动生成一个 8 位的方波时钟字节，
        /// 这个字节里 8 个连续位对应固定的 8 种频率（10/5/2.5/2/1.25/1/0.625/0.5 Hz，位顺序固定）。
        /// 这些信号在 PLC 程序里只能作为「脉冲驱动源」用（如 `Lamp := Clock_1Hz` 让灯闪烁），
        /// 不参与任何瞬时布尔约束（Pairing/Mutex/AllRequired/Safety）。
        ///
        /// 结构判定（跨项目通用，不依赖命名）：
        /// 1. 地址必须是 %M 区单 bit（如 %M1.0、%M0.3）
        /// 2. 同一 IO Tag 表里，**同一字节的 8 个连续位都登记为 Bool 类型且 comment 都是「N秒周期」模式**
        ///    → 该字节就是 CPU 时钟字节，里面所有信号都是脉冲位
        ///
        /// 调用方需要提前算好"哪些字节是时钟字节"的集合传进来，避免重复扫描。
        /// </summary>
        private static bool IsCpuClockPulseSignal(
            string? signalName,
            Dictionary<string, PLC_IOTagEntity> ioTagByName,
            HashSet<string> cpuClockByteAddresses)
        {
            if (string.IsNullOrWhiteSpace(signalName)) return false;
            if (!ioTagByName.TryGetValue(signalName.Trim(), out var tag)) return false;
            var addr = tag?.Address ?? string.Empty;
            if (!addr.StartsWith("%M", StringComparison.OrdinalIgnoreCase)) return false;
            // 取 byte 部分：%M1.7 → byte = "1"
            var dot = addr.IndexOf('.');
            if (dot <= 2) return false; // 至少 %Mx.y
            var bytePart = addr.Substring(2, dot - 2);
            return cpuClockByteAddresses.Contains(bytePart);
        }

        /// <summary>
        /// 扫描项目所有 IO Tag，识别出哪些 %M 字节是 CPU 时钟字节（8 个连续位都是 Bool + 注释是周期文本）。
        /// 返回的集合形如 {"1"}（表示 %M1 是时钟字节）。
        /// 在每次 SaveIOConstraintsAsync 调用开头算一次即可，复杂度 O(N)。
        /// </summary>
        private static HashSet<string> DetectCpuClockBytes(Dictionary<string, PLC_IOTagEntity> ioTagByName)
        {
            // 按 %M 字节分组，看每字节多少个 Bool bit + 它们的 comment 是否都是周期格式
            // 周期格式判定：comment 含「秒周期」(中文) 或匹配 \d+(\.\d+)?\s*(Hz|s 周期|s周期) 等
            var byteToBits = new Dictionary<string, List<PLC_IOTagEntity>>();
            foreach (var tag in ioTagByName.Values)
            {
                var addr = tag?.Address ?? string.Empty;
                if (!addr.StartsWith("%M", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(tag!.DataType, "Bool", StringComparison.OrdinalIgnoreCase)) continue;
                var dot = addr.IndexOf('.');
                if (dot <= 2) continue;
                var bytePart = addr.Substring(2, dot - 2);
                if (!byteToBits.TryGetValue(bytePart, out var list))
                {
                    list = new List<PLC_IOTagEntity>();
                    byteToBits[bytePart] = list;
                }
                list.Add(tag);
            }

            var clockBytes = new HashSet<string>();
            foreach (var kv in byteToBits)
            {
                // 一个字节里只有不到 6 个 Bool 位不算时钟字节（CPU 时钟字节固定 8 位）
                if (kv.Value.Count < 6) continue;
                // 每个 Bool bit 的 comment 都得是周期格式
                int periodLike = 0;
                foreach (var t in kv.Value)
                {
                    var c = t.Comment ?? string.Empty;
                    // 周期格式：包含中文「秒周期」/「s周期」，或英文 N(.N)Hz / Ns / N ms / period
                    if (System.Text.RegularExpressions.Regex.IsMatch(
                            c,
                            @"\d+(\.\d+)?\s*(秒|s|ms|Hz)\s*(周期|period|periods)?",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    {
                        periodLike++;
                    }
                }
                // 至少 6/8 位的 comment 都是周期格式 → 这个字节就是 CPU 时钟字节
                if (periodLike >= 6) clockBytes.Add(kv.Key);
            }
            return clockBytes;
        }

        /// <summary>
        /// 判断一个信号名是否允许进入约束表。
        /// 允许集合：① 本项目已登记的 IO Tag；② 末段名命中 KeyFbPinNames 的全局 DB 变量
        /// （如 General.E_StopStatus / ManualMod / AutoRunMod），与上游 Prompt 端策略一致。
        /// 其他（典型如 DB***Alarm.Type.*FwdError 这类幻觉报警位）一律丢弃。
        ///
        /// 黑名单优先：占位 / 常量信号（Null / AlwaysTRUE / Debug_* 等）一律拒绝，
        /// 即使 IO Tag 表里登记了也不允许进约束。
        /// </summary>
        internal static bool IsAllowedConstraintSignal(
            string? signalName,
            Dictionary<string, PLC_IOTagEntity> ioTagByName,
            HashSet<string>? extraAllowedSignals = null)
        {
            if (string.IsNullOrWhiteSpace(signalName))
            {
                return false;
            }

            // 黑名单：占位 / 常量 / 调试信号（高优先级，先拒绝；即使在 extraAllowedSignals 里也拒）
            if (IsPlaceholderSignal(signalName))
            {
                return false;
            }

            var trimmed = signalName.Trim();
            if (ioTagByName.ContainsKey(trimmed))
            {
                return true;
            }

            // 注意：含方括号的名字（如 Valve[12].FwdPmt）允许通过。
            // 这些是 PLC 程序里真实的 FB 实例 → 引脚连接，能反映约束关系。
            // 仅需通过 KeyFbPinNames 末段或 IO Tag 字典命中即可。

            // 末段命中 KeyFbPinNames 即放行。例如 General.E_StopStatus 末段是 E_StopStatus。
            var lastDot = trimmed.LastIndexOf('.');
            var leaf = lastDot >= 0 ? trimmed[(lastDot + 1)..] : trimmed;
            if (!string.IsNullOrWhiteSpace(leaf) && KeyFbPinNames.Contains(leaf))
            {
                return true;
            }

            // 结构判定补丁（与 §4.2 兼容）：本 LU SCoil/RCoil/Coil 真实驱动的 DB 内部信号
            // （典型如 DB***Alarm.Type.Errors.Error*）允许通过。
            // 这里依据「程序里被 coil 驱动」的结构证据 = 一定是有效的程序状态变量，
            // 不靠命名前缀匹配，跨项目通用。
            if (extraAllowedSignals != null && extraAllowedSignals.Contains(trimmed))
            {
                return true;
            }

            return false;
        }

        private static void NormalizeSignalInputs(ConstraintInput input)
        {
            if (input.Signals == null || input.Signals.Count == 0)
            {
                return;
            }

            input.Signals = input.Signals
                .Where(s => !string.IsNullOrWhiteSpace(s.SignalName))
                .Select((s, index) =>
                {
                    s.SignalName = s.SignalName?.Trim();
                    s.SignalRole = string.IsNullOrWhiteSpace(s.SignalRole)
                        ? "Member"
                        : s.SignalRole.Trim();
                    s.SortOrder ??= index;
                    return s;
                })
                .GroupBy(s => $"{s.SignalRole}|{s.SignalName}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderBy(x => x.SortOrder ?? int.MaxValue).First())
                .OrderBy(x => x.SortOrder ?? int.MaxValue)
                .ToList();
        }

        private static string BuildCompactConstraintRuleName(ConstraintInput input)
        {
            var signals = input.Signals ?? new List<SignalInput>();
            var sources = signals
                .Where(s => string.Equals(s.SignalRole, "Source", StringComparison.OrdinalIgnoreCase))
                .Select(s => s.SignalName?.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var targets = signals
                .Where(s => string.Equals(s.SignalRole, "Target", StringComparison.OrdinalIgnoreCase))
                .Select(s => s.SignalName?.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var members = signals
                .Where(s => string.Equals(s.SignalRole, "Member", StringComparison.OrdinalIgnoreCase))
                .Select(s => s.SignalName?.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var op = (input.Operator ?? string.Empty).Trim().ToUpperInvariant();
            var existingName = input.RuleName?.Trim();

            return op switch
            {
                "IMPLIES" => BuildImpliesRuleName(sources, targets, existingName),
                "ALL_TRUE" => BuildAllTrueRuleName(sources, targets, members, existingName),
                "AT_MOST_ONE" => BuildGroupRuleName(members, sources, targets, "互斥", existingName),
                "EXACTLY_ONE" => BuildGroupRuleName(members, sources, targets, "唯一选择", existingName),
                // REQUIRES_0 (Safety): 用 Source（联锁触发信号）起名，而非 Target。
                // 否则多 Target 的 Safety（如 1 个 E_Stop 锁多个 YV）会用第一个 Target 起名，
                // 导致 ruleName 误导（写成 OP030_YV13_W_零态要求 实际覆盖 60 个 YV），
                // 且不同 Source 锁同一 Target 时会出现 ruleName 重复（SafetyDoor3_Open_零态要求 ×7）。
                "REQUIRES_0" => BuildGroupRuleName(sources, targets, members, "零态联锁", existingName),
                "SAME_STATE" => BuildGroupRuleName(members, sources, targets, "同步", existingName),
                "CHAIN_ORDER" => BuildGroupRuleName(members, sources, targets, "时序", existingName),
                _ => !string.IsNullOrWhiteSpace(existingName)
                    ? existingName!
                    : BuildFallbackRuleName(sources, targets, members, input.RuleType, input.Operator)
            };
        }

        private static string BuildImpliesRuleName(List<string?> sources, List<string?> targets, string? existingName)
        {
            if (sources.Count == 1 && targets.Count == 1)
            {
                return $"{sources[0]}->{targets[0]}";
            }

            if (sources.Count == 1 && targets.Count > 1)
            {
                return $"{sources[0]}_驱动约束";
            }

            if (sources.Count > 1 && targets.Count == 1)
            {
                return $"{targets[0]}_来源约束";
            }

            return !string.IsNullOrWhiteSpace(existingName)
                ? existingName!
                : BuildFallbackRuleName(sources, targets, new List<string?>(), "Pairing", "IMPLIES");
        }

        private static string BuildAllTrueRuleName(
            List<string?> sources,
            List<string?> targets,
            List<string?> members,
            string? existingName)
        {
            if (sources.Count == 1)
            {
                return $"{sources[0]}_前置全满足";
            }

            if (targets.Count == 1)
            {
                return $"{targets[0]}_全满足";
            }

            var anchor = SelectBestAnchor(members);
            if (!string.IsNullOrWhiteSpace(anchor))
            {
                return $"{anchor}_全满足";
            }

            return !string.IsNullOrWhiteSpace(existingName)
                ? existingName!
                : "全满足约束";
        }

        private static string BuildGroupRuleName(
            List<string?> primarySignals,
            List<string?> secondarySignals,
            List<string?> tertiarySignals,
            string suffix,
            string? existingName)
        {
            var anchor = SelectBestAnchor(primarySignals)
                ?? SelectBestAnchor(secondarySignals)
                ?? SelectBestAnchor(tertiarySignals);

            if (!string.IsNullOrWhiteSpace(anchor))
            {
                return $"{anchor}_{suffix}";
            }

            return !string.IsNullOrWhiteSpace(existingName)
                ? existingName!
                : suffix;
        }

        private static string BuildFallbackRuleName(
            List<string?> sources,
            List<string?> targets,
            List<string?> members,
            string? ruleType,
            string? op)
        {
            var anchor = SelectBestAnchor(targets)
                ?? SelectBestAnchor(sources)
                ?? SelectBestAnchor(members);
            var suffix = !string.IsNullOrWhiteSpace(ruleType)
                ? ruleType!.Trim()
                : (!string.IsNullOrWhiteSpace(op) ? op!.Trim() : "约束");

            return !string.IsNullOrWhiteSpace(anchor)
                ? $"{anchor}_{suffix}"
                : suffix;
        }

        private static string? SelectBestAnchor(IEnumerable<string?> signals)
        {
            var candidates = signals
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (candidates.Count == 0)
            {
                return null;
            }

            if (candidates.Count == 1)
            {
                return candidates[0];
            }

            var commonPrefix = GetCommonPrefix(candidates);
            if (!string.IsNullOrWhiteSpace(commonPrefix) && commonPrefix!.Length >= 6)
            {
                // 清掉前缀末尾的分隔符（_/./-/ ）。
                // 修复 bug: General.ManualMod + General.AutoRunMod 共同前缀是 "General."，长度 8≥6，
                // 仅 TrimEnd('_') 会留下 "General."，最终生成 "General._互斥" 这种末段空命名。
                var trimmed = commonPrefix.TrimEnd('_', '.', '-', ' ');
                if (!string.IsNullOrWhiteSpace(trimmed) && trimmed.Length >= 4)
                {
                    // 修复 bug：两组信号共同前缀截断后相同（如 `OP020_CYL01_1_W/1_H` 和 `OP020_CYL01_1W/1H`
                    // 都截断成 `OP020_CYL01_1`），不同物理气缸被命名为同一 ruleName。
                    // 兜底：若截断结果 + 后续区分点的话 candidates[0] 比截断更长且差距 <= 4 字符，
                    // 优先用 candidates[0]（含完整区分点）。
                    if (candidates[0].Length - trimmed.Length <= 4)
                    {
                        return candidates[0];
                    }
                    return trimmed;
                }
                // 前缀清完太短（只剩 General/DB123 这种泛名）→ 回退第一个完整信号名
            }

            return candidates[0];
        }

        private static string? GetCommonPrefix(IReadOnlyList<string> values)
        {
            if (values == null || values.Count == 0)
            {
                return null;
            }

            var prefix = values[0];
            for (var i = 1; i < values.Count; i++)
            {
                var compare = values[i];
                var length = 0;
                var max = Math.Min(prefix.Length, compare.Length);
                while (length < max && char.ToUpperInvariant(prefix[length]) == char.ToUpperInvariant(compare[length]))
                {
                    length++;
                }

                prefix = prefix[..length];
                if (string.IsNullOrWhiteSpace(prefix))
                {
                    return null;
                }
            }

            return prefix;
        }

        /// <summary>
        /// 单条约束允许的最小信号数。绝大多数 operator 要 ≥2（互斥/同步/蕴含等都需要至少两个相关信号），
        /// 但 <c>MUST_BE_FALSE</c>（§4.5 屏蔽态报警单传感器约束）单 Target 形态合法 → 阈值 1。
        /// 用于 SaveIOConstraintsAsync 中各 signal-level 过滤后的「过滤剩余 &lt; min 则整条丢」判定。
        /// </summary>
        private static int GetMinSignalsForOperator(string? op) =>
            string.Equals(op, "MUST_BE_FALSE", StringComparison.OrdinalIgnoreCase) ? 1 : 2;

        private static bool TryValidateConstraintStructure(ConstraintInput input, out string reason)
        {
            reason = string.Empty;

            if (input.Signals == null || input.Signals.Count == 0)
            {
                reason = "signals 为空";
                return false;
            }

            var sourceCount = input.Signals.Count(s => string.Equals(s.SignalRole, "Source", StringComparison.OrdinalIgnoreCase));
            var targetCount = input.Signals.Count(s => string.Equals(s.SignalRole, "Target", StringComparison.OrdinalIgnoreCase));
            var memberCount = input.Signals.Count(s => string.Equals(s.SignalRole, "Member", StringComparison.OrdinalIgnoreCase));
            var op = (input.Operator ?? string.Empty).Trim().ToUpperInvariant();

            // MUST_BE_FALSE 是单 Target 形态（§4.5 屏蔽态报警单传感器约束），早于通用 ≥2 信号校验放行
            if (op == "MUST_BE_FALSE")
            {
                if (sourceCount > 0)
                {
                    reason = "MUST_BE_FALSE 不允许带 Source";
                    return false;
                }
                if (memberCount > 0)
                {
                    reason = "MUST_BE_FALSE 不允许带 Member";
                    return false;
                }
                if (targetCount < 1)
                {
                    reason = "MUST_BE_FALSE 必须包含至少 1 个 Target";
                    return false;
                }
                return true;
            }

            if (input.Signals.Select(s => s.SignalName).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2)
            {
                reason = "有效信号少于 2 个";
                return false;
            }

            switch (op)
            {
                case "IMPLIES":
                    if (sourceCount == 0 || targetCount == 0)
                    {
                        reason = "IMPLIES 必须同时包含 Source 和 Target";
                        return false;
                    }

                    if (memberCount > 0)
                    {
                        reason = "IMPLIES 不允许包含 Member";
                        return false;
                    }

                    return true;

                case "ALL_TRUE":
                    if (targetCount > 0 && memberCount > 0)
                    {
                        reason = "ALL_TRUE 不允许同时包含 Target 和 Member；请拆分为独立规则";
                        return false;
                    }

                    if (sourceCount > 0 && targetCount > 0)
                    {
                        reason = "ALL_TRUE 不允许同时包含 Source 和 Target；这类结构应改为 IMPLIES 或拆分规则";
                        return false;
                    }

                    if (memberCount == 0 && targetCount == 0)
                    {
                        reason = "ALL_TRUE 至少需要 Member 或 Target";
                        return false;
                    }

                    return true;

                case "REQUIRES_0":
                    if (targetCount == 0)
                    {
                        reason = "REQUIRES_0 必须包含 Target";
                        return false;
                    }

                    if (memberCount > 0)
                    {
                        reason = "REQUIRES_0 不允许包含 Member";
                        return false;
                    }

                    return true;

                case "AT_MOST_ONE":
                case "EXACTLY_ONE":
                    if (memberCount > 0 && (sourceCount > 0 || targetCount > 0))
                    {
                        reason = $"{op} 不允许同时混用 Member 与 Source/Target 分支";
                        return false;
                    }

                    if (memberCount >= 2)
                    {
                        return true;
                    }

                    if (sourceCount > 0 && targetCount > 0)
                    {
                        return true;
                    }

                    reason = $"{op} 需要至少两路 Member，或清晰的 Source/Target 分支";
                    return false;

                case "CHAIN_ORDER":
                    if (sourceCount > 0 || targetCount > 0)
                    {
                        reason = "CHAIN_ORDER 只允许使用 Member";
                        return false;
                    }

                    if (memberCount < 2)
                    {
                        reason = "CHAIN_ORDER 至少需要两个有序 Member";
                        return false;
                    }

                    return true;

                case "CUSTOM":
                    if (string.IsNullOrWhiteSpace(input.ValidStates))
                    {
                        reason = "CUSTOM 必须提供 ValidStates";
                        return false;
                    }

                    return true;

                default:
                    return true;
            }
        }

        /// <summary>
        /// 将 inputs 中 Source 相同的多条 ALL_TRUE 规则合并为一条，避免同一阀门驱动的多传感器被拆分存储。
        /// </summary>
        private static List<ConstraintInput> MergeAllTrueRulesBySameSource(List<ConstraintInput> inputs)
        {
            var allTrueInputs = inputs
                .Where(i => string.Equals(i.Operator, "ALL_TRUE", StringComparison.OrdinalIgnoreCase)
                            && i.Signals != null)
                .ToList();

            if (allTrueInputs.Count < 2) return inputs;

            // 按 Source 信号集合分组（同一组 Source 的规则应合并）
            var groups = allTrueInputs.GroupBy(i =>
            {
                var sources = i.Signals!
                    .Where(s => string.Equals(s.SignalRole, "Source", StringComparison.OrdinalIgnoreCase)
                                && !string.IsNullOrWhiteSpace(s.SignalName))
                    .Select(s => s.SignalName!)
                    .OrderBy(n => n);
                return string.Join("|", sources);
            }).Where(g => !string.IsNullOrWhiteSpace(g.Key) && g.Count() > 1).ToList();

            if (groups.Count == 0) return inputs;

            var toRemove = new HashSet<ConstraintInput>(ReferenceEqualityComparer.Instance);
            foreach (var group in groups)
            {
                var first = group.First();
                var existingMemberNames = new HashSet<string>(
                    first.Signals!
                        .Where(s => string.Equals(s.SignalRole, "Member", StringComparison.OrdinalIgnoreCase))
                        .Select(s => s.SignalName ?? ""),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var other in group.Skip(1))
                {
                    foreach (var sig in other.Signals!
                                 .Where(s => string.Equals(s.SignalRole, "Member", StringComparison.OrdinalIgnoreCase)
                                             && !string.IsNullOrWhiteSpace(s.SignalName)))
                    {
                        if (existingMemberNames.Add(sig.SignalName!))
                        {
                            first.Signals!.Add(new SignalInput
                            {
                                SignalName = sig.SignalName,
                                SignalAddress = sig.SignalAddress,
                                SignalRole = "Member",
                                SortOrder = first.Signals.Count
                            });
                        }
                    }
                    toRemove.Add(other);
                }
            }

            return inputs.Where(i => !toRemove.Contains(i)).ToList();
        }

        /// <summary>
        /// 将 inputs 中 Source 集合相同的多条同 Operator 规则合并为一条，Target 信号去重合并。
        /// 适用于 REQUIRES_0（急停→多个输出禁止）和 IMPLIES（条件→多个输出必须成立）。
        /// </summary>
        private static List<ConstraintInput> MergeSameSourceTargetRulesBySameSource(
            List<ConstraintInput> inputs,
            string operatorName,
            HashSet<string>? skipIfMutexWithExisting = null)
        {
            var candidates = inputs
                .Where(i => string.Equals(i.Operator, operatorName, StringComparison.OrdinalIgnoreCase)
                            && i.Signals != null)
                .ToList();

            if (candidates.Count < 2) return inputs;

            // 按 Source 信号集合分组（空 Source key 跳过，避免无源规则被错误合并）
            var groups = candidates
                .GroupBy(i =>
                {
                    var sources = i.Signals!
                        .Where(s => string.Equals(s.SignalRole, "Source", StringComparison.OrdinalIgnoreCase)
                                    && !string.IsNullOrWhiteSpace(s.SignalName))
                        .Select(s => s.SignalName!)
                        .OrderBy(n => n);
                    return string.Join("|", sources);
                })
                .Where(g => !string.IsNullOrWhiteSpace(g.Key) && g.Count() > 1)
                .ToList();

            if (groups.Count == 0) return inputs;

            // 工具函数：检查新 Target 与已合并 Target 是否构成互斥对
            bool IsMutexConflict(string newTarget, HashSet<string> existing)
            {
                if (skipIfMutexWithExisting == null || skipIfMutexWithExisting.Count == 0) return false;
                foreach (var existingName in existing)
                {
                    var key = string.Compare(newTarget, existingName, StringComparison.OrdinalIgnoreCase) < 0
                        ? $"{newTarget}|{existingName}" : $"{existingName}|{newTarget}";
                    if (skipIfMutexWithExisting.Contains(key)) return true;
                }
                return false;
            }

            var toRemove = new HashSet<ConstraintInput>(ReferenceEqualityComparer.Instance);
            foreach (var group in groups)
            {
                var first = group.First();
                var existingTargetNames = new HashSet<string>(
                    first.Signals!
                        .Where(s => string.Equals(s.SignalRole, "Target", StringComparison.OrdinalIgnoreCase))
                        .Select(s => s.SignalName ?? ""),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var other in group.Skip(1))
                {
                    bool keptAnyTarget = false;
                    foreach (var sig in other.Signals!
                                 .Where(s => string.Equals(s.SignalRole, "Target", StringComparison.OrdinalIgnoreCase)
                                             && !string.IsNullOrWhiteSpace(s.SignalName)))
                    {
                        // 防止合并产生物理矛盾：若新 Target 与 first 已合并 Target 集合中任何一个构成 Mutex
                        // (如 first 已有 CYL_W、other 想加 CYL_H) → 不合并这个 Target，保留 other 为独立条目。
                        // 修复 bug: Augmenter 输出的 Pairing(YV05_W → CYL05_W) 被 LLM 的 IMPLIES(YV05_W → CYL05_H)
                        // 合并丢失，导致正确 Pairing 消失。
                        if (IsMutexConflict(sig.SignalName!, existingTargetNames))
                        {
                            keptAnyTarget = true; // 保留 other 不合并
                            continue;
                        }
                        if (existingTargetNames.Add(sig.SignalName!))
                        {
                            first.Signals!.Add(new SignalInput
                            {
                                SignalName = sig.SignalName,
                                SignalAddress = sig.SignalAddress,
                                SignalRole = "Target",
                                SortOrder = first.Signals.Count
                            });
                        }
                    }
                    if (!keptAnyTarget) toRemove.Add(other);
                }
            }

            return inputs.Where(i => !toRemove.Contains(i)).ToList();
        }

        private static void NormalizeConstraintDirections(
            ConstraintInput input,
            Dictionary<string, PLC_IOTagEntity> ioTagByName)
        {
            if (input.Signals == null || input.Signals.Count == 0) return;

            var op = (input.Operator ?? string.Empty).ToUpperInvariant();

            if (op == "ALL_TRUE")
            {
                var sourceSignals = input.Signals.Where(s =>
                    string.Equals(s.SignalRole, "Source", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(s.SignalName)).ToList();

                var targetSignals = input.Signals.Where(s =>
                    string.Equals(s.SignalRole, "Target", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(s.SignalName)).ToList();

                var memberSignals = input.Signals.Where(s =>
                    string.Equals(s.SignalRole, "Member", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(s.SignalName)).ToList();

                // 修正1：Target 不允许在 ALL_TRUE 中出现。
                //   无 Source → Target 升格为 Source（驱动输出是触发方）
                //   有 Source → Target 是冗余角色，删除
                if (targetSignals.Count > 0)
                {
                    if (sourceSignals.Count == 0)
                    {
                        foreach (var sig in targetSignals)
                            sig.SignalRole = "Source";
                        sourceSignals = targetSignals; // 刷新引用
                    }
                    else
                    {
                        foreach (var sig in targetSignals)
                            input.Signals.Remove(sig);
                    }
                    targetSignals = [];
                }

                // 修正2：AI 有时把多个传感器（CYL）赋为 Source、单个阀门（YV）赋为 Member——结构倒置。
                // 判据：Source 数量 > Member 数量，且 Member 仅有 1 个（1 个驱动对多个反馈是正常，反之则倒置）。
                // 修正：所有 Source 降为 Member，所有 Member 升为 Source。
                if (sourceSignals.Count > 1 && memberSignals.Count == 1)
                {
                    foreach (var sig in sourceSignals)
                        sig.SignalRole = "Member";
                    foreach (var sig in memberSignals)
                        sig.SignalRole = "Source";
                    // 刷新引用，供后续修正3使用
                    (sourceSignals, memberSignals) = (memberSignals, sourceSignals.ToList());
                }

                // 修正3：ALL_TRUE + 1 Source + 1 Member → 降级为 IMPLIES。
                // ALL_TRUE 语义是"多个 Member 必须全部为 true"，仅有 1 个 Member 时"全部"没有意义，
                // 实质上是单对单的蕴含关系，应使用 IMPLIES（Source → Target）。
                if (sourceSignals.Count == 1 && memberSignals.Count == 1)
                {
                    input.Operator = "IMPLIES";
                    input.RuleType = "Pairing";
                    memberSignals[0].SignalRole = "Target";
                }
            }
        }

        private static bool ShouldSkipInvalidConstraintInput(ConstraintInput input)
        {
            // 当前仅做基础结构校验（空信号、空角色等），
            // 反向 IMPLIES 校验已迁移到 SaveIOConstraintsAsync 的互斥地图交叉校验中，不再依赖命名模式。
            if (input.Signals == null || input.Signals.Count == 0)
                return false;

            // IMPLIES 必须有且仅有一个 Source 和至少一个 Target
            if (string.Equals(input.Operator, "IMPLIES", StringComparison.OrdinalIgnoreCase))
            {
                var hasSource = input.Signals.Any(s => string.Equals(s.SignalRole, "Source", StringComparison.OrdinalIgnoreCase)
                                                       && !string.IsNullOrWhiteSpace(s.SignalName));
                var hasTarget = input.Signals.Any(s => string.Equals(s.SignalRole, "Target", StringComparison.OrdinalIgnoreCase)
                                                       && !string.IsNullOrWhiteSpace(s.SignalName));
                if (!hasSource || !hasTarget)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 检查一条 IMPLIES 约束的 Source 与 Target 是否已在互斥地图中共现（即两个信号在某个 AT_MOST_ONE 规则里）。
        /// 若互斥，则 IMPLIES 在逻辑上矛盾（互斥 ⇒ 不可同为 True ⇒ IMPLIES 无法成立），应跳过。
        /// </summary>
        private static bool IsImpliesConflictingWithMutex(
            ConstraintInput input,
            Dictionary<string, HashSet<string>> mutexSignalGroups)
        {
            if (input.Signals == null) return false;

            var sourceName = input.Signals
                .FirstOrDefault(s => string.Equals(s.SignalRole, "Source", StringComparison.OrdinalIgnoreCase))
                ?.SignalName;
            var targetName = input.Signals
                .FirstOrDefault(s => string.Equals(s.SignalRole, "Target", StringComparison.OrdinalIgnoreCase))
                ?.SignalName;

            if (string.IsNullOrWhiteSpace(sourceName) || string.IsNullOrWhiteSpace(targetName))
                return false;

            return mutexSignalGroups.TryGetValue(sourceName, out var peers)
                   && peers.Contains(targetName);
        }

        private static string? NormalizeImpliesRuleName(string? ruleName, string originalSourceName, string originalTargetName)
        {
            if (string.IsNullOrWhiteSpace(ruleName))
            {
                return ruleName;
            }

            var normalized = ruleName!;

            if (!string.IsNullOrWhiteSpace(originalSourceName) && !string.IsNullOrWhiteSpace(originalTargetName))
            {
                var swapped = SwapOrderedTokens(normalized, originalSourceName, originalTargetName);
                if (!string.Equals(swapped, normalized, StringComparison.Ordinal))
                {
                    normalized = swapped;
                }
            }

            normalized = Regex.Replace(normalized, "指令与输出配对", "输出与指令配对", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, "指令输出配对", "输出指令配对", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, "指令-输出配对", "输出-指令配对", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, "命令与输出配对", "输出与命令配对", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, "命令输出配对", "输出命令配对", RegexOptions.IgnoreCase);

            return normalized;
        }

        private static string SwapOrderedTokens(string text, string left, string right)
        {
            if (string.IsNullOrWhiteSpace(text)
                || string.IsNullOrWhiteSpace(left)
                || string.IsNullOrWhiteSpace(right))
            {
                return text;
            }

            var separatorPatterns = new[]
            {
                "_",
                "-",
                "→",
                "->",
                "=>"
            };

            foreach (var separator in separatorPatterns)
            {
                var pattern = $"{Regex.Escape(left)}{Regex.Escape(separator)}{Regex.Escape(right)}";
                var replacement = $"{right}{separator}{left}";
                var swapped = Regex.Replace(text, pattern, replacement, RegexOptions.IgnoreCase);
                if (!string.Equals(swapped, text, StringComparison.Ordinal))
                {
                    return swapped;
                }
            }

            return text;
        }

        private static bool ShouldReverseImpliesDirection(
            string sourceName,
            string targetName,
            Dictionary<string, PLC_IOTagEntity> ioTagByName)
        {
            if (IsCylinderCommandToValveOutput(sourceName, targetName))
            {
                return true;
            }

            var sourceTag = ioTagByName.TryGetValue(sourceName, out var sourceIoTag) ? sourceIoTag : null;
            var targetTag = ioTagByName.TryGetValue(targetName, out var targetIoTag) ? targetIoTag : null;

            var sourceLooksActuatorOutput = LooksLikeActuatorOutput(sourceName, sourceTag);
            var targetLooksActuatorOutput = LooksLikeActuatorOutput(targetName, targetTag);
            var sourceLooksCommand = LooksLikeCommandOrCondition(sourceName, sourceTag);
            var targetLooksCommand = LooksLikeCommandOrCondition(targetName, targetTag);

            if (!sourceLooksActuatorOutput && targetLooksActuatorOutput)
            {
                return true;
            }

            if (sourceLooksCommand && targetLooksActuatorOutput && !targetLooksCommand)
            {
                return true;
            }

            return false;
        }

        private static bool IsCylinderCommandToValveOutput(string sourceName, string targetName)
        {
            var sourceMatch = Regex.Match(sourceName, @"^(?<prefix>.+?)_CYL(?<index>\d+?)_(?<dir>W|H)$", RegexOptions.IgnoreCase);
            var targetMatch = Regex.Match(targetName, @"^(?<prefix>.+?)_YV(?<index>\d+?)_(?<dir>W|H)$", RegexOptions.IgnoreCase);

            if (!sourceMatch.Success || !targetMatch.Success)
            {
                return false;
            }

            return string.Equals(sourceMatch.Groups["prefix"].Value, targetMatch.Groups["prefix"].Value, StringComparison.OrdinalIgnoreCase)
                && string.Equals(sourceMatch.Groups["index"].Value, targetMatch.Groups["index"].Value, StringComparison.OrdinalIgnoreCase)
                && string.Equals(sourceMatch.Groups["dir"].Value, targetMatch.Groups["dir"].Value, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsInvalidOppositeDirectionalImpliesPair(string sourceSignalName, string targetSignalName)
        {
            var sourceMatch = Regex.Match(
                sourceSignalName,
                @"^(?<prefix>.+?)_(?<kind>CYL|YV)(?<index>\d+?)_(?<dir>W|H)$",
                RegexOptions.IgnoreCase);
            var targetMatch = Regex.Match(
                targetSignalName,
                @"^(?<prefix>.+?)_(?<kind>CYL|YV)(?<index>\d+?)_(?<dir>W|H)$",
                RegexOptions.IgnoreCase);

            if (!sourceMatch.Success || !targetMatch.Success)
            {
                return false;
            }

            var sameMechanism =
                string.Equals(sourceMatch.Groups["prefix"].Value, targetMatch.Groups["prefix"].Value, StringComparison.OrdinalIgnoreCase)
                && string.Equals(sourceMatch.Groups["index"].Value, targetMatch.Groups["index"].Value, StringComparison.OrdinalIgnoreCase);

            if (!sameMechanism)
            {
                return false;
            }

            return !string.Equals(sourceMatch.Groups["dir"].Value, targetMatch.Groups["dir"].Value, StringComparison.OrdinalIgnoreCase);
        }

        private static bool LooksLikeActuatorOutput(string signalName, PLC_IOTagEntity? ioTag)
        {
            var comment = ioTag?.Comment ?? string.Empty;
            var address = ioTag?.Address ?? string.Empty;

            return signalName.Contains("_YV", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(signalName, @"(^|[_\W])YV\d+([_\W]|$)", RegexOptions.IgnoreCase)
                || comment.Contains("输出", StringComparison.OrdinalIgnoreCase)
                || address.StartsWith("%Q", StringComparison.OrdinalIgnoreCase);
        }

        private static bool LooksLikeCommandOrCondition(string signalName, PLC_IOTagEntity? ioTag)
        {
            var comment = ioTag?.Comment ?? string.Empty;

            return signalName.Contains("_CYL", StringComparison.OrdinalIgnoreCase)
                || signalName.Contains("Cmd", StringComparison.OrdinalIgnoreCase)
                || signalName.Contains("Auto", StringComparison.OrdinalIgnoreCase)
                || signalName.Contains("Pmt", StringComparison.OrdinalIgnoreCase)
                || comment.Contains("指令", StringComparison.OrdinalIgnoreCase)
                || comment.Contains("命令", StringComparison.OrdinalIgnoreCase)
                || comment.Contains("允许", StringComparison.OrdinalIgnoreCase)
                || comment.Contains("条件", StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region ========== 工具 7：信号约束校验（N 元） ==========

        /// <summary>
        /// [工具] 用约束规则校验信号组合，发现物理矛盾（支持 N 元约束）
        /// 
        /// 支持 7 种约束运算符：
        /// - AT_MOST_ONE：Members 中同时为 true 的不超过 1（互斥组）
        /// - EXACTLY_ONE：Members 中为 true 的恰好 1（唯一选择）
        /// - ALL_TRUE：所有 Members 必须全为 true
        /// - IMPLIES：所有 Source=true 时，至少一个 Target=true
        /// - REQUIRES_0：任一 Source=false 时，所有 Target 必须=false
        /// - CHAIN_ORDER：按 SortOrder 排序，后序为 true 时前序也必须为 true
        /// - CUSTOM：当前信号值组合必须匹配 ValidStates 中定义的任一合法组合
        /// </summary>
        /// <param name="projectId">项目ID</param>
        /// <param name="signalValues">信号值字典 JSON（如 {"CYL01_FwdOK":true,"CYL01_BkdOK":true}），可选</param>
        public Task<object> ValidateIOSignalsAsync(long projectId, string signalValues = "")
        {
            return ValidateIOSignalsAsync(projectId, signalValues, "");
        }

        public async Task<object> ValidateIOSignalsAsync(long projectId, string signalValues, string stationCode = "")
        {
            _logger.LogInformation("[Diagnosis] ValidateIOSignals: projectId={ProjectId}, stationCode={StationCode}", projectId, stationCode);

            try
            {
                // 1. 获取该项目的所有约束规则
                var rules = await _ioConstraintRuleService.GetListAsync(
                    x => x.ProjectId == projectId && !x.IsDeleted);

                rules = FilterConstraintRulesByStation(rules, stationCode);

                if (rules == null || rules.Count == 0)
                {
                    return new
                    {
                        Success = true,
                        Message = "该项目尚未建立约束规则库。请先使用 GetIOTagsForAnalysisAsync + SaveIOConstraintsAsync 生成约束。",
                        ConstraintCount = 0,
                        Violations = new List<object>()
                    };
                }

                // 2. 批量加载所有规则的信号明细
                var ruleIds = rules.Select(r => r.Id).ToList();
                var allSignals = await _ioConstraintSignalService.GetListAsync(
                    x => ruleIds.Contains(x.RuleId) && !x.IsDeleted);
                var signalsByRule = allSignals.GroupBy(s => s.RuleId)
                    .ToDictionary(g => g.Key, g => g.OrderBy(s => s.SortOrder ?? 0).ToList());

                // 3. 如果提供了信号值，执行校验
                var violations = new List<object>();

                if (!string.IsNullOrWhiteSpace(signalValues))
                {
                    Dictionary<string, bool>? values = null;
                    try
                    {
                        values = JsonSerializer.Deserialize<Dictionary<string, bool>>(signalValues,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    }
                    catch (Exception ex)
                    {
                        return new { Success = false, Message = $"信号值 JSON 解析失败: {ex.Message}。格式示例: {{\"CYL01_FwdOK\":true,\"CYL01_BkdOK\":false}}" };
                    }

                    if (values != null)
                    {
                        rules = FilterConstraintRulesByInstanceAnchors(rules, signalsByRule, values, stationCode);
                        rules = FilterConstraintRulesByObservedSignals(rules, signalsByRule, values);
                        ruleIds = rules.Select(r => r.Id).ToList();
                        allSignals = allSignals.Where(s => ruleIds.Contains(s.RuleId)).ToList();
                        signalsByRule = allSignals.GroupBy(s => s.RuleId)
                            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.SortOrder ?? 0).ToList());
                        var mutexStates = BuildMutexGroupStates(rules, signalsByRule, values);

                        foreach (var rule in rules)
                        {
                            if (!signalsByRule.TryGetValue(rule.Id, out var ruleSignals) || ruleSignals.Count == 0)
                                continue;

                            // 获取各信号的实际值
                            var resolvedValues = ruleSignals
                                .Select(s => new SignalValidationSnapshot
                                {
                                    Signal = s,
                                    Value = FindSignalValue(values, s.SignalName)
                                })
                                .ToList();

                            // 跳过全部缺失值的规则
                            if (resolvedValues.All(sv => sv.Value == null)) continue;

                            var signalEvaluations = resolvedValues
                                .Select(sv => new SignalEvalItem
                                {
                                    SignalName = sv.Signal.SignalName,
                                    Role = sv.Signal.SignalRole,
                                    Value = sv.Value,
                                    SortOrder = sv.Signal.SortOrder ?? 0
                                })
                                .ToList();

                            var evaluationContext = BuildConstraintEvaluationContext(rule, signalEvaluations, mutexStates);
                            bool isViolation = EvaluateConstraint(rule.Operator, signalEvaluations, rule.ValidStates, evaluationContext);

                            if (isViolation)
                            {
                                var station = ResolveRuleStationCode(rule, ruleSignals);
                                var device = ResolveRuleDeviceLabel(rule, ruleSignals);
                                var reason = BuildViolationReason(rule, signalEvaluations, evaluationContext);
                                violations.Add(new
                                {
                                    Station = station,
                                    Device = device,
                                    Diagnosis = $"{station} {device} 卡住：{reason}",
                                    rule.RuleName,
                                    rule.RuleType,
                                    rule.Operator,
                                    rule.Priority,
                                    rule.EquipmentId,
                                    rule.Description,
                                    CurrentState = BuildCurrentStateSummary(resolvedValues),
                                    ExpectedState = BuildExpectedStateSummary(rule, signalEvaluations, evaluationContext),
                                    Reason = reason,
                                    FallbackMode = evaluationContext.Mode,
                                    Signals = resolvedValues.Select(sv => new
                                    {
                                        sv.Signal.SignalName,
                                        sv.Signal.SignalRole,
                                        ActualValue = sv.Value?.ToString() ?? "N/A"
                                    }).ToList(),
                                    ExpectedBehavior = GetExpectedBehavior(rule, ruleSignals)
                                });
                            }
                        }
                    }
                }

                var totalRules = rules.Count;
                var criticalViolations = violations.Count(v => ((dynamic)v).Priority == "Critical");
                var healthScore = violations.Count == 0 ? 100 : Math.Max(0, 100 - criticalViolations * 20 - (violations.Count - criticalViolations) * 5);

                return new
                {
                    Success = true,
                    ProjectId = projectId,
                    TotalConstraints = totalRules,
                    ConstraintsByType = rules.GroupBy(c => c.RuleType).ToDictionary(g => g.Key, g => g.Count()),
                    ValidationResult = string.IsNullOrWhiteSpace(signalValues)
                        ? "未提供信号值，仅返回约束规则概览。对接 IoT 后可自动传入实时值。"
                        : $"校验完成：{violations.Count} 处违规 / {totalRules} 条规则",
                    HealthScore = healthScore,
                    Violations = violations,
                    Hint = violations.Count > 0
                        ? $"⚠ 发现 {violations.Count} 处信号约束违规（其中 {criticalViolations} 处严重）。建议优先排查 Critical 级别的违规信号。"
                        : "✅ 所有信号组合通过约束校验，信号状态健康。"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Diagnosis] ValidateIOSignals 异常");
                return new { Success = false, Message = $"信号校验失败: {ex.Message}" };
            }
        }

        #endregion

        #region ========== 工具 8：信号关系查询 ==========

        public Task<object> GetIORelationshipAsync(string signalName)
        {
            return GetIORelationshipAsync(signalName, 0, null, "");
        }

        public async Task<object> GetIORelationshipAsync(string signalName, long projectId, long? callerLogicUnitId = null, string stationCode = "")
        {
            _logger.LogInformation("[Diagnosis] GetIORelationship: signal={Signal}", signalName);

            if (string.IsNullOrWhiteSpace(signalName))
            {
                return new { Success = false, Message = "信号名不能为空" };
            }

            try
            {
                {
                var resolvedProjectId = projectId > 0
                    ? projectId
                    : await InferProjectIdForSignalAsync(signalName);
                var normalizedStationCode = !string.IsNullOrWhiteSpace(stationCode)
                    ? stationCode.Trim().ToUpperInvariant()
                    : ExtractStationCode(signalName) ?? string.Empty;
                List<RelationshipConstraintDto> relationshipConstraints;
                if (resolvedProjectId > 0)
                {
                    var xmlConstraintExtractService = _serviceProvider.GetRequiredService<XmlConstraintExtractService>();
                    var constraintsJson = await xmlConstraintExtractService.ExtractSignalConstraintsAsync(
                        resolvedProjectId,
                        signalName,
                        callerLogicUnitId,
                        normalizedStationCode);
                    relationshipConstraints = DeserializeRelationshipConstraints(constraintsJson);
                }
                else
                {
                    relationshipConstraints = await LoadLegacyRelationshipConstraintsAsync(signalName);
                }

                relationshipConstraints = FilterRelationshipConstraintsByStation(relationshipConstraints, normalizedStationCode);

                var ctxConstraintInfo = relationshipConstraints.Select(r => new
                {
                    r.RuleName,
                    r.RuleType,
                    r.Operator,
                    r.Priority,
                    r.Description,
                    r.Confidence,
                    r.EquipmentId,
                    Signals = r.Signals
                        .OrderBy(s => s.SortOrder)
                        .Select(s => new
                        {
                            s.SignalName,
                            s.SignalAddress,
                            s.SignalRole,
                            s.SortOrder
                        }).ToList()
                }).ToList();

                var ctxIoTags = await _ioTagService.GetListAsync(
                    x => x.TagName!.Contains(signalName) && !x.IsDeleted);

                var ctxTagDef = ctxIoTags.Select(t => new
                {
                    t.TagName, t.Address, t.DataType, t.Comment,
                    t.TagCategory, t.IsSafety, t.EquipmentId
                }).ToList();

                var ctxAccesses = await _variableAccessService.GetListAsync(
                    x => x.VariableName!.Contains(signalName) && !x.IsDeleted);

                var ctxBlockIds = ctxAccesses.Where(a => a.BlockId.HasValue).Select(a => a.BlockId!.Value).Distinct().ToList();
                var ctxLuIds = ctxAccesses.Where(a => a.LogicUnitId.HasValue).Select(a => a.LogicUnitId!.Value).Distinct().ToList();

                var ctxBlocks = ctxBlockIds.Count > 0
                    ? await _blockService.GetListByIdsAsync(ctxBlockIds)
                    : new List<PLC_BlockEntity>();
                var ctxLogicUnits = ctxLuIds.Count > 0
                    ? await _logicUnitService.GetListByIdsAsync(ctxLuIds)
                    : new List<PLC_LogicUnitEntity>();

                var ctxBlockDict = ctxBlocks.ToDictionary(b => b.Id);
                var ctxLuDict = ctxLogicUnits.ToDictionary(lu => lu.Id);

                var ctxLogicRefs = ctxAccesses.Select(a =>
                {
                    PLC_BlockEntity? block = a.BlockId.HasValue && ctxBlockDict.ContainsKey(a.BlockId.Value)
                        ? ctxBlockDict[a.BlockId.Value] : null;
                    PLC_LogicUnitEntity? lu = a.LogicUnitId.HasValue && ctxLuDict.ContainsKey(a.LogicUnitId.Value)
                        ? ctxLuDict[a.LogicUnitId.Value] : null;
                    return new
                    {
                        BlockName = block?.BlockName,
                        NetworkTitle = lu?.Title,
                        a.AccessType,
                        a.LogicUnitId
                    };
                })
                .GroupBy(a => new { a.BlockName, a.NetworkTitle, a.AccessType, a.LogicUnitId })
                .Select(g => new
                {
                    g.Key.BlockName,
                    g.Key.NetworkTitle,
                    g.Key.AccessType,
                    g.Key.LogicUnitId,
                    Count = g.Count()
                }).Take(20).ToList();

                var ctxRelatedSignals = relationshipConstraints
                    .SelectMany(r => r.Signals)
                    .Select(s => s.SignalName)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Where(s => !string.Equals(s, signalName, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return new
                {
                    Success = true,
                    SignalName = signalName,
                    ProjectId = resolvedProjectId,
                    CallerLogicUnitId = callerLogicUnitId,
                    StationCode = normalizedStationCode,
                    IOTagDefinition = ctxTagDef,
                    Constraints = ctxConstraintInfo,
                    LogicReferences = ctxLogicRefs,
                    RelatedSignals = ctxRelatedSignals,
                    Summary = new
                    {
                        TotalConstraints = relationshipConstraints.Count,
                        ByType = relationshipConstraints
                            .GroupBy(r => r.RuleType ?? string.Empty)
                            .ToDictionary(g => g.Key, g => g.Count()),
                        LogicLocations = ctxLogicRefs.Count
                    },
                    Hint = relationshipConstraints.Count > 0
                        ? $"淇″彿 {signalName} 鏈?{relationshipConstraints.Count} 鏉＄害鏉熷叧绯伙紝鍏宠仈 {ctxRelatedSignals.Count} 涓浉鍏充俊鍙枫€?"
                        : $"淇″彿 {signalName} 鏆傛棤绾︽潫瑙勫垯銆傚彲浣跨敤 GetIOTagsForAnalysisAsync + SaveIOConstraintsAsync 鐢熸垚銆?"
                };
                // 1. 在 Signal 表中模糊查找匹配的信号
                }
                var matchedSignals = await _ioConstraintSignalService.GetListAsync(
                    x => x.SignalName.Contains(signalName) && !x.IsDeleted);

                // 2. 获取关联的 Rule IDs
                var ruleIds = matchedSignals.Select(s => s.RuleId).Distinct().ToList();

                var rules = ruleIds.Count > 0
                    ? await _ioConstraintRuleService.GetListByIdsAsync(ruleIds)
                    : new List<PLC_IOConstraintRuleEntity>();
                rules = rules.Where(r => !r.IsDeleted).ToList();
                ruleIds = rules.Select(r => r.Id).ToList();

                // 3. 加载这些规则下的所有信号（包含兄弟信号）
                var allRuleSignals = ruleIds.Count > 0
                    ? await _ioConstraintSignalService.GetListAsync(
                        x => ruleIds.Contains(x.RuleId) && !x.IsDeleted)
                    : new List<PLC_IOConstraintSignalEntity>();

                var constraintInfo = rules.Select(r =>
                {
                    var signals = allRuleSignals.Where(s => s.RuleId == r.Id).ToList();
                    return new
                    {
                        r.RuleName,
                        r.RuleType,
                        r.Operator,
                        r.Priority,
                        r.Description,
                        r.Confidence,
                        r.EquipmentId,
                        Signals = signals.Select(s => new
                        {
                            s.SignalName,
                            s.SignalAddress,
                            s.SignalRole,
                            s.SortOrder
                        }).ToList()
                    };
                }).ToList();

                // 4. 查 IOTag 定义
                var ioTags = await _ioTagService.GetListAsync(
                    x => x.TagName!.Contains(signalName) && !x.IsDeleted);

                var tagDef = ioTags.Select(t => new
                {
                    t.TagName, t.Address, t.DataType, t.Comment,
                    t.TagCategory, t.IsSafety, t.EquipmentId
                }).ToList();

                // 5. 查 PLC 逻辑中的引用位置
                var accesses = await _variableAccessService.GetListAsync(
                    x => x.VariableName!.Contains(signalName) && !x.IsDeleted);

                var blockIds = accesses.Where(a => a.BlockId.HasValue).Select(a => a.BlockId!.Value).Distinct().ToList();
                var luIds = accesses.Where(a => a.LogicUnitId.HasValue).Select(a => a.LogicUnitId!.Value).Distinct().ToList();

                var blocks = blockIds.Count > 0
                    ? await _blockService.GetListByIdsAsync(blockIds)
                    : new List<PLC_BlockEntity>();
                var logicUnits = luIds.Count > 0
                    ? await _logicUnitService.GetListByIdsAsync(luIds)
                    : new List<PLC_LogicUnitEntity>();

                var blockDict = blocks.ToDictionary(b => b.Id);
                var luDict = logicUnits.ToDictionary(lu => lu.Id);

                var logicRefs = accesses.Select(a =>
                {
                    PLC_BlockEntity? block = a.BlockId.HasValue && blockDict.ContainsKey(a.BlockId.Value)
                        ? blockDict[a.BlockId.Value] : null;
                    PLC_LogicUnitEntity? lu = a.LogicUnitId.HasValue && luDict.ContainsKey(a.LogicUnitId.Value)
                        ? luDict[a.LogicUnitId.Value] : null;
                    return new
                    {
                        BlockName = block?.BlockName,
                        NetworkTitle = lu?.Title,
                        a.AccessType,
                        a.LogicUnitId
                    };
                })
                .GroupBy(a => new { a.BlockName, a.NetworkTitle, a.AccessType, a.LogicUnitId })
                .Select(g => new
                {
                    g.Key.BlockName,
                    g.Key.NetworkTitle,
                    g.Key.AccessType,
                    g.Key.LogicUnitId,
                    Count = g.Count()
                }).Take(20).ToList();

                // 6. 提取关联信号全景
                var relatedSignals = allRuleSignals
                    .Select(s => s.SignalName)
                    .Where(s => !s.Contains(signalName, StringComparison.OrdinalIgnoreCase))
                    .Distinct()
                    .ToList();

                return new
                {
                    Success = true,
                    SignalName = signalName,
                    IOTagDefinition = tagDef,
                    Constraints = constraintInfo,
                    LogicReferences = logicRefs,
                    RelatedSignals = relatedSignals,
                    Summary = new
                    {
                        TotalConstraints = rules.Count,
                        ByType = rules.GroupBy(r => r.RuleType).ToDictionary(g => g.Key, g => g.Count()),
                        LogicLocations = logicRefs.Count
                    },
                    Hint = rules.Count > 0
                        ? $"信号 {signalName} 有 {rules.Count} 条约束关系，关联 {relatedSignals.Count} 个相关信号。"
                        : $"信号 {signalName} 暂无约束规则。可使用 GetIOTagsForAnalysisAsync + SaveIOConstraintsAsync 生成。"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Diagnosis] GetIORelationship 异常");
                return new { Success = false, Message = $"查询信号关系失败: {ex.Message}" };
            }
        }

        #endregion

        #region ========== IO约束辅助方法 ==========

        /// <summary>在信号值字典中模糊查找信号</summary>
        private static bool? FindSignalValue(Dictionary<string, bool> values, string signalName)
        {
            // 精确匹配
            if (values.TryGetValue(signalName, out var exactVal))
                return exactVal;

            // 模糊匹配（信号名包含在 key 中，或 key 包含在信号名中）
            var match = values.FirstOrDefault(kv =>
                kv.Key.Contains(signalName, StringComparison.OrdinalIgnoreCase) ||
                signalName.Contains(kv.Key, StringComparison.OrdinalIgnoreCase));

            return match.Key != null ? match.Value : null;
        }

        /// <summary>按约束运算符评估是否违规</summary>
        private static LogicExpressionEvalResult EvaluateLogicAst(LogicExpressionAstNode node, Dictionary<string, bool> values)
        {
            switch (node.Kind)
            {
                case "True":
                    return new LogicExpressionEvalResult(true);
                case "False":
                    return new LogicExpressionEvalResult(false);
                case "Signal":
                    var signalName = node.SignalName ?? string.Empty;
                    var signalValue = !string.IsNullOrWhiteSpace(signalName) && FindSignalValue(values, signalName) == true;
                    return signalValue
                        ? new LogicExpressionEvalResult(true)
                        : new LogicExpressionEvalResult(false, signalName);
                case "Not":
                    var child = node.Children.FirstOrDefault();
                    if (child == null)
                    {
                        return new LogicExpressionEvalResult(false);
                    }

                    if (child.Kind == "Signal" && !string.IsNullOrWhiteSpace(child.SignalName))
                    {
                        var actual = FindSignalValue(values, child.SignalName);
                        return actual == false
                            ? new LogicExpressionEvalResult(true)
                            : new LogicExpressionEvalResult(false, child.SignalName, false);
                    }

                    var childEval = EvaluateLogicAst(child, values);
                    return new LogicExpressionEvalResult(!childEval.Value, childEval.BlockingSignals, childEval.ExpectedBySignal);
                case "Or":
                    var orResults = node.Children.Select(x => EvaluateLogicAst(x, values)).ToList();
                    if (orResults.Any(x => x.Value))
                    {
                        return new LogicExpressionEvalResult(true);
                    }

                    return new LogicExpressionEvalResult(
                        false,
                        orResults.SelectMany(x => x.BlockingSignals).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                        orResults.SelectMany(x => x.ExpectedBySignal)
                            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase));
                case "And":
                    var andResults = node.Children.Select(x => EvaluateLogicAst(x, values)).ToList();
                    return andResults.All(x => x.Value)
                        ? new LogicExpressionEvalResult(true)
                        : new LogicExpressionEvalResult(
                            false,
                            andResults.Where(x => !x.Value).SelectMany(x => x.BlockingSignals).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                            andResults.Where(x => !x.Value).SelectMany(x => x.ExpectedBySignal)
                                .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                                .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase));
                default:
                    return new LogicExpressionEvalResult(false);
            }
        }

        private static bool EvaluateConstraint(
            string op,
            List<SignalEvalItem> items,
            string? validStatesJson = null,
            ConstraintEvaluationContext? context = null)
        {
            return op switch
            {
                "AT_MOST_ONE" => items.Where(i => i.Role == "Member" && i.Value == true).Count() > 1,
                "EXACTLY_ONE" => items.Where(i => i.Role == "Member" && i.Value.HasValue).Any()
                    && items.Where(i => i.Role == "Member" && i.Value == true).Count() != 1,
                "ALL_TRUE" => EvaluateAllTrue(items, context),
                "IMPLIES" => EvaluateImplies(items, context),
                "REQUIRES_0" => items.Any(i => i.Role == "Source" && i.Value == false)
                    && items.Any(i => i.Role == "Target" && i.Value == true),
                "CHAIN_ORDER" => EvaluateChainOrder(items),
                "CUSTOM" => EvaluateCustom(items, validStatesJson),
                _ => false
            };
        }

        private static bool EvaluateAllTrue(List<SignalEvalItem> items, ConstraintEvaluationContext? context = null)
        {
            var sourceItems = items.Where(i => i.Role == "Source").ToList();
            if (sourceItems.Any() && !sourceItems.Any(i => i.Value == true))
            {
                return false;
            }

            var memberItems = items.Where(i => i.Role == "Member" && i.Value.HasValue).ToList();
            if (memberItems.Any(i => i.Value == false))
            {
                if (context?.SuppressViolationByMutexFallback == true)
                {
                    return false;
                }

                return true;
            }

            var hasViolation = false;

            var targetItems = items.Where(i => i.Role == "Target" && i.Value.HasValue).ToList();
            if (targetItems.Any(i => i.Value == false))
            {
                hasViolation = true;
            }

            if (hasViolation && context?.SuppressViolationByMutexFallback == true)
            {
                return false;
            }

            return hasViolation;
        }

        private static bool EvaluateImplies(List<SignalEvalItem> items, ConstraintEvaluationContext? context = null)
        {
            var hasViolation = items.Any(i => i.Role == "Source" && i.Value == true)
                && !items.Any(i => i.Role == "Target" && i.Value == true);

            if (hasViolation && context?.SuppressViolationByMutexFallback == true)
            {
                return false;
            }

            return hasViolation;
        }

        /// <summary>
        /// 评估 CUSTOM 运算符：当前信号值组合必须匹配 ValidStates 中定义的任一合法组合
        /// ValidStates JSON 格式：[{"SignalA":true,"SignalB":false},{"SignalA":false,"SignalB":true}]
        /// </summary>
        private static bool EvaluateCustom(List<SignalEvalItem> items, string? validStatesJson)
        {
            if (string.IsNullOrWhiteSpace(validStatesJson)) return false;

            try
            {
                var validStates = JsonSerializer.Deserialize<List<Dictionary<string, bool>>>(validStatesJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (validStates == null || validStates.Count == 0) return false;

                // 构建当前实际状态（仅包含有值的信号）
                var actualValues = items
                    .Where(i => i.Value.HasValue)
                    .ToDictionary(i => i.SignalName, i => i.Value!.Value);

                if (actualValues.Count == 0) return false;

                // 检查是否匹配任意一个合法组合
                foreach (var validCombo in validStates)
                {
                    bool allMatch = true;
                    foreach (var kv in validCombo)
                    {
                        if (actualValues.TryGetValue(kv.Key, out var actual))
                        {
                            if (actual != kv.Value) { allMatch = false; break; }
                        }
                        // 如果该信号不在 actualValues 中，跳过（部分匹配）
                    }
                    if (allMatch) return false; // 匹配到合法组合，无违规
                }

                return true; // 没有匹配任何合法组合 → 违规
            }
            catch
            {
                return false; // JSON 解析失败时不报违规
            }
        }

        /// <summary>评估时序链约束：后序信号为 true 时，前序信号也必须为 true</summary>
        private static bool EvaluateChainOrder(List<SignalEvalItem> items)
        {
            var ordered = items.Where(i => i.Value.HasValue).OrderBy(i => i.SortOrder).ToList();
            for (int i = 1; i < ordered.Count; i++)
            {
                if (ordered[i].Value == true && ordered[i - 1].Value == false)
                    return true; // 违规：后序为 true 但前序为 false
            }
            return false;
        }

        /// <summary>信号评估项</summary>
        private class SignalEvalItem
        {
            public string SignalName { get; set; } = "";
            public string Role { get; set; } = "Member";
            public bool? Value { get; set; }
            public int SortOrder { get; set; }
        }

        private sealed class ConstraintEvaluationContext
        {
            public string Mode { get; set; } = "normal";
            public bool SuppressViolationByMutexFallback { get; set; }
            public List<string> RelatedMutexSignals { get; set; } = new();
        }

        private sealed class MutexGroupState
        {
            public long RuleId { get; init; }
            public List<string> MemberSignals { get; init; } = new();
            public List<string> TrueSignals { get; init; } = new();
        }

        private sealed class SignalValidationSnapshot
        {
            public PLC_IOConstraintSignalEntity Signal { get; set; } = default!;
            public bool? Value { get; set; }
        }

        private static string ResolveRuleStationCode(PLC_IOConstraintRuleEntity rule, List<PLC_IOConstraintSignalEntity> signals)
        {
            var candidates = new[]
            {
                rule.EquipmentId,
                rule.RuleName,
                rule.Description
            }
            .Concat(signals.Select(s => s.SignalName));

            foreach (var candidate in candidates)
            {
                var station = ExtractStationCode(candidate);
                if (!string.IsNullOrWhiteSpace(station))
                {
                    return station;
                }
            }

            return "UNKNOWN";
        }

        private static string ResolveRuleDeviceLabel(PLC_IOConstraintRuleEntity rule, List<PLC_IOConstraintSignalEntity> signals)
        {
            foreach (var signal in signals.Select(s => s.SignalName))
            {
                var match = Regex.Match(signal ?? string.Empty, @"OP\d{3}_(CYL|YV)\d+(?:_\d+)?", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    return match.Value.ToUpperInvariant();
                }
            }

            foreach (var signal in signals.Select(s => s.SignalName))
            {
                var match = Regex.Match(signal ?? string.Empty, @"Valve\[\d+\]", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    return match.Value;
                }
            }

            return !string.IsNullOrWhiteSpace(rule.EquipmentId)
                ? rule.EquipmentId
                : "UNKNOWN_DEVICE";
        }

        private static List<string> BuildCurrentStateSummary(List<SignalValidationSnapshot> resolvedValues)
        {
            return resolvedValues
                .Select(sv => $"{sv.Signal.SignalName}={FormatBool(sv.Value)}")
                .ToList();
        }

        private static List<string> BuildExpectedStateSummary(
            PLC_IOConstraintRuleEntity rule,
            List<SignalEvalItem> items,
            ConstraintEvaluationContext? context = null)
        {
            return GetExpectedSignalStates(rule.Operator, items, context)
                .Select(kv => $"{kv.Key} 应为 {kv.Value}")
                .ToList();
        }

        private static string BuildViolationReason(
            PLC_IOConstraintRuleEntity rule,
            List<SignalEvalItem> items,
            ConstraintEvaluationContext? context = null)
        {
            if (string.Equals(context?.Mode, "mutex_fallback_all_true", StringComparison.OrdinalIgnoreCase))
            {
                var groupedSignals = items
                    .Where(i => i.Role == "Member" || i.Role == "Target")
                    .Select(i => i.SignalName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return groupedSignals.Count > 0
                    ? $"信号组 [{string.Join(", ", groupedSignals)}] 未全部满足 alltrue 条件，且互斥侧未能提供可放行的有效链路"
                    : "当前信号组未满足 alltrue 条件";
            }

            if (string.Equals(context?.Mode, "mutex_fallback_implies", StringComparison.OrdinalIgnoreCase))
            {
                var targetSignals = items
                    .Where(i => i.Role == "Target")
                    .Select(i => i.SignalName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return targetSignals.Count > 0
                    ? $"目标链路 [{string.Join(", ", targetSignals)}] 未走通，且互斥侧也未出现可确认的有效链路"
                    : "目标链路未走通，且互斥侧也未出现可确认的有效链路";
            }

            var expectedStates = GetExpectedSignalStates(rule.Operator, items, context);
            if (expectedStates.Count == 0)
            {
                return string.IsNullOrWhiteSpace(rule.Description)
                    ? "当前信号组合不满足约束要求"
                    : rule.Description;
            }

            return string.Join("，", expectedStates.Select(kv =>
            {
                var actual = items.FirstOrDefault(x => string.Equals(x.SignalName, kv.Key, StringComparison.OrdinalIgnoreCase))?.Value;
                return $"{kv.Key} 当前为 {FormatBool(actual)}，应为 {kv.Value}";
            }));
        }

        private static Dictionary<string, string> GetExpectedSignalStates(
            string op,
            List<SignalEvalItem> items,
            ConstraintEvaluationContext? context = null)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            switch (op)
            {
                case "IMPLIES":
                    if (items.Any(i => i.Role == "Source" && i.Value == true))
                    {
                        foreach (var target in items.Where(i => i.Role == "Target" && i.Value != true))
                        {
                            result[target.SignalName] = "true";
                        }
                    }
                    break;
                case "ALL_TRUE":
                    var sourceTriggered = !items.Any(i => i.Role == "Source") || items.Any(i => i.Role == "Source" && i.Value == true);
                    if (sourceTriggered)
                    {
                        var groupedItems = items.Where(i => (i.Role == "Member" || i.Role == "Target") && i.Value != true);
                        foreach (var item in groupedItems)
                        {
                            result[item.SignalName] = "true";
                        }
                    }
                    break;
                case "AT_MOST_ONE":
                    foreach (var item in items.Where(i => i.Role == "Member" && i.Value == true))
                    {
                        result[item.SignalName] = "false（同组最多只能有一个为 true）";
                    }
                    break;
                case "REQUIRES_0":
                    if (items.Any(i => i.Role == "Source" && i.Value == false))
                    {
                        foreach (var target in items.Where(i => i.Role == "Target" && i.Value == true))
                        {
                            result[target.SignalName] = "false";
                        }
                    }
                    break;
            }

            return result;
        }

        private static string FormatBool(bool? value)
        {
            return value.HasValue ? value.Value.ToString().ToLowerInvariant() : "n/a";
        }

        private static ConstraintEvaluationContext BuildConstraintEvaluationContext(
            PLC_IOConstraintRuleEntity rule,
            List<SignalEvalItem> items,
            List<MutexGroupState> mutexStates)
        {
            var context = new ConstraintEvaluationContext();
            if (!HasIntermediateUnresolvedFallbackMarker(rule))
            {
                return context;
            }

            var op = (rule.Operator ?? string.Empty).ToUpperInvariant();
            if (op != "IMPLIES" && op != "ALL_TRUE")
            {
                return context;
            }

            var relatedGroups = mutexStates
                .Where(group => group.MemberSignals.Any(member =>
                    items.Any(item => string.Equals(item.SignalName, member, StringComparison.OrdinalIgnoreCase))))
                .ToList();

            if (relatedGroups.Count == 0)
            {
                return context;
            }

            var currentTrueSignals = items
                .Where(i => i.Value == true)
                .Select(i => i.SignalName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var opposingTrueSignals = relatedGroups
                .SelectMany(group => group.TrueSignals)
                .Where(signalName => !currentTrueSignals.Contains(signalName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (opposingTrueSignals.Count == 0)
            {
                context.Mode = op == "ALL_TRUE" ? "mutex_fallback_all_true" : "mutex_fallback_implies";
                return context;
            }

            context.Mode = "mutex_counterpart_resolved";
            context.SuppressViolationByMutexFallback = true;
            context.RelatedMutexSignals = opposingTrueSignals;
            return context;
        }

        private static bool HasIntermediateUnresolvedFallbackMarker(PLC_IOConstraintRuleEntity rule)
        {
            return !string.IsNullOrWhiteSpace(rule.Source)
                   && rule.Source.Contains("AI_INTERMEDIATE_UNRESOLVED", StringComparison.OrdinalIgnoreCase);
        }

        private static List<MutexGroupState> BuildMutexGroupStates(
            List<PLC_IOConstraintRuleEntity> rules,
            Dictionary<long, List<PLC_IOConstraintSignalEntity>> signalsByRule,
            Dictionary<string, bool> values)
        {
            var states = new List<MutexGroupState>();
            foreach (var rule in rules.Where(r => string.Equals(r.Operator, "AT_MOST_ONE", StringComparison.OrdinalIgnoreCase)))
            {
                if (!signalsByRule.TryGetValue(rule.Id, out var ruleSignals) || ruleSignals.Count == 0)
                {
                    continue;
                }

                var memberSignals = ruleSignals
                    .Where(s => string.Equals(s.SignalRole, "Member", StringComparison.OrdinalIgnoreCase))
                    .Select(s => s.SignalName)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (memberSignals.Count < 2)
                {
                    continue;
                }

                var trueSignals = memberSignals
                    .Where(signalName => FindSignalValue(values, signalName) == true)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                states.Add(new MutexGroupState
                {
                    RuleId = rule.Id,
                    MemberSignals = memberSignals,
                    TrueSignals = trueSignals
                });
            }

            return states;
        }

        /// <summary>根据约束规则和信号列表生成期望行为描述</summary>
        private sealed class LogicExpressionAstNode
        {
            public string Kind { get; set; } = string.Empty;
            public string? SignalName { get; set; }
            public string? ConstantValue { get; set; }
            public List<LogicExpressionAstNode> Children { get; set; } = new();
        }

        private sealed class LogicExpressionEvalResult
        {
            public LogicExpressionEvalResult(bool value)
            {
                Value = value;
                BlockingSignals = new List<string>();
                ExpectedBySignal = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            }

            public LogicExpressionEvalResult(bool value, string? signalName, bool? expected = true)
                : this(value)
            {
                if (!string.IsNullOrWhiteSpace(signalName))
                {
                    BlockingSignals.Add(signalName);
                    if (expected.HasValue)
                    {
                        ExpectedBySignal[signalName] = expected.Value;
                    }
                }
            }

            public LogicExpressionEvalResult(bool value, List<string> blockingSignals, Dictionary<string, bool> expectedBySignal)
            {
                Value = value;
                BlockingSignals = blockingSignals;
                ExpectedBySignal = expectedBySignal;
            }

            public bool Value { get; }
            public List<string> BlockingSignals { get; }
            public Dictionary<string, bool> ExpectedBySignal { get; }
        }

        private static string GetExpectedBehavior(PLC_IOConstraintRuleEntity rule, List<PLC_IOConstraintSignalEntity> signals)
        {
            var signalNames = signals.Select(s => s.SignalName).ToList();
            var nameList = string.Join(", ", signalNames.Take(4));
            if (signalNames.Count > 4) nameList += $" 等{signalNames.Count}个";

            return rule.Operator switch
            {
                "AT_MOST_ONE" => $"信号组 [{nameList}] 中最多只能有 1 个为 true（物理互斥）",
                "EXACTLY_ONE" => $"信号组 [{nameList}] 中必须恰好有 1 个为 true（唯一选择）",
                "ALL_TRUE" => $"信号组 [{nameList}] 必须全部为 true",
                "IMPLIES" => $"当驱动信号为 true 时，反馈信号应在预期时间内变为 true",
                "REQUIRES_0" => $"当安全信号断开时，受控输出必须为 false",
                "CHAIN_ORDER" => $"信号组 [{nameList}] 必须按顺序依次为 true，不可跳序",
                "CUSTOM" => $"信号组 [{nameList}] 当前值组合必须匹配预定义的合法状态之一",
                _ => "未知约束类型"
            };
        }

        #endregion

        #region ========== IoT 接口预留定义 ==========

        // 后续对接 IoT 平台时：
        // 1. 在 HSJM.AI.IDbService 项目中创建 IIoTDataService 接口
        // 2. 在 HSJM.AI.DbService 或独立项目中实现
        // 3. 在本类构造函数中注入
        // 4. 取消上方各工具中 IoT 注释代码

        /*
        public interface IIoTDataService
        {
            /// <summary>读取单个变量实时值</summary>
            Task<object?> ReadVariableAsync(string variableName);

            /// <summary>批量读取多个变量实时值</summary>
            Task<Dictionary<string, object>> ReadMultipleAsync(IEnumerable<string> variableNames);

            /// <summary>S7 协议直读 DB 数据块</summary>
            Task<byte[]> ReadS7DBAsync(int dbNumber, int offsetByte, int sizeBits);

            /// <summary>获取当前活跃报警</summary>
            Task<List<AlarmInfo>> GetActiveAlarmsAsync(string stationId);

            /// <summary>获取变量跳变历史（时序数据）</summary>
            Task<List<VariableHistoryPoint>> GetVariableHistoryAsync(string variableName, int count);
        }

        public class AlarmInfo
        {
            public string AlarmId { get; set; }
            public string Message { get; set; }
            public DateTime Timestamp { get; set; }
            public string Severity { get; set; }
        }

        public class VariableHistoryPoint
        {
            public DateTime Time { get; set; }
            public object Value { get; set; }
            public string Duration { get; set; }
        }
        */

        #endregion
    }
}
