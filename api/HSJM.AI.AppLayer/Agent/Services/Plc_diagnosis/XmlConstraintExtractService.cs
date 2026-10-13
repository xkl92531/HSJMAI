#nullable disable
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.Utility;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace HSJM.AI.AppLayer.Agent.Services.Plc_diagnosis
{
    /// <summary>
    /// XML 约束提取服务
    ///
    /// 职责：
    /// 直接从 PLC XML 逻辑中解析约束关系，不依赖 AI 推理
    /// 解析 LogicUnit 的 RawContent XML，提取约束表达式
    ///
    /// 约束类型：
    /// - Mutex: 互斥关系（两个反向驱动信号不能同时为 true）
    /// - ExactlyOne: 唯一选择（一组信号中只能一个为 true）
    /// - AllRequired: 全满足（一组信号必须全部为 true）
    /// - Pairing: 配对关系（驱动信号 = true 时反馈信号应为 true）
    /// - Safety: 安全约束（安全信号断开时，受控输出必须为 false）
    /// - Sequence: 时序链（信号必须按顺序激活）
    /// </summary>
    public class XmlConstraintExtractService : ISelfScopedAutoInject
    {
        private readonly ILogger<XmlConstraintExtractService> _logger;
        private readonly IPLC_IOTagService _ioTagService;
        private readonly IPLC_LogicUnitService _logicUnitService;
        private readonly IPLC_VariableAccessService _variableAccessService;
        private readonly IPLC_IOConstraintRuleService _constraintRuleService;
        private readonly IPLC_IOConstraintSignalService _constraintSignalService;
        private readonly IPLC_BlockService _blockService;
        private readonly PlcDiagnosisService _diagnosisService;
        private readonly LLMModelService _llmModelService;

        /// <summary>AI 调用超时时间（秒）</summary>
        private const int AI_TIMEOUT_SECONDS = 60;

        /// <summary>互斥信号关键词</summary>
        private static readonly string[] MutexKeywords = { "Fwd", "Bkd", "Forward", "Backward", "Open", "Close", "Up", "Down", "Left", "Right", "Run", "Stop", "Start", "Reset" };

        /// <summary>反馈信号关键词</summary>
        private static readonly string[] FeedbackKeywords = { "OK", "Done", "Pos", "Detect", "Sensor", "Limit", "到位", "反馈", "限位" };

        /// <summary>安全信号关键词</summary>
        private static readonly string[] SafetyKeywords =
        {
            // 急停 / 安全继电器
            "E_Stop", "EStop", "EmergencyStop", "急停",
            // 安全门 / 防护门 / 门锁
            "SafetyDoor", "SafeDoor", "DoorLock", "DoorGuard", "Door_Lock", "Door_Guard",
            "Guard", "GuardFence", "Fence", "Gate", "SafeGate",
            "安全门", "防护门", "门锁", "防护栏",
            // 光幕 / 安全光栅
            "LightCurtain", "Light_Curtain", "SafetyLight", "Curtain",
            "光幕", "光栅", "安全光幕",
            // 通用安全 / 互锁
            "Safe", "Safety", "Interlock", "安全",
            // 双手操作 / 使能
            "TwoHand", "Two_Hand", "Enable_Safe", "SafeEnable",
            "双手", "使能安全",
        };

/// <summary>
/// 
/// </summary>
/// <param name="logger"></param>
/// <param name="ioTagService"></param>
/// <param name="logicUnitService"></param>
/// <param name="variableAccessService"></param>
/// <param name=""></param>
/// <param name="constraintRuleService"></param>
/// <param name="constraintSignalService"></param>
/// <param name="blockService"></param>
/// <param name="diagnosisService"></param>
/// <param name="llmModelService"></param>
        public XmlConstraintExtractService(
            ILogger<XmlConstraintExtractService> logger,
            IPLC_IOTagService ioTagService,
            IPLC_LogicUnitService logicUnitService,
            IPLC_VariableAccessService variableAccessService,
            IPLC_IOConstraintRuleService constraintRuleService,
            IPLC_IOConstraintSignalService constraintSignalService,
            IPLC_BlockService blockService,
            PlcDiagnosisService diagnosisService,
            LLMModelService llmModelService)
        {
            _logger = logger;
            _ioTagService = ioTagService;
            _logicUnitService = logicUnitService;
            _variableAccessService = variableAccessService;
            _constraintRuleService = constraintRuleService;
            _constraintSignalService = constraintSignalService;
            _blockService = blockService;
            _diagnosisService = diagnosisService;
            _llmModelService = llmModelService;
        }

        #region 公开接口

        /// <summary>
        /// Step1 XML 预解析：对每个 LogicUnit 调用 LLM，根据 RawContent + UidSymbolMapJson + IO Tag 字典
        /// 生成结构化中文描述（业务意图、Q/I 点清单、驱动对应关系、互斥/联锁），保存到 ParsedLogicDescription。
        /// 这段描述会被 Phase2 约束推理 Prompt 直接引用，避免 AI 看万行 XML 时按命名后缀瞎猜。
        /// </summary>
        /// <param name="projectId">项目ID</param>
        /// <returns>解析结果</returns>
        /// <summary>
        /// 调试用：只对指定 LU 跑一次 Step1 渲染（不动其它 LU、不做拓扑排序、不刷 caller 摘要）。
        /// 用于 ExecuteSingleLuAsync 的 PLD 即时补渲染。
        /// </summary>
        public async Task<object> ConvertSingleLuAsync(long luId)
        {
            var lus = await _logicUnitService.GetListAsync(x => x.Id == luId && !x.IsDeleted);
            var lu = lus.FirstOrDefault();
            if (lu == null)
            {
                return new { Success = false, Message = $"LU#{luId} 不存在或已删除" };
            }
            if (string.IsNullOrWhiteSpace(lu.RawContent))
            {
                return new { Success = false, Message = $"LU#{luId} 无 RawContent" };
            }

            string? blockName = null;
            if (lu.BlockId.HasValue)
            {
                var blocks = await _blockService.GetListAsync(b => b.Id == lu.BlockId.Value && !b.IsDeleted);
                blockName = blocks.FirstOrDefault()?.BlockName;
            }

            var description = BuildParsedLogicDescription(lu, blockName);
            lu.ParsedLogicDescription = description;
            await _logicUnitService.UpdateAsync(lu);
            return new { Success = true, LuId = luId, PldLength = description?.Length ?? 0 };
        }

        public async Task<object> ConvertAndSaveCompactExpressionAsync(long projectId)
        {
            _logger.LogInformation("[XmlConstraint] 开始解析程序块代码: ProjectId={ProjectId}", projectId);

            try
            {
                // 1. 获取项目下的所有 Block，再获取关联的 LogicUnit
                var blocks = await _blockService.GetListAsync(x =>
                    x.ProjectId == projectId && !x.IsDeleted);

                var blockIds = blocks.Select(b => b.Id).ToList();

                // 幂等：只跑 ParsedLogicDescription 为空的 LU，已解析过的不重复消耗 LLM。
                // 重置（清空）需要由用户显式 UPDATE ParsedLogicDescription = NULL 触发。
                var logicUnits = blockIds.Count > 0
                    ? await _logicUnitService.GetListAsync(x =>
                        blockIds.Contains(x.BlockId!.Value)
                        && !string.IsNullOrEmpty(x.RawContent)
                        && (x.ParsedLogicDescription == null || x.ParsedLogicDescription == ""))
                    : new List<PLC_LogicUnitEntity>();

                _logger.LogInformation("[XmlConstraint] 找到 {Count} 个待预解析 LogicUnit（已跳过已有 ParsedLogicDescription 的）", logicUnits.Count);

                int processedCount = 0;
                int errorCount = 0;

                // 一次性加载项目 IO Tag 字典，传给每个 LU 的解析方法识别 Q/I 点方向。
                var projectIoTags = await _ioTagService.GetListAsync(x =>
                    x.ProjectId == projectId && !x.IsDeleted);
                var ioTagDict = projectIoTags
                    .Where(t => !string.IsNullOrWhiteSpace(t.TagName))
                    .GroupBy(t => t.TagName!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                // 不再使用 AST 渲染路径——AST 编译器在部分 LU 上简化丢信息（如 Valve[1] IWP 拓扑缺失），
                // 改为统一走硬编码 FlgNet 渲染（SiemensFlgNetConstraintExtractor.RenderLogicUnitToMarkdown）
                // 覆盖全部 6 类根：FlgNet/Transition/Interlock/Supervision/Actions/StructuredText

                // 构建 LU → block 名映射 + 已有 ParsedLogicDescription 缓存（递归用）。
                // Step1 按"叶子块优先"的顺序跑：先解析没有调用 FB 的 LU，再解析调用了已解析 callee 的 LU。
                // 这样上层 caller 拼 prompt 时能拿到 callee 已有的描述。
                var luToBlockName = new Dictionary<long, string>();
                foreach (var b in blocks)
                {
                    var bId = b.Id;
                    var bName = b.BlockName;
                    if (string.IsNullOrWhiteSpace(bName)) continue;
                    foreach (var lu in logicUnits.Where(x => x.BlockId == bId))
                    {
                        luToBlockName[lu.Id] = bName;
                    }
                }

                // 加载项目下所有 LU 已有的 ParsedLogicDescription，按 block 名聚合摘要给 callee 引用。
                var allLusForCallee = await _logicUnitService.GetListAsync(x =>
                    blockIds.Contains(x.BlockId!.Value) && !x.IsDeleted);
                var calleeBlockDescriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var b in blocks)
                {
                    var bName = b.BlockName;
                    if (string.IsNullOrWhiteSpace(bName)) continue;
                    var blockLus = allLusForCallee.Where(x => x.BlockId == b.Id).ToList();
                    var nonEmpty = blockLus
                        .Where(x => !string.IsNullOrWhiteSpace(x.ParsedLogicDescription))
                        .OrderBy(x => x.NetworkId ?? 0)
                        .ToList();
                    if (nonEmpty.Count > 0)
                    {
                        var sb = new System.Text.StringBuilder();
                        foreach (var nlu in nonEmpty)
                        {
                            sb.AppendLine($"-- Network {nlu.NetworkId} --");
                            sb.AppendLine(nlu.ParsedLogicDescription);
                        }
                        calleeBlockDescriptions[bName] = sb.ToString();
                    }
                }

                // 计算每个 LU 调用的 callee 块名（用于拓扑排序）。
                var luCallees = new Dictionary<long, HashSet<string>>();
                foreach (var lu in logicUnits)
                {
                    var calls = ExtractCalleeBlockNames(CleanXmlContent(lu.RawContent));
                    luCallees[lu.Id] = new HashSet<string>(calls, StringComparer.OrdinalIgnoreCase);
                }

                // 简单拓扑排序：优先跑那些 callees 都已就绪（或调用的块不在本批内）的 LU。
                var pending = new List<PLC_LogicUnitEntity>(logicUnits);
                int safety = pending.Count * 2 + 10;
                while (pending.Count > 0 && safety-- > 0)
                {
                    var ready = pending.Where(lu =>
                    {
                        var calls = luCallees.GetValueOrDefault(lu.Id, new HashSet<string>());
                        return calls.All(c => calleeBlockDescriptions.ContainsKey(c)
                                              || !blocks.Any(b => string.Equals(b.BlockName, c, StringComparison.OrdinalIgnoreCase)));
                    }).ToList();

                    if (ready.Count == 0)
                    {
                        // 有循环依赖或都还未就绪：本批剩下的全部直接跑，不再排序。
                        ready = pending.ToList();
                    }

                    foreach (var lu in ready)
                    {
                        pending.Remove(lu);
                        if (string.IsNullOrEmpty(lu.RawContent)) continue;

                        try
                        {
                            // 直接从已构造的字典取 blockName，避免每个 LU 都查一次 _blockService
                            luToBlockName.TryGetValue(lu.Id, out var blockNameForLu);
                            var description = BuildParsedLogicDescription(lu, blockNameForLu);

                            // 仅更新 ParsedLogicDescription 字段，避免覆盖其它列。
                            var luId = lu.Id;
                            var luDesc = description;
                            var ok = await _logicUnitService.UpdateAsync(
                                u => new PLC_LogicUnitEntity { ParsedLogicDescription = luDesc },
                                u => u.Id == luId);

                            if (!ok)
                            {
                                _logger.LogWarning("[XmlConstraint] LU={LuId} UpdateAsync 返回 false（PLD 未写入）", luId);
                            }

                            // 即时把本 LU 描述加进 callee 缓存，给后续依赖它的 LU 用。
                            if (luToBlockName.TryGetValue(lu.Id, out var bn) && !string.IsNullOrWhiteSpace(bn) && !string.IsNullOrWhiteSpace(description))
                            {
                                if (!calleeBlockDescriptions.TryGetValue(bn, out var existing))
                                {
                                    calleeBlockDescriptions[bn] = $"-- Network {lu.NetworkId} --\n{description}\n";
                                }
                                else
                                {
                                    calleeBlockDescriptions[bn] = existing + $"-- Network {lu.NetworkId} --\n{description}\n";
                                }
                            }

                            processedCount++;
                            // 每 100 个 LU 打一次进度
                            if (processedCount % 100 == 0)
                            {
                                _logger.LogInformation("[XmlConstraint] Step1 进度: {Processed}/{Total}", processedCount, logicUnits.Count);
                            }
                        }
                        catch (Exception ex)
                        {
                            errorCount++;
                            // 把异常打到 Warning（之前是 Debug，看不到）
                            _logger.LogWarning(ex, "[XmlConstraint] Step1 解析 LogicUnit {Id} 失败", lu.Id);
                        }
                    }
                }

                _logger.LogInformation("[XmlConstraint] 解析完成: 成功={Success}, 失败={Error}", processedCount, errorCount);

                return new
                {
                    Success = true,
                    TotalLogicUnits = logicUnits.Count,
                    Processed = processedCount,
                    Errors = errorCount
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[XmlConstraint] 解析程序块代码失败");
                return new { Success = false, Message = ex.Message };
            }
        }

        public async Task<object> ExtractAndSaveLogicExpressionsAsync(long projectId)
        {
            _logger.LogInformation("[XmlConstraint] 开始提取逻辑表达式: ProjectId={ProjectId}", projectId);

            try
            {
                var blocks = await _blockService.GetListAsync(x =>
                    x.ProjectId == projectId && !x.IsDeleted);
                var blockIds = blocks.Select(b => b.Id).ToList();
                var logicUnits = blockIds.Count > 0
                    ? await _logicUnitService.GetListAsync(x =>
                        blockIds.Contains(x.BlockId!.Value) && !string.IsNullOrEmpty(x.RawContent))
                    : new List<PLC_LogicUnitEntity>();

                var expressionCount = 0;
                var sampleExpressions = new List<object>();

                foreach (var lu in logicUnits)
                {
                    var blockName = blocks.FirstOrDefault(x => x.Id == lu.BlockId)?.BlockName;
                    var logicLabel = BuildStableLogicLabel(lu, blockName);
                    var drafts = SiemensFlgNetConstraintExtractor.ExtractLogicExpressionsFromLogicUnit(
                        CleanXmlContent(lu.RawContent),
                        lu.UidSymbolMapJson,
                        blockName,
                        logicLabel);

                    expressionCount += drafts.Count;
                    foreach (var draft in drafts.Take(Math.Max(0, 5 - sampleExpressions.Count)))
                    {
                        sampleExpressions.Add(new
                        {
                            draft.ExpressionName,
                            draft.ExpressionType,
                            draft.TargetSignal,
                            draft.ExpressionText
                        });
                    }
                }

                return new
                {
                    Success = true,
                    ProjectId = projectId,
                    TotalLogicUnits = logicUnits.Count,
                    ExtractedExpressions = expressionCount,
                    SampleExpressions = sampleExpressions
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[XmlConstraint] 提取逻辑表达式失败: ProjectId={ProjectId}", projectId);
                return new { Success = false, Message = ex.Message };
            }
        }

        private string BuildStableLogicLabel(PLC_LogicUnitEntity lu, string blockName)
        {
            if (!string.IsNullOrWhiteSpace(lu?.Title))
            {
                return lu.Title;
            }

            if (!string.IsNullOrWhiteSpace(blockName) && lu?.NetworkId != null)
            {
                return $"{blockName}.Network{lu.NetworkId}";
            }

            if (!string.IsNullOrWhiteSpace(blockName))
            {
                return blockName;
            }

            if (lu?.NetworkId != null)
            {
                return $"Network{lu.NetworkId}";
            }

            return $"LogicUnit{lu?.Id}";
        }

        /// <summary>
        /// Step1：把 RawContent XML + UidSymbolMapJson + IO Tag 字典 喂给 LLM，输出该 Network 的结构化中文描述。
        /// 输出覆盖：业务意图、Q/I 点清单、驱动对应关系、互斥/联锁。约束推理 Phase2 直接读这段描述。
        /// </summary>
        /// <summary>
        /// 同步生成本 LU 的 PLD（树形 Markdown 格式）。
        /// 统一走硬编码 FlgNet 渲染：SiemensFlgNetConstraintExtractor.RenderLogicUnitToMarkdown
        /// 覆盖全部 6 类根类型（FlgNet/Transition/Interlock/Supervision/Actions/StructuredText）。
        ///
        /// 为什么不用 AST 路径：AST 编译器在部分 LU 上简化丢信息（实测 Valve[1] LU 的 IWP 拓扑
        /// 只剩 ~Debug_Test.Debug[19]，丢了 OP030_CYL01_W 和 ByPass_CYL[1]），LLM 无法据此推 Pairing。
        /// 硬编码渲染从原始 FlgNet XML 直接解析 Wire 拓扑，信息完整准确。
        ///
        /// 输出格式：- 锚点 / - 且 (AND) / - 或 (OR) / - 触点(常开/常闭) / - 输入引脚 ← 表达式 / - 输出引脚 → 实际信号
        /// </summary>
        private string BuildParsedLogicDescription(
            PLC_LogicUnitEntity lu,
            string? blockName)
        {
            try
            {
                var cleanContent = CleanXmlContent(lu.RawContent);
                if (string.IsNullOrWhiteSpace(cleanContent))
                {
                    return "[空内容]";
                }

                // 非 XML 内容（STL/SCL 等 PLC 纯文本代码）直接当 PLD 存,跳过 XML 渲染。
                // 走 RenderLogicUnitToMarkdown 会返回「（XML 解析失败）」占位符,对 Phase2 无意义;
                // 把源码原样带块名/Network/语言 header 写进 PLD,让下游 LLM 拿真实代码推理。
                if (cleanContent[0] != '<')
                {
                    return BuildPlainCodePld(lu, blockName, cleanContent);
                }

                var description = SiemensFlgNetConstraintExtractor.RenderLogicUnitToMarkdown(
                    rawContent: cleanContent,
                    blockName: blockName,
                    networkId: lu.NetworkId,
                    networkTitle: lu.Title);

                return string.IsNullOrWhiteSpace(description) ? "[解析空]" : description;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[XmlConstraint] Step1 生成描述失败: LogicUnitId={Id}", lu.Id);
                return $"[解析失败: {ex.Message}]";
            }
        }

        private static string BuildPlainCodePld(PLC_LogicUnitEntity lu, string? blockName, string code)
        {
            var sb = new System.Text.StringBuilder();
            var titleParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(blockName)) titleParts.Add($"**{blockName}**");
            if (lu.NetworkId.HasValue) titleParts.Add($"Network {lu.NetworkId}");
            if (!string.IsNullOrWhiteSpace(lu.Title)) titleParts.Add($"「{lu.Title}」");
            var lang = string.IsNullOrWhiteSpace(lu.ProgrammingLanguage) ? "PLC" : lu.ProgrammingLanguage;
            titleParts.Add($"（{lang} 源码）");
            if (titleParts.Count > 0)
            {
                sb.AppendLine(string.Join(" ", titleParts));
                sb.AppendLine();
            }
            var fenceLang = string.IsNullOrWhiteSpace(lu.ProgrammingLanguage) ? string.Empty : lu.ProgrammingLanguage.ToLowerInvariant();
            sb.AppendLine("```" + fenceLang);
            sb.AppendLine(code);
            sb.AppendLine("```");
            return sb.ToString();
        }

        /// <summary>
        /// 从 FlgNet XML 提取本 Network 调用的所有 FB/FC 块名。XML 中 &lt;Call&gt; 元素的子元素携带 BlockName 属性。
        /// </summary>
        private static List<string> ExtractCalleeBlockNames(string xml)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(xml)) return result;
            try
            {
                // 同时匹配 <Call ...><CallInfo Name="FB3002_Valve" .../></Call> 和 BlockName 属性
                var rx = new Regex(@"(?:BlockName|Name)\s*=\s*""([A-Za-z_][A-Za-z0-9_]*)""",
                    RegexOptions.IgnoreCase | RegexOptions.Compiled);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match m in rx.Matches(xml))
                {
                    var name = m.Groups[1].Value;
                    // 只保留看起来像 FB/FC 块名的（含数字前缀 FB/FC 或全大写约定）
                    if (Regex.IsMatch(name, @"^(FB|FC|OB|DB|UDT)\d+", RegexOptions.IgnoreCase) ||
                        Regex.IsMatch(name, @"^FB\d|^FC\d", RegexOptions.IgnoreCase))
                    {
                        if (seen.Add(name)) result.Add(name);
                    }
                }
            }
            catch
            {
                // 解析失败不抛异常，返回空列表即可
            }
            return result;
        }

        private const string ParsedLogicDescriptionPromptTemplate = @"你是资深西门子博图（TIA Portal）PLC 程序解读专家。任务：根据下面的 FlgNet XML、UId→信号名映射、本 Network 涉及的 IO 标签列表、以及本 Network 调用的中间块（callee）已有描述，输出一段 10 段结构化中文描述，让下游约束推理引擎能直接据此推出 Mutex / AllRequired / Pairing / Safety 等约束。

