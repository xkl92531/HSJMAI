using HSJM.AI.AppLayer.Agent.Services.Plc_diagnosis;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Models;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Services;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.PLC
{
    /// <summary>
    /// IO 约束规则管理 App 层
    /// 
    /// 提供：
    /// 1. 约束规则分页查询（按项目、按设备、按类型）
    /// 2. 单信号约束分析（调用 IOConstraintPreprocessService 逐信号处理）
    /// 3. 删除约束规则
    /// </summary>
    public class PLC_IOConstraintApp : ISelfScopedAutoInject
    {
        private readonly ILogger<PLC_IOConstraintApp> _logger;
        private readonly IPLC_IOConstraintRuleService _ruleService;
        private readonly IPLC_IOConstraintSignalService _signalService;
        private readonly IPLC_IOTagService _ioTagService;
        private readonly IPLC_ProjectService _projectService;
        private readonly IOConstraintPreprocessService _preprocessService;
        private readonly IOConstraintProgressService _progressService;
        private readonly XmlConstraintExtractService _xmlConstraintExtractService;
        private readonly PlcDiagnosisService _plcDiagnosisService;
        private readonly StationShadowStateCacheService _stationShadowStateCacheService;
        private readonly StopCauseSignalSnapshotService _stopCauseSignalSnapshotService;
        private readonly IPrompt_TemplateService _promptTemplateService;

        public PLC_IOConstraintApp(
            ILogger<PLC_IOConstraintApp> logger,
            IPLC_IOConstraintRuleService ruleService,
            IPLC_IOConstraintSignalService signalService,
            IPLC_IOTagService ioTagService,
            IPLC_ProjectService projectService,
            IOConstraintPreprocessService preprocessService,
            IOConstraintProgressService progressService,
            XmlConstraintExtractService xmlConstraintExtractService,
            PlcDiagnosisService plcDiagnosisService,
            StationShadowStateCacheService stationShadowStateCacheService,
            StopCauseSignalSnapshotService stopCauseSignalSnapshotService,
            IPrompt_TemplateService promptTemplateService)
        {
            _logger = logger;
            _ruleService = ruleService;
            _signalService = signalService;
            _ioTagService = ioTagService;
            _projectService = projectService;
            _preprocessService = preprocessService;
            _progressService = progressService;
            _xmlConstraintExtractService = xmlConstraintExtractService;
            _plcDiagnosisService = plcDiagnosisService;
            _stationShadowStateCacheService = stationShadowStateCacheService;
            _stopCauseSignalSnapshotService = stopCauseSignalSnapshotService;
            _promptTemplateService = promptTemplateService;
        }

        /// <summary>
        /// 分页查询约束规则（含信号明细）
        /// </summary>
        public async Task<QueryPagedResponseModel<object>> GetConstraintRulesPagedAsync(
            long projectId, int page = 1, int limit = 20,
            string ruleType = null, string equipmentId = null)
        {
            // 1. 构建查询条件
            var rules = await _ruleService.GetListAsync(x =>
                x.ProjectId == projectId && !x.IsDeleted
                && (string.IsNullOrEmpty(ruleType) || x.RuleType == ruleType)
                && (string.IsNullOrEmpty(equipmentId) || x.EquipmentId == equipmentId));

            var total = rules.Count;

            // 2. 分页
            var pagedRules = rules
                .OrderByDescending(r => r.CreateTime)
                .Skip((page - 1) * limit)
                .Take(limit)
                .ToList();

            // 3. 批量加载信号明细
            var ruleIds = pagedRules.Select(r => r.Id).ToList();
            var allSignals = ruleIds.Count > 0
                ? await _signalService.GetListAsync(x => ruleIds.Contains(x.RuleId) && !x.IsDeleted)
                : new List<PLC_IOConstraintSignalEntity>();
            var signalsByRule = allSignals.GroupBy(s => s.RuleId)
                .ToDictionary(g => g.Key, g => g.OrderBy(s => s.SortOrder ?? 0).ToList());

            // 4. 组装结果
            var data = pagedRules.Select(r =>
            {
                signalsByRule.TryGetValue(r.Id, out var signals);
                return new
                {
                    r.Id,
                    r.ProjectId,
                    r.RuleName,
                    r.RuleType,
                    r.Operator,
                    r.EquipmentId,
                    r.Confidence,
                    r.Source,
                    r.Description,
                    r.Priority,
                    r.ValidStates,
                    r.CreateTime,
                    Signals = (signals ?? new List<PLC_IOConstraintSignalEntity>()).Select(s => new
                    {
                        s.Id,
                        s.SignalName,
                        s.SignalAddress,
                        s.SignalRole,
                        s.SortOrder
                    }).ToList()
                } as object;
            }).ToList();

            return new QueryPagedResponseModel<object>
            {
                Total = total,
                Data = data
            };
        }

        /// <summary>
        /// 获取项目约束规则统计摘要
        /// </summary>
        public async Task<object> GetConstraintSummaryAsync(long projectId)
        {
            var rules = await _ruleService.GetListAsync(x =>
                x.ProjectId == projectId && !x.IsDeleted);

            return new
            {
                TotalRules = rules.Count,
                ByType = rules.GroupBy(r => r.RuleType ?? "Unknown")
                    .ToDictionary(g => g.Key, g => g.Count()),
                ByPriority = rules.GroupBy(r => r.Priority ?? "Info")
                    .ToDictionary(g => g.Key, g => g.Count()),
                BySource = rules.GroupBy(r => r.Source ?? "Unknown")
                    .ToDictionary(g => g.Key, g => g.Count())
            };
        }

        public async Task<HttpResponseResultModel<object>> ValidateConstraintsAutoAsync(
            long projectId,
            string stationCode = null,
            bool forceLiveRead = false)
        {
            var result = new HttpResponseResultModel<object>();

            try
            {
                var stations = await ResolveAutoValidationStationsAsync(projectId, stationCode);
                if (stations.Count == 0)
                {
                    result.ErrorMessage = "未找到可用于自动校验的工位。请先确认项目 StationId 或 shadow 监控缓存已建立。";
                    return result;
                }

                var projectRules = await _ruleService.GetListAsync(x => x.ProjectId == projectId && !x.IsDeleted);
                var projectRuleIds = projectRules.Select(x => x.Id).Distinct().ToList();
                var allConstraintSignals = projectRuleIds.Count == 0
                    ? new List<PLC_IOConstraintSignalEntity>()
                    : await _signalService.GetListAsync(x => projectRuleIds.Contains(x.RuleId) && !x.IsDeleted);
                var projectConstraintSignals = allConstraintSignals
                    .Where(x => !string.IsNullOrWhiteSpace(x.SignalName))
                    .Select(x => x.SignalName!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var stationResults = new List<object>();
                var totalViolations = 0;
                var failedStations = new List<string>();

                foreach (var station in stations)
                {
                    var resolvedValues = await LoadAutoValidationValuesAsync(projectId, station, projectConstraintSignals, forceLiveRead);
                    if (resolvedValues.Values.Count == 0)
                    {
                        failedStations.Add(station);
                        stationResults.Add(new
                        {
                            StationCode = station,
                            resolvedValues.DataSource,
                            resolvedValues.SignalCount,
                            Success = false,
                            Message = "未获取到可用于约束校验的布尔信号值"
                        });
                        continue;
                    }

                    var signalValuesJson = JsonSerializer.Serialize(resolvedValues.Values);
                    var validation = await _plcDiagnosisService.ValidateIOSignalsAsync(projectId, signalValuesJson, station);
                    var summary = ExtractValidationSummary(validation);
                    totalViolations += summary.ViolationCount;

                    stationResults.Add(new
                    {
                        StationCode = station,
                        resolvedValues.DataSource,
                        resolvedValues.SignalCount,
                        summary.TotalConstraints,
                        summary.HealthScore,
                        summary.ViolationCount,
                        summary.ValidationResult,
                        Validation = validation
                    });
                }

                result.IsSuccess = true;
                result.BackResult = new
                {
                    ProjectId = projectId,
                    RequestedStationCode = stationCode,
                    AutoMode = true,
                    ForceLiveRead = forceLiveRead,
                    StationCount = stationResults.Count,
                    FailedStations = failedStations,
                    TotalViolationCount = totalViolations,
                    HasViolation = totalViolations > 0,
                    Stations = stationResults
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[IOConstraint] 自动校验约束失败: ProjectId={ProjectId}, StationCode={StationCode}, ForceLiveRead={ForceLiveRead}",
                    projectId,
                    stationCode,
                    forceLiveRead);
                result.ErrorMessage = $"自动校验失败: {ex.Message}";
            }

            return result;
        }

        public async Task<object> GetPreprocessProgressAsync(long projectId)
        {
            var progress = _progressService.GetProgress(projectId);
            var boolTags = await _ioTagService.GetListAsync(x =>
                x.ProjectId == projectId && !x.IsDeleted && x.DataType == "Bool");
            var rules = await _ruleService.GetListAsync(x =>
                x.ProjectId == projectId && !x.IsDeleted);

            var totalSignals = boolTags.Count;
            var completedSignals = boolTags.Count(x => x.ConstraintStatus == 2);
            var failedSignals = boolTags.Count(x => x.ConstraintStatus == 3);
            var processingSignals = boolTags.Count(x => x.ConstraintStatus == 1);
            var pendingSignals = boolTags.Count(x => x.ConstraintStatus == null || x.ConstraintStatus == 0);

            var runtimeProcessed = progress?.ProcessedSignals ?? (completedSignals + failedSignals + processingSignals);
            var runtimePercent = progress?.Percentage
                ?? (totalSignals > 0 ? (int)Math.Min(100, Math.Round(runtimeProcessed * 100.0 / totalSignals)) : 0);

            return new
            {
                ProjectId = projectId,
                Runtime = progress == null ? null : new
                {
                    Status = progress.Status.ToString(),
                    progress.TotalSignals,
                    progress.ProcessedSignals,
                    progress.GeneratedConstraints,
                    progress.FailedSignals,
                    progress.NoContextSignals,
                    progress.ZeroInsertSignals,
                    progress.Percentage,
                    progress.CurrentSignal,
                    progress.Message,
                    progress.ErrorMessage,
                    progress.StartTime,
                    progress.EndTime
                },
                Database = new
                {
                    TotalSignals = totalSignals,
                    CompletedSignals = completedSignals,
                    FailedSignals = failedSignals,
                    ProcessingSignals = processingSignals,
                    PendingSignals = pendingSignals,
                    GeneratedRules = rules.Count
                },
                Display = new
                {
                    Status = progress?.Status.ToString() ?? (processingSignals > 0 ? "Running" : "Idle"),
                    Percentage = runtimePercent,
                    CurrentSignal = progress?.CurrentSignal,
                    Message = progress?.Message ?? $"待处理 {pendingSignals}，处理中 {processingSignals}，完成 {completedSignals}，失败 {failedSignals}",
                    GeneratedRules = progress?.GeneratedConstraints > 0 ? progress.GeneratedConstraints : rules.Count
                }
            };
        }

        /// <summary>
        /// 单信号约束分析（同步执行，返回分析结果）
        /// 供前端"规则分析"按钮调用
        /// </summary>
        public async Task<HttpResponseResultModel<object>> AnalyzeSingleSignalAsync(long ioTagId)
        {
            var result = new HttpResponseResultModel<object>();

            try
            {
                // 1. 查找 IOTag
                var ioTag = await _ioTagService.GetByIdAsync(ioTagId);
                if (ioTag == null)
                {
                    result.ErrorMessage = $"未找到 ID 为 {ioTagId} 的 IOTag 记录";
                    return result;
                }

                if (!ioTag.ProjectId.HasValue)
                {
                    result.ErrorMessage = "该 IOTag 未关联项目";
                    return result;
                }

                // 2. 调用预处理服务处理单个信号
                await _preprocessService.ProcessSingleSignalExternalAsync(ioTag.ProjectId.Value, ioTag);

                // 3. 查询该信号关联的约束规则
                var relatedSignals = await _signalService.GetListAsync(x =>
                    x.SignalName == ioTag.TagName && !x.IsDeleted);

                var ruleIds = relatedSignals.Select(s => s.RuleId).Distinct().ToList();
                var rules = ruleIds.Count > 0
                    ? (await _ruleService.GetListByIdsAsync(ruleIds)).Where(r => !r.IsDeleted).ToList()
                    : new List<PLC_IOConstraintRuleEntity>();

                // 4. 加载所有关联规则的完整信号
                var allRuleSignals = ruleIds.Count > 0
                    ? await _signalService.GetListAsync(x => ruleIds.Contains(x.RuleId) && !x.IsDeleted)
                    : new List<PLC_IOConstraintSignalEntity>();

                var constraintResults = rules.Select(r =>
                {
                    var signals = allRuleSignals.Where(s => s.RuleId == r.Id)
                        .OrderBy(s => s.SortOrder ?? 0).ToList();
                    return new
                    {
                        r.Id,
                        r.RuleName,
                        r.RuleType,
                        r.Operator,
                        r.EquipmentId,
                        r.Confidence,
                        r.Description,
                        r.Priority,
                        r.CreateTime,
                        Signals = signals.Select(s => new
                        {
                            s.SignalName,
                            s.SignalAddress,
                            s.SignalRole,
                            s.SortOrder
                        }).ToList()
                    };
                }).ToList();

                // 5. 重新读取 IOTag 状态
                var updatedTag = await _ioTagService.GetByIdAsync(ioTagId);

                result.IsSuccess = true;
                result.BackResult = new
                {
                    Signal = new
                    {
                        ioTag.TagName,
                        ioTag.Address,
                        ioTag.Comment,
                        ioTag.EquipmentId,
                        ConstraintStatus = updatedTag?.ConstraintStatus,
                        ConstraintAnalyzedAt = updatedTag?.ConstraintAnalyzedAt
                    },
                    ConstraintRules = constraintResults,
                    TotalRules = constraintResults.Count
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[IOConstraint] 单信号分析失败: ioTagId={Id}", ioTagId);
                result.ErrorMessage = $"约束分析失败: {ex.Message}";
            }

            return result;
        }

        /// <summary>
        /// 删除约束规则（含从表信号）
        /// </summary>
        public async Task<HttpResponseResultModel<bool>> DeleteRuleAsync(long ruleId)
        {
            var result = new HttpResponseResultModel<bool>();

            try
            {
                // 软删除从表信号
                await _signalService.SoftDeleteAsync(x => x.RuleId == ruleId);
                // 软删除主表规则
                await _ruleService.SoftDeleteByIdAsync(ruleId);

                result.IsSuccess = true;
                result.BackResult = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[IOConstraint] 删除规则失败: ruleId={Id}", ruleId);
                result.ErrorMessage = $"删除失败: {ex.Message}";
            }

            return result;
        }

        #region 约束生成系统提示词配置

        /// <summary>获取约束生成系统提示词（含默认值与是否已自定义标记）。</summary>
        public async Task<object> GetConstraintPromptAsync()
        {
            var def = IOConstraintPreprocessService.DefaultLogicUnitConstraintSystemPrompt();
            string dbContent = null;
            try
            {
                var rows = await _promptTemplateService.GetListAsync(
                    t => t.PromptKey == ConstraintPromptKeys.Phase2System && t.IsDeleted == false);
                dbContent = rows.Count > 0 ? rows[0].Content : null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[IOConstraint] 读取约束提示词失败");
            }

            var isCustomized = !string.IsNullOrWhiteSpace(dbContent);
            return new
            {
                Content = isCustomized ? dbContent : def,
                IsCustomized = isCustomized,
                DefaultContent = def
            };
        }

        /// <summary>保存（upsert）约束生成系统提示词。</summary>
        public async Task<HttpResponseResultModel<bool>> SaveConstraintPromptAsync(string content)
        {
            var result = new HttpResponseResultModel<bool>();
            if (string.IsNullOrWhiteSpace(content))
            {
                result.ErrorMessage = "提示词内容不能为空";
                return result;
            }

            try
            {
                var rows = await _promptTemplateService.GetListAsync(
                    t => t.PromptKey == ConstraintPromptKeys.Phase2System && t.IsDeleted == false);
                var row = rows.Count > 0 ? rows[0] : null;
                if (row == null)
                {
                    row = new Prompt_TemplateEntity
                    {
                        PromptKey = ConstraintPromptKeys.Phase2System,
                        Scenario = ConstraintPromptKeys.Scenario,
                        Status = 1,
                        Content = content
                    };
                    result.IsSuccess = await _promptTemplateService.InsertAsync(row);
                }
                else
                {
                    row.Content = content;
                    row.Status = 1;
                    result.IsSuccess = await _promptTemplateService.UpdateAsync(row);
                }
                result.BackResult = result.IsSuccess;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[IOConstraint] 保存约束提示词失败");
                result.ErrorMessage = $"保存失败: {ex.Message}";
            }
            return result;
        }

        /// <summary>恢复约束生成系统提示词为默认（软删自定义行）。</summary>
        public async Task<HttpResponseResultModel<bool>> ResetConstraintPromptAsync()
        {
            var result = new HttpResponseResultModel<bool>();
            try
            {
                var rows = await _promptTemplateService.GetListAsync(
                    t => t.PromptKey == ConstraintPromptKeys.Phase2System && t.IsDeleted == false);
                foreach (var row in rows)
                {
                    await _promptTemplateService.SoftDeleteByIdAsync(row.Id);
                }
                result.IsSuccess = true;
                result.BackResult = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[IOConstraint] 恢复默认约束提示词失败");
                result.ErrorMessage = $"操作失败: {ex.Message}";
            }
            return result;
        }

        #endregion

        #region 约束手动增删改（Source=Manual，不经 AI 硬过滤）

        /// <summary>手动新增约束规则（含信号）。</summary>
        public async Task<HttpResponseResultModel<long>> InsertConstraintRuleAsync(ConstraintRuleSaveDto dto)
        {
            var result = new HttpResponseResultModel<long>();
            var err = ValidateRuleDto(dto, out var signals);
            if (err != null) { result.ErrorMessage = err; return result; }

            try
            {
                var rule = new PLC_IOConstraintRuleEntity
                {
                    ProjectId = dto.ProjectId,
                    RuleName = dto.RuleName.Trim(),
                    RuleType = dto.RuleType.Trim(),
                    Operator = dto.Operator.Trim(),
                    EquipmentId = string.IsNullOrWhiteSpace(dto.EquipmentId) ? null : dto.EquipmentId.Trim(),
                    Priority = dto.Priority,
                    Confidence = dto.Confidence,
                    Description = dto.Description,
                    ValidStates = dto.ValidStates,
                    Source = "Manual",
                    SourceLuId = null
                };
                // P2（2026-10-09 检查清单 #6）：主表与信号子表放同一事务——原先分开写，
                // 子表插入中途失败会留下「主表已保存 + 部分信号」的不完整规则。
                using (var tran = _ruleService.BeginTran())
                {
                    await _ruleService.InsertAsync(rule);
                    await InsertSignalsAsync(rule.Id, signals);
                    tran.Complete();
                }

                result.IsSuccess = true;
                result.BackResult = rule.Id;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[IOConstraint] 手动新增约束失败");
                result.ErrorMessage = $"新增失败: {ex.Message}";
            }
            return result;
        }

        /// <summary>编辑约束规则（替换信号子表）。手动编辑后 Source 升级为 Manual，受 ResetRule 保护。</summary>
        public async Task<HttpResponseResultModel<bool>> UpdateConstraintRuleAsync(long id, ConstraintRuleSaveDto dto)
        {
            var result = new HttpResponseResultModel<bool>();
            if (id <= 0) { result.ErrorMessage = "无效的规则ID"; return result; }
            var err = ValidateRuleDto(dto, out var signals);
            if (err != null) { result.ErrorMessage = err; return result; }

            try
            {
                var rule = await _ruleService.GetByIdAsync(id);
                if (rule == null || rule.IsDeleted)
                {
                    result.ErrorMessage = "约束规则不存在或已删除";
                    return result;
                }

                rule.RuleName = dto.RuleName.Trim();
                rule.RuleType = dto.RuleType.Trim();
                rule.Operator = dto.Operator.Trim();
                rule.EquipmentId = string.IsNullOrWhiteSpace(dto.EquipmentId) ? null : dto.EquipmentId.Trim();
                rule.Priority = dto.Priority;
                rule.Confidence = dto.Confidence;
                rule.Description = dto.Description;
                rule.ValidStates = dto.ValidStates;
                rule.Source = "Manual";

                // P2（2026-10-09 检查清单 #6）：主表保存 + 旧信号软删 + 新信号插入放同一事务——
                // 原先依次执行，第二条信号插入失败时接口报错但旧信号已删、主表与第一条新信号已保存，
                // 编辑前的完整规则被破坏；并发诊断也可能看到中间状态。失败时整体回滚，保留编辑前状态。
                using (var tran = _ruleService.BeginTran())
                {
                    await _ruleService.UpdateAsync(rule);

                    // 替换信号子表：软删旧 + 插新
                    await _signalService.SoftDeleteAsync(s => s.RuleId == id);
                    await InsertSignalsAsync(id, signals);

                    tran.Complete();
                }

                result.IsSuccess = true;
                result.BackResult = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[IOConstraint] 编辑约束失败: ruleId={Id}", id);
                result.ErrorMessage = $"编辑失败: {ex.Message}";
            }
            return result;
        }

        /// <summary>校验规则 DTO，返回错误信息（null=通过），并输出过滤后的有效信号。</summary>
        private static string ValidateRuleDto(ConstraintRuleSaveDto dto, out List<ConstraintSignalDto> signals)
        {
            signals = new List<ConstraintSignalDto>();
            if (dto == null) return "参数为空";
            if (dto.ProjectId <= 0) return "无效的项目ID";
            if (string.IsNullOrWhiteSpace(dto.RuleName)) return "规则名称不能为空";
            if (string.IsNullOrWhiteSpace(dto.RuleType)) return "约束类型不能为空";
            if (string.IsNullOrWhiteSpace(dto.Operator)) return "运算符不能为空";
            signals = (dto.Signals ?? new List<ConstraintSignalDto>())
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.SignalName))
                .ToList();
            if (signals.Count < 1) return "至少需要一个有效信号";
            return null;
        }

        /// <summary>按顺序插入信号子表。</summary>
        private async Task InsertSignalsAsync(long ruleId, List<ConstraintSignalDto> signals)
        {
            for (var i = 0; i < signals.Count; i++)
            {
                var s = signals[i];
                await _signalService.InsertAsync(new PLC_IOConstraintSignalEntity
                {
                    RuleId = ruleId,
                    SignalName = s.SignalName.Trim(),
                    SignalAddress = string.IsNullOrWhiteSpace(s.SignalAddress) ? null : s.SignalAddress.Trim(),
                    SignalRole = string.IsNullOrWhiteSpace(s.SignalRole) ? "Member" : s.SignalRole.Trim(),
                    SortOrder = s.SortOrder ?? i
                });
            }
        }

        #endregion

        #region XML 约束提取（不依赖 AI）

        /// <summary>
        /// 解析程序块代码，生成简洁表达式保存到 Compact_logic_xml
        /// </summary>
        /// <param name="projectId">项目ID</param>
        /// <returns>解析结果</returns>
        public async Task<HttpResponseResultModel<object>> ExtractConstraintsFromXmlAsync(long projectId)
        {
            var result = new HttpResponseResultModel<object>();

            try
            {
                _logger.LogInformation("[IOConstraint] 解析程序块代码: ProjectId={ProjectId}", projectId);

                // 调用 XmlConstraintExtractService 解析并保存到 Compact_logic_xml
                var saveResult = await _xmlConstraintExtractService.ConvertAndSaveCompactExpressionAsync(projectId);

                result.IsSuccess = true;
                result.BackResult = saveResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[IOConstraint] 解析程序块代码失败: ProjectId={ProjectId}", projectId);
                result.ErrorMessage = $"解析程序块代码失败: {ex.Message}";
            }

            return result;
        }

        /// <summary>
        /// 将约束规则转换为 C# 表达式（用于校验）
        /// </summary>
        /// <param name="projectId">项目ID</param>
        /// <param name="signalName">信号名称（可选）</param>
        /// <returns>C# 表达式列表</returns>
        public async Task<HttpResponseResultModel<string>> GetConstraintExpressionsAsync(long projectId, string signalName = null)
        {
            var result = new HttpResponseResultModel<string>();

            try
            {
                var expressions = await _xmlConstraintExtractService.ConvertConstraintsToExpressionsAsync(projectId, signalName);

                result.IsSuccess = true;
                result.BackResult = expressions;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[IOConstraint] 转换约束为C#表达式失败");
                result.ErrorMessage = $"转换失败: {ex.Message}";
            }

            return result;
        }

        public async Task<HttpResponseResultModel<object>> ExtractLogicExpressionsAsync(long projectId)
        {
            var result = new HttpResponseResultModel<object>();

            try
            {
                var saveResult = await _xmlConstraintExtractService.ExtractAndSaveLogicExpressionsAsync(projectId);
                result.IsSuccess = true;
                result.BackResult = saveResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[IOConstraint] 生成逻辑表达式表失败: ProjectId={ProjectId}", projectId);
                result.ErrorMessage = $"生成逻辑表达式表失败: {ex.Message}";
            }

            return result;
        }
        #endregion

        private async Task<List<string>> ResolveAutoValidationStationsAsync(long projectId, string stationCode)
        {
            var stations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(stationCode))
            {
                stations.Add(stationCode.Trim().ToUpperInvariant());
            }

            foreach (var cachedStation in await _stationShadowStateCacheService.GetProjectStationsAsync(projectId))
            {
                if (!string.IsNullOrWhiteSpace(cachedStation))
                {
                    stations.Add(cachedStation.Trim().ToUpperInvariant());
                }
            }

            var project = await _projectService.GetByIdAsync(projectId);
            if (!string.IsNullOrWhiteSpace(project?.StationId))
            {
                stations.Add(project.StationId.Trim().ToUpperInvariant());
            }

            var rules = await _ruleService.GetListAsync(x => x.ProjectId == projectId && !x.IsDeleted);
            var ruleIds = rules.Select(x => x.Id).Distinct().ToList();
            var signals = ruleIds.Count == 0
                ? new List<PLC_IOConstraintSignalEntity>()
                : await _signalService.GetListAsync(x => ruleIds.Contains(x.RuleId) && !x.IsDeleted);

            foreach (var candidate in rules.SelectMany(rule => new[] { rule.RuleName, rule.EquipmentId, rule.Description })
                         .Concat(signals.Select(x => x.SignalName)))
            {
                foreach (Match match in Regex.Matches(candidate ?? string.Empty, @"OP\d{3}", RegexOptions.IgnoreCase))
                {
                    stations.Add(match.Value.ToUpperInvariant());
                }
            }

            return stations.OrderBy(x => x).ToList();
        }

        private async Task<AutoValidationValuesResult> LoadAutoValidationValuesAsync(
            long projectId,
            string stationCode,
            List<string> signalNames,
            bool forceLiveRead)
        {
            if (!forceLiveRead)
            {
                var mirrors = await _stationShadowStateCacheService.GetSignalMirrorsAsync(projectId, stationCode);
                var mirrorValues = mirrors?.Values
                    .Where(x => !string.IsNullOrWhiteSpace(x.VariableName))
                    .Select(x => new { x.VariableName, BoolValue = TryConvertToBool(x.CurrentValue) })
                    .Where(x => x.BoolValue.HasValue)
                    .GroupBy(x => x.VariableName!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().BoolValue!.Value, StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

                if (mirrorValues.Count > 0)
                {
                    return new AutoValidationValuesResult
                    {
                        DataSource = "ShadowCache",
                        Values = mirrorValues
                    };
                }
            }

            var snapshot = await _stopCauseSignalSnapshotService.AcquireAsync(new AcquireDiagnosticSnapshotRequestDto
            {
                ProjectId = projectId,
                StationCode = stationCode,
                IncludeStationAnchorVariables = false,
                SymbolPaths = signalNames ?? new List<string>()
            });

            var snapshotValues = (snapshot?.ExecutionContext?.CurrentValues ?? new Dictionary<string, object>())
                .Select(x => new { x.Key, BoolValue = TryConvertToBool(x.Value) })
                .Where(x => x.BoolValue.HasValue)
                .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().BoolValue!.Value, StringComparer.OrdinalIgnoreCase);

            return new AutoValidationValuesResult
            {
                DataSource = "LiveSnapshot",
                Values = snapshotValues
            };
        }

        private static ValidationSummary ExtractValidationSummary(object validation)
        {
            var summary = new ValidationSummary();
            if (validation == null)
            {
                return summary;
            }

            try
            {
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(validation));
                var root = doc.RootElement;

                if (root.TryGetProperty("TotalConstraints", out var totalConstraints) && totalConstraints.TryGetInt32(out var total))
                {
                    summary.TotalConstraints = total;
                }

                if (root.TryGetProperty("HealthScore", out var healthScore) && healthScore.TryGetInt32(out var health))
                {
                    summary.HealthScore = health;
                }

                if (root.TryGetProperty("ValidationResult", out var validationResult) && validationResult.ValueKind == JsonValueKind.String)
                {
                    summary.ValidationResult = validationResult.GetString() ?? string.Empty;
                }

                if (root.TryGetProperty("Violations", out var violations) && violations.ValueKind == JsonValueKind.Array)
                {
                    summary.ViolationCount = violations.GetArrayLength();
                }
            }
            catch
            {
            }

            return summary;
        }

        private static bool? TryConvertToBool(object value)
        {
            if (value == null)
            {
                return null;
            }

            if (value is bool boolValue)
            {
                return boolValue;
            }

            if (value is JsonElement element)
            {
                return element.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number when element.TryGetInt64(out var intNumber) => intNumber != 0,
                    JsonValueKind.Number when element.TryGetDouble(out var doubleNumber) => Math.Abs(doubleNumber) > double.Epsilon,
                    JsonValueKind.String => TryConvertToBool(element.GetString()),
                    _ => null
                };
            }

            if (value is string text)
            {
                if (bool.TryParse(text, out var parsedBool))
                {
                    return parsedBool;
                }

                if (long.TryParse(text, out var parsedInt))
                {
                    return parsedInt != 0;
                }

                if (double.TryParse(text, out var parsedDouble))
                {
                    return Math.Abs(parsedDouble) > double.Epsilon;
                }

                return text.Trim() switch
                {
                    "ON" => true,
                    "OFF" => false,
                    _ => null
                };
            }

            if (value is sbyte or byte or short or ushort or int or uint or long or ulong)
            {
                return Convert.ToInt64(value) != 0;
            }

            if (value is float or double or decimal)
            {
                return Math.Abs(Convert.ToDouble(value)) > double.Epsilon;
            }

            return null;
        }

        private sealed class AutoValidationValuesResult
        {
            public string DataSource { get; init; } = string.Empty;

            public Dictionary<string, bool> Values { get; init; } = new(StringComparer.OrdinalIgnoreCase);

            public int SignalCount => Values.Count;
        }

        private sealed class ValidationSummary
        {
            public int TotalConstraints { get; set; }

            public int HealthScore { get; set; }

            public int ViolationCount { get; set; }

            public string ValidationResult { get; set; } = string.Empty;
        }

        /// <summary>
        /// 模拟约束违规：从真实数据加载规则和信号值，智能构造违规场景，运行校验并比对结果。
        /// </summary>
        public async Task<HttpResponseResultModel<object>> SimulateConstraintViolationsAsync(
            long projectId,
            string stationCode = null,
            int maxPerOperator = 3,
            long? seed = null)
        {
            var result = new HttpResponseResultModel<object>();

            try
            {
                var rules = (await _ruleService.GetListAsync(
                    x => x.ProjectId == projectId && !x.IsDeleted).ConfigureAwait(false))
                    .ToList();

                if (rules.Count == 0)
                {
                    result.IsSuccess = false;
                    result.ErrorMessage = "该项目暂无约束规则";
                    return result;
                }

                var ruleIds = rules.Select(r => r.Id).ToList();
                var allSignals = await _signalService.GetListAsync(
                    x => ruleIds.Contains(x.RuleId) && !x.IsDeleted).ConfigureAwait(false);
                var signalsByRule = allSignals
                    .GroupBy(s => s.RuleId)
                    .ToDictionary(g => g.Key, g => g.OrderBy(s => s.SortOrder ?? 0).ToList());

                var stations = await ResolveAutoValidationStationsAsync(projectId, stationCode)
                    .ConfigureAwait(false);

                var allStationResults = new List<object>();

                foreach (var station in stations)
                {
                    var stationValues = await LoadAutoValidationValuesAsync(
                        projectId, station, allSignals.Select(s => s.SignalName).Distinct().ToList(), false)
                        .ConfigureAwait(false);

                    if (stationValues.Values.Count == 0) continue;

                    var scenarios = ConstraintViolationFabricator.FabricateViolations(
                        rules, signalsByRule, stationValues.Values, maxPerOperator, seed);

                    if (scenarios.Count == 0) continue;

                    var scenarioResults = new List<object>();
                    foreach (var scenario in scenarios)
                    {
                        var mergedValues = new Dictionary<string, bool>(
                            stationValues.Values, StringComparer.OrdinalIgnoreCase);
                        foreach (var kv in scenario.TamperedValues)
                            mergedValues[kv.Key] = kv.Value;

                        var mergedJson = JsonSerializer.Serialize(mergedValues);
                        var validation = await _plcDiagnosisService.ValidateIOSignalsAsync(
                            projectId, mergedJson, station).ConfigureAwait(false);

                        var validationJson = JsonSerializer.Serialize(validation);
                        var hasViolation = validationJson.Contains("\"HasViolation\":true",
                            StringComparison.OrdinalIgnoreCase) ||
                            validationJson.Contains("\"ViolationCount\":",
                            StringComparison.OrdinalIgnoreCase);

                        scenarioResults.Add(new
                        {
                            scenario.RuleId,
                            scenario.RuleName,
                            scenario.RuleType,
                            scenario.Operator,
                            scenario.OriginalValues,
                            scenario.TamperedValues,
                            scenario.Mutations,
                            scenario.ExpectedViolationReason,
                            ExpectedViolation = true,
                            ActualValidation = validation
                        });
                    }

                    allStationResults.Add(new
                    {
                        StationCode = station,
                        DataSource = stationValues.DataSource,
                        SignalCount = stationValues.Values.Count,
                        ScenarioCount = scenarioResults.Count,
                        Scenarios = scenarioResults
                    });
                }

                var totalScenarios = allStationResults
                    .Cast<dynamic>()
                    .Sum(s => (int)s.ScenarioCount);

                result.IsSuccess = true;
                result.BackResult = new
                {
                    ProjectId = projectId,
                    StationCount = allStationResults.Count,
                    TotalScenarios = totalScenarios,
                    Stations = allStationResults
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SimulateViolations] 模拟约束违规失败: ProjectId={ProjectId}", projectId);
                result.IsSuccess = false;
                result.ErrorMessage = $"模拟约束违规失败: {ex.Message}";
            }

            return result;
        }
    }
}
