using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.States;
using HSJM.AI.AppLayer.Agent.Services.Plc_diagnosis;
using HSJM.AI.AppLayer.PLC.PLC_Upload;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace HSJM.AI.AppLayer.PLC.PLC_AlarmVariable
{
    /// <summary>
    /// 报警变量识别服务（三通道）。
    ///
    /// 通道 A｜结构证据扫描（纯代码，零 LLM 成本，先跑）：
    ///   - SCoil/RCoil 报警网络：PLD 中「— 置位线圈 SCoil / — 复位线圈 RCoil」锚点 → DB 报警位；
    ///     其子树触点中命中 IO Tag 的 %I 传感器 → 报警触发传感器（可采集、可监控）。
    ///   - DB 报警路径段：变量全路径含 .Errors./.Alarms./.Faults./.Warnings.（大小写兼容）→ 报警位。
    ///   证据来源均为结构证据（线圈语义/路径层级），跨项目通用（遵守 §4.2：不依赖命名前后缀规则）。
    ///
    /// 通道 B｜LLM 批量语义分类（主力）：
    ///   IO Tag 清单（名+注释+地址+类型）分批（100/批）喂 LLM，产出 isAlarm/类别/等级/描述/置信度/理由。
    ///   复用 IOConstraintPreprocessService 的 ChatClient 与重试基建。
    ///
    /// 合并规则（upsert，key = project_id + variable_name）：
    ///   新变量 → 插入；已存在未确认 → 更新证据/分类；manually_confirmed=true → 跳过（人工确认锁）；
    ///   软删行 → 跳过不复活。
    ///
    /// 触发：上传解析完成钩子（PLC_UploadApp）自动入队 + 管理页面手动重跑。
    /// 队列：local_&lt;机器名&gt;（与约束生成一致，避免多机抢跑版本不一致）。
    /// </summary>
    public class AlarmVariableRecognitionService : ISelfScopedAutoInject
    {
        private const int LLM_BATCH_SIZE = 100;
        private const int MAX_PARALLELISM = 4;
        private const string SOURCE_STRUCTURAL = "Structural";
        private const string SOURCE_LLM = "LLM";

        private static readonly ConcurrentDictionary<long, byte> _runningProjects = new();

        private readonly IPLC_AlarmVariableService _alarmVariableService;
        private readonly IPLC_IOTagService _tagService;
        private readonly IPLC_LogicUnitService _logicUnitService;
        private readonly IPLC_BlockService _blockService;
        private readonly IPLC_VariableAccessService _variableAccessService;
        private readonly IOConstraintPreprocessService _constraintPreprocessService;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<AlarmVariableRecognitionService> _logger;

        public AlarmVariableRecognitionService(
            IPLC_AlarmVariableService alarmVariableService,
            IPLC_IOTagService tagService,
            IPLC_LogicUnitService logicUnitService,
            IPLC_BlockService blockService,
            IPLC_VariableAccessService variableAccessService,
            IOConstraintPreprocessService constraintPreprocessService,
            IServiceProvider serviceProvider,
            ILogger<AlarmVariableRecognitionService> logger)
        {
            _alarmVariableService = alarmVariableService;
            _tagService = tagService;
            _logicUnitService = logicUnitService;
            _blockService = blockService;
            _variableAccessService = variableAccessService;
            _constraintPreprocessService = constraintPreprocessService;
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        #region ========== 入队（本机队列，模式与 IOConstraintPreprocessService 一致） ==========

        /// <summary>
        /// 入队识别任务（同项目已有任务在跑时返回 false）。上传钩子与手动重跑共用。
        /// 落到 local_&lt;机器名&gt; 队列，避免多机共享 Hangfire 库时被旧版本 worker 抢跑。
        /// </summary>
        public static bool EnqueueIfNotRunning(long projectId)
        {
            if (_runningProjects.ContainsKey(projectId))
            {
                return false;
            }

            var queue = BuildCurrentMachineQueue();
            var client = new BackgroundJobClient();
            client.Create<AlarmVariableRecognitionService>(
                svc => svc.ExecuteAsync(projectId),
                new EnqueuedState(queue));
            return true;
        }

        /// <summary>
        /// 手动重跑入口：携带外部进度 taskId 入队（页面进度轮询用）。
        /// </summary>
        public static bool EnqueueWithProgressTaskId(string taskId, long projectId)
        {
            if (_runningProjects.ContainsKey(projectId))
            {
                return false;
            }

            var queue = BuildCurrentMachineQueue();
            var client = new BackgroundJobClient();
            client.Create<AlarmVariableRecognitionService>(
                svc => svc.ExecuteWithProgressAsync(taskId, projectId),
                new EnqueuedState(queue));
            return true;
        }

        /// <summary>指定项目的识别任务是否正在运行（防重复入队/页面提示用）。</summary>
        public static bool IsProjectRunning(long projectId)
        {
            return _runningProjects.ContainsKey(projectId);
        }

        /// <summary>计算本机专用 Hangfire 队列名（与 HSJM.AI/HangfireQueueNames 等价语义；AppLayer 不能引用顶层项目，故 inline）。</summary>
        private static string BuildCurrentMachineQueue()
        {
            var raw = Environment.GetEnvironmentVariable("HSJM_HANGFIRE_LOCAL_QUEUE");
            if (string.IsNullOrWhiteSpace(raw)) raw = Environment.MachineName;
            var builder = new StringBuilder();
            foreach (var ch in raw.Trim().ToLowerInvariant())
            {
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
                {
                    builder.Append(ch);
                }
                else if (builder.Length == 0 || builder[builder.Length - 1] != '_')
                {
                    builder.Append('_');
                }
            }
            var result = builder.ToString().Trim('_');
            return $"local_{(string.IsNullOrWhiteSpace(result) ? "current" : result)}";
        }

        #endregion

        #region ========== Hangfire 执行入口 ==========

        /// <summary>
        /// Hangfire 入口（上传钩子/自动触发）：内部生成进度任务后执行。
        /// </summary>
        [Hangfire.JobDisplayName("报警变量识别 · 项目{0}")]
        public async Task ExecuteAsync(long projectId)
        {
            var taskId = $"alarm_var_{projectId}_{DateTime.Now:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}";
            await ExecuteWithProgressAsync(taskId, projectId);
        }

        /// <summary>
        /// 带外部进度任务 ID 的执行体（手动重跑入口由 App 层创建 taskId 后入队本方法）。
        /// </summary>
        [Hangfire.JobDisplayName("报警变量识别·手动重跑 · 项目{1}")]
        public async Task ExecuteWithProgressAsync(string taskId, long projectId)
        {
            if (!_runningProjects.TryAdd(projectId, 0))
            {
                _logger.LogWarning("[AlarmVar] 项目 {ProjectId} 识别任务已在运行，跳过重复执行", projectId);
                return;
            }

            var progressService = _serviceProvider.GetRequiredService<PLC_UploadProgressService>();
            try
            {
                progressService.CreateProgress(taskId, projectId, "识别报警变量");
                _logger.LogInformation("[AlarmVar] 开始识别: ProjectId={ProjectId}, TaskId={TaskId}", projectId, taskId);

                // ============ 准备：IO Tag 字典（collectable 判定 + LLM 输入） ============
                var ioTags = await _tagService.GetListAsync(it => it.IsDeleted == false && it.ProjectId == projectId);
                var ioTagDict = new Dictionary<string, PLC_IOTagEntity>(StringComparer.OrdinalIgnoreCase);
                foreach (var tag in ioTags)
                {
                    if (string.IsNullOrWhiteSpace(tag.TagName)) continue;
                    ioTagDict[tag.TagName.Trim()] = tag;
                }

                // ============ 通道 A：结构证据扫描 ============
                progressService.SetMessage(taskId, "通道A：结构证据扫描（SCoil/RCoil 报警网络 + DB 报警路径）...");
                var candidates = await ScanStructuralEvidenceAsync(projectId, ioTagDict);
                var structuralCount = candidates.Count;
                _logger.LogInformation("[AlarmVar] 通道A结构扫描完成: ProjectId={ProjectId}, 候选={Count}", projectId, structuralCount);

                // ============ 通道 B：LLM 批量语义分类 ============
                var llmCount = 0;
                var llmSkipped = false;
                var client = await _constraintPreprocessService.GetChatClientAsync();
                if (client == null)
                {
                    // 已知坑（2026-05-28）：ai_llm_model 全部 isenabled=false 时 GetChatClientAsync 返回 null。
                    // 结构通道结果照常入库，但必须在摘要里显式说明 LLM 通道未跑，避免静默半成品。
                    llmSkipped = true;
                    _logger.LogError("[AlarmVar] 无可用的 LLM 模型，通道B跳过（结构通道结果仍入库）: ProjectId={ProjectId}", projectId);
                }
                else
                {
                    var pendingTags = ioTags
                        .Where(t => !string.IsNullOrWhiteSpace(t.TagName))
                        .Where(t => !IsPlaceholderSignal(t.TagName))
                        .Where(t => !candidates.ContainsKey(t.TagName.Trim()))
                        .ToList();

                    var batches = Chunk(pendingTags, LLM_BATCH_SIZE).ToList();
                    progressService.SetTotalFiles(taskId, batches.Count + 1, "识别报警变量");

                    // 通道 A 记 1 步
                    progressService.IncrementProcessed(taskId, "结构证据扫描", "通道A完成");

                    var semaphore = new SemaphoreSlim(MAX_PARALLELISM);
                    var batchIndex = 0;
                    var tasks = batches.Select(batch =>
                    {
                        var index = Interlocked.Increment(ref batchIndex);
                        return Task.Run(async () =>
                        {
                            try
                            {
                                await semaphore.WaitAsync();
                                var added = await ClassifyBatchWithLlmAsync(client, projectId, batch, candidates);
                                Interlocked.Add(ref llmCount, added);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "[AlarmVar] LLM 批次执行失败: ProjectId={ProjectId}, BatchIndex={BatchIndex}", projectId, index);
                            }
                            finally
                            {
                                progressService.IncrementProcessed(taskId, $"LLM 批次 {index}", "通道B分类中");
                                semaphore.Release();
                            }
                        });
                    }).ToList();
                    await Task.WhenAll(tasks);
                }

                if (llmSkipped && candidates.Count == 0)
                {
                    progressService.MarkFailed(taskId, "无可用 LLM 模型且结构扫描无候选，识别终止（请检查 ai_llm_model.isenabled）");
                    return;
                }

                // ============ 合并入库（upsert + 人工确认锁） ============
                progressService.SetMessage(taskId, "正在保存到报警变量台账...");
                var (inserted, updated, skippedConfirmed, skippedDeleted) = await MergeIntoLedgerAsync(projectId, candidates, ioTagDict);

                var summary = $"识别完成：新增 {inserted}（结构候选 {structuralCount} / LLM 命中 {llmCount}）、更新 {updated}、人工确认跳过 {skippedConfirmed}、软删跳过 {skippedDeleted}";
                if (llmSkipped)
                {
                    summary = $"识别完成：新增 {inserted}（结构候选 {structuralCount}）、更新 {updated}、人工确认跳过 {skippedConfirmed}、软删跳过 {skippedDeleted}；LLM 通道跳过（无可用 LLM 模型）";
                }
                progressService.MarkCompleted(taskId, projectId.ToString(), summary);
                _logger.LogInformation("[AlarmVar] 识别完成: ProjectId={ProjectId}, {Summary}", projectId, summary);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AlarmVar] 识别任务异常: ProjectId={ProjectId}", projectId);
                try { progressService.MarkFailed(taskId, $"报警变量识别失败: {ex.Message}"); } catch { /* ignored */ }
            }
            finally
            {
                _runningProjects.TryRemove(projectId, out _);
            }
        }

        #endregion

        #region ========== 通道 A：结构证据扫描 ==========

        private static readonly Regex BacktickTokenRegex = new Regex("`([^`]+)`", RegexOptions.Compiled);

        /// <summary>
        /// 结构证据扫描：SCoil/RCoil 报警网络 + DB 报警路径段。
        /// 只消费 ParsedLogicDescription / VariableName 文本证据，不二次解析 RawContent XML。
        /// </summary>
        private async Task<Dictionary<string, AlarmCandidate>> ScanStructuralEvidenceAsync(
            long projectId, Dictionary<string, PLC_IOTagEntity> ioTagDict)
        {
            var candidates = new Dictionary<string, AlarmCandidate>(StringComparer.OrdinalIgnoreCase);

            // --- A-1: SCoil/RCoil 报警网络 LU（PLD 含线圈语义标记） ---
            var blocks = await _blockService.GetListAsync(it => it.IsDeleted == false && it.ProjectId == projectId);
            var blockIds = blocks.Select(b => b.Id).ToList();
            var blockNameById = blocks.ToDictionary(b => b.Id, b => b.BlockName ?? "");

            if (blockIds.Count > 0)
            {
                var alarmLus = await _logicUnitService.GetListAsync(it => it.IsDeleted == false
                    && blockIds.Contains(it.BlockId.Value)
                    && it.ParsedLogicDescription != null
                    && (it.ParsedLogicDescription.Contains("SCoil") || it.ParsedLogicDescription.Contains("RCoil")));

                foreach (var lu in alarmLus)
                {
                    var blockName = lu.BlockId.HasValue && blockNameById.TryGetValue(lu.BlockId.Value, out var bn) ? bn : "";
                    var luLabel = $"{blockName} {lu.Title}".Trim();
                    var pld = lu.ParsedLogicDescription ?? "";
                    var lines = pld.Split('\n');

                    var isAlarmNetwork = false;
                    foreach (var line in lines)
                    {
                        // 锚点行 + 置位/复位线圈语义 → 本 LU 是报警网络
                        if (line.Contains("锚点") && (line.Contains("SCoil") || line.Contains("RCoil")))
                        {
                            isAlarmNetwork = true;
                            var anchor = BacktickTokenRegex.Match(line);
                            if (anchor.Success)
                            {
                                AddCandidate(candidates, anchor.Groups[1].Value, SOURCE_STRUCTURAL, "设备故障", null,
                                    $"SCoil/RCoil 报警位（{luLabel}）", 1.0m, ioTagDict);
                            }
                        }

                        // 线圈输出行（报警网络内的 SCoil/RCoil 输出）
                        if (isAlarmNetwork && line.Contains("线圈输出"))
                        {
                            var coil = BacktickTokenRegex.Match(line);
                            if (coil.Success)
                            {
                                AddCandidate(candidates, coil.Groups[1].Value, SOURCE_STRUCTURAL, "设备故障", null,
                                    $"报警网络线圈输出（{luLabel}）", 1.0m, ioTagDict);
                            }
                        }
                    }

                    // 报警网络子树中的 %I 触点 → 报警触发传感器（可采集，是真正要监控的变量）
                    if (isAlarmNetwork)
                    {
                        foreach (var line in lines)
                        {
                            if (!line.Contains("触点")) continue;
                            var contact = BacktickTokenRegex.Match(line);
                            if (!contact.Success) continue;
                            var name = contact.Groups[1].Value.Trim();
                            if (!ioTagDict.TryGetValue(name, out var tag)) continue;
                            if (!(tag.Address ?? "").StartsWith("%I", StringComparison.OrdinalIgnoreCase)) continue;
                            AddCandidate(candidates, name, SOURCE_STRUCTURAL, "设备故障", null,
                                $"报警网络触发传感器（{luLabel}）", 1.0m, ioTagDict);
                        }
                    }
                }
            }

            // --- A-2: DB 报警路径段（.Errors./.Alarms./.Faults./.Warnings.，大小写兼容） ---
            var pathCandidates = await _variableAccessService.GetListAsync(it => it.IsDeleted == false
                && it.ProjectId == projectId
                && it.VariableName != null
                && (it.VariableName.Contains(".Errors.") || it.VariableName.Contains(".errors.")
                    || it.VariableName.Contains(".Alarms.") || it.VariableName.Contains(".alarms.")
                    || it.VariableName.Contains(".Faults.") || it.VariableName.Contains(".faults.")
                    || it.VariableName.Contains(".Warnings.") || it.VariableName.Contains(".warnings.")));

            foreach (var va in pathCandidates)
            {
                var name = (va.VariableName ?? "").Trim();
                if (string.IsNullOrWhiteSpace(name) || IsPlaceholderSignal(name)) continue;
                AddCandidate(candidates, name, SOURCE_STRUCTURAL, "设备故障", null, "DB 报警路径段（Errors/Alarms/Faults/Warnings）", 1.0m, ioTagDict);
            }

            return candidates;
        }

        private static void AddCandidate(Dictionary<string, AlarmCandidate> candidates, string rawName,
            string source, string category, string level, string evidence, decimal confidence,
            Dictionary<string, PLC_IOTagEntity> ioTagDict)
        {
            var name = (rawName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name) || IsPlaceholderSignal(name)) return;
            if (name.Length > 256) name = name.Substring(0, 256);

            if (candidates.TryGetValue(name, out var existing))
            {
                existing.Evidences.Add(evidence);
                // 结构证据置信度最高
                if (source == SOURCE_STRUCTURAL) existing.Confidence = Math.Max(existing.Confidence, confidence);
                return;
            }

            ioTagDict.TryGetValue(name, out var tag);
            candidates[name] = new AlarmCandidate
            {
                VariableName = name,
                Source = source,
                Category = category,
                Level = level,
                Description = BuildStructuralDescription(name, tag),
                Confidence = confidence,
                Evidences = new List<string> { evidence },
                Address = tag?.Address,
                DataType = tag?.DataType,
                Collectable = tag != null
            };
        }

        private static string BuildStructuralDescription(string name, PLC_IOTagEntity tag)
        {
            var comment = (tag?.Comment ?? "").Trim();
            return comment.Length > 0 ? Truncate($"{comment}（结构证据识别）", 480) : $"结构证据识别的报警变量（{Truncate(name, 60)}）";
        }

        #endregion

        #region ========== 通道 B：LLM 批量语义分类 ==========

        private async Task<int> ClassifyBatchWithLlmAsync(IChatClient client, long projectId,
            List<PLC_IOTagEntity> batch, Dictionary<string, AlarmCandidate> candidates)
        {
            var input = batch.Select(t => new
            {
                tagName = t.TagName?.Trim(),
                comment = t.Comment ?? "",
                address = t.Address ?? "",
                dataType = t.DataType ?? ""
            }).ToList();

            var systemPrompt =
                "你是工业 PLC 报警变量识别专家。根据给定的 IO 标签清单（名称、注释、地址、数据类型），" +
                "判断每个标签是否为报警/故障相关变量。判定依据：注释语义（首选，如包含 报警/故障/错误/异常/报警灯/Alarm/Error/Fault/Warning 等语义）" +
                "与命名语义综合判断；关键词仅作语义提示而非硬规则，无明确证据时 isAlarm=false，不编造。" +
                "模式/状态类（自动模式、步序、心跳）与普通反馈类（位置到位）不算报警。仅输出严格 JSON 数组，无任何其它文字。";

            var userPrompt =
                $"IO 标签清单（JSON，共 {input.Count} 条）：\n{JsonConvert.SerializeObject(input)}\n\n" +
                "输出格式（仅 JSON 数组）：\n" +
                "[{\"tagName\":\"...\",\"isAlarm\":true,\"category\":\"设备故障|安全|质量|其它\",\"level\":\"Info|Warn|Critical\",\"description\":\"不超过30字的报警描述\",\"confidence\":0.9,\"reason\":\"判定依据\"}]\n" +
                "要求：\n" +
                "1) 输出条目数与顺序必须与输入完全一致，每个输入标签都要给出判定（含 isAlarm=false 的）；\n" +
                "2) 布尔值用小写 true/false；\n" +
                "3) isAlarm=false 时 category/level/description 输出空字符串；\n" +
                "4) 不得编造输入中不存在的标签名。";

            var response = await _constraintPreprocessService.CallAIWithRetryAsync(
                client, systemPrompt, userPrompt,
                $"alarm-batch-{projectId}-{batch.First().Id}", "AlarmVarPhase");

            if (string.IsNullOrWhiteSpace(response))
            {
                _logger.LogWarning("[AlarmVar] LLM 批次返回为空: ProjectId={ProjectId}", projectId);
                return 0;
            }

            try
            {
                var results = JsonConvert.DeserializeObject<List<AlarmTagLlmResult>>(FixJsonLooseToStrict(response));
                if (results == null) return 0;

                var added = 0;
                foreach (var r in results)
                {
                    if (r == null || !r.isAlarm || string.IsNullOrWhiteSpace(r.tagName)) continue;
                    var name = r.tagName.Trim();
                    if (name.Length > 256) name = name.Substring(0, 256);
                    if (IsPlaceholderSignal(name)) continue;
                    if (candidates.ContainsKey(name)) continue; // 结构通道优先，不覆盖

                    candidates[name] = new AlarmCandidate
                    {
                        VariableName = name,
                        Source = SOURCE_LLM,
                        Category = NormalizeCategory(r.category),
                        Level = NormalizeLevel(r.level),
                        Description = Truncate(r.description, 480),
                        Confidence = r.confidence > 0 ? Math.Min(r.confidence, 1.0m) : 0.5m,
                        Evidences = new List<string> { $"LLM 语义分类：{Truncate(r.reason, 200)}" },
                        Address = null,
                        DataType = null,
                        Collectable = false
                    };
                    added++;
                }
                return added;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AlarmVar] LLM 批次结果解析失败: ProjectId={ProjectId}", projectId);
                return 0;
            }
        }

        private static string NormalizeCategory(string category)
        {
            var c = (category ?? "").Trim();
            if (c.Contains("安全")) return "安全";
            if (c.Contains("质量")) return "质量";
            if (c.Contains("设备") || c.Contains("故障")) return "设备故障";
            if (c.Length == 0) return null;
            return Truncate(c, 64);
        }

        private static string NormalizeLevel(string level)
        {
            var l = (level ?? "").Trim().ToLowerInvariant();
            if (l.Contains("crit") || l.Contains("严重")) return "Critical";
            if (l.Contains("info") || l.Contains("提示")) return "Info";
            return "Warn";
        }

        #endregion

        #region ========== 合并入库（upsert + 人工确认锁） ==========

        private async Task<(int inserted, int updated, int skippedConfirmed, int skippedDeleted)> MergeIntoLedgerAsync(
            long projectId, Dictionary<string, AlarmCandidate> candidates, Dictionary<string, PLC_IOTagEntity> ioTagDict)
        {
            // 加载项目全部台账行（含软删，用于防复活）
            var existingRows = await _alarmVariableService.GetListAsync(it => it.ProjectId == projectId);
            var activeByName = new Dictionary<string, PLC_AlarmVariableEntity>(StringComparer.OrdinalIgnoreCase);
            var deletedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in existingRows)
            {
                var name = (row.VariableName ?? "").Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (row.IsDeleted) deletedNames.Add(name);
                else activeByName[name] = row;
            }

            var toInsert = new List<PLC_AlarmVariableEntity>();
            var toUpdate = new List<PLC_AlarmVariableEntity>();
            var skippedConfirmed = 0;
            var skippedDeleted = 0;
            var now = DateTime.Now;
            var sortOrder = 0;

            foreach (var candidate in candidates.Values)
            {
                sortOrder++;

                if (deletedNames.Contains(candidate.VariableName))
                {
                    skippedDeleted++;
                    continue;
                }

                // LLM 候选补齐 iotag 关联信息（结构候选在 AddCandidate 时已带）
                if (!candidate.Collectable && ioTagDict.TryGetValue(candidate.VariableName, out var tag))
                {
                    candidate.Address = tag.Address;
                    candidate.DataType = tag.DataType;
                    candidate.Collectable = true;
                }

                if (activeByName.TryGetValue(candidate.VariableName, out var existing))
                {
                    // 人工确认锁：LLM/结构重跑永不覆盖人工判断
                    if (existing.ManuallyConfirmed == true)
                    {
                        skippedConfirmed++;
                        continue;
                    }

                    existing.AlarmCategory = candidate.Category ?? existing.AlarmCategory;
                    existing.AlarmLevel = candidate.Level ?? existing.AlarmLevel ?? "Warn";
                    if (!string.IsNullOrWhiteSpace(candidate.Description))
                    {
                        existing.Description = candidate.Description;
                    }
                    existing.EvidenceJson = JsonConvert.SerializeObject(new { evidences = candidate.Evidences });
                    existing.LlmConfidence = candidate.Confidence;
                    existing.Address = candidate.Address ?? existing.Address;
                    existing.DataType = candidate.DataType ?? existing.DataType;
                    existing.Collectable = candidate.Collectable;
                    if (existing.Collectable == false)
                    {
                        existing.MonitorEnabled = false; // 不可采集硬禁监控
                    }
                    existing.Source = existing.Source == "Manual" ? "Manual" : candidate.Source;
                    existing.ModifyTime = now;
                    toUpdate.Add(existing);
                }
                else
                {
                    toInsert.Add(new PLC_AlarmVariableEntity
                    {
                        ProjectId = projectId,
                        VariableName = candidate.VariableName,
                        Address = candidate.Address,
                        DataType = candidate.DataType,
                        AlarmCategory = candidate.Category,
                        AlarmLevel = candidate.Level ?? "Warn",
                        Description = candidate.Description,
                        Source = candidate.Source,
                        ManuallyConfirmed = false,
                        MonitorEnabled = false, // 默认不监控，页面人工确认后再开
                        PushEnabled = true,
                        Collectable = candidate.Collectable,
                        EvidenceJson = JsonConvert.SerializeObject(new { evidences = candidate.Evidences }),
                        LlmConfidence = candidate.Confidence,
                        AlarmCount = 0,
                        SortOrder = sortOrder,
                        CreateTime = now,
                        IsDeleted = false
                    });
                }
            }

            if (toInsert.Count > 0)
            {
                await _alarmVariableService.InsertRangeAsync(toInsert);
            }
            if (toUpdate.Count > 0)
            {
                await _alarmVariableService.BulkUpdateAsync(toUpdate);
            }

            return (toInsert.Count, toUpdate.Count, skippedConfirmed, skippedDeleted);
        }

        #endregion

        #region ========== 工具 ==========

        private static bool IsPlaceholderSignal(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return true;
            var n = name.Trim();
            if (n.Equals("Null", StringComparison.OrdinalIgnoreCase)
                || n.Equals("AlwaysTRUE", StringComparison.OrdinalIgnoreCase)
                || n.Equals("AlwaysFALSE", StringComparison.OrdinalIgnoreCase)
                || n.Equals("True", StringComparison.OrdinalIgnoreCase)
                || n.Equals("False", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return n.StartsWith("Debug_", StringComparison.OrdinalIgnoreCase)
                || n.StartsWith("Debug.", StringComparison.OrdinalIgnoreCase);
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value;
            return value.Substring(0, maxLength) + "…";
        }

        private static List<List<T>> Chunk<T>(List<T> source, int size)
        {
            var result = new List<List<T>>();
            for (int i = 0; i < source.Count; i += size)
            {
                result.Add(source.GetRange(i, Math.Min(size, source.Count - i)));
            }
            return result;
        }

        /// <summary>
        /// JSON 修复器：去除 markdown 包裹、单引号→双引号、去尾逗号、裁剪前后非 JSON 字符。
        /// （与 PLC_VariableAccessApp.FixJsonLooseToStrict 同语义）
        /// </summary>
        private static string FixJsonLooseToStrict(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            var s = raw.Trim();
            s = s.Replace("```json", string.Empty).Replace("```", string.Empty).Trim();
            int start = Math.Min(
                s.IndexOf('{') >= 0 ? s.IndexOf('{') : int.MaxValue,
                s.IndexOf('[') >= 0 ? s.IndexOf('[') : int.MaxValue);
            int end = Math.Max(s.LastIndexOf('}'), s.LastIndexOf(']'));
            if (start != int.MaxValue && end > start)
            {
                s = s.Substring(start, end - start + 1);
            }
            if (s.Contains("'{") || s.Contains("'}") || s.Contains("':"))
            {
                s = s.Replace("'", "\"");
            }
            s = s.Replace(",}\n", "}\n").Replace(",}\r\n", "}\r\n").Replace(",]", "]");
            return s.Trim();
        }

        /// <summary>识别候选（内存中间态，不落库）</summary>
        private class AlarmCandidate
        {
            public string VariableName { get; set; }
            public string Source { get; set; }
            public string Category { get; set; }
            public string Level { get; set; }
            public string Description { get; set; }
            public decimal Confidence { get; set; }
            public List<string> Evidences { get; set; }
            public string Address { get; set; }
            public string DataType { get; set; }
            public bool Collectable { get; set; }
        }

        /// <summary>LLM 输出条目（字段名与 prompt 约定一致）</summary>
        public class AlarmTagLlmResult
        {
            public string tagName { get; set; }
            public bool isAlarm { get; set; }
            public string category { get; set; }
            public string level { get; set; }
            public string description { get; set; }
            public decimal confidence { get; set; }
            public string reason { get; set; }
        }

        #endregion
    }
}