## 程序块上下文
- Network 编号: {0}
- 标题: {1}
- 注释: {2}
- 编程语言: {3}

## UId→信号名映射表
{4}

## 本 Network 涉及的 IO 标签（按地址分好方向）
{5}

字段说明：
- Direction=Q_Output：物理输出（电磁阀/线圈/指示灯），%Q 地址，本 Network 写入端，是因。
- Direction=I_Input：物理输入（到位/限位/按钮传感器），%I 地址，本 Network 读取端，是果。
- Direction=DB_Internal：DB 内部变量（如 General.E_StopStatus、DB245_OP80Cylinder.Control.xxx），不是 IO 但可能是关键控制信号。
- Direction=M_Merker：M 存储区中间变量。

## 本 Network 调用的中间块（callee）已有描述
{6}

## FlgNet XML 原文
{7}

## ⚠️ 业务先验强制规则（识别到对应场景必须输出，遗漏视为错误）

下面 5 大类场景在 PLC 行业里有固定的物理/逻辑约定，**只要 XML 中出现对应模式，就必须在第 7/9/10 段写出对应约束**：

### 强制规则 A：阀门控制 FB 调用（FB 名含 Valve / Cylinder / Motor / 阀 / 缸 / Pump 等）

只要本 Network 调用了一个阀门控制类 FB（如 FB3002_Valve）并有以下引脚组合：
- 输出引脚 QFwd 绑定到外部 Q 信号 W（如 OP030_YV03_W），输出引脚 QBkd 绑定到外部 Q 信号 H（如 OP030_YV03_H）
  → §9 必须写「同一 FB 实例反向输出互斥：W ↔ H（Mutex/AT_MOST_ONE）」
- 输出引脚 QFwd 绑定到 W，输入引脚 IWP 绑定到 I 信号或 I 信号组
  → §7 必须写「W → 驱动 → IWP 绑定的 I（Pairing/IMPLIES）」
- 输出引脚 QBkd 绑定到 H，输入引脚 IHP 绑定到 I 信号或 I 信号组
  → §7 必须写「H → 驱动 → IHP 绑定的 I（Pairing/IMPLIES）」
- 输入引脚 IWP 绑定到 I 信号 IW，输入引脚 IHP 绑定到 I 信号 IH
  → §9 必须写「同一气缸反向到位互斥：IW ↔ IH（Mutex/AT_MOST_ONE）」

**不允许保守拒绝**：见到这些引脚组合就必须写出对应约束，**不允许**写「物理上互斥但 XML 中未直接体现」「未发现 Q 对 I 的逻辑驱动条件」这类回避话。FB 引脚绑定本身就是 XML 结构证据。

### 强制规则 B：安全 / 急停 / 复位联锁

当 FB 实例的 InputParam 引脚 (E_StopStatus / SafetyStatus / SafeStop 等) 绑定到一个外部信号 X，
且本 Network 有 OutputParam QFwd/QBkd 绑定的 Q 输出 Y：
- 若 X 名含 SafetyDoor / SafeDoor / Door / Guard / LightCurtain / 急停 / E_Stop / EStop
  → §10 必须写「安全联锁：X → Y 必为 false（Safety/REQUIRES_0）」
- 若 X 名含 Reset / 复位
  → §10 必须写「复位联锁：X → Y 必为 false（Safety/REQUIRES_0）」

### 强制规则 C：模式选择互斥

§4 中如果同时出现 2 个或以上模式选择信号（AutoMod / ManualMod / AutoRunMod / DebugMod / SemiAutoMod / Model 等），
→ §9 必须写「模式互斥：所有这些信号互为 Member（Mutex/AT_MOST_ONE）」

### 强制规则 D：多缸同步组

当 §5 中某个引脚 (IWP 或 IHP) 的实参是 AND 串联的多个 I 信号（如 4 个传感器 AND）：
→ §7 必须写「多缸同步：Q 输出 Source 驱动 N 个 I 信号 Member 同步到位（AllRequired/ALL_TRUE）」
→ 同时对每个气缸的 (W, H) 配对生成 Mutex（如果该气缸的 H 信号也出现）

### 强制规则 E：宁多勿少

- 入库会有信号白名单 + operator 校验自动过滤错误约束
- 你的任务是「最大化识别可能的约束」，不要因为「不确定」而拒绝输出
- 看到 FB 引脚绑定就要输出；XML 即使没有显式互斥语句，FB 设计约定就是结构证据

## 输出要求（必须严格遵守）

输出一段**纯中文**结构化描述，按下面 10 段顺序，每段用「### N. 标题」开头。不要输出 JSON、不要 markdown 表格、不要 think 标签、不要推理过程，只输出最终结果。

### 1. 业务意图
一句话或两句话说清这个 Network 在做什么（如「驱动 OP020 工位 Valve[3] 控制 4 个 CYL01 气缸同步前进/后退」「实现 OP070 全线急停联锁」「将自动/手动模式互斥切换」）。

### 2. Q 点清单（物理输出端）
逐个列出本 Network 涉及的 Q 点（Direction=Q_Output），每行：
- 信号名（地址）：物理意义（如「电磁阀前进得电」「电磁阀后退得电」「指示灯绿灯」「蜂鸣器」）
若无写「本 Network 无 Q 点」。

### 3. I 点清单（物理输入端）
逐个列出 I 点（Direction=I_Input），每行：
- 信号名（地址）：物理意义（如「CYL01 第 1 缸前进到位传感器」「急停按钮」「光幕」）
若无写「本 Network 无 I 点」。

### 4. DB 关键变量清单
列出参与逻辑的 DB 内部变量，分类标注：
- 模式选择：General.AutoMod、General.ManualMod、General.AutoRunMod 等
- 安全/急停：General.E_StopStatus 等
- 复位：General.ResetStatus 等
- 许可/使能：FwdPmt、BkdPmt 等
- 中间块状态/命令：DBxxx.cmd.xxx 等
若无写「本 Network 无关键 DB 变量」。

### 5. FB/FC 调用与引脚绑定（最关键的结构证据）
对本 Network 调用的每一个 FB/FC，按以下格式输出：

调用 #1：实例名 = FB 块名
- 输入引脚：
  - PinName1 := 实参表达式（如 IWP := (OP020_CYL01_1_W AND OP020_CYL01_2_W AND OP020_CYL01_3_W AND OP020_CYL01_4_W) — 即 4 个 I 点 AND 串联）
  - PinName2 := 实参表达式
- 输出引脚：
  - PinName3 → 实际信号（如 QFwd → OP020_YV03_W）
  - PinName4 → 实际信号（如 QBkd → OP020_YV03_H）

每个引脚必须明确写出绑定的实际信号；多信号 AND 串联用 (A AND B AND C)，并联用 (A OR B)，常闭用 NOT(X)；纯数字常量直接写出（如 10.0）。
若同一 FB 块被多次调用（多实例），每个实例独立列出，实例名带索引（如 Valve[1] / Valve[2] / Valve[3] / Valve[4]）。
若本 Network 不调用任何 FB/FC，写「本 Network 不含 FB/FC 调用」。

**重要**：写本段时实例形参（如 Valve[12].QFwd、Valve[12].IWP）**仅作为绑定关系标识**出现，不要把它们写成「输出引脚: Valve[12].QFwd」这种孤立形式。
正确写法是「QFwd → OP030_YV12_W」（输出引脚名 → 绑定的实际外部 IO Tag），让下游 Phase2 看到的就是实际信号名 OP030_YV12_W。

### 6. 中间块内部逻辑（callee 内部因果链）
基于「本 Network 调用的中间块（callee）已有描述」段，提取并总结 callee 内部的关键因果链：
- QFwd 在 callee 内部如何被驱动？（典型：QFwd := (FwdCmd OR QFwd_self_hold) AND NOT(QBkd) AND NOT(VentValve) AND E_StopStatus）
- QBkd 类似
- E_StopStatus 在 callee 内部如何参与（常闭联锁）
- 复位条件
若 callee 没有描述或本 Network 不调用 FB，写「本 Network 无中间块内部逻辑可参考」。

### 7. Q→I 驱动对应链（必须按强制规则 A/D 输出）
**强制要求**：每个调用的阀门控制 FB 都至少要在这里写出 2 条 Q→I 链（QFwd→IWP绑定的I、QBkd→IHP绑定的I），格式：
- Q 信号 → 驱动 → I 信号 / I 信号组（说明拓扑：单对单、一对多 AND 同步、一对多 OR 任一）

例如：
- OP030_YV03_W → 驱动 → OP030_CYL03_W （来自 Valve[3].QFwd → Valve[3].IWP 绑定，单对单 Pairing）
- OP020_YV03_W → 驱动 → (OP020_CYL01_1_W AND OP020_CYL01_2_W AND OP020_CYL01_3_W AND OP020_CYL01_4_W)  ← **4 个 I 点 AND 串联到 Valve[3].IWP，是同步到位关系（AllRequired），互相不互斥**

【铁律】：在出现 AND 串联的多个 I 时，必须显式写出「AllRequired，互相不互斥」字样。
【禁止】写「本 Network 无 Q→I 驱动链」，只要 §5 列出了 QFwd/QBkd 引脚绑定就必须写出对应 Pairing。

### 8. I 拓扑结构（串/并/混合/常闭）
独立总结 I 点之间的逻辑结构（即使没有 Q 驱动也要写）：
- 串联（AND）组：列出哪几个 I 串联在一起
- 并联（OR）组：列出哪几个 I 并联
- 常闭（NOT）触点：哪些 I 在串联链中以常闭形式出现
- 串并混合：(A AND B) OR (C AND D) 这种结构
若 I 之间无逻辑关系，写「本 Network 中 I 点之间无串/并逻辑」。

### 9. 互斥关系（必须按强制规则 A/C 输出）
**强制要求**：每个调用的阀门控制 FB 都至少要在这里写出 2 条 Mutex（同 FB 反向 Q 输出 + 同 FB 反向到位反馈）：
- 同一 FB 实例 QFwd/QBkd 反向输出对：W ↔ H 互斥
- 同一 FB 实例 IWP/IHP 反向到位反馈：IW ↔ IH 互斥
- 模式互斥（若 §4 同时出现 2 个或以上模式信号）
- **不同 FB 实例之间的同方向输出不互斥**（如 Valve[1].QFwd 和 Valve[2].QFwd 可以同时为真）

【禁止】写「物理上互斥但 XML 中未直接体现」「本 Network 无互斥关系」（除非确实没有 FB 调用）—— 见到 §5 里 QFwd/QBkd 同 FB 实例的引脚组合就必须写。

### 10. 安全/复位联锁（必须按强制规则 B 输出）
**强制要求**：每个调用的阀门控制 FB 都要在这里写出：
- 安全联锁：若 FB 的 InputParam:E_StopStatus 绑定到外部信号 X (如 General.E_StopStatus) → X → 该 FB 输出的 Q 信号必为 false (Safety/REQUIRES_0)
- 复位联锁：若 FB 的 InputParam:ResetStatus 绑定到 X → X → 该 FB 输出的 Q 信号必为 false
- 模式限制：若 FB 的 InputParam:AutoMod / ManualMod 等绑定到 X → X 与 FwdAuto/BkdAuto 的关系

【禁止】跳过本段；只要 §5 列出了 E_StopStatus 或 ResetStatus 绑定就必须写。

## 严禁

- 严禁仅凭信号名后缀（如 _W/_H、_1W~_4W）推断因果或互斥，必须有 XML Wire 拓扑或 FB 引脚绑定证据（但 FB 引脚组合本身就是充分证据）。
- 严禁把 N 个 I 点 AND 串联到同一引脚的情形说成互斥；这种结构是同步到位（AllRequired）。
- 严禁把不同 FB 实例的同方向输出（如 Valve[1].QFwd 与 Valve[2].QFwd）描述成互斥。
- 严禁猜测 XML 中没有出现的信号或连线。
- 严禁输出 think 标签或推理过程。
- 【独立传感器互斥铁律】§9 输出的 Mutex 只允许在以下结构证据上：(a) 同 FB 实例反向 Q 输出对（QFwd 绑定 ↔ QBkd 绑定）；(b) 同 FB 实例反向 I 反馈对（IWP 绑定 ↔ IHP 绑定，单对）；(c) 同一组模式选择信号（AutoMod/ManualMod/AutoRunMod 等 KeyFbPin 末段命中）。**严禁把 3 个或以上独立 I 输入传感器（不论它们的命名前后缀如何）放在同一条 Mutex 里**——物理上多个独立传感器允许同时为 true，互斥语义不成立。
- 【FB 形参替换铁律】不允许把 Valve[N].QFwd / Valve[N].IWP 等 FB 实例形参写进 §7 §9 §10 的约束信号名里，必须用第 5 段引脚绑定表替换为实际 IO Tag（如 OP030_YV12_W）。如果引脚未绑定外部 IO Tag（开路），该约束跳过。

**不要做任何字数限制**：完整覆盖本 Network 涉及的全部 FB 调用、引脚绑定、Q/I 信号、Wire 拓扑。结构证据不齐全时 Phase2 会推错约束，绝不允许为了精简而省略证据。

直接开始输出 10 段中文描述。";

        private async Task<string> CallParsedLogicDescriptionAIAsync(Microsoft.Extensions.AI.IChatClient chatClient, string prompt)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(AI_TIMEOUT_SECONDS));
                var messages = new List<Microsoft.Extensions.AI.ChatMessage>
                {
                    new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System,
                        "你是西门子博图 PLC 程序解读专家，专长是把 FlgNet XML 转译成结构化的中文程序意图描述，只输出最终结果，不输出推理过程。"),
                    new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, prompt)
                };
                var response = await chatClient.GetResponseAsync(messages, cancellationToken: cts.Token);
                return response?.Text?.Trim() ?? string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[XmlConstraint] Step1 AI 调用失败");
                return $"[AI 调用失败: {ex.Message}]";
            }
        }

        /// <summary>
        /// 将 RawContent XML 使用 AI 转换为简洁表达式
        /// </summary>
        private async Task<string> ConvertToCompactExpressionAsync(long projectId, PLC_LogicUnitEntity lu)
        {
            try
            {
                // 清理 XML 内容
                var cleanContent = CleanXmlContent(lu.RawContent);

                if (string.IsNullOrWhiteSpace(cleanContent))
                {
                    return "[空内容]";
                }

                // 解析 UidSymbolMapJson
                var uidMap = new Dictionary<string, string>();
                if (!string.IsNullOrEmpty(lu.UidSymbolMapJson))
                {
                    try
                    {
                        uidMap = JsonSerializer.Deserialize<Dictionary<string, string>>(lu.UidSymbolMapJson)
                            ?? new Dictionary<string, string>();
                    }
                    catch { }
                }

                // 获取 AI ChatClient
                var chatClient = await GetChatClientAsync();
                if (chatClient == null)
                {
                    return "[无可用 AI 模型]";
                }

                // 构建 Prompt
                var prompt = BuildXmlToExpressionPrompt(cleanContent, uidMap, lu);

                // 调用 AI
                var response = await CallAIAsync(chatClient, prompt);

                return string.IsNullOrWhiteSpace(response) ? "[AI 无返回]" : response;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[XmlConstraint] AI 转换表达式失败: LogicUnitId={Id}", lu.Id);
                return $"[解析失败: {ex.Message}]";
            }
        }

        /// <summary>
        /// 构建 XML 转表达式的 Prompt
        /// </summary>
        private string BuildXmlToExpressionPrompt(string rawContent, Dictionary<string, string> uidMap, PLC_LogicUnitEntity lu)
        {
            var uidMapStr = uidMap.Count > 0
                ? JsonSerializer.Serialize(uidMap)
                : "{}";

            return $@"你是一个专业的 PLC 程序解析专家。请将下面的 TIA Portal XML 代码转换为简洁的逻辑表达式。

## 程序块信息
- Network编号: {lu.NetworkId}
- 标题: {lu.Title ?? "无"}
- 注释: {lu.Comment ?? "无"}
- 编程语言: {lu.ProgrammingLanguage ?? "未知"}

## UID 到信号名映射（用于替换 XML 中的 UId）
{uidMapStr}

## XML 程序代码
{rawContent}

## 重要说明 - 三种格式解析规则

### 1. FlgNet 格式（函数调用块）
- 包含 <Call> 标签：表示调用 FB/FC 函数块
- 包含 <Access Scope=""GlobalVariable"">：表示访问全局变量（IO信号）
- 解析规则：
  - 找出所有被访问的全局变量（GlobalVariable）
  - 如果是输入参数(General等)，表示该变量作为条件
  - 输出格式：条件变量 => FB调用

### 2. StructuredText 格式（SCL/ST 语言）
- 包含 <StructuredText> 标签
- 包含 <Access Scope=""LocalVariable"">：访问局部变量
- 包含 <Access Scope=""Call"">：调用指令（如 MC_Instruction）
- 解析规则：
  - 提取变量赋值语句
  - 提取函数调用及参数
  - 输出格式：变量 := 值 或 指令(参数)

### 3. Transition 格式（LAD 梯形图）
- 包含 <Transition> 标签，外层有 <FlgNet>
- <Part Name=""Contact"">：触点（条件）
- <Part Name=""Contact""><Negated>：常闭触点
- <Part Name=""TrCoil"">：转换线圈（输出）
- <Part Name=""Coil"">：输出线圈
- <Part Name=""O"">：并联分支
- <Access Scope=""GlobalVariable"">：全局变量引用
- <Wire><IdentCon>：连接到变量
- <Wire><NameCon>：连接到触点/线圈
- 解析规则：
  - 从 <Wires> 中追踪信号流向
  - <Negated Name=""operand""/> 表示常闭触点（取反）
  - 多个触点串联 = AND，并联 = OR
  - 输出格式：触点条件 => 线圈输出

## 输出要求
1. 替换所有 UId 为信号名
2. 多语句每行输出一个
3. 只输出表达式，不要解释

示例：
DB.Sensor[1] := IO1 OR DB.Cmd.Bypass[1]

请直接输出表达式：";
        }

        /// <summary>
        /// 获取 AI ChatClient
        /// </summary>
        private async Task<Microsoft.Extensions.AI.IChatClient> GetChatClientAsync()
        {
            try
            {
                var model = await _llmModelService.RecommendModelAsync();
                if (model == null)
                {
                    _logger.LogWarning("[XmlConstraint] 无可用的 LLM 模型");
                    return null;
                }

                var openAiClient = await _llmModelService.CreateChatClientByModelIdAsync(model.Id);
                return openAiClient.AsIChatClient();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[XmlConstraint] 获取 ChatClient 失败");
                return null;
            }
        }

        /// <summary>
        /// 调用 AI
        /// </summary>
        private async Task<string> CallAIAsync(Microsoft.Extensions.AI.IChatClient chatClient, string prompt)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(AI_TIMEOUT_SECONDS));
                var messages = new List<Microsoft.Extensions.AI.ChatMessage>
                {
                    new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System, "你是一个专业的 PLC 梯形图解析专家。"),
                    new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, prompt)
                };
                var response = await chatClient.GetResponseAsync(messages, cancellationToken: cts.Token);
                return response?.Text?.Trim() ?? "";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[XmlConstraint] AI 调用失败");
                return $"[AI 调用失败: {ex.Message}]";
            }
        }

        /// <summary>
        /// 清理 XML 内容，处理 BOM 头等特殊字符
        /// </summary>
        private string CleanXmlContent(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return content;

            // 移除 BOM 头 (UTF-8 BOM: 0xEF 0xBB 0xBF)
            if (content.Length > 0 && content[0] == '\uFEFF')
            {
                content = content.Substring(1);
            }

            // 移除其他不可见的控制字符（保留换行、制表、空格）
            content = new string(content.Where(c => !char.IsControl(c) || c == '\n' || c == '\r' || c == '\t').ToArray());

            // 尝试自动检测编码
            try
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(content);
                content = System.Text.Encoding.UTF8.GetString(bytes);
            }
            catch { }

            return content.Trim();
        }

        /// <summary>
        /// 解析信号名（从 UID 或直接名称）
        /// </summary>
        private string ResolveSignalName(string variable, Dictionary<string, string> uidMap)
        {
            if (string.IsNullOrEmpty(variable)) return null;

            // 如果是 UID 格式，尝试从映射中获取
            if (uidMap.TryGetValue(variable, out var signalName))
            {
                return signalName;
            }

            // 直接返回
            return variable;
        }

        /// <summary>
        /// 从 XML 提取约束表达式（保留 JSON 格式）- 保留原有方法
        /// </summary>
        public async Task<string> ExtractConstraintsAsync(long projectId, string signalName = null)
        {
            _logger.LogInformation("[XmlConstraint] 开始提取约束: ProjectId={ProjectId}, Signal={Signal}",
                projectId, signalName ?? "全部");

            try
            {
                // 1. 获取所有 Bool IO Tag
                var allTags = await _ioTagService.GetListAsync(x =>
                    x.ProjectId == projectId && !x.IsDeleted && x.DataType == "Bool");

                var ioTagDict = allTags.ToDictionary(t => t.TagName, t => t);

                // 2. 获取项目下的所有 Block，再获取关联的 LogicUnit
                var blocks = await _blockService.GetListAsync(x =>
                    x.ProjectId == projectId && !x.IsDeleted);

                var blockIds = blocks.Select(b => b.Id).ToList();

                var logicUnits = blockIds.Count > 0
                    ? await _logicUnitService.GetListAsync(x =>
                        blockIds.Contains(x.BlockId!.Value) && !string.IsNullOrEmpty(x.RawContent))
                    : new List<PLC_LogicUnitEntity>();

                _logger.LogInformation("[XmlConstraint] 找到 {Count} 个 LogicUnit", logicUnits.Count);

                // 3. 解析每个 LogicUnit 的 XML，提取约束
                var allConstraints = new List<ExtractedConstraint>();

                foreach (var lu in logicUnits)
                {
                    if (string.IsNullOrEmpty(lu.RawContent)) continue;

                    try
                    {
                        var constraints = ParseXmlConstraints(lu, ioTagDict);
                        allConstraints.AddRange(constraints);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "[XmlConstraint] 解析 LogicUnit {Id} 失败", lu.Id);
                    }
                }

                // 4. 去重并合并约束
                var mergedConstraints = MergeConstraints(allConstraints);

                // 5. 转换为 JSON 格式
                var json = JsonHelper.SerializeObject(mergedConstraints);

                _logger.LogInformation("[XmlConstraint] 提取完成: 共 {Count} 条约束", mergedConstraints.Count);

                return json;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[XmlConstraint] 提取约束失败");
                return "[]";
            }
        }

        /// <summary>
        /// 从 XML 提取单个信号的约束
        /// </summary>
        public Task<string> ExtractSignalConstraintsAsync(long projectId, string signalName)
        {
            return ExtractSignalConstraintsInternalAsync(projectId, signalName, null, null);
        }

        public Task<string> ExtractSignalConstraintsAsync(
            long projectId,
            string signalName,
            long? callerLogicUnitId,
            string stationCode = null)
        {
            return ExtractSignalConstraintsInternalAsync(projectId, signalName, callerLogicUnitId, stationCode);
        }

        private async Task<string> ExtractSignalConstraintsInternalAsync(
            long projectId,
            string signalName,
            long? callerLogicUnitId,
            string stationCode)
        {
            if (string.IsNullOrWhiteSpace(signalName))
            {
                return "[]";
            }

            try
            {
                var normalizedSignalName = signalName.Trim();
                var allTags = await _ioTagService.GetListAsync(x =>
                    x.ProjectId == projectId && !x.IsDeleted && x.DataType == "Bool");
                var ioTagDict = allTags
                    .GroupBy(x => x.TagName, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                var allConstraints = new List<ExtractedConstraint>();

                // 1. 先从已入库规则中反查，确保中间块/公共块内部信号也能直接命中
                var persistedConstraints = await LoadPersistedConstraintsBySignalAsync(projectId, normalizedSignalName);
                allConstraints.AddRange(persistedConstraints);

                // 2. 再从 VariableAccess/LogicUnit 反查原始逻辑，作为补充
                var logicUnits = await FindRelevantLogicUnitsAsync(projectId, normalizedSignalName);
                foreach (var lu in logicUnits)
                {
                    if (string.IsNullOrEmpty(lu.RawContent))
                    {
                        continue;
                    }

                    try
                    {
                        var constraints = ParseXmlConstraints(lu, ioTagDict, normalizedSignalName);
                        allConstraints.AddRange(constraints);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex,
                            "[XmlConstraint] 按信号提取约束时解析 LogicUnit 失败: ProjectId={ProjectId}, LogicUnitId={LogicUnitId}, Signal={SignalName}",
                            projectId, lu.Id, normalizedSignalName);
                    }
                }

                // 3. 如果查询的是公共块内部信号，再沿 FormalPin -> ActualSignal 映射补一层实例侧约束
                var allowShortPinFallback = callerLogicUnitId.HasValue || persistedConstraints.Count == 0;
                var aliasSignals = await ResolveCallBindingAliasSignalsAsync(
                    projectId,
                    normalizedSignalName,
                    logicUnits,
                    allowShortPinFallback,
                    callerLogicUnitId,
                    stationCode);
                foreach (var aliasSignal in aliasSignals)
                {
                    allConstraints.AddRange(await LoadPersistedConstraintsBySignalAsync(projectId, aliasSignal));
                }

                return JsonHelper.SerializeObject(DeduplicateConstraints(allConstraints));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[XmlConstraint] 提取单个信号约束失败: ProjectId={ProjectId}, Signal={SignalName}",
                    projectId, signalName);
                return "[]";
            }
        }

        private async Task<List<ExtractedConstraint>> LoadPersistedConstraintsBySignalAsync(long projectId, string signalName)
        {
            var matchedSignals = await _constraintSignalService.GetListAsync(x =>
                x.SignalName == signalName && !x.IsDeleted);

            if (matchedSignals.Count == 0)
            {
                return new List<ExtractedConstraint>();
            }

            var ruleIds = matchedSignals.Select(x => x.RuleId).Distinct().ToList();
            var rules = ruleIds.Count > 0
                ? await _constraintRuleService.GetListAsync(x =>
                    x.ProjectId == projectId &&
                    ruleIds.Contains(x.Id) &&
                    !x.IsDeleted)
                : new List<PLC_IOConstraintRuleEntity>();

            if (rules.Count == 0)
            {
                return new List<ExtractedConstraint>();
            }

            var allSignals = await _constraintSignalService.GetListAsync(x =>
                ruleIds.Contains(x.RuleId) && !x.IsDeleted);

            var signalsByRule = allSignals
                .GroupBy(x => x.RuleId)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderBy(x => x.SortOrder ?? int.MaxValue)
                          .ThenBy(x => x.SignalName, StringComparer.OrdinalIgnoreCase)
                          .ToList());

            return rules
                .Where(x => signalsByRule.ContainsKey(x.Id))
                .Select(rule => new ExtractedConstraint
                {
                    RuleName = rule.RuleName,
                    RuleType = rule.RuleType,
                    Operator = rule.Operator,
                    EquipmentId = rule.EquipmentId,
                    Confidence = Convert.ToDouble(rule.Confidence ?? 0m),
                    Description = rule.Description,
                    Priority = rule.Priority,
                    Signals = signalsByRule[rule.Id]
                        .Select(x => new ConstraintSignal
                        {
                            SignalName = x.SignalName,
                            SignalRole = x.SignalRole
                        })
                        .ToList()
                })
                .ToList();
        }

        private async Task<List<PLC_LogicUnitEntity>> FindRelevantLogicUnitsAsync(long projectId, string signalName)
        {
            var accesses = await _variableAccessService.GetListAsync(x =>
                x.VariableName == signalName && !x.IsDeleted);

            var luIds = accesses
                .Where(x => x.LogicUnitId.HasValue)
                .Select(x => x.LogicUnitId!.Value)
                .Distinct()
                .ToList();

            var logicUnits = luIds.Count > 0
                ? await _logicUnitService.GetListAsync(x => luIds.Contains(x.Id) && !x.IsDeleted)
                : new List<PLC_LogicUnitEntity>();

            if (logicUnits.Count > 0)
            {
                return logicUnits;
            }

            var blocks = await _blockService.GetListAsync(x =>
                x.ProjectId == projectId && !x.IsDeleted);
            var blockIds = blocks.Select(x => x.Id).ToList();
            if (blockIds.Count == 0)
            {
                return new List<PLC_LogicUnitEntity>();
            }

            return await _logicUnitService.GetListAsync(x =>
                x.BlockId.HasValue &&
                blockIds.Contains(x.BlockId.Value) &&
                !x.IsDeleted &&
                (
                    (!string.IsNullOrEmpty(x.RawContent) && x.RawContent.Contains(signalName)) ||
                    (!string.IsNullOrEmpty(x.UidSymbolMapJson) && x.UidSymbolMapJson.Contains(signalName)) ||
                    (!string.IsNullOrEmpty(x.Compact_logic_xml) && x.Compact_logic_xml.Contains(signalName))
                ));
        }

        private async Task<List<string>> ResolveCallBindingAliasSignalsAsync(
            long projectId,
            string signalName,
            List<PLC_LogicUnitEntity> relatedLogicUnits,
            bool allowShortPinFallback,
            long? callerLogicUnitId,
            string stationCode)
        {
            if (relatedLogicUnits == null || relatedLogicUnits.Count == 0)
            {
                return new List<string>();
            }

            var pinCandidates = BuildFormalPinCandidates(signalName, allowShortPinFallback);
            if (pinCandidates.Count == 0)
            {
                return new List<string>();
            }

            var blockIds = relatedLogicUnits
                .Where(x => x.BlockId.HasValue)
                .Select(x => x.BlockId!.Value)
                .Distinct()
                .ToList();

            if (blockIds.Count == 0)
            {
                return new List<string>();
            }

            var blocks = await _blockService.GetListAsync(x =>
                blockIds.Contains(x.Id) && !x.IsDeleted);
            var calleeNames = blocks
                .Select(x => x.BlockName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (calleeNames.Count == 0)
            {
                return new List<string>();
            }

            var projectBlocks = await _blockService.GetListAsync(x =>
                x.ProjectId == projectId && !x.IsDeleted);
            var projectBlockIds = projectBlocks.Select(x => x.Id).ToList();
            if (projectBlockIds.Count == 0)
            {
                return new List<string>();
            }

            var callerLogicUnits = await _logicUnitService.GetListAsync(x =>
                x.BlockId.HasValue &&
                projectBlockIds.Contains(x.BlockId.Value) &&
                !x.IsDeleted &&
                !string.IsNullOrEmpty(x.CallPinBindingsJson));
            var normalizedStationCode = NormalizeStationCode(stationCode);

            var candidateSets = new List<List<PLC_LogicUnitEntity>>();
            if (callerLogicUnitId.HasValue)
            {
                var exactCaller = callerLogicUnits
                    .Where(x => x.Id == callerLogicUnitId.Value)
                    .ToList();
                if (exactCaller.Count > 0)
                {
                    candidateSets.Add(exactCaller);
                }
            }

            candidateSets.Add(callerLogicUnits);

            foreach (var candidateLogicUnits in candidateSets)
            {
                var aliases = CollectAliasSignals(
                    candidateLogicUnits,
                    pinCandidates,
                    calleeNames,
                    normalizedStationCode,
                    requireCalleeMatch: true,
                    requireStationMatch: !string.IsNullOrWhiteSpace(normalizedStationCode));
                if (aliases.Count > 0)
                {
                    aliases.RemoveWhere(x => x.Equals(signalName, StringComparison.OrdinalIgnoreCase));
                    return aliases.ToList();
                }

                aliases = CollectAliasSignals(
                    candidateLogicUnits,
                    pinCandidates,
                    calleeNames,
                    normalizedStationCode,
                    requireCalleeMatch: false,
                    requireStationMatch: !string.IsNullOrWhiteSpace(normalizedStationCode));
                if (aliases.Count > 0)
                {
                    aliases.RemoveWhere(x => x.Equals(signalName, StringComparison.OrdinalIgnoreCase));
                    return aliases.ToList();
                }

                aliases = CollectAliasSignals(
                    candidateLogicUnits,
                    pinCandidates,
                    calleeNames,
                    normalizedStationCode,
                    requireCalleeMatch: false,
                    requireStationMatch: false);
                if (aliases.Count > 0)
                {
                    aliases.RemoveWhere(x => x.Equals(signalName, StringComparison.OrdinalIgnoreCase));
                    return aliases.ToList();
                }
            }

            return new List<string>();
        }

        private static HashSet<string> CollectAliasSignals(
            List<PLC_LogicUnitEntity> logicUnits,
            HashSet<string> pinCandidates,
            HashSet<string> calleeNames,
            string normalizedStationCode,
            bool requireCalleeMatch,
            bool requireStationMatch)
        {
            var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (logicUnits == null || logicUnits.Count == 0 || pinCandidates == null || pinCandidates.Count == 0)
            {
                return aliases;
            }

            foreach (var logicUnit in logicUnits)
            {
                foreach (var binding in ParseCallPinBindingRecords(logicUnit.CallPinBindingsJson))
                {
                    if (requireCalleeMatch &&
                        calleeNames != null &&
                        calleeNames.Count > 0 &&
                        !calleeNames.Contains(binding.CalleeName))
                    {
                        continue;
                    }

                    var bindingStationCode = InferBindingStationCode(binding);
                    if (requireStationMatch &&
                        !string.Equals(bindingStationCode, normalizedStationCode, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    foreach (var pinName in pinCandidates)
                    {
                        if (binding.InputPins.TryGetValue(pinName, out var inputActual) && !string.IsNullOrWhiteSpace(inputActual))
                        {
                            aliases.Add(inputActual);
                        }

                        if (binding.OutputPins.TryGetValue(pinName, out var outputActual) && !string.IsNullOrWhiteSpace(outputActual))
                        {
                            aliases.Add(outputActual);
                        }

                        if (binding.InOutPins.TryGetValue(pinName, out var inOutActual) && !string.IsNullOrWhiteSpace(inOutActual))
                        {
                            aliases.Add(inOutActual);
                        }
                    }
                }
            }

            return aliases;
        }

        private static string NormalizeStationCode(string stationCode)
        {
            return string.IsNullOrWhiteSpace(stationCode)
                ? null
                : stationCode.Trim().ToUpperInvariant();
        }

        private static string InferBindingStationCode(CallPinBindingRecord binding)
        {
            var candidates = binding.InputPins.Values
                .Concat(binding.OutputPins.Values)
                .Concat(binding.InOutPins.Values)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            foreach (var candidate in candidates)
            {
                var station = ExtractStationCode(candidate);
                if (!string.IsNullOrWhiteSpace(station))
                {
                    return station;
                }
            }

            return null;
        }

        private static string ExtractStationCode(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var match = Regex.Match(text, @"\bOP\d{3}\b", RegexOptions.IgnoreCase);
            return match.Success ? match.Value.ToUpperInvariant() : null;
        }

        private static HashSet<string> BuildFormalPinCandidates(string signalName, bool allowShortPinFallback)
        {
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(signalName))
            {
                return candidates;
            }

            candidates.Add(signalName.Trim());

            var dotIndex = signalName.LastIndexOf('.');
            if (allowShortPinFallback && dotIndex >= 0 && dotIndex < signalName.Length - 1)
            {
                candidates.Add(signalName[(dotIndex + 1)..]);
            }

            return candidates;
        }

        private static List<CallPinBindingRecord> ParseCallPinBindingRecords(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<CallPinBindingRecord>();
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return new List<CallPinBindingRecord>();
                }

                var results = new List<CallPinBindingRecord>();
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    results.Add(new CallPinBindingRecord
                    {
                        CalleeName = item.TryGetProperty("callee_name", out var calleeName)
                            ? calleeName.GetString()
                            : null,
                        InputPins = ReadPinMap(item, "input_pins"),
                        OutputPins = ReadPinMap(item, "output_pins"),
                        InOutPins = ReadPinMap(item, "inout_pins")
                    });
                }

                return results;
            }
            catch
            {
                return new List<CallPinBindingRecord>();
            }
        }

        private static Dictionary<string, string> ReadPinMap(JsonElement item, string propertyName)
        {
            if (!item.TryGetProperty(propertyName, out var pinElement) ||
                pinElement.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in pinElement.EnumerateObject())
            {
                var value = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : property.Value.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    map[property.Name] = value;
                }
            }

            return map;
        }

        /// <summary>
        /// 从 XML 提取约束并保存到数据库（调用 SaveIOConstraintsAsync）
        /// <para>启发式 XML 解析准确性有限，已由 SiemensFlgNetConstraintExtractor + IOConstraintPreprocessService 取代。当前仅保留兼容，请勿新增调用。</para>
        /// </summary>
        /// <param name="projectId">项目ID</param>
        /// <param name="signalName">信号名称（可选，为空则分析所有信号）</param>
        /// <returns>保存结果</returns>
        [Obsolete("启发式解析已由 SiemensFlgNetConstraintExtractor + IOConstraintPreprocessService 取代")]
        public async Task<object> ExtractAndSaveConstraintsAsync(long projectId, string signalName = null)
        {
            _logger.LogInformation("[XmlConstraint] 提取并保存约束: ProjectId={ProjectId}, Signal={Signal}",
                projectId, signalName ?? "全部");

            try
            {
                // 1. 提取约束 JSON
                var constraintsJson = await ExtractConstraintsAsync(projectId, signalName);

                if (string.IsNullOrWhiteSpace(constraintsJson) || constraintsJson == "[]")
                {
                    return new
                    {
                        Success = true,
                        Message = "未提取到任何约束规则",
                        Inserted = 0,   
                        Skipped = 0
                    };
                }

                // 2. 调用 SaveIOConstraintsAsync 保存到数据库
                var saveResult = await _diagnosisService.SaveIOConstraintsAsync(projectId, constraintsJson);

                _logger.LogInformation("[XmlConstraint] 保存完成: ProjectId={ProjectId}", projectId);

                return saveResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[XmlConstraint] 提取并保存约束失败: ProjectId={ProjectId}", projectId);
                return new
                {
                    Success = false,
                    Message = $"提取并保存约束失败: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// 将约束规则转换为 C# 表达式（用于实时校验信号约束关系）
        /// </summary>
        /// <param name="projectId">项目ID</param>
        /// <param name="signalName">信号名称（可选，为空则获取所有约束）</param>
        /// <returns>C# 表达式列表</returns>
        public async Task<string> ConvertConstraintsToExpressionsAsync(long projectId, string signalName = null)
        {
            _logger.LogInformation("[XmlConstraint] 转换约束为C#表达式: ProjectId={ProjectId}, Signal={Signal}",
                projectId, signalName ?? "全部");

            try
            {
                // 1. 获取约束规则
                var rules = await _constraintRuleService.GetListAsync(x =>
                    x.ProjectId == projectId && !x.IsDeleted);

                if (signalName != null)
                {
                    var ruleIds = (await _constraintSignalService.GetListAsync(x =>
                        x.SignalName == signalName && !x.IsDeleted))
                        .Select(x => x.RuleId).Distinct();

                    rules = rules.Where(r => ruleIds.Contains(r.Id)).ToList();  
                }

                if (rules.Count == 0)
                {
                    return "[]";
                }

                // 2. 批量获取规则的信号
                var ruleIdsList = rules.Select(r => r.Id).ToList();
                var allSignals = await _constraintSignalService.GetListAsync(x =>
                    ruleIdsList.Contains(x.RuleId) && !x.IsDeleted);

                var signalsByRule = allSignals.GroupBy(s => s.RuleId)
                    .ToDictionary(g => g.Key, g => g.OrderBy(s => s.SortOrder ?? 0).ToList());

                // 3. 转换为 C# 表达式
                var expressions = new List<ConstraintExpression>();

                foreach (var rule in rules)
                {
                    if (!signalsByRule.TryGetValue(rule.Id, out var ruleSignals) || ruleSignals.Count == 0)
                        continue;

                    var expression = ConvertRuleToExpression(rule, ruleSignals);
                    expressions.Add(expression);
                }

                return JsonHelper.SerializeObject(expressions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[XmlConstraint] 转换约束为C#表达式失败");
                return "[]";
            }
        }

        /// <summary>
        /// 将单条约束规则转换为 C# 表达式
        /// </summary>
        private static ConstraintExpression ConvertRuleToExpression(PLC_IOConstraintRuleEntity rule, List<PLC_IOConstraintSignalEntity> signals)
        {
            var expression = new ConstraintExpression
            {
                RuleId = rule.Id,
                RuleName = rule.RuleName,
                RuleType = rule.RuleType,
                Operator = rule.Operator,
                Priority = rule.Priority,
                Description = rule.Description
            };

            var signalNames = signals.Select(s => s.SignalName).ToList();
            var sourceSignals = signals.Where(s => s.SignalRole == "Source").Select(s => s.SignalName).ToList();
            var targetSignals = signals.Where(s => s.SignalRole == "Target").Select(s => s.SignalName).ToList();
            var memberSignals = signals.Where(s => s.SignalRole == "Member").Select(s => s.SignalName).ToList();

            // 根据 Operator 转换为 C# 表达式
            expression.CSharpExpression = rule.Operator switch
            {
                // AT_MOST_ONE: 最多1个为true（互斥）
                "AT_MOST_ONE" => $"// {rule.RuleName}\n// 约束: {rule.Description}\nvar {rule.RuleName.Replace(" ", "_")}_Count = {string.Join(" + ", signalNames.Select(s => $"(bool?)(values[\"{s}\"] ?? false) == true ? 1 : 0"))};\nAssert({rule.RuleName.Replace(" ", "_")}_Count <= 1, \"{rule.RuleName}: 最多1个信号为true\");",

                // EXACTLY_ONE: 恰好1个为true
                "EXACTLY_ONE" => $"// {rule.RuleName}\n// 约束: {rule.Description}\nvar {rule.RuleName.Replace(" ", "_")}_Count = {string.Join(" + ", signalNames.Select(s => $"(bool?)(values[\"{s}\"] ?? false) == true ? 1 : 0"))};\nAssert({rule.RuleName.Replace(" ", "_")}_Count == 1, \"{rule.RuleName}: 恰好1个信号为true\");",

                // ALL_TRUE: 全部为true
                "ALL_TRUE" => $"// {rule.RuleName}\n// 约束: {rule.Description}\nAssert({string.Join(" && ", signalNames.Select(s => $"(bool?)(values[\"{s}\"] ?? false) == true"))}, \"{rule.RuleName}: 所有信号必须为true\");",

                // IMPLIES: 蕴含关系 (Source => Target)
                "IMPLIES" when sourceSignals.Count > 0 && targetSignals.Count > 0 =>
                    $"// {rule.RuleName}\n// 约束: {rule.Description}\nif ({string.Join(" || ", sourceSignals.Select(s => $"(bool?)(values[\"{s}\"] ?? false) == true"))}) {{\n    Assert({string.Join(" || ", targetSignals.Select(s => $"(bool?)(values[\"{s}\"] ?? false) == true"))}, \"{rule.RuleName}: Source为true时，Target必须为true\");\n}}",

                // REQUIRES_0: Source为false时，Target必须为false
                "REQUIRES_0" when sourceSignals.Count > 0 && targetSignals.Count > 0 =>
                    $"// {rule.RuleName}\n// 约束: {rule.Description}\nif ({string.Join(" || ", sourceSignals.Select(s => $"(bool?)(values[\"{s}\"] ?? false) == false"))}) {{\n    Assert(!({string.Join(" || ", targetSignals.Select(s => $"(bool?)(values[\"{s}\"] ?? false) == true"))}), \"{rule.RuleName}: Source为false时，Target必须为false\");\n}}",

                // CHAIN_ORDER: 时序链
                "CHAIN_ORDER" =>
                    $"// {rule.RuleName}\n// 约束: {rule.Description}\n// 时序链校验: {string.Join(" -> ", signalNames)}",

                // VALUE_RANGE: 数值范围
                "VALUE_RANGE" =>
                    $"// {rule.RuleName}\n// 约束: {rule.Description}\n// 范围校验: 请根据具体信号值范围实现",

                // CUSTOM: 自定义
                "CUSTOM" =>
                    $"// {rule.RuleName}\n// 约束: {rule.Description}\n// 自定义规则: {rule.ValidStates}",

                // 默认
                _ => $"// {rule.RuleName}: {rule.Description}"
            };

            // 生成简化的布尔表达式（用于快速判断）
            expression.BooleanExpression = rule.Operator switch
            {
                "AT_MOST_ONE" => $"({string.Join(" + ", signalNames.Select(s => $"({s}?1:0"))}) <= 1",
                "EXACTLY_ONE" => $"({string.Join(" + ", signalNames.Select(s => $"({s}?1:0"))}) == 1",
                "ALL_TRUE" => string.Join(" && ", signalNames.Select(s => s)),
                "IMPLIES" when sourceSignals.Count > 0 && targetSignals.Count > 0 =>
                    $"(!({string.Join(" || ", sourceSignals.Select(s => s))}) || ({string.Join(" || ", targetSignals.Select(s => s))}))",
                "REQUIRES_0" when sourceSignals.Count > 0 && targetSignals.Count > 0 =>
                    $"(!({string.Join(" || ", sourceSignals.Select(s => s))}) || !({string.Join(" || ", targetSignals.Select(s => s))}))",
                _ => null
            };

            // 生成 Func 委托（可直接调用）
            expression.FuncSignature = rule.Operator switch
            {
                "AT_MOST_ONE" => $"Func<Dictionary<string, bool?>, bool> {rule.RuleName.Replace(" ", "_")} = (values) => {signalNames.Select(s => $"(values[\"{s}\"] ?? false)").Aggregate((a, b) => $"({a} ? 1 : 0) + ({b} ? 1 : 0)")} <= 1;",
                "EXACTLY_ONE" => $"Func<Dictionary<string, bool?>, bool> {rule.RuleName.Replace(" ", "_")} = (values) => {signalNames.Select(s => $"(values[\"{s}\"] ?? false)").Aggregate((a, b) => $"({a} ? 1 : 0) + ({b} ? 1 : 0)")} == 1;",
                "ALL_TRUE" => $"Func<Dictionary<string, bool?>, bool> {rule.RuleName.Replace(" ", "_")} = (values) => {string.Join(" && ", signalNames.Select(s => $"(values[\"{s}\"] ?? false)"))};",
                _ => $"// {rule.RuleName}: 自定义校验逻辑"
            };

            return expression;
        }

        #endregion

        #region XML 解析核心

        /// <summary>
        /// 解析单个 LogicUnit 的 XML，提取约束
        /// <para>启发式规则（基于 Attribute 而非 Wire 拓扑），准确性有限。结构化解析请使用 SiemensFlgNetConstraintExtractor。</para>
        /// </summary>
        private List<ExtractedConstraint> ParseXmlConstraints(
            PLC_LogicUnitEntity lu,
            Dictionary<string, PLC_IOTagEntity> ioTagDict,
            string targetSignal = null)
        {
            var constraints = new List<ExtractedConstraint>();

            try
            {
                var doc = XDocument.Parse(lu.RawContent);

                // 提取所有线圈（输出）
                var coils = ExtractCoils(doc);
                // 提取所有触点（输入）
                var contacts = ExtractContacts(doc);
                // 提取比较器
                var comparators = ExtractComparators(doc);
                // 提取定时器/计数器
                var timersCounters = ExtractTimersCounters(doc);
                // 提取 Wire 连接关系
                var wireConnections = ExtractWireConnections(doc);

                // 1. 检测互斥约束 (Mutex)
                constraints.AddRange(DetectMutexConstraints(coils, ioTagDict, lu));

                // 2. 检测唯一选择约束 (ExactlyOne)
                constraints.AddRange(DetectExactlyOneConstraints(coils, ioTagDict, lu));

                // 3. 检测全满足约束 (AllRequired)
                constraints.AddRange(DetectAllRequiredConstraints(contacts, coils, ioTagDict, lu));

                // 4. 检测配对约束 (Pairing)
                constraints.AddRange(PairingConstraints(contacts, coils, ioTagDict, lu));

                // 5. 检测安全约束 (Safety)
                constraints.AddRange(DetectSafetyConstraints(contacts, coils, ioTagDict, lu));

                // 6. 检测时序约束 (Sequence)
                constraints.AddRange(DetectSequenceConstraints(timersCounters, contacts, coils, ioTagDict, lu));

                // 7. 检测范围约束 (RangeLimit)
                constraints.AddRange(DetectRangeLimitConstraints(comparators, ioTagDict, lu));

                // 过滤：如果指定了目标信号，只保留包含该信号的约束
                if (!string.IsNullOrEmpty(targetSignal))
                {
                    constraints = constraints
                        .Where(c => c.Signals.Any(s => s.SignalName.Equals(targetSignal, StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                }

                // 去除重复
                constraints = DeduplicateConstraints(constraints);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[XmlConstraint] 解析 XML 失败: LogicUnitId={Id}", lu.Id);
            }

            return constraints;
        }

        /// <summary>
        /// 提取所有线圈（基于 Attribute，实际 FlgNet 通过 Wire 连接引用变量，此方法可能无法正确提取）
        /// </summary>
        private List<LadderElement> ExtractCoils(XDocument doc)
        {
            var coils = new List<LadderElement>();

            // 查找 Coil, TrCoil
            var elements = doc.Descendants()
                .Where(e => e.Name.LocalName == "Coil" || e.Name.LocalName == "TrCoil");

            foreach (var el in elements)
            {
                var variable = el.Attribute("Variable")?.Value;
                var negated = el.Attribute("Negated")?.Value == "true";

                if (!string.IsNullOrEmpty(variable))
                {
                    coils.Add(new LadderElement
                    {
                        Name = variable,
                        Type = el.Name.LocalName,
                        IsNegated = negated,
                        Element = el
                    });
                }
            }

            return coils;
        }

        /// <summary>
        /// 提取所有触点（基于 Attribute，实际 FlgNet 通过 Wire 连接引用变量，此方法可能无法正确提取）
        /// </summary>
        private List<LadderElement> ExtractContacts(XDocument doc)
        {
            var contacts = new List<LadderElement>();

            var elements = doc.Descendants()
                .Where(e => e.Name.LocalName == "Contact");

            foreach (var el in elements)
            {
                var variable = el.Attribute("Variable")?.Value;
                var negated = el.Attribute("Negated")?.Value == "true";

                if (!string.IsNullOrEmpty(variable))
                {
                    contacts.Add(new LadderElement
                    {
                        Name = variable,
                        Type = "Contact",
                        IsNegated = negated,
                        Element = el
                    });
                }
            }

            return contacts;
        }

        /// <summary>
        /// 提取比较器
        /// </summary>
        private List<ComparatorElement> ExtractComparators(XDocument doc)
        {
            var comparators = new List<ComparatorElement>();

            // 查找 GE, LE, EQ, NE, GT, LT
            var elements = doc.Descendants()
                .Where(e => new[] { "GE", "LE", "EQ", "NE", "GT", "LT" }.Contains(e.Name.LocalName));

            foreach (var el in elements)
            {
                var op1 = el.Attribute("Operand1")?.Value ?? el.Element("Operand1")?.Value;
                var op2 = el.Attribute("Operand2")?.Value ?? el.Element("Operand2")?.Value;

                if (!string.IsNullOrEmpty(op1))
                {
                    comparators.Add(new ComparatorElement
                    {
                        Operator = el.Name.LocalName,
                        Operand1 = op1,
                        Operand2 = op2,
                        Element = el
                    });
                }
            }

            return comparators;
        }

        /// <summary>
        /// 提取定时器/计数器
        /// </summary>
        private List<TimerCounterElement> ExtractTimersCounters(XDocument doc)
        {
            var elements = new List<TimerCounterElement>();

            // TON, TOF, TP
            var timers = doc.Descendants()
                .Where(e => new[] { "TON", "TOF", "TP" }.Contains(e.Name.LocalName));

            foreach (var el in timers)
            {
                var timerName = el.Attribute("Name")?.Value;
                var input = el.Attribute("IN")?.Value;
                var output = el.Attribute("Q")?.Value;

                if (!string.IsNullOrEmpty(timerName))
                {
                    elements.Add(new TimerCounterElement
                    {
                        Name = timerName,
                        Type = el.Name.LocalName,
                        Input = input,
                        Output = output,
                        Element = el
                    });
                }
            }

            // CTU, CTD, CTUD
            var counters = doc.Descendants()
                .Where(e => new[] { "CTU", "CTD", "CTUD" }.Contains(e.Name.LocalName));

            foreach (var el in counters)
            {
                var counterName = el.Attribute("Name")?.Value;
                var cu = el.Attribute("CU")?.Value;
                var cd = el.Attribute("CD")?.Value;
                var q = el.Attribute("Q")?.Value;

                if (!string.IsNullOrEmpty(counterName))
                {
                    elements.Add(new TimerCounterElement
                    {
                        Name = counterName,
                        Type = el.Name.LocalName,
                        CU = cu,
                        CD = cd,
                        Output = q,
                        Element = el
                    });
                }
            }

            return elements;
        }

        /// <summary>
        /// 提取 Wire 连接关系（当前实现无法匹配 FlgNet 的 IdentCon/NameCon 端点结构，始终返回空列表）
        /// </summary>
        private List<WireConnection> ExtractWireConnections(XDocument doc)
        {
            var connections = new List<WireConnection>();

            var wires = doc.Descendants("Wire");
            foreach (var wire in wires)
            {
                var source = wire.Attribute("Source")?.Value;
                var target = wire.Attribute("Target")?.Value;

                if (!string.IsNullOrEmpty(source) && !string.IsNullOrEmpty(target))
                {
                    connections.Add(new WireConnection
                    {
                        Source = source,
                        Target = target
                    });
                }
            }

            return connections;
        }

        #endregion

        #region 约束检测规则

        /// <summary>
        /// 检测互斥约束 (Mutex)
        /// 规则：两个同设备、反向命名的驱动线圈不能同时为 true
        /// </summary>
        private List<ExtractedConstraint> DetectMutexConstraints(
            List<LadderElement> coils,
            Dictionary<string, PLC_IOTagEntity> ioTagDict,
            PLC_LogicUnitEntity lu)
        {
            var constraints = new List<ExtractedConstraint>();

            // 找出所有驱动线圈（非反馈）
            var driveCoils = coils.Where(c => !IsFeedbackSignal(c.Name)).ToList();

            for (int i = 0; i < driveCoils.Count; i++)
            {
                for (int j = i + 1; j < driveCoils.Count; j++)
                {
                    var c1 = driveCoils[i];
                    var c2 = driveCoils[j];

                    // 检查是否为反向信号
                    if (IsOppositeSignal(c1.Name, c2.Name))
                    {
                        var tag1 = GetIoTag(c1.Name, ioTagDict);
                        var tag2 = GetIoTag(c2.Name, ioTagDict);

                        // 同设备或无法判断时建立约束
                        if (tag1?.EquipmentId == tag2?.EquipmentId || tag1 == null || tag2 == null)
                        {
                            constraints.Add(new ExtractedConstraint
                            {
                                RuleName = $"{GetBaseName(c1.Name)}_{GetBaseName(c2.Name)}_互斥",
                                RuleType = "Mutex",
                                Operator = "AT_MOST_ONE",
                                EquipmentId = tag1?.EquipmentId ?? tag2?.EquipmentId,
                                Confidence = 0.95,
                                Description = $"信号 {c1.Name} 和 {c2.Name} 为反向动作，不能同时为 true（互斥）",
                                Priority = "Warning",
                                Signals = new List<ConstraintSignal>
                                {
                                    new ConstraintSignal { SignalName = c1.Name, SignalRole = "Member" },
                                    new ConstraintSignal { SignalName = c2.Name, SignalRole = "Member" }
                                }
                            });
                        }
                    }
                }
            }

            return constraints;
        }

        /// <summary>
        /// 检测唯一选择约束 (ExactlyOne)
        /// 规则：同一分支下的多个驱动信号只能有一个为 true
        /// </summary>
        private List<ExtractedConstraint> DetectExactlyOneConstraints(
            List<LadderElement> coils,
            Dictionary<string, PLC_IOTagEntity> ioTagDict,
            PLC_LogicUnitEntity lu)
        {
            var constraints = new List<ExtractedConstraint>();

            // 按父元素分组，找出同一分支下的线圈
            var branchGroups = coils
                .Where(c => c.Element.Parent != null)
                .GroupBy(c => c.Element.Parent)
                .Where(g => g.Count() > 1);

            foreach (var group in branchGroups)
            {
                var branchCoils = group.ToList();
                // 过滤掉反馈信号
                var driveCoils = branchCoils.Where(c => !IsFeedbackSignal(c.Name)).ToList();

                if (driveCoils.Count > 1)
                {
                    // 检查是否为互斥选择（同类型信号）
                    if (AreSimilarSignals(driveCoils.Select(c => c.Name).ToList()))
                    {
                        var signals = driveCoils.Select(c => new ConstraintSignal
                        {
                            SignalName = c.Name,
                            SignalRole = "Member"
                        }).ToList();

                        var tag1 = GetIoTag(driveCoils[0].Name, ioTagDict);

                        constraints.Add(new ExtractedConstraint
                        {
                            RuleName = $"{GetBaseName(driveCoils[0].Name)}_唯一选择",
                            RuleType = "ExactlyOne",
                            Operator = "EXACTLY_ONE",
                            EquipmentId = tag1?.EquipmentId,
                            Confidence = 0.9,
                            Description = $"信号组 [{string.Join(", ", driveCoils.Select(c => c.Name))}] 中只能有一个为 true",
                            Priority = "Warning",
                            Signals = signals
                        });
                    }
                }
            }

            return constraints;
        }

        /// <summary>
        /// 检测全满足约束 (AllRequired)
        /// 规则：常开触点串联在驱动线圈前，必须全部为 true 才能驱动
        /// </summary>
        private List<ExtractedConstraint> DetectAllRequiredConstraints(
            List<LadderElement> contacts,
            List<LadderElement> coils,
            Dictionary<string, PLC_IOTagEntity> ioTagDict,
            PLC_LogicUnitEntity lu)
        {
            var constraints = new List<ExtractedConstraint>();

            foreach (var coil in coils)
            {
                // 找出驱动该线圈的所有常开触点（非反馈、非安全）
                var enableContacts = contacts
                    .Where(c => !c.IsNegated && !IsFeedbackSignal(c.Name) && !IsSafetySignal(c.Name))
                    .ToList();

                if (enableContacts.Count >= 2)
                {
                    // 检查是否为同类型信号（如模式选择、条件许可）
                    if (AreRelatedEnableSignals(enableContacts.Select(c => c.Name).ToList()))
                    {
                        var tag = GetIoTag(coil.Name, ioTagDict);

                        constraints.Add(new ExtractedConstraint
                        {
                            RuleName = $"{coil.Name}_前置条件",
                            RuleType = "AllRequired",
                            Operator = "ALL_TRUE",
                            EquipmentId = tag?.EquipmentId,
                            Confidence = 0.85,
                            Description = $"信号 [{string.Join(", ", enableContacts.Select(c => c.Name))}] 必须全部为 true 才能驱动 {coil.Name}",
                            Priority = "Info",
                            Signals = enableContacts.Select(c => new ConstraintSignal
                            {
                                SignalName = c.Name,
                                SignalRole = "Member"
                            }).ToList()
                        });
                    }
                }
            }

            return constraints;
        }

        /// <summary>
        /// 检测配对约束 (Pairing)
        /// 规则：驱动信号与反馈信号的配对关系
        /// </summary>
        private List<ExtractedConstraint> PairingConstraints(
            List<LadderElement> contacts,
            List<LadderElement> coils,
            Dictionary<string, PLC_IOTagEntity> ioTagDict,
            PLC_LogicUnitEntity lu)
        {
            var constraints = new List<ExtractedConstraint>();

            foreach (var coil in coils)
            {
                // 查找对应的反馈信号
                var feedback = FindPairedFeedback(coil.Name, contacts);

                if (feedback != null)
                {
                    var coilTag = GetIoTag(coil.Name, ioTagDict);
                    var fbTag = GetIoTag(feedback.Name, ioTagDict);

                    // 驱动 → 反馈 配对
                    constraints.Add(new ExtractedConstraint
                    {
                        RuleName = $"{coil.Name}_配对",
                        RuleType = "Pairing",
                        Operator = "IMPLIES",
                        EquipmentId = coilTag?.EquipmentId ?? fbTag?.EquipmentId,
                        Confidence = 0.9,
                        Description = $"当 {coil.Name} = true 时，{feedback.Name} 应为 true（驱动-反馈配对）",
                        Priority = "Info",
                        Signals = new List<ConstraintSignal>
                        {
                            new ConstraintSignal { SignalName = coil.Name, SignalRole = "Source" },
                            new ConstraintSignal { SignalName = feedback.Name, SignalRole = "Target" }
                        }
                    });
                }
            }

            return constraints;
        }

        /// <summary>
        /// 检测安全约束 (Safety)
        /// 规则：安全信号断开时，受控输出必须为 false
        /// </summary>
        private List<ExtractedConstraint> DetectSafetyConstraints(
            List<LadderElement> contacts,
            List<LadderElement> coils,
            Dictionary<string, PLC_IOTagEntity> ioTagDict,
            PLC_LogicUnitEntity lu)
        {
            var constraints = new List<ExtractedConstraint>();

            // 常闭触点形式：安全门断开 → 触点断开 → 输出停止（IsNegated=true）
            var safetyNcContacts = contacts
                .Where(c => c.IsNegated && IsSafetySignal(c.Name, ioTagDict))
                .ToList();

            // 常开触点形式：安全门关闭=true → 触点闭合 → 允许输出（IsNegated=false）
            // 语义同样是"安全门打开时输出必须为 false"，属于 Safety(REQUIRES_0)
            var safetyNoContacts = contacts
                .Where(c => !c.IsNegated && IsSafetySignal(c.Name, ioTagDict))
                .ToList();

            var allSafetyContacts = safetyNcContacts
                .Concat(safetyNoContacts)
                .DistinctBy(c => c.Name)
                .ToList();

            foreach (var safety in allSafetyContacts)
            {
                var controlledCoils = FindControlledCoils(safety, coils);

                foreach (var coil in controlledCoils)
                {
                    var safetyTag = GetIoTag(safety.Name, ioTagDict);
                    var coilTag = GetIoTag(coil.Name, ioTagDict);
                    var isNc = safety.IsNegated;

                    constraints.Add(new ExtractedConstraint
                    {
                        RuleName = $"{safety.Name}_安全联锁",
                        RuleType = "Safety",
                        Operator = "REQUIRES_0",
                        EquipmentId = safetyTag?.EquipmentId ?? coilTag?.EquipmentId,
                        Confidence = isNc ? 0.95 : 0.88,
                        Description = isNc
                            ? $"安全信号 {safety.Name} = false 时，{coil.Name} 必须为 false"
                            : $"安全门信号 {safety.Name} = false（门打开）时，{coil.Name} 必须为 false",
                        Priority = "Critical",
                        Signals = new List<ConstraintSignal>
                        {
                            new ConstraintSignal { SignalName = safety.Name, SignalRole = "Source" },
                            new ConstraintSignal { SignalName = coil.Name, SignalRole = "Target" }
                        }
                    });
                }
            }

            return constraints;
        }

        /// <summary>
        /// 检测时序约束 (Sequence)
        /// 规则：定时器/计数器驱动的信号存在时序依赖
        /// </summary>
        private List<ExtractedConstraint> DetectSequenceConstraints(
            List<TimerCounterElement> timersCounters,
            List<LadderElement> contacts,
            List<LadderElement> coils,
            Dictionary<string, PLC_IOTagEntity> ioTagDict,
            PLC_LogicUnitEntity lu)
        {
            var constraints = new List<ExtractedConstraint>();

            foreach (var tc in timersCounters)
            {
                // 定时器/计数器的输出（Q）控制的线圈
                var outputCoils = coils.Where(c =>
                    c.Name.Equals(tc.Output, StringComparison.OrdinalIgnoreCase)).ToList();

                // 定时器/计数器的输入（IN/CU）触点
                var inputContacts = contacts.Where(c =>
                    c.Name.Equals(tc.Input, StringComparison.OrdinalIgnoreCase) ||
                    c.Name.Equals(tc.CU, StringComparison.OrdinalIgnoreCase)).ToList();

                foreach (var coil in outputCoils)
                {
                    var coilTag = GetIoTag(coil.Name, ioTagDict);

                    constraints.Add(new ExtractedConstraint
                    {
                        RuleName = $"{tc.Name}_时序",
                        RuleType = "Sequence",
                        Operator = "CHAIN_ORDER",
                        EquipmentId = coilTag?.EquipmentId,
                        Confidence = 0.85,
                        Description = $"定时器/计数器 {tc.Name} 触发后，{coil.Name} 才能动作（时序依赖）",
                        Priority = "Info",
                        Signals = new List<ConstraintSignal>
                        {
                            new ConstraintSignal { SignalName = tc.Input ?? tc.CU, SignalRole = "Source" },
                            new ConstraintSignal { SignalName = coil.Name, SignalRole = "Target" }
                        }
                    });
                }
            }

            return constraints;
        }

        /// <summary>
        /// 检测范围约束 (RangeLimit)
        /// 规则：数值信号通过比较器有上下限约束
        /// </summary>
        private List<ExtractedConstraint> DetectRangeLimitConstraints(
            List<ComparatorElement> comparators,
            Dictionary<string, PLC_IOTagEntity> ioTagDict,
            PLC_LogicUnitEntity lu)
        {
            var constraints = new List<ExtractedConstraint>();

            foreach (var comp in comparators)
            {
                // 尝试解析上下限
                if (double.TryParse(comp.Operand2, out var limit))
                {
                    var tag = GetIoTag(comp.Operand1, ioTagDict);

                    if (tag != null && tag.DataType != "Bool") // 数值型
                    {
                        var (ruleType, desc, priority) = comp.Operator switch
                        {
                            "GE" or "GT" => ("RangeLimit", $"信号 {comp.Operand1} >= {limit}", "Warning"),
                            "LE" or "LT" => ("RangeLimit", $"信号 {comp.Operand1} <= {limit}", "Warning"),
                            "EQ" => ("RangeLimit", $"信号 {comp.Operand1} = {limit}", "Info"),
                            _ => (null, null, null)
                        };

                        if (ruleType != null)
                        {
                            constraints.Add(new ExtractedConstraint
                            {
                                RuleName = $"{comp.Operand1}_范围限制",
                                RuleType = ruleType,
                                Operator = "VALUE_RANGE",
                                EquipmentId = tag.EquipmentId,
                                Confidence = 0.95,
                                Description = desc,
                                Priority = priority,
                                Signals = new List<ConstraintSignal>
                                {
                                    new ConstraintSignal { SignalName = comp.Operand1, SignalRole = "Member" }
                                },
                                MinValue = comp.Operator is "GE" or "GT" ? limit : (double?)null,
                                MaxValue = comp.Operator is "LE" or "LT" ? limit : (double?)null
                            });
                        }
                    }
                }
            }

            return constraints;
        }

        #endregion

        #region 辅助方法

        /// <summary>
        /// 判断是否为反馈信号
        /// </summary>
        private bool IsFeedbackSignal(string signalName)
        {
            var name = signalName.ToUpperInvariant();
            return FeedbackKeywords.Any(k => name.Contains(k.ToUpperInvariant()));
        }

        /// <summary>
        /// 判断是否为安全信号（优先查 IsSafety 字段，缺省时降级关键词兜底）
        /// </summary>
        private bool IsSafetySignal(string signalName, Dictionary<string, PLC_IOTagEntity> ioTagDict = null)
        {
            // 优先：数据库/CSV 导入的权威标记
            if (ioTagDict != null)
            {
                var tag = GetIoTag(signalName, ioTagDict);
                if (tag?.IsSafety == true) return true;
                if (tag?.IsSafety == false) return false; // 明确标记为非安全，不再走关键词
            }

            // 降级：关键词模糊匹配（标记缺省时兜底）
            var name = signalName.ToUpperInvariant();
            return SafetyKeywords.Any(k => name.Contains(k.ToUpperInvariant()));
        }

        /// <summary>
        /// 判断两个信号是否为反向信号
        /// </summary>
        private bool IsOppositeSignal(string signal1, string signal2)
        {
            var s1 = signal1.ToUpperInvariant();
            var s2 = signal2.ToUpperInvariant();

            // 提取基础名称
            var base1 = GetBaseName(s1);
            var base2 = GetBaseName(s2);

            // 基础名称相同，且有反向关键词
            if (base1 == base2 && base1.Length < s1.Length)
            {
                var suffix1 = s1.Replace(base1, "");
                var suffix2 = s2.Replace(base2, "");

                return IsOppositeSuffix(suffix1, suffix2);
            }

            return false;
        }

        /// <summary>
        /// 判断后缀是否为反向
        /// </summary>
        private bool IsOppositeSuffix(string suffix1, string suffix2)
        {
            var oppositePairs = new[]
            {
                ("FWD", "BKD"), ("FORWARD", "BACKWARD"),
                ("OPEN", "CLOSE"), ("UP", "DOWN"),
                ("LEFT", "RIGHT"), ("RUN", "STOP"),
                ("START", "RESET"), ("ON", "OFF")
            };

            foreach (var (a, b) in oppositePairs)
            {
                if ((suffix1.Contains(a) && suffix2.Contains(b)) ||
                    (suffix1.Contains(b) && suffix2.Contains(a)))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 获取信号基础名称
        /// </summary>
        private string GetBaseName(string signalName)
        {
            foreach (var keyword in MutexKeywords)
            {
                var idx = signalName.ToUpperInvariant().IndexOf(keyword.ToUpperInvariant());
                if (idx > 0)
                {
                    return signalName.Substring(0, idx);
                }
            }

            // 去掉常见后缀
            var suffixes = new[] { "_O", "_I", "_Q", "_M", "_X" };
            foreach (var suffix in suffixes)
            {
                if (signalName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return signalName.Substring(0, signalName.Length - suffix.Length);
                }
            }

            return signalName;
        }

        /// <summary>
        /// 判断是否为相似信号（同类型选择）
        /// </summary>
        private bool AreSimilarSignals(List<string> signals)
        {
            if (signals.Count < 2) return false;

            var bases = signals.Select(GetBaseName).ToList();
            // 所有基础名称相同
            return bases.Distinct().Count() == 1;
        }

        /// <summary>
        /// 判断是否为相关的使能信号
        /// </summary>
        private bool AreRelatedEnableSignals(List<string> signals)
        {
            if (signals.Count < 2) return false;

            // 检查是否有相同的模式前缀
            var prefixes = signals.Select(s =>
            {
                var idx = s.IndexOf('_');
                return idx > 0 ? s.Substring(0, idx) : s;
            }).ToList();

            // 至少有一半以上相同前缀
            var mostCommon = prefixes.GroupBy(p => p).OrderByDescending(g => g.Count()).First();
            return mostCommon.Count() >= signals.Count / 2.0;
        }

        /// <summary>
        /// 查找配对的反馈信号
        /// </summary>
        private LadderElement FindPairedFeedback(string coilName, List<LadderElement> contacts)
        {
            var baseName = GetBaseName(coilName);

            // 查找同基础名的反馈信号
            return contacts.FirstOrDefault(c =>
                c.Name.Contains(baseName, StringComparison.OrdinalIgnoreCase) &&
                IsFeedbackSignal(c.Name));
        }

        /// <summary>
        /// 查找安全信号控制的线圈
        /// </summary>
        private List<LadderElement> FindControlledCoils(LadderElement safetyContact, List<LadderElement> coils)
        {
            // 简化：返回同父元素下的所有线圈
            var parent = safetyContact.Element.Parent;
            if (parent == null) return new List<LadderElement>();

            return coils.Where(c =>
                c.Element.Ancestors().Any(a => a == parent)).ToList();
        }

        /// <summary>
        /// 获取 IO Tag 信息
        /// </summary>
        private PLC_IOTagEntity GetIoTag(string signalName, Dictionary<string, PLC_IOTagEntity> ioTagDict)
        {
            if (ioTagDict.TryGetValue(signalName, out var tag))
                return tag;

            // 尝试模糊匹配
            return ioTagDict.Values.FirstOrDefault(t =>
                t.TagName.Contains(signalName, StringComparison.OrdinalIgnoreCase) ||
                signalName.Contains(t.TagName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 合并/去重约束
        /// </summary>
        private List<ExtractedConstraint> MergeConstraints(List<ExtractedConstraint> constraints)
        {
            var merged = new List<ExtractedConstraint>();
            var seen = new HashSet<string>();

            foreach (var c in constraints.OrderByDescending(x => x.Confidence))
            {
                var key = $"{c.RuleType}:{string.Join(",", c.Signals.Select(s => s.SignalName).OrderBy(n => n))}";

                if (!seen.Contains(key))
                {
                    seen.Add(key);
                    merged.Add(c);
                }
            }

            return merged;
        }

        /// <summary>
        /// 去除重复约束
        /// </summary>
        private List<ExtractedConstraint> DeduplicateConstraints(List<ExtractedConstraint> constraints)
        {
            var seen = new HashSet<string>();
            var result = new List<ExtractedConstraint>();

            foreach (var c in constraints)
            {
                var signalNames = string.Join(",", c.Signals.Select(s => s.SignalName).OrderBy(n => n));
                var key = $"{c.RuleType}:{signalNames}";

                if (!seen.Contains(key))
                {
                    seen.Add(key);
                    result.Add(c);
                }
            }

            return result;
        }

        #endregion

        #region 数据结构

        /// <summary>
        /// 梯形图元素
        /// </summary>
        private class LadderElement
        {
            public string Name { get; set; }
            public string Type { get; set; }
            public bool IsNegated { get; set; }
            public XElement Element { get; set; }
        }

        /// <summary>
        /// 比较器元素
        /// </summary>
        private class ComparatorElement
        {
            public string Operator { get; set; }
            public string Operand1 { get; set; }
            public string Operand2 { get; set; }
            public XElement Element { get; set; }
        }

        /// <summary>
        /// 定时器/计数器元素
        /// </summary>
        private class TimerCounterElement
        {
            public string Name { get; set; }
            public string Type { get; set; }
            public string Input { get; set; }
            public string Output { get; set; }
            public string CU { get; set; }
            public string CD { get; set; }
            public XElement Element { get; set; }
        }

        /// <summary>
        /// Wire 连接
        /// </summary>
        private class WireConnection
        {
            public string Source { get; set; }
            public string Target { get; set; }
        }

        /// <summary>
        /// 提取的约束
        /// </summary>
        private class ExtractedConstraint
        {
            public string RuleName { get; set; }
            public string RuleType { get; set; }
            public string Operator { get; set; }
            public string EquipmentId { get; set; }
            public double Confidence { get; set; }
            public string Description { get; set; }
            public string Priority { get; set; }
            public List<ConstraintSignal> Signals { get; set; }
            public double? MinValue { get; set; }
            public double? MaxValue { get; set; }
        }

        /// <summary>
        /// 约束中的信号
        /// </summary>
        private class ConstraintSignal
        {
            public string SignalName { get; set; }
            public string SignalRole { get; set; }
        }

        /// <summary>
        /// 约束表达式（用于C#代码生成）
        /// </summary>
        private class ConstraintExpression
        {
            public long RuleId { get; set; }
            public string RuleName { get; set; }
            public string RuleType { get; set; }
            public string Operator { get; set; }
            public string Priority { get; set; }
            public string Description { get; set; }
            public string CSharpExpression { get; set; }
            public string BooleanExpression { get; set; }
            public string FuncSignature { get; set; }
        }

        private sealed class CallPinBindingRecord
        {
            public string CalleeName { get; set; }
            public Dictionary<string, string> InputPins { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> OutputPins { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> InOutPins { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }

        #endregion
    }
}
