using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;

namespace HSJM.AI.AppLayer.Agent.Services.Plc_diagnosis
{
    /// <summary>
    /// 基于西门子 TIA Portal 导出的 FlgNet XML，提取可被证明的确定性约束。
    /// 这里只做接线级别的结构解析，不推断 FB 内部语义。
    /// </summary>
    public static class SiemensFlgNetConstraintExtractor
    {
        public static List<DeterministicConstraintDraft> ExtractFromLogicUnit(
            string rawContent,
            string uidSymbolMapJson = null,
            string blockName = null,
            string logicTitle = null)
        {
            if (string.IsNullOrWhiteSpace(rawContent))
            {
                return new List<DeterministicConstraintDraft>();
            }

            var document = ParseFlgNetDocument(rawContent);
            var root = document?.Root;
            if (root == null)
            {
                return new List<DeterministicConstraintDraft>();
            }

            var rootName = root.Name.LocalName;
            if (string.Equals(rootName, "FlgNet", StringComparison.OrdinalIgnoreCase))
            {
                return Extract(rawContent);
            }

            if (string.Equals(rootName, "Transition", StringComparison.OrdinalIgnoreCase))
            {
                return ExtractConditionWrapperRules(root, "Transition", Attr(root, "Name") ?? logicTitle ?? "UnnamedTransition");
            }

            if (string.Equals(rootName, "Interlock", StringComparison.OrdinalIgnoreCase))
            {
                return ExtractConditionWrapperRules(root, "Interlock", logicTitle ?? blockName ?? "UnnamedInterlock");
            }

            if (string.Equals(rootName, "Supervision", StringComparison.OrdinalIgnoreCase))
            {
                return ExtractConditionWrapperRules(root, "Supervision", logicTitle ?? blockName ?? "UnnamedSupervision");
            }

            if (string.Equals(rootName, "Actions", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rootName, "StructuredText", StringComparison.OrdinalIgnoreCase))
            {
                return new List<DeterministicConstraintDraft>();
            }

            var nestedDrafts = new List<DeterministicConstraintDraft>();
            foreach (var nestedRoot in root.Descendants().Where(x => IsSupportedLogicRoot(x.Name.LocalName)))
            {
                nestedDrafts.AddRange(ExtractFromLogicUnit(
                    nestedRoot.ToString(SaveOptions.DisableFormatting),
                    uidSymbolMapJson,
                    blockName,
                    logicTitle));
            }

            return Deduplicate(nestedDrafts);
        }

        public static List<DeterministicConstraintDraft> ExtractFromLogicUnit(
            string rawContent,
            string uidSymbolMapJson,
            string blockName,
            string logicTitle,
            HashSet<string> externalSignals)
        {
            var drafts = ExtractFromLogicUnit(rawContent, uidSymbolMapJson, blockName, logicTitle);
            if (externalSignals == null || externalSignals.Count == 0)
            {
                return drafts;
            }

            var callPinNames = new HashSet<string>(
                ExtractCallPinMappingsFromLogicUnit(rawContent, blockName, logicTitle)
                    .Select(x => x.SignalName)
                    .Where(x => !string.IsNullOrWhiteSpace(x)),
                StringComparer.OrdinalIgnoreCase);
            callPinNames.UnionWith(ExtractInternalCallPinNamesFromLogicUnit(rawContent, blockName, logicTitle));

            return drafts
                .Where(draft => ShouldKeepConstraintDraft(draft, externalSignals, callPinNames))
                .ToList();
        }

        public static List<DeterministicLogicExpressionDraft> ExtractLogicExpressionsFromLogicUnit(
            string rawContent,
            string uidSymbolMapJson = null,
            string blockName = null,
            string logicTitle = null)
        {
            if (string.IsNullOrWhiteSpace(rawContent))
            {
                return new List<DeterministicLogicExpressionDraft>();
            }

            var document = ParseFlgNetDocument(rawContent);
            var root = document?.Root;
            if (root == null)
            {
                return new List<DeterministicLogicExpressionDraft>();
            }

            var drafts = new List<DeterministicLogicExpressionDraft>();
            var sortOrder = 0;

            foreach (var logicRoot in EnumerateSupportedLogicRoots(root))
            {
                if (!TryBuildLogicContext(logicRoot, out var partsElement, out var accessMap, out var partMap, out var wireIndex))
                {
                    continue;
                }

                foreach (var call in Children(partsElement, "Call"))
                {
                    var callUid = Attr(call, "UId");
                    if (string.IsNullOrWhiteSpace(callUid))
                    {
                        continue;
                    }

                    var callInfo = Child(call, "CallInfo");
                    var callTypeName = Attr(callInfo, "Name") ?? "UnknownCall";
                    var callInstanceName = BuildCallInstanceName(call, callInfo);
                    var inputParameters = GetParameters(callInfo, "Input");
                    var outputParameters = GetParameters(callInfo, "Output");

                    foreach (var inputParameter in inputParameters)
                    {
                        var expression = BuildExpressionForTarget(
                            Endpoint.Name(callUid, inputParameter),
                            wireIndex,
                            accessMap,
                            partMap,
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                        if (expression == null)
                        {
                            continue;
                        }

                        drafts.Add(BuildLogicExpressionDraft(
                            $"{callInstanceName}.{inputParameter}",
                            "CallInput",
                            $"{callInstanceName}.{inputParameter}",
                            expression,
                            $"{callTypeName} 输入 pin {inputParameter} 的布尔表达式。",
                            sortOrder++));
                    }

                    foreach (var outputParameter in outputParameters)
                    {
                        var targetSignal = ResolveMappedSignal(Endpoint.Name(callUid, outputParameter), wireIndex, accessMap);
                        if (string.IsNullOrWhiteSpace(targetSignal) || IsLiteralValue(targetSignal))
                        {
                            continue;
                        }

                        var outputExpression = BoolExpression.Signal($"{callInstanceName}.{outputParameter}");
                        drafts.Add(BuildLogicExpressionDraft(
                            targetSignal,
                            "CallOutput",
                            targetSignal,
                            outputExpression,
                            $"{targetSignal} 由 {callTypeName}.{outputParameter} 直接驱动。",
                            sortOrder++));
                    }
                }

                var coilParts = Children(partsElement, "Part")
                    .Where(part => string.Equals(Attr(part, "Name"), "Coil", StringComparison.OrdinalIgnoreCase));

                foreach (var part in coilParts)
                {
                    var coilUid = Attr(part, "UId");
                    if (string.IsNullOrWhiteSpace(coilUid))
                    {
                        continue;
                    }

                    var targetSignal = ResolveMappedSignal(Endpoint.Name(coilUid, "operand"), wireIndex, accessMap);
                    if (string.IsNullOrWhiteSpace(targetSignal) || IsLiteralValue(targetSignal))
                    {
                        continue;
                    }

                    var expression = BuildExpressionForTarget(
                        Endpoint.Name(coilUid, "in"),
                        wireIndex,
                        accessMap,
                        partMap,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    if (expression == null)
                    {
                        continue;
                    }

                    drafts.Add(BuildLogicExpressionDraft(
                        targetSignal,
                        "Coil",
                        targetSignal,
                        expression,
                        $"{targetSignal} 的线圈驱动表达式。",
                        sortOrder++));
                }
            }

            return DeduplicateLogicExpressions(drafts);
        }

        public static string RenderCompactLogicFromLogicUnit(
            string rawContent,
            string uidSymbolMapJson = null,
            string blockName = null,
            string logicTitle = null)
        {
            var drafts = ExtractLogicExpressionsFromLogicUnit(rawContent, uidSymbolMapJson, blockName, logicTitle);
            return string.Join(
                Environment.NewLine,
                drafts
                    .OrderBy(x => x.SortOrder ?? int.MaxValue)
                    .ThenBy(x => x.TargetSignal, StringComparer.OrdinalIgnoreCase)
                    .Select(x => $"{x.TargetSignal} := {x.ExpressionText}"));
        }

        /// <summary>
        /// 把单个 LogicUnit 渲染成树形 Markdown，格式与 AstMarkdownRendererService.RenderAstToMarkdown 对齐：
        ///   **BlockName** Network N
        ///   - 锚点: `主输出 (UnitType)`
        ///     - **且** (AND) — 以下条件全部满足:
        ///       - 触点(常开): `SignalName`
        ///       - 触点(常闭): `SignalName`
        ///       - **或** (OR) — 以下条件任一满足:
        ///         - 触点(常开): `A`
        ///         - 触点(常闭): `B`
        ///       - 线圈输出: `TargetSignal`
        /// 用于：Interlock / Supervision（AST 不覆盖）+ AST 不完整时的 fallback。
        /// </summary>
        public static string RenderLogicUnitToMarkdown(
            string rawContent,
            string? blockName = null,
            int? networkId = null,
            string? networkTitle = null)
        {
            if (string.IsNullOrWhiteSpace(rawContent))
            {
                return "（本 LogicUnit 内容为空）";
            }

            var document = ParseFlgNetDocument(rawContent);
            var root = document?.Root;
            if (root == null)
            {
                return "（XML 解析失败）";
            }

            var rootName = root.Name.LocalName;
            var sb = new System.Text.StringBuilder();

            // 标题行
            if (!string.IsNullOrWhiteSpace(blockName) || networkId.HasValue || !string.IsNullOrWhiteSpace(networkTitle))
            {
                var titleParts = new List<string>();
                if (!string.IsNullOrWhiteSpace(blockName)) titleParts.Add($"**{blockName}**");
                if (networkId.HasValue) titleParts.Add($"Network {networkId}");
                if (!string.IsNullOrWhiteSpace(networkTitle)) titleParts.Add($"「{networkTitle}」");
                sb.AppendLine(string.Join(" ", titleParts));
                sb.AppendLine();
            }

            // Actions / StructuredText：扁平列出 LHS := RHS
            if (string.Equals(rootName, "Actions", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rootName, "StructuredText", StringComparison.OrdinalIgnoreCase))
            {
                var assigns = ExtractScriptAssignments(root, rootName);
                if (assigns.Count == 0)
                {
                    sb.AppendLine($"- 脚本段（{rootName}）：未识别到显式赋值");
                }
                else
                {
                    // 结构判定 Sensor Mapping LU：
                    //   1. 全部赋值的 LHS 都指向同一个 DB 数组目标（如 `DB610_OP060_User.DI.Sensor`）
                    //   2. 所有 RHS 都是不同的外部 IO Tag 名字
                    // 这种 LU 是「IO 数据搬运」中转，所有信号都没有控制语义，约束生成应跳过整个 LU
                    bool isSensorMapping = IsSensorMappingPattern(assigns);
                    if (isSensorMapping)
                    {
                        sb.AppendLine($"- 脚本段（{rootName}）— **Sensor Mapping LU**（所有赋值都是 IO Tag → 同一 DB 数组中转，无控制语义，约束生成应整体跳过）:");
                        // 仍然列出所有赋值给 LLM 看，但带明确标记
                    }
                    else
                    {
                        sb.AppendLine($"- 脚本段（{rootName}）— 含 {assigns.Count} 处赋值:");
                    }
                    foreach (var (lhs, rhs) in assigns.Take(40))
                    {
                        sb.AppendLine($"  - 赋值: `{lhs}` := `{rhs}`");
                    }
                    if (assigns.Count > 40)
                    {
                        sb.AppendLine($"  - ...（共 {assigns.Count} 处赋值，已截断）");
                    }
                }
                return sb.ToString().TrimEnd();
            }

            // FlgNet / Transition / Interlock / Supervision：走 FlgNet 解析
            // 它们都内嵌一个 FlgNet（Transition/Interlock/Supervision 的 FlgNet 是子节点）
            var logicRoots = EnumerateSupportedLogicRoots(root).ToList();
            if (logicRoots.Count == 0)
            {
                sb.AppendLine($"- {rootName}: 未找到可解析的 FlgNet");
                return sb.ToString().TrimEnd();
            }

            int idx = 0;
            foreach (var lr in logicRoots)
            {
                idx++;
                if (!TryBuildLogicContext(lr, out var partsElement, out var accessMap, out var partMap, out var wireIndex))
                {
                    continue;
                }

                var wrapperName = lr.Name.LocalName;  // FlgNet / Transition / Interlock / Supervision
                var displayKind = wrapperName switch
                {
                    "Transition" => "Graph 步骤转移条件 (Transition)",
                    "Interlock" => "Graph 联锁条件 (Interlock)",
                    "Supervision" => "Graph 监视/超时条件 (Supervision)",
                    _ => "LAD/FBD 网络 (FlgNet)"
                };

                // 1) Coil 输出（驱动 IO Tag 或 DB 变量）
                //    博图三种 coil 部件：
                //    - Coil  : 普通赋值线圈，target := in_expr（瞬时跟随）
                //    - SCoil : 置位线圈，in_expr=true → target 置 true 并保持（约束语义：IMPLIES(in_expr → target)）
                //    - RCoil : 复位线圈，in_expr=true → target 置 false 并保持（约束语义：REQUIRES_0(in_expr → target)）
                //    三者锚点结构一致，只是 PLD 标记不同的 set/reset 语义，让 LLM 据此选 IMPLIES/REQUIRES_0
                var anyOutput = false;
                foreach (var part in Children(partsElement, "Part"))
                {
                    var pname = Attr(part, "Name");
                    if (!IsCoilPartName(pname)) continue;
                    var coilUid = Attr(part, "UId");
                    if (string.IsNullOrWhiteSpace(coilUid)) continue;
                    var target = ResolveMappedSignal(Endpoint.Name(coilUid, "operand"), wireIndex, accessMap);
                    if (string.IsNullOrWhiteSpace(target) || IsLiteralValue(target)) continue;
                    var inExpr = BuildExpressionForTarget(
                        Endpoint.Name(coilUid, "in"),
                        wireIndex, accessMap, partMap,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    var (anchorSuffix, outputSuffix) = ClassifyCoilSemantics(pname);
                    sb.AppendLine($"- {PldMarkdownTokens.AnchorLabel}: `{target}` ({displayKind}{anchorSuffix})");
                    RenderBoolExpressionAsTree(sb, inExpr, indent: 1);
                    sb.AppendLine($"  - 线圈输出: `{target}`{outputSuffix}");
                    sb.AppendLine();
                    anyOutput = true;
                }

                // 2) Tr/Il/Sv Coil（Graph 条件 coil，没有 operand，触发表达式即"激活/许可"条件）
                foreach (var part in Children(partsElement, "Part"))
                {
                    var pname = Attr(part, "Name");
                    if (string.IsNullOrWhiteSpace(pname)) continue;
                    if (!string.Equals(pname, "TrCoil", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(pname, "IlCoil", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(pname, "SvCoil", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var coilUid = Attr(part, "UId");
                    if (string.IsNullOrWhiteSpace(coilUid)) continue;
                    var inExpr = BuildExpressionForTarget(
                        Endpoint.Name(coilUid, "in"),
                        wireIndex, accessMap, partMap,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    sb.AppendLine($"- {PldMarkdownTokens.AnchorLabel}: `{pname}` ({displayKind})");
                    if (inExpr == null)
                    {
                        // 含比较类（Gt/Lt 等）部件，无法构建 Bool 表达式 → 列出涉及的全部 Access 符号
                        var accessSigs = accessMap.Values
                            .Where(v => !string.IsNullOrWhiteSpace(v) && !IsLiteralValue(v))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        sb.AppendLine($"  - **比较/复杂表达式** — 无法展开为布尔表达式，涉及信号:");
                        foreach (var s in accessSigs)
                            sb.AppendLine($"    - 引用: `{s}`");
                    }
                    else
                    {
                        RenderBoolExpressionAsTree(sb, inExpr, indent: 1);
                    }
                    sb.AppendLine();
                    anyOutput = true;
                }

                // 3) FB/FC Call（FlgNet 主类型）
                foreach (var call in Children(partsElement, "Call"))
                {
                    var callUid = Attr(call, "UId");
                    if (string.IsNullOrWhiteSpace(callUid)) continue;
                    var callInfo = Child(call, "CallInfo");
                    var callTypeName = Attr(callInfo, "Name") ?? "UnknownCall";
                    var callInstanceName = BuildCallInstanceName(call, callInfo);
                    var inputParams = GetParameters(callInfo, "Input");
                    var outputParams = GetParameters(callInfo, "Output");
                    sb.AppendLine($"- {PldMarkdownTokens.AnchorLabel}: `{callInstanceName}` ({PldMarkdownTokens.FbCallToken}: {callTypeName})");
                    foreach (var pin in inputParams)
                    {
                        var expr = BuildExpressionForTarget(
                            Endpoint.Name(callUid, pin),
                            wireIndex, accessMap, partMap,
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                        sb.AppendLine($"  - {PldMarkdownTokens.InPinLabel}: `{pin}`");
                        RenderBoolExpressionAsTree(sb, expr, indent: 2);
                    }
                    foreach (var pin in outputParams)
                    {
                        var sig = ResolveMappedSignal(Endpoint.Name(callUid, pin), wireIndex, accessMap);
                        // 加引脚语义标记（基于博图标准 FB 引脚名，跨项目通用）：
                        //   QFwd/QBkd          → Q 输出（真正的电磁阀/线圈输出，参与 Pairing/Mutex/Safety）
                        //   FwdPos/BkdPos      → 到位反馈位（DB 内部状态，不参与瞬时约束）
                        //   FwdErr/BkdErr/FwdLockErr/BkdLockErr → 报警位（时延报警，不参与瞬时 Pairing）
                        //   其它               → 自行判断
                        var pinNote = ClassifyOutputPinSemantics(pin);
                        if (!string.IsNullOrEmpty(pinNote))
                            sb.AppendLine($"  - {PldMarkdownTokens.OutPinLabel}: `{pin}` → `{sig ?? "(未绑定)"}`  {pinNote}");
                        else
                            sb.AppendLine($"  - {PldMarkdownTokens.OutPinLabel}: `{pin}` → `{sig ?? "(未绑定)"}`");
                    }
                    sb.AppendLine();
                    anyOutput = true;
                }

                if (!anyOutput)
                {
                    sb.AppendLine($"- {displayKind}：本网络无 Coil/Call 输出（可能是纯逻辑或中间状态）");
                    sb.AppendLine();
                }
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// 厂商无关的「PLC LogicUnit → 锚点结构」入口。从 FlgNet/Transition/Interlock/Supervision XML
        /// 抽取 <see cref="PldAnchorBlock"/> 列表，跳过 Markdown round-trip。
        ///
        /// 输入：原始 LogicUnit XML 字符串。
        /// 输出：FB 调用块（含 OutPins / InPinContacts）+ 独立 Coil 块（含 DirectContacts）的有序列表。
        /// 跳过：Actions / StructuredText 脚本段（无锚点结构语义）。
        ///
        /// 用途：<see cref="PldStructuralConstraintAugmenter"/> 等约束推理服务直接消费此结构，无需读 PLD 文本。
        /// </summary>
        public static List<PldAnchorBlock> ExtractAnchorBlocks(string rawContent)
        {
            var blocks = new List<PldAnchorBlock>();
            if (string.IsNullOrWhiteSpace(rawContent)) return blocks;

            var document = ParseFlgNetDocument(rawContent);
            var root = document?.Root;
            if (root == null) return blocks;

            var rootName = root.Name.LocalName;
            // 脚本段没有锚点结构语义，直接返回空列表（调用方应当通过其它途径处理脚本段）
            if (string.Equals(rootName, "Actions", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rootName, "StructuredText", StringComparison.OrdinalIgnoreCase))
            {
                return blocks;
            }

            var logicRoots = EnumerateSupportedLogicRoots(root).ToList();
            foreach (var lr in logicRoots)
            {
                if (!TryBuildLogicContext(lr, out var partsElement, out var accessMap, out var partMap, out var wireIndex))
                    continue;

                var wrapperName = lr.Name.LocalName;
                var displayKind = wrapperName switch
                {
                    "Transition" => "Graph 步骤转移条件 (Transition)",
                    "Interlock" => "Graph 联锁条件 (Interlock)",
                    "Supervision" => "Graph 监视/超时条件 (Supervision)",
                    _ => "LAD/FBD 网络 (FlgNet)"
                };

                // 1) Coil 输出（含 Coil / SCoil / RCoil 三种）
                //    SCoil/RCoil 的锚点结构与 Coil 一致，只是 set/reset 语义不同。
                //    PldAnchorBlock 当前不携带 set/reset 标志（Augmenter 只看反向 Q/I 拓扑，不区分 set/reset），
                //    所以这里都建成 Coil 类型；TypeText 标出 set/reset 仅供调试。
                foreach (var part in Children(partsElement, "Part"))
                {
                    var pname = Attr(part, "Name");
                    if (!IsCoilPartName(pname)) continue;
                    var coilUid = Attr(part, "UId");
                    if (string.IsNullOrWhiteSpace(coilUid)) continue;
                    var target = ResolveMappedSignal(Endpoint.Name(coilUid, "operand"), wireIndex, accessMap);
                    if (string.IsNullOrWhiteSpace(target) || IsLiteralValue(target)) continue;
                    var inExpr = BuildExpressionForTarget(
                        Endpoint.Name(coilUid, "in"),
                        wireIndex, accessMap, partMap,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase));

                    var (anchorSuffix, _) = ClassifyCoilSemantics(pname);
                    var block = new PldAnchorBlock
                    {
                        Type = PldAnchorType.Coil,
                        Anchor = target,
                        TypeText = displayKind + anchorSuffix
                    };
                    CollectContactsFromExpression(inExpr, block.DirectContacts);
                    blocks.Add(block);
                }

                // 2) Graph 类 Coil（TrCoil / IlCoil / SvCoil）
                foreach (var part in Children(partsElement, "Part"))
                {
                    var pname = Attr(part, "Name");
                    if (string.IsNullOrWhiteSpace(pname)) continue;
                    if (!string.Equals(pname, "TrCoil", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(pname, "IlCoil", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(pname, "SvCoil", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var coilUid = Attr(part, "UId");
                    if (string.IsNullOrWhiteSpace(coilUid)) continue;
                    var inExpr = BuildExpressionForTarget(
                        Endpoint.Name(coilUid, "in"),
                        wireIndex, accessMap, partMap,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase));

                    var block = new PldAnchorBlock
                    {
                        Type = PldAnchorType.Coil,
                        Anchor = pname,
                        TypeText = displayKind
                    };
                    if (inExpr != null) CollectContactsFromExpression(inExpr, block.DirectContacts);
                    blocks.Add(block);
                }

                // 3) FB/FC Call
                foreach (var call in Children(partsElement, "Call"))
                {
                    var callUid = Attr(call, "UId");
                    if (string.IsNullOrWhiteSpace(callUid)) continue;
                    var callInfo = Child(call, "CallInfo");
                    var callTypeName = Attr(callInfo, "Name") ?? "UnknownCall";
                    var callInstanceName = BuildCallInstanceName(call, callInfo);
                    var inputParams = GetParameters(callInfo, "Input");
                    var outputParams = GetParameters(callInfo, "Output");

                    var block = new PldAnchorBlock
                    {
                        Type = PldAnchorType.FbCall,
                        Anchor = callInstanceName,
                        TypeText = $"{PldMarkdownTokens.FbCallToken}: {callTypeName}"
                    };
                    foreach (var pin in inputParams)
                    {
                        var expr = BuildExpressionForTarget(
                            Endpoint.Name(callUid, pin),
                            wireIndex, accessMap, partMap,
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                        var contacts = new List<ContactRef>();
                        if (expr != null) CollectContactsFromExpression(expr, contacts);
                        block.InPinContacts[pin] = contacts;
                    }
                    foreach (var pin in outputParams)
                    {
                        var sig = ResolveMappedSignal(Endpoint.Name(callUid, pin), wireIndex, accessMap);
                        if (!string.IsNullOrWhiteSpace(sig)) block.OutPins[pin] = sig;
                    }
                    blocks.Add(block);
                }
            }

            return blocks;
        }

        /// <summary>
        /// 扫描 LogicUnit XML，返回屏蔽态报警网络识别的关键证据：
        /// <list type="number">
        ///   <item><c>HasScoil</c>：本 LU 是否含 ≥1 个 <c>SCoil</c> 部件（置位线圈 — 典型报警网络结构）。</item>
        ///   <item><c>HasMaskPlaceholder</c>：本 LU 任意 <c>&lt;Access Scope="GlobalVariable"&gt;</c> 末段是否为
        ///     <c>AlwaysTRUE</c> / <c>AlwaysFALSE</c> / <c>Null</c> 占位符（屏蔽开关）。</item>
        ///   <item><c>AllGlobalVariableNames</c>：本 LU 出现过的所有 <c>&lt;Access Scope="GlobalVariable"&gt;</c>
        ///     完整 operand 名称去重集合（按 BuildAccessName 规则拼装的点路径）。</item>
        /// </list>
        ///
        /// 用于 <see cref="PldStructuralConstraintAugmenter"/> 屏蔽态报警识别：
        /// <c>HasScoil &amp;&amp; HasMaskPlaceholder</c> = 屏蔽态报警网络，对
        /// <c>AllGlobalVariableNames</c> 里命中 IO Tag 表 + 地址 <c>%I</c> 的信号各产 1 条
        /// <c>MUST_BE_FALSE</c> 单 Target 约束。
        ///
        /// **不沿 Wire 拓扑追触发条件**——避免 <c>TON</c>/<c>TOF</c> 等非布尔部件阻断（典型 FZ92 → TON → SCoil 链路）。
        /// </summary>
        public static (bool HasScoil, bool HasMaskPlaceholder, HashSet<string> AllGlobalVariableNames)
            ScanMaskedAlarmEvidence(string? rawContent)
        {
            var globals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(rawContent)) return (false, false, globals);
            var document = ParseFlgNetDocument(rawContent);
            var root = document?.Root;
            if (root == null) return (false, false, globals);

            bool hasScoil = false;
            bool hasPlaceholder = false;
            foreach (var lr in EnumerateSupportedLogicRoots(root))
            {
                if (!TryBuildLogicContext(lr, out var partsElement, out _, out _, out _)) continue;
                foreach (var part in Children(partsElement, "Part"))
                {
                    if (string.Equals(Attr(part, "Name"), "SCoil", StringComparison.OrdinalIgnoreCase))
                    {
                        hasScoil = true;
                        break;
                    }
                }
                foreach (var access in Children(partsElement, "Access"))
                {
                    if (!string.Equals(Attr(access, "Scope"), "GlobalVariable", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var name = BuildAccessName(access);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    globals.Add(name);
                    if (hasPlaceholder) continue;
                    var lastDot = name.LastIndexOf('.');
                    var lastSeg = lastDot >= 0 ? name[(lastDot + 1)..] : name;
                    if (lastSeg.Equals("AlwaysTRUE", StringComparison.OrdinalIgnoreCase)
                        || lastSeg.Equals("AlwaysFALSE", StringComparison.OrdinalIgnoreCase)
                        || lastSeg.Equals("Null", StringComparison.OrdinalIgnoreCase))
                    {
                        hasPlaceholder = true;
                    }
                }
            }
            return (hasScoil, hasPlaceholder, globals);
        }

        /// <summary>
        /// 把 <see cref="BoolExpression"/> 递归展开成扁平 <see cref="ContactRef"/> 列表：
        /// <c>Signal</c> → NO 触点；<c>Not(Signal)</c> → NC 触点；<c>And/Or/Not(复合)</c> → 递归。
        /// </summary>
        private static void CollectContactsFromExpression(BoolExpression? expr, List<ContactRef> sink)
        {
            if (expr == null) return;
            switch (expr.Kind)
            {
                case "Signal":
                    if (!string.IsNullOrWhiteSpace(expr.SignalName))
                        sink.Add(new ContactRef("NO", expr.SignalName!));
                    break;
                case "Not":
                    if (expr.Children.Count == 1 && expr.Children[0].Kind == "Signal"
                        && !string.IsNullOrWhiteSpace(expr.Children[0].SignalName))
                    {
                        sink.Add(new ContactRef("NC", expr.Children[0].SignalName!));
                    }
                    else
                    {
                        foreach (var c in expr.Children) CollectContactsFromExpression(c, sink);
                    }
                    break;
                case "And":
                case "Or":
                    foreach (var c in expr.Children) CollectContactsFromExpression(c, sink);
                    break;
                // True / False / Constant / 其它 → 没有触点，跳过
            }
        }

        /// <summary>
        /// 结构判定：是否是 Sensor Mapping LU。
        /// 特征（跨项目通用，与命名无关）：
        ///   1. 所有赋值的 LHS 都包含同一个 DB 路径前缀（如 `DB610_OP060_User.DI.Sensor` 出现 N 次）
        ///   2. 所有 RHS 都是不同的「单一符号名」（外部 IO Tag），且 N≥3 个
        ///   3. 至少 80% 的赋值匹配该模式（少量例外允许）
        /// 这种 LU 是「IO 数据搬运」中转，所有信号都没有控制语义，约束生成应跳过整个 LU。
        /// </summary>
        private static bool IsSensorMappingPattern(List<(string Lhs, string Rhs)> assigns)
        {
            if (assigns == null || assigns.Count < 3) return false;
            // 统计 LHS 的"DB 路径前缀"出现次数
            // LHS 形如 "DB610_OP060_User.DI.Sensor" 或 "DB610_OP060_User.DI.Sensor[1]" 等
            var lhsPrefixCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (lhs, _) in assigns)
            {
                if (string.IsNullOrWhiteSpace(lhs)) continue;
                // 取 LHS 的"DB.路径"前缀（去掉末尾 [N] 等下标）
                var key = System.Text.RegularExpressions.Regex.Replace(lhs.Trim(), @"\[[^\]]*\]$", "");
                if (!lhsPrefixCount.ContainsKey(key)) lhsPrefixCount[key] = 0;
                lhsPrefixCount[key]++;
            }
            // 找出最频繁的 LHS 前缀
            var topLhs = lhsPrefixCount.OrderByDescending(kv => kv.Value).First();
            if (topLhs.Value < 3) return false;
            // 至少 80% 的赋值要指向这个 LHS
            if (topLhs.Value * 1.0 / assigns.Count < 0.8) return false;
            // RHS 都是不同的单一符号（不含 := 或括号或运算符）
            var rhsSymbolRegex = new System.Text.RegularExpressions.Regex(@"^[A-Za-z_][A-Za-z0-9_\-\.\[\]]*$");
            int rhsSimpleCount = 0;
            var rhsSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (lhs, rhs) in assigns)
            {
                var key = System.Text.RegularExpressions.Regex.Replace((lhs ?? "").Trim(), @"\[[^\]]*\]$", "");
                if (!string.Equals(key, topLhs.Key, StringComparison.OrdinalIgnoreCase)) continue;
                var r = (rhs ?? "").Trim();
                if (rhsSymbolRegex.IsMatch(r))
                {
                    rhsSimpleCount++;
                    rhsSeen.Add(r);
                }
            }
            // 至少 80% 的相关赋值是 RHS 单一符号，且至少 3 个不同的 RHS
            if (rhsSimpleCount * 1.0 / topLhs.Value < 0.8) return false;
            if (rhsSeen.Count < 3) return false;
            return true;
        }

        /// <summary>
        /// 按博图 FB 标准引脚名分类输出引脚语义：
        ///   Q 输出（QFwd/QBkd 等）  → 真正的物理输出，参与 Pairing/Mutex/Safety
        ///   到位反馈（FwdPos/BkdPos）→ DB 内部状态字段，不参与瞬时约束
        ///   报警位（FwdErr/BkdErr/FwdLockErr/BkdLockErr） → 时延报警位，绝不参与瞬时 Pairing
        /// 跨项目通用 —— 博图所有 FB 设计都遵循这套引脚命名约定。
        /// </summary>
        private static string ClassifyOutputPinSemantics(string pinName)
        {
            if (string.IsNullOrWhiteSpace(pinName)) return string.Empty;
            // Q 输出（参与瞬时约束）
            if (pinName.Equals("QFwd", StringComparison.OrdinalIgnoreCase)
                || pinName.Equals("QBkd", StringComparison.OrdinalIgnoreCase)
                || pinName.StartsWith("Q", StringComparison.OrdinalIgnoreCase) && pinName.Length <= 6)
            {
                return "【Q 输出，参与 Pairing/Mutex/Safety】";
            }
            // 到位反馈位（DB 内部，不参与瞬时）
            if (pinName.EndsWith("Pos", StringComparison.OrdinalIgnoreCase))
            {
                return "【到位反馈位（DB 内部状态），不参与瞬时约束】";
            }
            // 报警位（时延，不参与瞬时 Pairing）
            if (pinName.EndsWith("Err", StringComparison.OrdinalIgnoreCase)
                || pinName.EndsWith("Error", StringComparison.OrdinalIgnoreCase)
                || pinName.EndsWith("LockErr", StringComparison.OrdinalIgnoreCase)
                || pinName.EndsWith("Warn", StringComparison.OrdinalIgnoreCase)
                || pinName.EndsWith("LockWarn", StringComparison.OrdinalIgnoreCase))
            {
                return "【报警位（时延），绝不参与瞬时 Pairing/IMPLIES】";
            }
            return string.Empty;
        }

        /// <summary>
        /// 把 BoolExpression 按缩进树渲染（与 AstMarkdownRendererService.RenderNode 风格对齐）。
        /// </summary>
        private static void RenderBoolExpressionAsTree(System.Text.StringBuilder sb, BoolExpression? expr, int indent)
        {
            var pad = new string(' ', indent * 2);
            if (expr == null)
            {
                sb.AppendLine($"{pad}- （无输入连接 / 始终成立）");
                return;
            }

            switch (expr.Kind)
            {
                case "True":
                    sb.AppendLine($"{pad}- 常量: `TRUE`");
                    break;
                case "False":
                    sb.AppendLine($"{pad}- 常量: `FALSE`");
                    break;
                case "Constant":
                    sb.AppendLine($"{pad}- 常量: `{expr.ConstantValue ?? "?"}`");
                    break;
                case "Signal":
                    sb.AppendLine($"{pad}- {PldMarkdownTokens.ContactOpenLabel}: `{expr.SignalName}`");
                    break;
                case "Not":
                    if (expr.Children.Count == 1 && expr.Children[0].Kind == "Signal")
                    {
                        sb.AppendLine($"{pad}- {PldMarkdownTokens.ContactClosedLabel}: `{expr.Children[0].SignalName}`");
                    }
                    else
                    {
                        sb.AppendLine($"{pad}- **非** (NOT) — 取反:");
                        foreach (var c in expr.Children)
                            RenderBoolExpressionAsTree(sb, c, indent + 1);
                    }
                    break;
                case "And":
                    sb.AppendLine($"{pad}- **且** (AND) — 以下条件全部满足:");
                    foreach (var c in expr.Children)
                        RenderBoolExpressionAsTree(sb, c, indent + 1);
                    break;
                case "Or":
                    sb.AppendLine($"{pad}- **或** (OR) — 以下条件任一满足:");
                    foreach (var c in expr.Children)
                        RenderBoolExpressionAsTree(sb, c, indent + 1);
                    break;
                default:
                    sb.AppendLine($"{pad}- {expr.Kind}");
                    foreach (var c in expr.Children)
                        RenderBoolExpressionAsTree(sb, c, indent + 1);
                    break;
            }
        }

        /// <summary>
        /// 从 Actions / StructuredText 段提取显式赋值（LHS := RHS）列表。
        /// </summary>
        private static List<(string Lhs, string Rhs)> ExtractScriptAssignments(XElement root, string rootName)
        {
            var assigns = new List<(string Lhs, string Rhs)>();

            // Actions：每个 <Action><Token> 序列
            foreach (var action in root.Descendants().Where(x => string.Equals(x.Name.LocalName, "Action", StringComparison.OrdinalIgnoreCase)))
            {
                var tokens = action.Descendants()
                    .Where(x => string.Equals(x.Name.LocalName, "Token", StringComparison.OrdinalIgnoreCase))
                    .Select(x => Attr(x, "Text") ?? "")
                    .ToList();
                var line = string.Join(" ", tokens).Replace("\"", "");
                var idx = line.IndexOf(":=", StringComparison.Ordinal);
                if (idx > 0)
                {
                    var lhs = line.Substring(0, idx).Trim().TrimEnd('\n', '\r', ' ');
                    var rhs = line.Substring(idx + 2).Trim().TrimEnd('\n', '\r', ' ');
                    if (!string.IsNullOrWhiteSpace(lhs))
                        assigns.Add((lhs, rhs));
                }
            }

            // StructuredText：根直接子按顺序扫 Access/Token，遇 Token Text=":=" 时
            if (string.Equals(rootName, "StructuredText", StringComparison.OrdinalIgnoreCase))
            {
                var direct = root.Elements().ToList();
                for (int i = 0; i < direct.Count; i++)
                {
                    var elem = direct[i];
                    if (string.Equals(elem.Name.LocalName, "Token", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(Attr(elem, "Text"), ":=", StringComparison.Ordinal))
                    {
                        string? lhs = null, rhs = null;
                        for (int k = i - 1; k >= 0; k--)
                        {
                            if (string.Equals(direct[k].Name.LocalName, "Access", StringComparison.OrdinalIgnoreCase))
                            {
                                lhs = BuildAccessName(direct[k]);
                                if (!string.IsNullOrWhiteSpace(lhs)) break;
                            }
                        }
                        for (int k = i + 1; k < direct.Count; k++)
                        {
                            if (string.Equals(direct[k].Name.LocalName, "Access", StringComparison.OrdinalIgnoreCase))
                            {
                                rhs = BuildAccessName(direct[k]);
                                if (!string.IsNullOrWhiteSpace(rhs)) break;
                            }
                        }
                        if (!string.IsNullOrWhiteSpace(lhs))
                            assigns.Add((lhs!, rhs ?? "(复杂表达式)"));
                    }
                }
            }
            return assigns;
        }

        public static List<CallPinMapping> ExtractCallPinMappingsFromLogicUnit(
            string rawContent,
            string blockName = null,
            string logicTitle = null)
        {
            if (string.IsNullOrWhiteSpace(rawContent))
            {
                return new List<CallPinMapping>();
            }

            var document = ParseFlgNetDocument(rawContent);
            var root = document?.Root;
            if (root == null)
            {
                return new List<CallPinMapping>();
            }

            var mappings = new List<CallPinMapping>();

            foreach (var logicRoot in EnumerateSupportedLogicRoots(root))
            {
                if (!TryBuildLogicContext(logicRoot, out var partsElement, out var accessMap, out _, out var wireIndex))
                {
                    continue;
                }

                foreach (var call in Children(partsElement, "Call"))
                {
                    var callUid = Attr(call, "UId");
                    if (string.IsNullOrWhiteSpace(callUid))
                    {
                        continue;
                    }

                    var callInfo = Child(call, "CallInfo");
                    var callInstanceName = BuildCallInstanceName(call, callInfo);

                    foreach (var parameter in GetParameters(callInfo, "Input"))
                    {
                        var signal = ResolveMappedSignal(Endpoint.Name(callUid, parameter), wireIndex, accessMap);
                        if (string.IsNullOrWhiteSpace(signal) || IsLiteralValue(signal))
                        {
                            continue;
                        }

                        mappings.Add(new CallPinMapping
                        {
                            SignalName = signal,
                            CallName = callInstanceName,
                            PinName = parameter,
                            PinSection = "Input",
                            BlockName = blockName ?? string.Empty
                        });
                    }

                    foreach (var parameter in GetParameters(callInfo, "Output"))
                    {
                        var signal = ResolveMappedSignal(Endpoint.Name(callUid, parameter), wireIndex, accessMap);
                        if (string.IsNullOrWhiteSpace(signal) || IsLiteralValue(signal))
                        {
                            continue;
                        }

                        mappings.Add(new CallPinMapping
                        {
                            SignalName = signal,
                            CallName = callInstanceName,
                            PinName = parameter,
                            PinSection = "Output",
                            BlockName = blockName ?? string.Empty
                        });
                    }
                }
            }

            return mappings
                .GroupBy(x => $"{x.SignalName}|{x.CallName}|{x.PinName}|{x.PinSection}|{x.BlockName}", StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .ToList();
        }

        private static HashSet<string> ExtractInternalCallPinNamesFromLogicUnit(
            string rawContent,
            string blockName = null,
            string logicTitle = null)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(rawContent))
            {
                return result;
            }

            var document = ParseFlgNetDocument(rawContent);
            var root = document?.Root;
            if (root == null)
            {
                return result;
            }

            foreach (var logicRoot in EnumerateSupportedLogicRoots(root))
            {
                if (!TryBuildLogicContext(logicRoot, out var partsElement, out _, out _, out _))
                {
                    continue;
                }

                foreach (var call in Children(partsElement, "Call"))
                {
                    var callInfo = Child(call, "CallInfo");
                    var callInstanceName = BuildCallInstanceName(call, callInfo);
                    if (string.IsNullOrWhiteSpace(callInstanceName))
                    {
                        continue;
                    }

                    foreach (var parameter in GetParameters(callInfo, "Input"))
                    {
                        result.Add($"{callInstanceName}.{parameter}");
                    }

                    foreach (var parameter in GetParameters(callInfo, "Output"))
                    {
                        result.Add($"{callInstanceName}.{parameter}");
                    }
                }
            }

            return result;
        }

        public static List<DeterministicConstraintDraft> Extract(string flgNetXml)
        {
            if (string.IsNullOrWhiteSpace(flgNetXml))
            {
                return new List<DeterministicConstraintDraft>();
            }

            var document = ParseFlgNetDocument(flgNetXml);
            var root = document?.Root;
            if (root == null || !string.Equals(root.Name.LocalName, "FlgNet", StringComparison.OrdinalIgnoreCase))
            {
                return new List<DeterministicConstraintDraft>();
            }

            var partsElement = Child(root, "Parts");
            var wiresElement = Child(root, "Wires");
            if (partsElement == null || wiresElement == null)
            {
                return new List<DeterministicConstraintDraft>();
            }

            var accessMap = BuildAccessMap(partsElement);
            var partMap = BuildPartMap(partsElement);
            var wireIndex = BuildWireIndex(wiresElement);
            var drafts = new List<DeterministicConstraintDraft>();
            var callSemanticSnapshots = new List<(string OriginCallName, string SignalCallName, Dictionary<string, string> OutputMappings, Dictionary<string, List<List<ExpressionTerm>>> InputBranches)>();

            foreach (var call in Children(partsElement, "Call"))
            {
                var callUid = Attr(call, "UId");
                if (string.IsNullOrWhiteSpace(callUid))
                {
                    continue;
                }

                var callInfo = Child(call, "CallInfo");
                var callTypeName = Attr(callInfo, "Name") ?? "UnknownCall";
                var callSignalName = BuildCallInstanceName(call, callInfo);
                var callName = string.IsNullOrWhiteSpace(callSignalName) ? callTypeName : callSignalName;
                var callDraftStartIndex = drafts.Count;
                var inputParameters = GetParameters(callInfo, "Input");
                var outputParameters = GetParameters(callInfo, "Output");
                var outputMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var inputBranches = new Dictionary<string, List<List<ExpressionTerm>>>(StringComparer.OrdinalIgnoreCase);

                foreach (var outputParameter in outputParameters)
                {
                    var targetSignal = ResolveMappedSignal(Endpoint.Name(callUid, outputParameter), wireIndex, accessMap);
                    if (string.IsNullOrWhiteSpace(targetSignal) || IsLiteralValue(targetSignal))
                    {
                        continue;
                    }

                    outputMappings[outputParameter] = targetSignal;
                    drafts.Add(new DeterministicConstraintDraft
                    {
                        RuleName = $"{targetSignal}_输出映射",
                        RuleType = "Pairing",
                        Operator = "IMPLIES",
                        OriginSemanticKey = $"CallOutputMap:{outputParameter}",
                        Confidence = 0.99m,
                        Priority = "Info",
                        Description = $"{callName}.{outputParameter} 这个块输出直接接到了 {targetSignal}，也就是前者动作时，后者应作为对应输出信号成立。",
                        Signals = new List<DeterministicSignalDraft>
                        {
                            new() { SignalName = $"{callName}.{outputParameter}", SignalRole = "Source" },
                            new() { SignalName = targetSignal, SignalRole = "Target" }
                        }
                    });
                }

                AddDirectionalMutex(outputMappings, drafts);

                foreach (var inputParameter in inputParameters)
                {
                    var expression = BuildExpressionForTarget(
                        Endpoint.Name(callUid, inputParameter),
                        wireIndex,
                        accessMap,
                        partMap,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase));

                    if (expression == null || !expression.TryCollectTerms(out var branches))
                    {
                        continue;
                    }

                    inputBranches[inputParameter] = branches;

                    var signalNames = branches
                        .SelectMany(branch => branch)
                        .Select(term => term.SignalName)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (signalNames.Count == 0)
                    {
                        continue;
                    }

                    var targetName = $"{callName}.{inputParameter}";
                    var containsNegation = branches.SelectMany(branch => branch).Any(term => !term.ExpectedValue);

                    if (branches.Count == 1 && !containsNegation)
                    {
                        var onlyBranch = branches[0];
                        if (onlyBranch.Count == 1)
                        {
                            drafts.Add(new DeterministicConstraintDraft
                            {
                                RuleName = $"{targetName}_输入映射",
                                RuleType = "Pairing",
                                Operator = "IMPLIES",
                                OriginSemanticKey = $"CallInputMap:{inputParameter}",
                                Confidence = 0.98m,
                                Priority = "Info",
                                Description = $"{onlyBranch[0].SignalName} 直接送入 {targetName}，也就是前一个信号成立时，这个输入条件应被视为已满足。",
                                Signals = new List<DeterministicSignalDraft>
                                {
                                    new() { SignalName = onlyBranch[0].SignalName, SignalRole = "Source" },
                                    new() { SignalName = targetName, SignalRole = "Target" }
                                }
                            });
                            continue;
                        }

                        // 对任意中间块/库块调用输入，不能仅凭“多个正向触点串联到同一 pin”
                        // 就直接固化成 ALL_TRUE。复杂调用 pin 的真实语义必须结合调用展开后的
                        // callee 逻辑、pin 绑定和完整上下文统一判断。
                        drafts.Add(new DeterministicConstraintDraft
                        {
                            RuleName = $"{targetName}_前置全满足",
                            RuleType = "AllRequired",
                            Operator = "ALL_TRUE",
                            OriginSemanticKey = $"CallInputLogic:{inputParameter}:AllTrue",
                            Confidence = 0.97m,
                            Priority = "Warning",
                            Description = $"{targetName} 要成立，前面这一串条件必须同时满足；少一个条件不成立，这个输入条件就不应成立。",
                            Signals = onlyBranch
                                .Select((term, index) => new DeterministicSignalDraft
                                {
                                    SignalName = term.SignalName,
                                    SignalRole = "Member",
                                    SortOrder = index
                                })
                                .Concat(new[]
                                {
                                    new DeterministicSignalDraft
                                    {
                                        SignalName = targetName,
                                        SignalRole = "Target",
                                        SortOrder = onlyBranch.Count
                                    }
                                })
                                .ToList()
                        });
                        continue;
                    }

                    // 对带 OR / NOT / 多分支的调用输入同样保持保守，不在这里直接生成 CUSTOM。
                    // 这类规则统一交给“调用展开后的完整上下文 + AI”处理。
                    drafts.Add(new DeterministicConstraintDraft
                    {
                        RuleName = $"{targetName}_合法组合",
                        RuleType = "Custom",
                        Operator = "CUSTOM",
                        OriginSemanticKey = $"CallInputLogic:{inputParameter}:Custom",
                        Confidence = 0.99m,
                        Priority = "Warning",
                        Description = $"{targetName} 不是单一信号直接决定的，而是由前面多路并联、串联或取反后的组合关系共同决定。",
                        ValidStates = BuildTruthTableJson(signalNames, targetName, expression),
                        Signals = signalNames
                            .Select((signalName, index) => new DeterministicSignalDraft
                            {
                                SignalName = signalName,
                                SignalRole = "Member",
                                SortOrder = index
                            })
                            .Concat(new[]
                            {
                                new DeterministicSignalDraft
                                {
                                    SignalName = targetName,
                                    SignalRole = "Target",
                                    SortOrder = signalNames.Count
                                }
                            })
                        .ToList()
                    });
                }

                // 调用输入的方向/许可语义不再在这里靠 pin 名称直接固化为规则，
                // 统一交给“调用展开后的完整上下文 + AI”处理，避免不同中间块被同一套命名启发式误判。
                callSemanticSnapshots.Add((callTypeName, callName, outputMappings, inputBranches));
                MarkDraftOrigin(drafts, callDraftStartIndex, callTypeName);
            }

            // Call pin input semantics are evaluated after each call's branches have been collected.
            foreach (var snapshot in callSemanticSnapshots)
            {
                var snapshotDraftStartIndex = drafts.Count;
                AddDirectionalMutexFromCallInputs(snapshot.SignalCallName, snapshot.InputBranches, drafts);
                AddPermitInterlockRules(snapshot.SignalCallName, snapshot.InputBranches, drafts);
                AddOutputFeedbackImplicationRules(snapshot.SignalCallName, snapshot.OutputMappings, snapshot.InputBranches, drafts);
                MarkDraftOrigin(drafts, snapshotDraftStartIndex, snapshot.OriginCallName);
            }

            ExtractStandardCoilRules(partsElement, accessMap, partMap, wireIndex, drafts);

            return Deduplicate(drafts);
        }

        /// <summary>
        /// 判断一个信号（路径可能是 A.B.C 形式）的末段是否为安全信号。
        /// 优先匹配末段，避免把 DB 名称里的无关词误判。
        /// </summary>
        private static bool IsSafetySignal(string signalName)
        {
            if (string.IsNullOrWhiteSpace(signalName)) return false;

            // 取最后一段（点号分隔路径的末段，如 General.E_StopStatus → E_StopStatus）
            var lastSegment = signalName.Contains('.')
                ? signalName[(signalName.LastIndexOf('.') + 1)..]
                : signalName;

            var upper = lastSegment.ToUpperInvariant();

            // 急停
            if (upper.Contains("E_STOP") || upper.Contains("ESTOP") ||
                upper.Contains("EMERGENCYSTOP") || upper.Contains("急停"))
                return true;

            // 安全门 / 防护门 / 门锁
            if (upper.Contains("SAFEDOOR") || upper.Contains("SAFETYDOOR") ||
                upper.Contains("DOORLOCK") || upper.Contains("DOORGUARD") ||
                upper.Contains("SAFEGATE") || upper.Contains("SAFETYGATE") ||
                upper.Contains("安全门") || upper.Contains("防护门") || upper.Contains("门锁"))
                return true;

            // 光幕 / 光栅
            if (upper.Contains("LIGHTCURTAIN") || upper.Contains("SAFETYCURTAIN") ||
                upper.Contains("光幕") || upper.Contains("光栅"))
                return true;

            // 安全区 / 安全带
            if (upper.Contains("SAFETYZONE") || upper.Contains("SAFEZONE"))
                return true;

            // 通用安全标记（末段必须以 Safe/Safety 开头或包含 Safe，且不只是 SafeDoorValveFbk 这类反馈）
            // 规则：末段包含 SAFE 但不以 FBK/STATUS/OK/NOTOK 结尾，认为是安全使能信号
            if (upper.Contains("SAFE") &&
                !upper.EndsWith("FBK") && !upper.EndsWith("STATUS") &&
                !upper.EndsWith("OK") && !upper.EndsWith("NOTOK") &&
                !upper.EndsWith("VALVE"))
                return true;

            // 通用互锁 Interlock（末段）
            if (upper == "INTERLOCK" || upper.StartsWith("IL_") || upper.EndsWith("_IL"))
                return true;

            return false;
        }

        private static List<DeterministicConstraintDraft> ExtractConditionWrapperRules(
            XElement wrapperRoot,
            string wrapperType,
            string wrapperName)
        {
            var flgNetElement = Child(wrapperRoot, "FlgNet");
            if (flgNetElement == null)
            {
                return new List<DeterministicConstraintDraft>();
            }

            var partsElement = Child(flgNetElement, "Parts");
            var wiresElement = Child(flgNetElement, "Wires");
            if (partsElement == null || wiresElement == null)
            {
                return new List<DeterministicConstraintDraft>();
            }

            var accessMap = BuildAccessMap(partsElement);
            var partMap = BuildPartMap(partsElement);
            var wireIndex = BuildWireIndex(wiresElement);
            var drafts = new List<DeterministicConstraintDraft>();

            var targetParts = Children(partsElement, "Part")
                .Where(part =>
                {
                    var name = Attr(part, "Name");
                    return string.Equals(name, "TrCoil", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(name, "IlCoil", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(name, "SvCoil", StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            foreach (var part in targetParts)
            {
                var coilTypeName = Attr(part, "Name"); // TrCoil / IlCoil / SvCoil
                var isInterlock = string.Equals(coilTypeName, "IlCoil", StringComparison.OrdinalIgnoreCase);

                var targetUid = Attr(part, "UId");
                if (string.IsNullOrWhiteSpace(targetUid))
                {
                    continue;
                }

                var expression = BuildExpressionForTarget(
                    Endpoint.Name(targetUid, "in"),
                    wireIndex,
                    accessMap,
                    partMap,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase));

                if (expression == null || !expression.TryCollectTerms(out var branches))
                {
                    continue;
                }

                var allTerms = branches.SelectMany(branch => branch).ToList();
                var signalNames = allTerms
                    .Select(term => term.SignalName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (signalNames.Count == 0)
                {
                    continue;
                }

                var targetName = $"{wrapperType}:{wrapperName}";

                // ── Safety 约束识别 ──────────────────────────────────────────────
                // 规则1：IlCoil（Interlock块）本身就是安全联锁语义，所有前置信号都是安全条件
                // 规则2：TrCoil/SvCoil 里含安全关键词的信号（常闭或常开）也生成 Safety
                var safetyTerms = isInterlock
                    ? allTerms  // IlCoil 全部视为安全
                    : allTerms.Where(t => IsSafetySignal(t.SignalName)).ToList();

                foreach (var st in safetyTerms.DistinctBy(t => t.SignalName, StringComparer.OrdinalIgnoreCase))
                {
                    drafts.Add(new DeterministicConstraintDraft
                    {
                        RuleName = $"{st.SignalName}_{wrapperName}_安全联锁",
                        RuleType = "Safety",
                        Operator = "REQUIRES_0",
                        Confidence = isInterlock ? 0.99m : 0.92m,
                        Priority = "Critical",
                        Description = isInterlock
                            ? $"Interlock 联锁条件：{st.SignalName} 不满足时，步 {wrapperName} 的动作必须被禁止。"
                            : $"安全信号 {st.SignalName} 不满足时，{wrapperType} {wrapperName} 不应成立。",
                        Signals = new List<DeterministicSignalDraft>
                        {
                            new() { SignalName = st.SignalName, SignalRole = "Source", SortOrder = 0 },
                            new() { SignalName = targetName, SignalRole = "Target", SortOrder = 1 }
                        }
                    });
                }

                // 非安全信号部分继续走原有 AllRequired / Custom 逻辑
                var nonSafetyTerms = isInterlock
                    ? new List<ExpressionTerm>()  // IlCoil 全部已作为 Safety 处理，不再生成 AllRequired
                    : allTerms.Where(t => !IsSafetySignal(t.SignalName)).ToList();

                var nonSafetySignals = nonSafetyTerms
                    .Select(t => t.SignalName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (nonSafetySignals.Count == 0)
                {
                    continue;
                }

                var containsNegation = nonSafetyTerms.Any(term => !term.ExpectedValue);

                if (branches.Count == 1 && !containsNegation)
                {
                    var onlyBranch = nonSafetyTerms;
                    drafts.Add(new DeterministicConstraintDraft
                    {
                        RuleName = $"{targetName}_前置条件",
                        RuleType = "AllRequired",
                        Operator = "ALL_TRUE",
                        Confidence = 0.98m,
                        Priority = "Warning",
                        Description = $"{wrapperType} {wrapperName} 要触发，前置条件需要同时满足；只要缺少其中之一，就不应成立。",
                        Signals = onlyBranch
                            .Select((term, index) => new DeterministicSignalDraft
                            {
                                SignalName = term.SignalName,
                                SignalRole = "Member",
                                SortOrder = index
                            })
                            .Concat(new[]
                            {
                                new DeterministicSignalDraft
                                {
                                    SignalName = targetName,
                                    SignalRole = "Target",
                                    SortOrder = onlyBranch.Count
                                }
                            })
                            .ToList()
                    });
                    continue;
                }

                drafts.Add(new DeterministicConstraintDraft
                {
                    RuleName = $"{targetName}_合法组合",
                    RuleType = "Custom",
                    Operator = "CUSTOM",
                    Confidence = 0.99m,
                    Priority = "Warning",
                    Description = $"{wrapperType} {wrapperName} 的触发不是单点决定的，而是由前面多路条件组合后共同决定。",
                    ValidStates = BuildTruthTableJson(nonSafetySignals, targetName, expression),
                    Signals = nonSafetySignals
                        .Select((signalName, index) => new DeterministicSignalDraft
                        {
                            SignalName = signalName,
                            SignalRole = "Member",
                            SortOrder = index
                        })
                        .Concat(new[]
                        {
                            new DeterministicSignalDraft
                            {
                                SignalName = targetName,
                                SignalRole = "Target",
                                SortOrder = nonSafetySignals.Count
                            }
                        })
                        .ToList()
                });
            }

            return Deduplicate(drafts);
        }

        private static IEnumerable<XElement> EnumerateSupportedLogicRoots(XElement root)
        {
            if (IsSupportedLogicRoot(root.Name.LocalName))
            {
                yield return root;
                yield break;
            }

            foreach (var nestedRoot in root.Descendants().Where(x => IsSupportedLogicRoot(x.Name.LocalName)))
            {
                yield return nestedRoot;
            }
        }

        private static bool TryBuildLogicContext(
            XElement logicRoot,
            out XElement partsElement,
            out Dictionary<string, string> accessMap,
            out Dictionary<string, PartInfo> partMap,
            out Dictionary<Endpoint, List<Endpoint>> wireIndex)
        {
            partsElement = null!;
            accessMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            partMap = new Dictionary<string, PartInfo>(StringComparer.OrdinalIgnoreCase);
            wireIndex = new Dictionary<Endpoint, List<Endpoint>>();

            var flgNet = string.Equals(logicRoot.Name.LocalName, "FlgNet", StringComparison.OrdinalIgnoreCase)
                ? logicRoot
                : Child(logicRoot, "FlgNet");
            if (flgNet == null)
            {
                return false;
            }

            var wiresElement = Child(flgNet, "Wires");
            partsElement = Child(flgNet, "Parts")!;
            if (partsElement == null || wiresElement == null)
            {
                return false;
            }

            accessMap = BuildAccessMap(partsElement);
            partMap = BuildPartMap(partsElement);
            wireIndex = BuildWireIndex(wiresElement);
            return true;
        }

        private static string BuildCallInstanceName(XElement call, XElement? callInfo)
        {
            var instance = Child(callInfo, "Instance");
            var instanceName = instance == null ? null : BuildAccessName(instance);
            return string.IsNullOrWhiteSpace(instanceName)
                ? (Attr(callInfo, "Name") ?? "UnknownCall")
                : instanceName;
        }

        private static DeterministicLogicExpressionDraft BuildLogicExpressionDraft(
            string expressionName,
            string expressionType,
            string targetSignal,
            BoolExpression expression,
            string description,
            int sortOrder)
        {
            return new DeterministicLogicExpressionDraft
            {
                ExpressionName = expressionName,
                ExpressionType = expressionType,
                TargetSignal = targetSignal,
                ExpressionText = RenderExpression(expression),
                ExpressionAstJson = SerializeExpressionAst(expression),
                Description = description,
                SortOrder = sortOrder,
                Signals = CollectExpressionSignals(expression)
                    .Select((signal, index) => new DeterministicLogicExpressionSignalDraft
                    {
                        SignalName = signal.SignalName,
                        SignalRole = "Operand",
                        ExpectedBoolValue = signal.ExpectedValue,
                        SortOrder = index
                    })
                    .ToList()
            };
        }

        private static string SerializeExpressionAst(BoolExpression expression)
        {
            object BuildAstNode(BoolExpression node)
            {
                return new
                {
                    kind = node.Kind,
                    signalName = node.SignalName,
                    constantValue = node.ConstantValue,
                    children = node.Children.Select(BuildAstNode).ToList()
                };
            }

            return JsonSerializer.Serialize(BuildAstNode(expression));
        }

        private static string RenderExpression(BoolExpression expression)
        {
            return expression.Kind switch
            {
                "True" => "TRUE",
                "False" => "FALSE",
                "Signal" => expression.SignalName ?? string.Empty,
                "Constant" => expression.ConstantValue ?? string.Empty,
                "Not" => $"NOT ({RenderExpression(expression.Children[0])})",
                "And" => $"({string.Join(" AND ", expression.Children.Select(RenderExpression))})",
                "Or" => $"({string.Join(" OR ", expression.Children.Select(RenderExpression))})",
                _ => string.Empty
            };
        }

        private static List<ExpressionTerm> CollectExpressionSignals(BoolExpression expression)
        {
            var signals = new List<ExpressionTerm>();

            void Visit(BoolExpression node, bool expectedValue = true)
            {
                switch (node.Kind)
                {
                    case "Signal" when !string.IsNullOrWhiteSpace(node.SignalName):
                        signals.Add(new ExpressionTerm(node.SignalName!, expectedValue));
                        break;
                    case "Not" when node.Children.Count == 1:
                        Visit(node.Children[0], !expectedValue);
                        break;
                    default:
                        foreach (var child in node.Children)
                        {
                            Visit(child, expectedValue);
                        }
                        break;
                }
            }

            Visit(expression);

            return signals
                .GroupBy(x => $"{x.SignalName}|{x.ExpectedValue}", StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .ToList();
        }

        private static List<DeterministicLogicExpressionDraft> DeduplicateLogicExpressions(
            List<DeterministicLogicExpressionDraft> drafts)
        {
            return drafts
                .Where(x => !string.IsNullOrWhiteSpace(x.TargetSignal) && !string.IsNullOrWhiteSpace(x.ExpressionText))
                .GroupBy(
                    x => $"{x.ExpressionType}|{x.TargetSignal}|{x.ExpressionText}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .OrderBy(x => x.SortOrder ?? int.MaxValue)
                .ToList();
        }

        private static bool ShouldKeepConstraintDraft(
            DeterministicConstraintDraft draft,
            HashSet<string> externalSignals,
            HashSet<string> callPinSignals)
        {
            if (draft?.Signals == null || draft.Signals.Count == 0)
            {
                return false;
            }

            var materialSignals = draft.Signals
                .Select(x => x.SignalName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (string.Equals(draft.OriginKind, "CallSite", StringComparison.OrdinalIgnoreCase))
            {
                return materialSignals.All(signal =>
                    !IsLiteralValue(signal) &&
                    (externalSignals.Contains(signal) ||
                     callPinSignals.Contains(signal) ||
                     LooksLikeStructuredSignal(signal)));
            }

            var externalCount = materialSignals.Count(signal => externalSignals.Contains(signal));
            if (externalCount == 0)
            {
                return false;
            }

            return materialSignals.All(signal =>
                externalSignals.Contains(signal) ||
                callPinSignals.Contains(signal) ||
                signal.StartsWith("Transition:", StringComparison.OrdinalIgnoreCase) ||
                signal.StartsWith("Interlock:", StringComparison.OrdinalIgnoreCase) ||
                signal.StartsWith("Supervision:", StringComparison.OrdinalIgnoreCase));
        }

        private static void AddOutputFeedbackImplicationRules(
            string callName,
            Dictionary<string, string> outputMappings,
            Dictionary<string, List<List<ExpressionTerm>>> inputBranches,
            List<DeterministicConstraintDraft> drafts)
        {
            AddOutputFeedbackImplicationRule(callName, "QFwd", "IWP", outputMappings, inputBranches, drafts);
            AddOutputFeedbackImplicationRule(callName, "QBkd", "IHP", outputMappings, inputBranches, drafts);
        }

        private static void AddOutputFeedbackImplicationRule(
            string callName,
            string outputPinName,
            string feedbackPinName,
            Dictionary<string, string> outputMappings,
            Dictionary<string, List<List<ExpressionTerm>>> inputBranches,
            List<DeterministicConstraintDraft> drafts)
        {
            if (!outputMappings.TryGetValue(outputPinName, out var outputSignal) ||
                !inputBranches.TryGetValue(feedbackPinName, out var feedbackBranches))
            {
                return;
            }

            var feedbackSignals = feedbackBranches
                .SelectMany(branch => branch)
                .Where(term => term.ExpectedValue && !string.IsNullOrWhiteSpace(term.SignalName))
                .Select(term => term.SignalName)
                .Where(signal => !LooksLikeSameOutputPinReference(signal, outputPinName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var feedbackSignal in feedbackSignals)
            {
                drafts.Add(new DeterministicConstraintDraft
                {
                    RuleName = $"{outputSignal}_{feedbackSignal}_方向配对",
                    RuleType = "Pairing",
                    Operator = "IMPLIES",
                    OriginSemanticKey = $"CallFeedback:{outputPinName}:{feedbackPinName}",
                    Confidence = 0.97m,
                    Priority = "Warning",
                    Description = $"在 FlgNet 接线中，{outputSignal} 属于 {callName}.{outputPinName} 的外部输出，{feedbackSignal} 属于 {callName}.{feedbackPinName} 的外部反馈，因此方向应为输出到反馈。",
                    Signals = new List<DeterministicSignalDraft>
                    {
                        new() { SignalName = outputSignal, SignalRole = "Source", SortOrder = 0 },
                        new() { SignalName = feedbackSignal, SignalRole = "Target", SortOrder = 1 }
                    }
                });
            }
        }

        private static XDocument? ParseFlgNetDocument(string xml)
        {
            try
            {
                return XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            }
            catch
            {
                return null;
            }
        }

        private static List<string> GetParameters(XElement? callInfo, string section)
        {
            return callInfo?
                .Elements()
                .Where(x => string.Equals(x.Name.LocalName, "Parameter", StringComparison.OrdinalIgnoreCase))
                .Where(x => string.Equals(Attr(x, "Section"), section, StringComparison.OrdinalIgnoreCase))
                .Select(x => Attr(x, "Name"))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Cast<string>()
                .ToList()
                ?? new List<string>();
        }

        private static Dictionary<string, string> BuildAccessMap(XElement partsElement)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var access in Children(partsElement, "Access"))
            {
                var uid = Attr(access, "UId");
                if (string.IsNullOrWhiteSpace(uid))
                {
                    continue;
                }

                var accessName = BuildAccessName(access);
                if (!string.IsNullOrWhiteSpace(accessName))
                {
                    map[uid] = accessName;
                }
            }

            return map;
        }

        private static string BuildAccessName(XElement access)
        {
            var scope = Attr(access, "Scope");
            if (string.Equals(scope, "LiteralConstant", StringComparison.OrdinalIgnoreCase))
            {
                return access.Descendants().FirstOrDefault(x => x.Name.LocalName == "ConstantValue")?.Value?.Trim() ?? string.Empty;
            }

            var symbol = Child(access, "Symbol");
            var componentContainer = symbol ?? access;

            var segments = new List<string>();
            foreach (var component in Children(componentContainer, "Component"))
            {
                var componentName = Attr(component, "Name");
                if (string.IsNullOrWhiteSpace(componentName))
                {
                    continue;
                }

                if (string.Equals(Attr(component, "AccessModifier"), "Array", StringComparison.OrdinalIgnoreCase))
                {
                    var index = component.Descendants().FirstOrDefault(x => x.Name.LocalName == "ConstantValue")?.Value?.Trim();
                    segments.Add(string.IsNullOrWhiteSpace(index) ? componentName : $"{componentName}[{index}]");
                    continue;
                }

                segments.Add(componentName);
            }

            return string.Join(".", segments);
        }

        private static Dictionary<string, PartInfo> BuildPartMap(XElement partsElement)
        {
            var map = new Dictionary<string, PartInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in Children(partsElement, "Part"))
            {
                var uid = Attr(part, "UId");
                var name = Attr(part, "Name");
                if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var negatedPorts = Children(part, "Negated")
                    .Select(x => Attr(x, "Name"))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Cast<string>()
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                map[uid] = new PartInfo(name, negatedPorts);
            }

            return map;
        }

        private static Dictionary<Endpoint, List<Endpoint>> BuildWireIndex(XElement wiresElement)
        {
            var index = new Dictionary<Endpoint, List<Endpoint>>();

            foreach (var wire in Children(wiresElement, "Wire"))
            {
                var endpoints = new List<Endpoint>();
                foreach (var node in wire.Elements())
                {
                    switch (node.Name.LocalName)
                    {
                        case "Powerrail":
                            endpoints.Add(Endpoint.Powerrail());
                            break;
                        case "IdentCon":
                            endpoints.Add(Endpoint.Ident(Attr(node, "UId")));
                            break;
                        case "OpenCon":
                            endpoints.Add(Endpoint.Open(Attr(node, "UId")));
                            break;
                        case "NameCon":
                            endpoints.Add(Endpoint.Name(Attr(node, "UId"), Attr(node, "Name")));
                            break;
                    }
                }

                for (var i = 0; i < endpoints.Count; i++)
                {
                    for (var j = 0; j < endpoints.Count; j++)
                    {
                        if (i == j)
                        {
                            continue;
                        }

                        if (!index.TryGetValue(endpoints[i], out var peers))
                        {
                            peers = new List<Endpoint>();
                            index[endpoints[i]] = peers;
                        }

                        peers.Add(endpoints[j]);
                    }
                }
            }

            return index;
        }

        private static string? ResolveMappedSignal(
            Endpoint endpoint,
            Dictionary<Endpoint, List<Endpoint>> wireIndex,
            Dictionary<string, string> accessMap)
        {
            if (!wireIndex.TryGetValue(endpoint, out var peers))
            {
                return null;
            }

            return peers
                .Where(x => x.Kind == EndpointKind.IdentCon)
                .Select(x => accessMap.TryGetValue(x.UId ?? string.Empty, out var signal) ? signal : null)
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        }

        private static BoolExpression? BuildExpressionForTarget(
            Endpoint targetEndpoint,
            Dictionary<Endpoint, List<Endpoint>> wireIndex,
            Dictionary<string, string> accessMap,
            Dictionary<string, PartInfo> partMap,
            HashSet<string> visited)
        {
            var visitKey = targetEndpoint.ToString();
            if (!visited.Add(visitKey))
            {
                return null;
            }

            if (!wireIndex.TryGetValue(targetEndpoint, out var peers))
            {
                return null;
            }

            foreach (var peer in peers)
            {
                var expression = BuildExpressionFromEndpoint(peer, wireIndex, accessMap, partMap, visited);
                if (expression != null)
                {
                    return expression;
                }
            }

            return null;
        }

        private static BoolExpression? BuildExpressionFromEndpoint(
            Endpoint endpoint,
            Dictionary<Endpoint, List<Endpoint>> wireIndex,
            Dictionary<string, string> accessMap,
            Dictionary<string, PartInfo> partMap,
            HashSet<string> visited)
        {
            return endpoint.Kind switch
            {
                EndpointKind.Powerrail => BoolExpression.True(),
                EndpointKind.OpenCon => null,
                EndpointKind.IdentCon => BuildIdentExpression(endpoint, accessMap),
                EndpointKind.NameCon => BuildExpressionFromPartPort(endpoint, wireIndex, accessMap, partMap, visited),
                _ => null
            };
        }

        private static BoolExpression? BuildIdentExpression(Endpoint endpoint, Dictionary<string, string> accessMap)
        {
            if (!accessMap.TryGetValue(endpoint.UId ?? string.Empty, out var signalName) || string.IsNullOrWhiteSpace(signalName))
            {
                return null;
            }

            if (IsLiteralValue(signalName))
            {
                if (TryParseBooleanLiteral(signalName, out var boolValue))
                {
                    return boolValue ? BoolExpression.True() : BoolExpression.False();
                }

                return BoolExpression.Constant(signalName);
            }

            return BoolExpression.Signal(signalName);
        }

        private static BoolExpression? BuildExpressionFromPartPort(
            Endpoint endpoint,
            Dictionary<Endpoint, List<Endpoint>> wireIndex,
            Dictionary<string, string> accessMap,
            Dictionary<string, PartInfo> partMap,
            HashSet<string> visited)
        {
            if (string.IsNullOrWhiteSpace(endpoint.UId) || string.IsNullOrWhiteSpace(endpoint.PortName))
            {
                return null;
            }

            if (!partMap.TryGetValue(endpoint.UId, out var partInfo))
            {
                return null;
            }

            if (string.Equals(partInfo.Name, "Contact", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(endpoint.PortName, "out", StringComparison.OrdinalIgnoreCase))
            {
                var inExpression = BuildExpressionForTarget(Endpoint.Name(endpoint.UId, "in"), wireIndex, accessMap, partMap, visited)
                    ?? BoolExpression.True();
                var operandExpression = BuildExpressionForTarget(Endpoint.Name(endpoint.UId, "operand"), wireIndex, accessMap, partMap, visited);
                if (operandExpression == null)
                {
                    return null;
                }

                if (partInfo.NegatedPorts.Contains("operand"))
                {
                    operandExpression = BoolExpression.Not(operandExpression);
                }

                return BoolExpression.And(inExpression, operandExpression);
            }

            if (string.Equals(partInfo.Name, "O", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(endpoint.PortName, "out", StringComparison.OrdinalIgnoreCase))
            {
                var branches = new List<BoolExpression>();
                foreach (var inputPort in new[] { "in1", "in2", "in3", "in4", "in5", "in6", "in7", "in8" })
                {
                    var branch = BuildExpressionForTarget(Endpoint.Name(endpoint.UId, inputPort), wireIndex, accessMap, partMap, visited);
                    if (branch != null)
                    {
                        branches.Add(branch);
                    }
                }

                return branches.Count switch
                {
                    0 => null,
                    1 => branches[0],
                    _ => BoolExpression.Or(branches)
                };
            }

            // 定时器部件穿透（TON/TOF/TP/TONR）：
            // 这些部件输出 Q 是 IN 输入的"延时/脉冲"后状态，但对约束生成而言，IN 才是真正的逻辑条件。
            // 沿 Q 端反向追时，把 IN 端表达式作为透传源（忽略 PT 延时的瞬时语义）。
            // 典型场景（CLAUDE.md §4.5 Error2 漏识）：FZ92 → TON(50ms) → SCoil(Error2)。
            // 不加穿透：BuildExpressionForTarget(SCoil.in) → TON.Q → return null → SCoil 子树为空，FZ92 漏识
            // 加穿透：TON.Q 反向到 TON.IN → Contact(FZ92).out → Signal(FZ92)，SCoil 子树正常含 FZ92
            if (IsTimerPartName(partInfo.Name) &&
                (string.Equals(endpoint.PortName, "Q", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(endpoint.PortName, "q", StringComparison.OrdinalIgnoreCase)))
            {
                return BuildExpressionForTarget(
                    Endpoint.Name(endpoint.UId, "IN"),
                    wireIndex, accessMap, partMap, visited);
            }

            return BuildExpressionForTarget(endpoint, wireIndex, accessMap, partMap, visited);
        }

        /// <summary>
        /// 识别博图标准定时器部件（TON/TOF/TP/TONR）。
        /// 这些部件的 Q 输出对约束生成而言应「透传」自 IN 输入，忽略 PT 延时的瞬时语义。
        /// </summary>
        private static bool IsTimerPartName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            return string.Equals(name, "TON", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "TOF", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "TP", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "TONR", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "TOFR", StringComparison.OrdinalIgnoreCase);
        }

        private static void AddDirectionalMutex(
            Dictionary<string, string> outputMappings,
            List<DeterministicConstraintDraft> drafts)
        {
            if (!outputMappings.TryGetValue("QFwd", out var forwardSignal) ||
                !outputMappings.TryGetValue("QBkd", out var backwardSignal))
            {
                return;
            }

            if (!LooksLikeDirectionalPair(forwardSignal, backwardSignal))
            {
                return;
            }

            drafts.Add(new DeterministicConstraintDraft
            {
                RuleName = $"{forwardSignal}_{backwardSignal}_互斥",
                RuleType = "Mutex",
                Operator = "AT_MOST_ONE",
                OriginSemanticKey = "CallOutputMutex:QFwd:QBkd",
                Confidence = 0.99m,
                Priority = "Critical",
                    Description = $"{forwardSignal} 和 {backwardSignal} 属于同一机构的两个相反输出方向，现场上不允许同时成立，否则容易出现动作冲突。",
                Signals = new List<DeterministicSignalDraft>
                {
                    new() { SignalName = forwardSignal, SignalRole = "Member", SortOrder = 0 },
                    new() { SignalName = backwardSignal, SignalRole = "Member", SortOrder = 1 }
                }
            });
        }

        private static void AddDirectionalMutexFromCallInputs(
            string callName,
            Dictionary<string, List<List<ExpressionTerm>>> inputBranches,
            List<DeterministicConstraintDraft> drafts)
        {
            if (!inputBranches.TryGetValue("FwdAuto", out var forwardBranches) ||
                !inputBranches.TryGetValue("BkdAuto", out var backwardBranches))
            {
                return;
            }

            if (!TryGetSinglePositiveSignal(forwardBranches, out var forwardSignal) ||
                !TryGetSinglePositiveSignal(backwardBranches, out var backwardSignal))
            {
                return;
            }

            if (!LooksLikeDirectionalPair(forwardSignal, backwardSignal) ||
                AreEquivalentBranches(forwardBranches, backwardBranches))
            {
                return;
            }

            drafts.Add(new DeterministicConstraintDraft
            {
                RuleName = $"{callName}_{ExtractDirectionalBaseName(forwardSignal, backwardSignal)}_动作互锁",
                RuleType = "Mutex",
                Operator = "AT_MOST_ONE",
                OriginSemanticKey = "CallInputMutex:FwdAuto:BkdAuto",
                Confidence = 0.98m,
                Priority = "Critical",
                Description = $"{forwardSignal} 和 {backwardSignal} 表示同一机构的相反动作请求，两路同时成立不合理，需要保持互斥。",
                Signals = new List<DeterministicSignalDraft>
                {
                    new() { SignalName = forwardSignal, SignalRole = "Member", SortOrder = 0 },
                    new() { SignalName = backwardSignal, SignalRole = "Member", SortOrder = 1 }
                }
            });
        }

        private static void AddPermitInterlockRules(
            string callName,
            Dictionary<string, List<List<ExpressionTerm>>> inputBranches,
            List<DeterministicConstraintDraft> drafts)
        {
            AddSingleDirectionPermitRule(
                callName,
                "FwdAuto",
                "FwdPmt",
                "伸出互锁",
                inputBranches,
                drafts);

            AddSingleDirectionPermitRule(
                callName,
                "BkdAuto",
                "BkdPmt",
                "缩回互锁",
                inputBranches,
                drafts);
        }

        private static void AddSingleDirectionPermitRule(
            string callName,
            string autoParameterName,
            string permitParameterName,
            string suffixName,
            Dictionary<string, List<List<ExpressionTerm>>> inputBranches,
            List<DeterministicConstraintDraft> drafts)
        {
            if (!inputBranches.TryGetValue(autoParameterName, out var autoBranches) ||
                !inputBranches.TryGetValue(permitParameterName, out var permitBranches))
            {
                return;
            }

            if (!TryGetSinglePositiveSignal(autoBranches, out var sourceSignal))
            {
                return;
            }

            if (!TryBuildPositiveMemberSignals(permitBranches, out var memberSignals))
            {
                return;
            }

            if (memberSignals.Count == 0)
            {
                return;
            }

            var baseName = memberSignals
                .Concat(new[] { sourceSignal })
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? callName;

            drafts.Add(new DeterministicConstraintDraft
            {
                RuleName = $"{callName}_{ExtractDirectionalBaseName(baseName, sourceSignal)}_{suffixName}",
                RuleType = "AllRequired",
                Operator = "ALL_TRUE",
                OriginSemanticKey = $"CallPermit:{autoParameterName}:{permitParameterName}",
                Confidence = 0.99m,
                Priority = "Critical",
                Description = $"当 {sourceSignal} 请求该方向动作时，相关互锁条件也必须全部满足；如果互锁没放行，这个动作就不应继续。",
                Signals = new[]
                {
                    new DeterministicSignalDraft
                    {
                        SignalName = sourceSignal,
                        SignalRole = "Source",
                        SortOrder = 0
                    }
                }
                .Concat(memberSignals.Select((signalName, index) => new DeterministicSignalDraft
                {
                    SignalName = signalName,
                    SignalRole = "Member",
                    SortOrder = index + 1
                }))
                .ToList()
            });
        }

        private static void ExtractStandardCoilRules(
            XElement partsElement,
            Dictionary<string, string> accessMap,
            Dictionary<string, PartInfo> partMap,
            Dictionary<Endpoint, List<Endpoint>> wireIndex,
            List<DeterministicConstraintDraft> drafts)
        {
            var coilCandidates = new List<CoilCandidate>();
            var coilParts = Children(partsElement, "Part")
                .Where(part => string.Equals(Attr(part, "Name"), "Coil", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var part in coilParts)
            {
                var coilUid = Attr(part, "UId");
                if (string.IsNullOrWhiteSpace(coilUid))
                {
                    continue;
                }

                var targetSignal = ResolveMappedSignal(Endpoint.Name(coilUid, "operand"), wireIndex, accessMap);
                if (string.IsNullOrWhiteSpace(targetSignal) || IsLiteralValue(targetSignal))
                {
                    continue;
                }

                var expression = BuildExpressionForTarget(
                    Endpoint.Name(coilUid, "in"),
                    wireIndex,
                    accessMap,
                    partMap,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase));

                if (expression == null || !expression.TryCollectTerms(out var branches))
                {
                    continue;
                }

                coilCandidates.Add(new CoilCandidate(targetSignal, branches));

                var allTerms = branches.SelectMany(branch => branch).ToList();
                var signalNames = allTerms
                    .Select(term => term.SignalName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (signalNames.Count == 0)
                {
                    continue;
                }

                // ── Safety 约束：常闭触点（ExpectedValue=false）是安全信号 ───────
                // 安全信号以常闭接法串入使能支路：门打开/急停 → 信号=false → 线圈必须为false
                var safetyNcTerms = allTerms
                    .Where(t => !t.ExpectedValue && IsSafetySignal(t.SignalName))
                    .DistinctBy(t => t.SignalName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var st in safetyNcTerms)
                {
                    drafts.Add(new DeterministicConstraintDraft
                    {
                        RuleName = $"{st.SignalName}_{targetSignal}_安全联锁",
                        RuleType = "Safety",
                        Operator = "REQUIRES_0",
                        Confidence = 0.97m,
                        Priority = "Critical",
                        Description = $"安全信号 {st.SignalName} = false（断开/急停/安全门打开）时，线圈输出 {targetSignal} 必须为 false。",
                        Signals = new List<DeterministicSignalDraft>
                        {
                            new() { SignalName = st.SignalName, SignalRole = "Source", SortOrder = 0 },
                            new() { SignalName = targetSignal, SignalRole = "Target", SortOrder = 1 }
                        }
                    });
                }

                // 从后续 AllRequired/Custom 中排除已作为 Safety 处理的安全信号
                var safetySignalSet = safetyNcTerms.Select(t => t.SignalName)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var remainTerms = allTerms
                    .Where(t => !safetySignalSet.Contains(t.SignalName))
                    .ToList();
                var remainSignals = remainTerms
                    .Select(t => t.SignalName)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (remainSignals.Count == 0)
                {
                    continue;
                }

                var containsNegation = remainTerms.Any(term => !term.ExpectedValue);

                if (branches.Count == 1 && !containsNegation)
                {
                    if (remainSignals.Count == 1)
                    {
                        drafts.Add(new DeterministicConstraintDraft
                        {
                            RuleName = $"{targetSignal}_线圈驱动",
                            RuleType = "Pairing",
                            Operator = "IMPLIES",
                            Confidence = 0.98m,
                            Priority = "Info",
                            Description = $"{remainSignals[0]} 直接驱动线圈输出 {targetSignal}，也就是前者成立时，后者应被拉起。",
                            Signals = new List<DeterministicSignalDraft>
                            {
                                new() { SignalName = remainSignals[0], SignalRole = "Source" },
                                new() { SignalName = targetSignal, SignalRole = "Target" }
                            }
                        });
                        continue;
                    }

                    drafts.Add(new DeterministicConstraintDraft
                    {
                        RuleName = $"{targetSignal}_线圈前置全满足",
                        RuleType = "AllRequired",
                        Operator = "ALL_TRUE",
                        Confidence = 0.97m,
                        Priority = "Warning",
                        Description = $"线圈输出 {targetSignal} 要动作，前面这些条件必须同时满足；缺少任何一个都不应动作。",
                        Signals = remainTerms
                            .Select((term, index) => new DeterministicSignalDraft
                            {
                                SignalName = term.SignalName,
                                SignalRole = "Member",
                                SortOrder = index
                            })
                            .Concat(new[]
                            {
                                new DeterministicSignalDraft
                                {
                                    SignalName = targetSignal,
                                    SignalRole = "Target",
                                    SortOrder = remainTerms.Count
                                }
                            })
                            .ToList()
                    });
                    continue;
                }

                drafts.Add(new DeterministicConstraintDraft
                {
                    RuleName = $"{targetSignal}_线圈合法组合",
                    RuleType = "Custom",
                    Operator = "CUSTOM",
                    Confidence = 0.99m,
                    Priority = "Warning",
                    Description = $"线圈输出 {targetSignal} 由多路条件组合决定，不是单一信号直接控制，需要按整段逻辑一起判断。",
                    ValidStates = BuildTruthTableJson(remainSignals, targetSignal, expression),
                    Signals = remainSignals
                        .Select((signalName, index) => new DeterministicSignalDraft
                        {
                            SignalName = signalName,
                            SignalRole = "Member",
                            SortOrder = index
                        })
                        .Concat(new[]
                        {
                            new DeterministicSignalDraft
                            {
                                SignalName = targetSignal,
                                SignalRole = "Target",
                                SortOrder = remainSignals.Count
                            }
                        })
                        .ToList()
                });
            }

            AddDirectionalMutexFromCoils(coilCandidates, drafts);
        }

        private static void AddDirectionalMutexFromCoils(
            List<CoilCandidate> coilCandidates,
            List<DeterministicConstraintDraft> drafts)
        {
            for (var i = 0; i < coilCandidates.Count; i++)
            {
                for (var j = i + 1; j < coilCandidates.Count; j++)
                {
                    var left = coilCandidates[i];
                    var right = coilCandidates[j];

                    if (!LooksLikeDirectionalPair(left.TargetSignal, right.TargetSignal))
                    {
                        continue;
                    }

                    if (AreEquivalentBranches(left.Branches, right.Branches))
                    {
                        continue;
                    }

                    drafts.Add(new DeterministicConstraintDraft
                    {
                        RuleName = $"{ExtractDirectionalBaseName(left.TargetSignal, right.TargetSignal)}_输出互锁",
                        RuleType = "Mutex",
                        Operator = "AT_MOST_ONE",
                        Confidence = 0.98m,
                        Priority = "Critical",
                        Description = $"{left.TargetSignal} 和 {right.TargetSignal} 是同一机构的两个反向输出，虽然来自不同支路，但现场上仍应保持互斥，不能同时成立。",
                        Signals = new List<DeterministicSignalDraft>
                        {
                            new() { SignalName = left.TargetSignal, SignalRole = "Member", SortOrder = 0 },
                            new() { SignalName = right.TargetSignal, SignalRole = "Member", SortOrder = 1 }
                        }
                    });
                }
            }
        }

        private static bool LooksLikeDirectionalPair(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            var normalizedLeft = left.ToUpperInvariant();
            var normalizedRight = right.ToUpperInvariant();

            static string StripDirectionalSuffix(string value)
            {
                return value
                    .Replace(".QFWD", string.Empty)
                    .Replace(".QBKD", string.Empty)
                    .Replace("_W", string.Empty)
                    .Replace("_H", string.Empty)
                    .Replace("FWDAUTO", string.Empty)
                    .Replace("BKDAUTO", string.Empty)
                    .Replace("FWDPMT", string.Empty)
                    .Replace("BKDPMT", string.Empty)
                    .Replace("FWDPOS", string.Empty)
                    .Replace("BKDPOS", string.Empty)
                    .Replace("_FWD", string.Empty)
                    .Replace("_BKD", string.Empty)
                    .Replace("QFWD", string.Empty)
                    .Replace("QBKD", string.Empty)
                    .Replace("FWD", string.Empty)
                    .Replace("BKD", string.Empty);
            }

            return StripDirectionalSuffix(normalizedLeft) == StripDirectionalSuffix(normalizedRight)
                   && normalizedLeft != normalizedRight;
        }

        private static bool TryGetSinglePositiveSignal(List<List<ExpressionTerm>> branches, out string signalName)
        {
            signalName = string.Empty;
            if (branches.Count != 1 || branches[0].Count != 1)
            {
                return false;
            }

            var term = branches[0][0];
            if (!term.ExpectedValue || string.IsNullOrWhiteSpace(term.SignalName))
            {
                return false;
            }

            signalName = term.SignalName;
            return true;
        }

        private static bool TryBuildPositiveMemberSignals(List<List<ExpressionTerm>> branches, out List<string> memberSignals)
        {
            memberSignals = new List<string>();

            if (branches.Count != 1)
            {
                return false;
            }

            foreach (var term in branches[0])
            {
                if (!term.ExpectedValue || string.IsNullOrWhiteSpace(term.SignalName))
                {
                    return false;
                }

                memberSignals.Add(term.SignalName);
            }

            memberSignals = memberSignals
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return memberSignals.Count > 0;
        }

        private static bool AreEquivalentBranches(List<List<ExpressionTerm>> left, List<List<ExpressionTerm>> right)
        {
            return string.Equals(
                BuildBranchSignature(left),
                BuildBranchSignature(right),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildBranchSignature(List<List<ExpressionTerm>> branches)
        {
            return string.Join(
                "||",
                branches
                    .Select(branch => string.Join(
                        "&",
                        branch
                            .OrderBy(term => term.SignalName, StringComparer.OrdinalIgnoreCase)
                            .ThenBy(term => term.ExpectedValue)
                            .Select(term => $"{term.SignalName}:{term.ExpectedValue}")))
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        }

        private static string ExtractDirectionalBaseName(string left, string right)
        {
            static string Normalize(string value)
            {
                return value
                    .Replace(".QFwd", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Replace(".QBkd", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Replace("_W", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Replace("_H", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Replace("FwdAuto", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Replace("BkdAuto", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Replace("FwdPmt", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Replace("BkdPmt", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Replace("FwdPos", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Replace("BkdPos", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Replace("_Fwd", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Replace("_Bkd", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Replace("_FWD", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Replace("_BKD", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Replace("QFwd", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Replace("QBkd", string.Empty, StringComparison.OrdinalIgnoreCase);
            }

            var normalizedLeft = Normalize(left ?? string.Empty);
            var normalizedRight = Normalize(right ?? string.Empty);
            return normalizedLeft.Length <= normalizedRight.Length ? normalizedLeft : normalizedRight;
        }

        private static bool LooksLikeStructuredSignal(string signal)
        {
            return !string.IsNullOrWhiteSpace(signal)
                && (signal.Contains('.', StringComparison.OrdinalIgnoreCase)
                    || signal.Contains('[', StringComparison.OrdinalIgnoreCase)
                    || signal.Contains(']', StringComparison.OrdinalIgnoreCase));
        }

        private static bool LooksLikeSameOutputPinReference(string signalName, string outputPinName)
        {
            if (string.IsNullOrWhiteSpace(signalName) || string.IsNullOrWhiteSpace(outputPinName))
            {
                return false;
            }

            return signalName.EndsWith($".{outputPinName}", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildTruthTableJson(List<string> signalNames, string targetName, BoolExpression expression)
        {
            if (signalNames.Count > 12)
            {
                return "[]";
            }

            var rows = new List<Dictionary<string, bool>>();
            var combinations = 1 << signalNames.Count;

            for (var mask = 0; mask < combinations; mask++)
            {
                var values = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                for (var bit = 0; bit < signalNames.Count; bit++)
                {
                    values[signalNames[bit]] = (mask & (1 << bit)) != 0;
                }

                var row = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (var signalName in signalNames)
                {
                    row[signalName] = values[signalName];
                }

                row[targetName] = expression.Evaluate(values);
                rows.Add(row);
            }

            return JsonSerializer.Serialize(rows);
        }

        private static void MarkDraftOrigin(
            List<DeterministicConstraintDraft> drafts,
            int startIndex,
            string? callName)
        {
            if (drafts == null || startIndex < 0 || startIndex >= drafts.Count || string.IsNullOrWhiteSpace(callName))
            {
                return;
            }

            for (var i = startIndex; i < drafts.Count; i++)
            {
                drafts[i].OriginCallName = callName;
                drafts[i].OriginKind = "CallSite";
            }
        }

        private static bool IsLiteralValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return bool.TryParse(value, out _)
                   || decimal.TryParse(value, out _);
        }

        /// <summary>
        /// 判定 Part 是否是 coil 类输出（驱动一个目标信号 + 一个 in 端表达式）。
        /// 涵盖博图三种：Coil（普通赋值）/ SCoil（置位）/ RCoil（复位）。
        /// 不包含 Graph 的 TrCoil / IlCoil / SvCoil（那些没有 operand，由后续 Graph 块单独处理）。
        /// </summary>
        private static bool IsCoilPartName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            return string.Equals(name, "Coil", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "SCoil", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "RCoil", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 给 coil 类 Part 生成 PLD 锚点/输出行的语义后缀。
        /// 普通 Coil 不加后缀（保留原有渲染兼容）。
        /// SCoil / RCoil 加置位/复位标识，引导 LLM 选 IMPLIES / REQUIRES_0。
        /// 返回 (anchorSuffix, outputSuffix)：拼到 `({displayKind}{anchorSuffix})` 和 `\`{target}\`{outputSuffix}` 末尾。
        /// </summary>
        private static (string AnchorSuffix, string OutputSuffix) ClassifyCoilSemantics(string? partName)
        {
            if (string.Equals(partName, "SCoil", StringComparison.OrdinalIgnoreCase))
                return (" — 置位线圈 SCoil", " (置位/保持为 true)");
            if (string.Equals(partName, "RCoil", StringComparison.OrdinalIgnoreCase))
                return (" — 复位线圈 RCoil", " (复位/保持为 false)");
            return (string.Empty, string.Empty);
        }

        private static bool TryParseBooleanLiteral(string value, out bool result)
        {
            if (bool.TryParse(value, out result))
            {
                return true;
            }

            if (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase))
            {
                result = true;
                return true;
            }

            if (string.Equals(value, "0", StringComparison.OrdinalIgnoreCase))
            {
                result = false;
                return true;
            }

            result = false;
            return false;
        }

        private static List<DeterministicConstraintDraft> Deduplicate(List<DeterministicConstraintDraft> drafts)
        {
            return drafts
                .GroupBy(
                    x => $"{x.RuleType}|{x.Operator}|{x.ValidStates}|{string.Join(",", x.Signals.OrderBy(s => s.SortOrder).ThenBy(s => s.SignalRole).ThenBy(s => s.SignalName).Select(s => $"{s.SignalRole}:{s.SignalName}"))}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .ToList();
        }

        private static string? Attr(XElement? element, string attributeName) => element?.Attribute(attributeName)?.Value;

        private static bool IsSupportedLogicRoot(string localName)
        {
            return string.Equals(localName, "FlgNet", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(localName, "Transition", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(localName, "Interlock", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(localName, "Supervision", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(localName, "Actions", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(localName, "StructuredText", StringComparison.OrdinalIgnoreCase);
        }

        private static XElement? Child(XElement? element, string localName)
        {
            return element?
                .Elements()
                .FirstOrDefault(x => string.Equals(x.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));
        }

        private static IEnumerable<XElement> Children(XElement? element, string localName)
        {
            return element?
                .Elements()
                .Where(x => string.Equals(x.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase))
                ?? Enumerable.Empty<XElement>();
        }

        private enum EndpointKind
        {
            NameCon,
            IdentCon,
            OpenCon,
            Powerrail
        }

        private sealed record Endpoint(EndpointKind Kind, string? UId = null, string? PortName = null)
        {
            public static Endpoint Name(string? uid, string? portName) => new(EndpointKind.NameCon, uid, portName);
            public static Endpoint Ident(string? uid) => new(EndpointKind.IdentCon, uid);
            public static Endpoint Open(string? uid) => new(EndpointKind.OpenCon, uid);
            public static Endpoint Powerrail() => new(EndpointKind.Powerrail, "POWER");

            public override string ToString() => $"{Kind}:{UId}:{PortName}";
        }

        private sealed record PartInfo(string Name, HashSet<string> NegatedPorts);

        private sealed record CoilCandidate(string TargetSignal, List<List<ExpressionTerm>> Branches);

        public sealed class DeterministicConstraintDraft
        {
            public string RuleName { get; set; } = string.Empty;
            public string RuleType { get; set; } = string.Empty;
            public string Operator { get; set; } = string.Empty;
            public string? OriginCallName { get; set; }
            public string? OriginKind { get; set; }
            public string? OriginSemanticKey { get; set; }
            public string? EquipmentId { get; set; }
            public decimal? Confidence { get; set; }
            public string Description { get; set; } = string.Empty;
            public string? Priority { get; set; }
            public string? ValidStates { get; set; }
            public List<DeterministicSignalDraft> Signals { get; set; } = new();
        }

        public sealed class DeterministicSignalDraft
        {
            public string SignalName { get; set; } = string.Empty;
            public string? SignalAddress { get; set; }
            public string SignalRole { get; set; } = "Member";
            public int? SortOrder { get; set; }
        }

        public sealed class DeterministicLogicExpressionDraft
        {
            public string ExpressionName { get; set; } = string.Empty;
            public string ExpressionType { get; set; } = string.Empty;
            public string TargetSignal { get; set; } = string.Empty;
            public string ExpressionText { get; set; } = string.Empty;
            public string ExpressionAstJson { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public int? SortOrder { get; set; }
            public List<DeterministicLogicExpressionSignalDraft> Signals { get; set; } = new();
        }

        public sealed class DeterministicLogicExpressionSignalDraft
        {
            public string SignalName { get; set; } = string.Empty;
            public string? SignalAddress { get; set; }
            public string SignalRole { get; set; } = "Operand";
            public bool? ExpectedBoolValue { get; set; }
            public int? SortOrder { get; set; }
        }

        public sealed class CallPinMapping
        {
            public string SignalName { get; set; } = string.Empty;
            public string CallName { get; set; } = string.Empty;
            public string PinName { get; set; } = string.Empty;
            public string PinSection { get; set; } = string.Empty;
            public string BlockName { get; set; } = string.Empty;
        }

        private sealed class BoolExpression
        {
            private BoolExpression(string kind, string? signalName = null, string? constantValue = null, List<BoolExpression>? children = null)
            {
                Kind = kind;
                SignalName = signalName;
                ConstantValue = constantValue;
                Children = children ?? new List<BoolExpression>();
            }

            public string Kind { get; }
            public string? SignalName { get; }
            public string? ConstantValue { get; }
            public List<BoolExpression> Children { get; }

            public static BoolExpression Signal(string signalName) => new("Signal", signalName: signalName);
            public static BoolExpression Constant(string constantValue) => new("Constant", constantValue: constantValue);
            public static BoolExpression True() => new("True");
            public static BoolExpression False() => new("False");

            public static BoolExpression Not(BoolExpression child)
            {
                return child.Kind switch
                {
                    "True" => False(),
                    "False" => True(),
                    "Not" => child.Children[0],
                    _ => new BoolExpression("Not", children: new List<BoolExpression> { child })
                };
            }

            public static BoolExpression And(BoolExpression left, BoolExpression right)
            {
                var nodes = new List<BoolExpression>();
                nodes.AddRange(left.Kind == "And" ? left.Children : new[] { left });
                nodes.AddRange(right.Kind == "And" ? right.Children : new[] { right });
                nodes = nodes.Where(x => x.Kind != "True").ToList();
                if (nodes.Any(x => x.Kind == "False"))
                {
                    return False();
                }

                return nodes.Count switch
                {
                    0 => True(),
                    1 => nodes[0],
                    _ => new BoolExpression("And", children: nodes)
                };
            }

            public static BoolExpression Or(IEnumerable<BoolExpression> children)
            {
                var nodes = children
                    .SelectMany(x => x.Kind == "Or" ? x.Children : (IEnumerable<BoolExpression>)new[] { x })
                    .Where(x => x.Kind != "False")
                    .ToList();

                if (nodes.Any(x => x.Kind == "True"))
                {
                    return True();
                }

                return nodes.Count switch
                {
                    0 => False(),
                    1 => nodes[0],
                    _ => new BoolExpression("Or", children: nodes)
                };
            }

            public bool TryCollectTerms(out List<List<ExpressionTerm>> branches)
            {
                branches = Kind switch
                {
                    "True" => new List<List<ExpressionTerm>> { new List<ExpressionTerm>() },
                    "False" => new List<List<ExpressionTerm>>(),
                    "Signal" when !string.IsNullOrWhiteSpace(SignalName) => new List<List<ExpressionTerm>>
                    {
                        new List<ExpressionTerm> { new ExpressionTerm(SignalName!, true) }
                    },
                    "Not" when Children.Count == 1 && Children[0].Kind == "Signal" && !string.IsNullOrWhiteSpace(Children[0].SignalName) => new List<List<ExpressionTerm>>
                    {
                        new List<ExpressionTerm> { new ExpressionTerm(Children[0].SignalName!, false) }
                    },
                    "Constant" => new List<List<ExpressionTerm>> { new List<ExpressionTerm>() },
                    "And" => MergeAndBranches(Children),
                    "Or" => MergeOrBranches(Children),
                    _ => new List<List<ExpressionTerm>>()
                };

                return branches.Count > 0;
            }

            public bool Evaluate(IReadOnlyDictionary<string, bool> values)
            {
                return Kind switch
                {
                    "True" => true,
                    "False" => false,
                    "Signal" => !string.IsNullOrWhiteSpace(SignalName) && values.TryGetValue(SignalName, out var signalValue) && signalValue,
                    "Not" => Children.Count == 1 && !Children[0].Evaluate(values),
                    "Constant" => TryParseBooleanLiteral(ConstantValue ?? string.Empty, out var literal) && literal,
                    "And" => Children.All(x => x.Evaluate(values)),
                    "Or" => Children.Any(x => x.Evaluate(values)),
                    _ => false
                };
            }

            private static List<List<ExpressionTerm>> MergeAndBranches(List<BoolExpression> nodes)
            {
                var result = new List<List<ExpressionTerm>> { new List<ExpressionTerm>() };

                foreach (var node in nodes)
                {
                    if (!node.TryCollectTerms(out var childBranches))
                    {
                        return new List<List<ExpressionTerm>>();
                    }

                    var merged = new List<List<ExpressionTerm>>();
                    foreach (var left in result)
                    {
                        foreach (var right in childBranches)
                        {
                            var combined = left.Concat(right).ToList();
                            if (!HasConflict(combined))
                            {
                                merged.Add(combined);
                            }
                        }
                    }

                    result = merged;
                    if (result.Count == 0)
                    {
                        break;
                    }
                }

                return result;
            }

            private static List<List<ExpressionTerm>> MergeOrBranches(List<BoolExpression> nodes)
            {
                var result = new List<List<ExpressionTerm>>();
                foreach (var node in nodes)
                {
                    if (node.TryCollectTerms(out var branches))
                    {
                        result.AddRange(branches);
                    }
                }

                return result;
            }

            private static bool HasConflict(IEnumerable<ExpressionTerm> terms)
            {
                return terms
                    .GroupBy(x => x.SignalName, StringComparer.OrdinalIgnoreCase)
                    .Any(group => group.Select(x => x.ExpectedValue).Distinct().Count() > 1);
            }
        }

        private sealed record ExpressionTerm(string SignalName, bool ExpectedValue);

        // ============================================================================
        // ParsedLogicDescription（PLD）硬编码生成器
        // ----------------------------------------------------------------------------
        // 取代 LLM Step1，纯 C# 解析 FlgNet XML 输出 10 段结构化中文描述。
        // 速度 vs LLM：从 5-15 秒/LU 降到毫秒级，3000+ LU 几分钟跑完。
        // 通用性原则（CLAUDE.md §4.2）：
        //   - 不按命名前后缀匹配（不写「含 _W/_H 」「含 -SQ」之类）
        //   - 只看结构证据：FB 引脚名（QFwd/IWP/IHP/E_StopStatus/ResetStatus 是博图标准引脚名）
        //     + 地址前缀（%Q/%I/%M）+ Wire 拓扑
        // ============================================================================
        /// <summary>
        /// 通过纯硬编码（不调 LLM）从 FlgNet XML 生成 ParsedLogicDescription 10 段描述。
        /// 输入与 LLM 版完全一致；输出格式也尽量对齐，让下游 Phase2 prompt 无感切换。
        /// </summary>
        /// <param name="rawContent">LogicUnit.RawContent（FlgNet XML）</param>
        /// <param name="ioTagInfoBySymbol">符号名 → (Address, Comment) 的字典，给 §2/§3 标 Q/I/M/DB 方向</param>
        /// <param name="networkId">Network 序号（只用于段首信息行）</param>
        /// <param name="networkTitle">Network 标题（可空）</param>
        /// <param name="networkComment">Network 注释（可空）</param>
        /// <param name="blockName">本 LU 所在 Block 名（可空）</param>
        /// <param name="calleeBlockDescriptions">已解析的 callee block → 已生成 PLD 文本字典，供 §6 引用（可空）</param>
        /// <returns>10 段中文 PLD 文本</returns>
        public static string BuildParsedLogicDescription(
            string rawContent,
            Dictionary<string, (string Address, string? Comment)> ioTagInfoBySymbol,
            int? networkId = null,
            string? networkTitle = null,
            string? networkComment = null,
            string? blockName = null,
            Dictionary<string, string>? calleeBlockDescriptions = null)
        {
            if (string.IsNullOrWhiteSpace(rawContent))
            {
                return "### 1. 业务意图\n本 Network 内容为空。\n\n" + BuildEmptySection2To10();
            }

            var document = ParseFlgNetDocument(rawContent);
            var root = document?.Root;
            if (root == null)
            {
                return "### 1. 业务意图\n本 Network XML 解析失败。\n\n" + BuildEmptySection2To10();
            }

            ioTagInfoBySymbol ??= new Dictionary<string, (string, string?)>(StringComparer.OrdinalIgnoreCase);
            calleeBlockDescriptions ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var rootName = root.Name.LocalName;

            // ====== Actions / StructuredText 专用走 SCL token 流分析（不走 FlgNet）======
            // - Actions：GRAPH 步骤的动作段，每个 <Action Qualifier="..."> 含若干 <Token Text="..."> 序列
            //   典型形式：LHS := RHS（赋值）；Qualifier 表示动作类型（N=每步执行、S=置位、R=复位、L=限时等）
            //   动作里可能：(a) 直接给 Q 信号赋值；(b) 调 FB（Token 含 ("FBName")）；
            //   把这一段当作"驱动逻辑"放进 §5 自定义段，让 Phase2 看到本 LU 在 SET 哪些信号
            // - StructuredText：与 Actions 同理，结构略简
            if (string.Equals(rootName, "Actions", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rootName, "StructuredText", StringComparison.OrdinalIgnoreCase))
            {
                return BuildScriptStylePld(root, rootName, ioTagInfoBySymbol, networkId, networkTitle, networkComment, blockName);
            }

            // 收集所有 logic root（FlgNet / Transition / Interlock / Supervision）下的接线上下文。
            // 通常一个 LU 只有一个 FlgNet 根；但 Graph LU 可能多个 Transition。
            var allCallMappings = new List<CallPinMapping>();           // §5 段原料
            var allCallInfo = new List<CallInfo>();                       // §5/§7/§9/§10 段原料
            var allCoilOutputs = new List<CoilOutput>();                  // §2/§5 段原料：直接 Coil 驱动
            var allSignalUsages = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // 出现的所有信号
            var allConditionCoils = new List<ConditionCoil>();           // §10 段原料：Tr/Il/Sv Coil 的触发表达式

            foreach (var logicRoot in EnumerateSupportedLogicRoots(root))
            {
                if (!TryBuildLogicContext(logicRoot, out var partsElement, out var accessMap, out var partMap, out var wireIndex))
                {
                    continue;
                }

                // 累计所有出现的信号
                foreach (var kv in accessMap)
                {
                    if (!string.IsNullOrWhiteSpace(kv.Value) && !IsLiteralValue(kv.Value))
                    {
                        allSignalUsages.Add(kv.Value);
                    }
                }

                // 提取所有 Call 的引脚绑定 + 输入引脚布尔表达式
                foreach (var call in Children(partsElement, "Call"))
                {
                    var callUid = Attr(call, "UId");
                    if (string.IsNullOrWhiteSpace(callUid)) continue;

                    var callInfo = Child(call, "CallInfo");
                    var callTypeName = Attr(callInfo, "Name") ?? "UnknownCall";
                    var callInstanceName = BuildCallInstanceName(call, callInfo);
                    var inputParams = GetParameters(callInfo, "Input");
                    var outputParams = GetParameters(callInfo, "Output");

                    var info = new CallInfo
                    {
                        CallTypeName = callTypeName,
                        CallInstanceName = callInstanceName,
                    };

                    foreach (var pin in inputParams)
                    {
                        var expr = BuildExpressionForTarget(
                            Endpoint.Name(callUid, pin),
                            wireIndex, accessMap, partMap,
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                        var exprText = expr == null ? null : RenderExpression(expr);
                        var directSignal = ResolveMappedSignal(Endpoint.Name(callUid, pin), wireIndex, accessMap);
                        info.InputPins.Add(new PinBinding
                        {
                            PinName = pin,
                            BoundSignals = expr == null ? new List<string>() : CollectExpressionSignals(expr).Select(s => s.SignalName).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                            ExpressionText = exprText,
                            DirectSignal = directSignal
                        });

                        if (!string.IsNullOrWhiteSpace(directSignal) && !IsLiteralValue(directSignal))
                        {
                            allCallMappings.Add(new CallPinMapping
                            {
                                SignalName = directSignal!,
                                CallName = callInstanceName,
                                PinName = pin,
                                PinSection = "Input",
                                BlockName = blockName ?? string.Empty
                            });
                        }
                    }

                    foreach (var pin in outputParams)
                    {
                        var signal = ResolveMappedSignal(Endpoint.Name(callUid, pin), wireIndex, accessMap);
                        info.OutputPins.Add(new PinBinding
                        {
                            PinName = pin,
                            DirectSignal = signal,
                            BoundSignals = string.IsNullOrWhiteSpace(signal) ? new List<string>() : new List<string> { signal! }
                        });

                        if (!string.IsNullOrWhiteSpace(signal) && !IsLiteralValue(signal))
                        {
                            allCallMappings.Add(new CallPinMapping
                            {
                                SignalName = signal!,
                                CallName = callInstanceName,
                                PinName = pin,
                                PinSection = "Output",
                                BlockName = blockName ?? string.Empty
                            });
                        }
                    }

                    allCallInfo.Add(info);
                }

                // 提取直接 Coil 输出（不经过 FB 的 LAD 直接驱动）
                foreach (var part in Children(partsElement, "Part"))
                {
                    var pname = Attr(part, "Name");
                    if (!string.Equals(pname, "Coil", StringComparison.OrdinalIgnoreCase)) continue;
                    var coilUid = Attr(part, "UId");
                    if (string.IsNullOrWhiteSpace(coilUid)) continue;

                    var target = ResolveMappedSignal(Endpoint.Name(coilUid, "operand"), wireIndex, accessMap);
                    if (string.IsNullOrWhiteSpace(target) || IsLiteralValue(target)) continue;

                    var expr = BuildExpressionForTarget(
                        Endpoint.Name(coilUid, "in"),
                        wireIndex, accessMap, partMap,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase));

                    allCoilOutputs.Add(new CoilOutput
                    {
                        TargetSignal = target!,
                        ExpressionText = expr == null ? "TRUE" : RenderExpression(expr),
                        InputSignals = expr == null ? new List<string>() : CollectExpressionSignals(expr).Select(s => s.SignalName).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                    });
                }

                // 提取 Graph 条件 Coil：TrCoil（Transition）/IlCoil（Interlock）/SvCoil（Supervision）
                // 这些 Coil 没有外部信号 operand，只有 in 端的布尔表达式 → 用作 §10 联锁条件描述
                // 注：如果 in 端连到比较类（Gt/Lt/Eq 等非纯 Bool）part，无法构建完整 BoolExpression，
                // 退化为列出该 Coil "in" 链路上出现的所有 Access 符号（保证至少能产 §10 列出涉及信号）
                foreach (var part in Children(partsElement, "Part"))
                {
                    var pname = Attr(part, "Name");
                    if (string.IsNullOrWhiteSpace(pname)) continue;
                    if (!string.Equals(pname, "TrCoil", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(pname, "IlCoil", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(pname, "SvCoil", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var coilUid = Attr(part, "UId");
                    if (string.IsNullOrWhiteSpace(coilUid)) continue;

                    var expr = BuildExpressionForTarget(
                        Endpoint.Name(coilUid, "in"),
                        wireIndex, accessMap, partMap,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase));

                    string exprText;
                    List<string> sigs;
                    if (expr != null)
                    {
                        exprText = RenderExpression(expr);
                        sigs = CollectExpressionSignals(expr).Select(s => s.SignalName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    }
                    else
                    {
                        // 退化：本 logicRoot 下出现的所有非常量 Access 都列出来
                        // （单条 Coil 单元里所有符号即"涉及的信号"）
                        exprText = "(含比较/非纯 Bool 部件，无法展开为布尔表达式)";
                        sigs = accessMap.Values
                            .Where(v => !string.IsNullOrWhiteSpace(v) && !IsLiteralValue(v))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                    }
                    if (sigs.Count == 0) continue;

                    allConditionCoils.Add(new ConditionCoil
                    {
                        CoilKind = pname,  // TrCoil / IlCoil / SvCoil
                        ExpressionText = exprText,
                        InputSignals = sigs
                    });
                }
            }

            // ===== 开始拼装 10 段 =====
            var sb = new System.Text.StringBuilder();

            sb.AppendLine("### 1. 业务意图");
            sb.AppendLine(BuildBusinessIntent(rootName, blockName, networkId, networkTitle, networkComment, allCallInfo, allCoilOutputs, allConditionCoils));
            sb.AppendLine();

            // §2 Q 点清单 / §3 I 点清单 / §4 DB 关键变量
            var (qPoints, iPoints, mPoints, dbPoints) = ClassifySignals(allSignalUsages, ioTagInfoBySymbol);

            sb.AppendLine("### 2. Q 点清单（物理输出端）");
            if (qPoints.Count == 0) sb.AppendLine("本 Network 无 Q 点。");
            else foreach (var (name, addr, comment) in qPoints)
                sb.AppendLine($"- {name}（{addr}）：{comment ?? "（无注释）"}");
            sb.AppendLine();

            sb.AppendLine("### 3. I 点清单（物理输入端）");
            if (iPoints.Count == 0) sb.AppendLine("本 Network 无 I 点。");
            else foreach (var (name, addr, comment) in iPoints)
                sb.AppendLine($"- {name}（{addr}）：{comment ?? "（无注释）"}");
            sb.AppendLine();

            sb.AppendLine("### 4. DB 关键变量清单");
            var keyDb = FilterKeyDbVariables(dbPoints);
            if (keyDb.Count == 0) sb.AppendLine("本 Network 无关键 DB 变量。");
            else
            {
                var modeVars = keyDb.Where(x => IsModeSelectionPin(LastSegment(x))).ToList();
                var safetyVars = keyDb.Where(x => IsSafetyPin(LastSegment(x))).ToList();
                var resetVars = keyDb.Where(x => IsResetPin(LastSegment(x))).ToList();
                var permitVars = keyDb.Where(x => IsPermitPin(LastSegment(x))).ToList();
                var others = keyDb.Except(modeVars).Except(safetyVars).Except(resetVars).Except(permitVars).ToList();

                if (modeVars.Count > 0) { sb.AppendLine("- 模式选择："); foreach (var v in modeVars) sb.AppendLine($"  - {v}"); }
                if (safetyVars.Count > 0) { sb.AppendLine("- 安全/急停："); foreach (var v in safetyVars) sb.AppendLine($"  - {v}"); }
                if (resetVars.Count > 0) { sb.AppendLine("- 复位："); foreach (var v in resetVars) sb.AppendLine($"  - {v}"); }
                if (permitVars.Count > 0) { sb.AppendLine("- 许可/使能："); foreach (var v in permitVars) sb.AppendLine($"  - {v}"); }
                if (others.Count > 0) { sb.AppendLine("- 其它："); foreach (var v in others) sb.AppendLine($"  - {v}"); }
            }
            sb.AppendLine();

            // §5 FB/FC 调用与引脚绑定
            sb.AppendLine("### 5. FB/FC 调用与引脚绑定（最关键的结构证据）");
            if (allCallInfo.Count == 0)
            {
                if (allCoilOutputs.Count > 0)
                {
                    sb.AppendLine("本 Network 不含 FB/FC 调用，但有直接 Coil 输出（见下方）。");
                    foreach (var coil in allCoilOutputs)
                    {
                        sb.AppendLine($"- 直接 Coil：{coil.TargetSignal} := {coil.ExpressionText}");
                    }
                }
                else
                {
                    sb.AppendLine("本 Network 不含 FB/FC 调用。");
                }
            }
            else
            {
                int idx = 1;
                foreach (var ci in allCallInfo)
                {
                    sb.AppendLine($"调用 #{idx++}：实例名 = {ci.CallInstanceName}，FB 块名 = {ci.CallTypeName}");
                    if (ci.InputPins.Any(p => p.BoundSignals.Count > 0))
                    {
                        sb.AppendLine("- 输入引脚：");
                        foreach (var pin in ci.InputPins.Where(p => p.BoundSignals.Count > 0))
                        {
                            sb.AppendLine($"  - {pin.PinName} := {pin.ExpressionText ?? (pin.DirectSignal ?? "(未绑定)")}");
                        }
                    }
                    if (ci.OutputPins.Any(p => !string.IsNullOrWhiteSpace(p.DirectSignal)))
                    {
                        sb.AppendLine("- 输出引脚：");
                        foreach (var pin in ci.OutputPins.Where(p => !string.IsNullOrWhiteSpace(p.DirectSignal)))
                        {
                            sb.AppendLine($"  - {pin.PinName} → {pin.DirectSignal}");
                        }
                    }
                }
                if (allCoilOutputs.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("额外直接 Coil 输出（非 FB 调用驱动）：");
                    foreach (var coil in allCoilOutputs)
                    {
                        sb.AppendLine($"- {coil.TargetSignal} := {coil.ExpressionText}");
                    }
                }
            }
            sb.AppendLine();

            // §6 中间块内部逻辑
            sb.AppendLine("### 6. 中间块内部逻辑（callee 内部因果链）");
            var calleeBlocks = allCallInfo.Select(c => c.CallTypeName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var calleeUsedCount = 0;
            foreach (var cb in calleeBlocks)
            {
                if (calleeBlockDescriptions.TryGetValue(cb, out var desc) && !string.IsNullOrWhiteSpace(desc))
                {
                    if (calleeUsedCount++ == 0) sb.AppendLine("已解析的 callee 描述摘要：");
                    sb.AppendLine($"#### Callee {cb}");
                    // 截前 1200 字防止过长
                    sb.AppendLine(desc.Length > 1200 ? desc.Substring(0, 1200) + "..." : desc);
                }
            }
            if (calleeUsedCount == 0)
            {
                sb.AppendLine("本 Network 无中间块内部逻辑可参考（callee 尚未解析或本 Network 不调用 FB）。");
            }
            sb.AppendLine();

            // §7 Q→I 驱动对应链
            sb.AppendLine("### 7. Q→I 驱动对应链");
            var qiPairs = BuildQIPairs(allCallInfo, ioTagInfoBySymbol);
            if (qiPairs.Count == 0)
            {
                if (allCallInfo.Count == 0)
                    sb.AppendLine("本 Network 无 FB 调用，无 Q→I 驱动链。");
                else
                    sb.AppendLine("本 Network 的 FB 调用未形成 Q→I 驱动链（QFwd/QBkd 未绑定 Q 信号或 IWP/IHP 未绑定 I 信号）。");
            }
            else
            {
                foreach (var p in qiPairs)
                {
                    sb.AppendLine($"- {p.QSignal} → 驱动 → {p.IDescription} （来自 {p.CallInstance}.{p.QPin} → {p.CallInstance}.{p.IPin} 绑定，{p.Topology}）");
                }
            }
            sb.AppendLine();

            // §8 I 拓扑结构
            sb.AppendLine("### 8. I 拓扑结构（串/并/混合/常闭）");
            var iTopology = BuildITopology(allCallInfo, ioTagInfoBySymbol);
            if (string.IsNullOrWhiteSpace(iTopology))
                sb.AppendLine("本 Network 中 I 点之间无串/并逻辑。");
            else
                sb.AppendLine(iTopology);
            sb.AppendLine();

            // §9 互斥关系
            sb.AppendLine("### 9. 互斥关系");
            var mutexes = BuildMutexes(allCallInfo, ioTagInfoBySymbol);
            if (mutexes.Count == 0)
            {
                if (allCallInfo.Count == 0)
                    sb.AppendLine("本 Network 无 FB 调用，无互斥关系。");
                else
                    sb.AppendLine("本 Network 的 FB 调用未形成反向输出/反向到位对，无互斥。");
            }
            else
            {
                foreach (var m in mutexes) sb.AppendLine($"- {m}");
            }
            sb.AppendLine();

            // §10 安全/复位联锁
            sb.AppendLine("### 10. 安全/复位联锁");
            var safetyLines = BuildSafetyInterlocks(allCallInfo);

            // Graph 条件 Coil 加入联锁段（Interlock 主要、Transition/Supervision 次要）
            foreach (var c in allConditionCoils)
            {
                var kindCh = c.CoilKind switch
                {
                    "IlCoil" => "Graph 联锁条件",
                    "TrCoil" => "Graph 步骤转移条件",
                    "SvCoil" => "Graph 监视条件",
                    _ => c.CoilKind
                };
                safetyLines.Add($"{kindCh}（{c.CoilKind}） := {c.ExpressionText}  // 涉及信号 {string.Join(", ", c.InputSignals)}");
            }

            if (safetyLines.Count == 0)
                sb.AppendLine("本 Network 无安全/复位联锁约束（未发现 FB 实例 E_StopStatus / ResetStatus 引脚绑定，也无 Graph 条件 Coil）。");
            else foreach (var line in safetyLines) sb.AppendLine($"- {line}");

            return sb.ToString();
        }

        /// <summary>
        /// Actions / StructuredText 类 LU 的 PLD 生成器。
        /// 这些段没有 FlgNet，是 Token 序列形式的 SCL 语句。
        /// 提取：
        ///   - 出现的所有 Symbol 名（去掉 &quot; 转义后即 DB.A.B 这种符号）
        ///   - 简单识别赋值：LHS := RHS / Token "S"=Set / "R"=Reset
        ///   - 如果 LHS 是 IO Tag 字典里 %Q 信号 → 这是 Q 输出驱动 → §5 段标
        /// </summary>
        private static string BuildScriptStylePld(
            XElement root,
            string rootName,
            Dictionary<string, (string Address, string? Comment)> ioInfo,
            int? networkId,
            string? title,
            string? comment,
            string? blockName)
        {
            // 1) 收集所有 <Access> 子树代表的符号（StructuredText / Actions 都可能有）
            //    Access 内嵌 <Symbol><Component Name="...">.<Component Name="...">[...]
            //    把整树拼回 "DB.Comp1.Comp2[N]" 这种命名
            var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var access in root.Descendants().Where(x => string.Equals(x.Name.LocalName, "Access", StringComparison.OrdinalIgnoreCase)))
            {
                var name = BuildAccessName(access);
                if (!string.IsNullOrWhiteSpace(name) && !IsLiteralValue(name))
                    symbols.Add(name);
            }

            // 2) 收集所有 Token 文本（Actions 的 <Action><Token>... 形式）
            var allTokens = root.Descendants()
                .Where(x => string.Equals(x.Name.LocalName, "Token", StringComparison.OrdinalIgnoreCase))
                .Select(x => Attr(x, "Text") ?? "")
                .ToList();

            // 用正则从 Token 拼接里再补一遍符号（Actions 的 Token 字符串里直接含 "DB_X".X.Y）
            var joined = string.Join(" ", allTokens).Replace("\"", "");
            var symbolRegex = new System.Text.RegularExpressions.Regex(
                @"[A-Za-z_][A-Za-z0-9_]*(?:\[[^\]]+\])?(?:\.[A-Za-z_][A-Za-z0-9_]*(?:\[[^\]]+\])?)+",
                System.Text.RegularExpressions.RegexOptions.Compiled);
            foreach (System.Text.RegularExpressions.Match m in symbolRegex.Matches(joined))
            {
                var sym = m.Value.Trim();
                if (sym.Length > 0 && !char.IsDigit(sym[0])) symbols.Add(sym);
            }

            // 3) 提取显式赋值（LHS := RHS）
            //    Actions 形式：<Action Qualifier="N">  → token 串 "LHS := RHS"
            //    StructuredText 形式：直接在根下 Access(LHS) Token(":=") Access(RHS) ...
            var assignTargets = new List<(string Lhs, string Rhs)>();
            // 3a) Actions 形式
            foreach (var action in root.Descendants().Where(x => string.Equals(x.Name.LocalName, "Action", StringComparison.OrdinalIgnoreCase)))
            {
                var tokens = action.Descendants()
                    .Where(x => string.Equals(x.Name.LocalName, "Token", StringComparison.OrdinalIgnoreCase))
                    .Select(x => Attr(x, "Text") ?? "")
                    .ToList();
                var line = string.Join(" ", tokens).Replace("\"", "");
                var assignIdx = line.IndexOf(":=");
                if (assignIdx > 0)
                {
                    var lhs = line.Substring(0, assignIdx).Trim();
                    var rhs = line.Substring(assignIdx + 2).Trim();
                    if (!string.IsNullOrWhiteSpace(lhs))
                        assignTargets.Add((lhs.TrimEnd('\n', '\r', ' '), rhs.TrimEnd('\n', '\r', ' ')));
                }
            }
            // 3b) StructuredText 形式：扫根直接子（按顺序的 Access/Token），遇 Token Text=":=" 时
            //     把它前后最近的 Access 作为 LHS/RHS（简化处理）
            if (string.Equals(rootName, "StructuredText", StringComparison.OrdinalIgnoreCase))
            {
                var direct = root.Elements().ToList();
                for (int i = 0; i < direct.Count; i++)
                {
                    var elem = direct[i];
                    if (string.Equals(elem.Name.LocalName, "Token", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(Attr(elem, "Text"), ":=", StringComparison.Ordinal))
                    {
                        // 向前找最近的 Access
                        string? lhs = null;
                        for (int k = i - 1; k >= 0; k--)
                        {
                            if (string.Equals(direct[k].Name.LocalName, "Access", StringComparison.OrdinalIgnoreCase))
                            {
                                lhs = BuildAccessName(direct[k]);
                                if (!string.IsNullOrWhiteSpace(lhs)) break;
                            }
                        }
                        // 向后找最近的 Access
                        string? rhs = null;
                        for (int k = i + 1; k < direct.Count; k++)
                        {
                            if (string.Equals(direct[k].Name.LocalName, "Access", StringComparison.OrdinalIgnoreCase))
                            {
                                rhs = BuildAccessName(direct[k]);
                                if (!string.IsNullOrWhiteSpace(rhs)) break;
                            }
                        }
                        if (!string.IsNullOrWhiteSpace(lhs))
                            assignTargets.Add((lhs!, rhs ?? "(复杂表达式)"));
                    }
                }
            }

            // 用 ioInfo 区分 Q/I/M/DB
            var (qPoints, iPoints, mPoints, dbPoints) = ClassifySignals(symbols, ioInfo);

            var sb = new System.Text.StringBuilder();
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(blockName)) parts.Add($"块 {blockName}");
            if (networkId.HasValue) parts.Add($"Network {networkId}");
            if (!string.IsNullOrWhiteSpace(title)) parts.Add($"标题「{title}」");
            var prefix = parts.Count > 0 ? string.Join("，", parts) + "：" : "";

            sb.AppendLine("### 1. 业务意图");
            var rootDescCh = string.Equals(rootName, "Actions", StringComparison.OrdinalIgnoreCase)
                ? "Graph 顺控的动作段（Actions）"
                : "SCL 结构化文本段（StructuredText）";
            sb.AppendLine($"{prefix}本 Network 是 {rootDescCh}，含 {allTokens.Count} 个 Token、{symbols.Count} 个符号引用、{assignTargets.Count} 处显式赋值。");
            sb.AppendLine();

            sb.AppendLine("### 2. Q 点清单（物理输出端）");
            if (qPoints.Count == 0) sb.AppendLine("本 Network 无 Q 点。");
            else foreach (var (n, a, c) in qPoints) sb.AppendLine($"- {n}（{a}）：{c ?? "（无注释）"}");
            sb.AppendLine();

            sb.AppendLine("### 3. I 点清单（物理输入端）");
            if (iPoints.Count == 0) sb.AppendLine("本 Network 无 I 点。");
            else foreach (var (n, a, c) in iPoints) sb.AppendLine($"- {n}（{a}）：{c ?? "（无注释）"}");
            sb.AppendLine();

            sb.AppendLine("### 4. DB 关键变量清单");
            var keyDb = FilterKeyDbVariables(dbPoints);
            if (keyDb.Count == 0) sb.AppendLine("本 Network 无关键 DB 变量。");
            else foreach (var v in keyDb) sb.AppendLine($"- {v}");
            sb.AppendLine();

            sb.AppendLine("### 5. FB/FC 调用与引脚绑定（最关键的结构证据）");
            sb.AppendLine($"本 Network 是 {rootDescCh}，不含 FlgNet 接线证据；不在此段产 FB 引脚绑定。");
            if (assignTargets.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("以下是显式赋值（LHS := RHS）摘录（最多前 30 条）：");
                foreach (var (lhs, rhs) in assignTargets.Take(30))
                    sb.AppendLine($"- {lhs} := {rhs}");
                if (assignTargets.Count > 30)
                    sb.AppendLine($"  ...（共 {assignTargets.Count} 处赋值，已截断）");
            }
            sb.AppendLine();

            sb.AppendLine("### 6. 中间块内部逻辑（callee 内部因果链）");
            sb.AppendLine("本 Network 是脚本段，无 callee 描述可参考。");
            sb.AppendLine();

            sb.AppendLine("### 7. Q→I 驱动对应链");
            // 脚本段没有 Wire 拓扑可推断 Pairing；只列「显式被赋值的 Q 信号」
            var qAssignedSet = assignTargets.Select(t => t.Lhs).Where(l => ioInfo.TryGetValue(l, out var x) && (x.Address ?? "").StartsWith("%Q", StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
            if (qAssignedSet.Count == 0)
                sb.AppendLine("本 Network 无 Q 输出被显式赋值，无 Q→I 驱动链。");
            else
            {
                sb.AppendLine("以下 Q 信号在本段被显式赋值（脚本段无 FB 引脚绑定，无法直接推 Pairing，仅列出供 Phase2 参考）：");
                foreach (var q in qAssignedSet) sb.AppendLine($"- {q} 在本段被显式赋值");
            }
            sb.AppendLine();

            sb.AppendLine("### 8. I 拓扑结构（串/并/混合/常闭）");
            sb.AppendLine("本 Network 是脚本段，无 LAD 串/并/常闭结构。");
            sb.AppendLine();

            sb.AppendLine("### 9. 互斥关系");
            sb.AppendLine("本 Network 是脚本段，无 FB 反向输出 / 反向到位对，无互斥。");
            sb.AppendLine();

            sb.AppendLine("### 10. 安全/复位联锁");
            sb.AppendLine("本 Network 是脚本段，无 FB E_StopStatus/ResetStatus 引脚绑定。");

            return sb.ToString();
        }

        private static string BuildEmptySection2To10()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 2; i <= 10; i++)
            {
                sb.AppendLine($"### {i}. （略）");
                sb.AppendLine("本 Network 无相应内容。");
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private static string BuildBusinessIntent(
            string rootName,
            string? blockName,
            int? networkId,
            string? title,
            string? comment,
            List<CallInfo> calls,
            List<CoilOutput> coils,
            List<ConditionCoil> condCoils)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(blockName)) parts.Add($"块 {blockName}");
            if (networkId.HasValue) parts.Add($"Network {networkId}");
            if (!string.IsNullOrWhiteSpace(title)) parts.Add($"标题「{title}」");
            if (!string.IsNullOrWhiteSpace(comment)) parts.Add($"注释「{comment}」");

            // 根类型说明
            string rootDescCh = rootName switch
            {
                "Transition" => "Graph 步骤转移条件段（Transition）",
                "Interlock" => "Graph 联锁条件段（Interlock）",
                "Supervision" => "Graph 监视/超时条件段（Supervision）",
                _ => "LAD/FBD 网络（FlgNet）"
            };

            var prefix = parts.Count > 0 ? string.Join("，", parts) + "：" : "";

            // 条件段优先描述（Tr/Il/Sv 自带 Coil 表达式更能反映意图）
            if (condCoils.Count > 0)
            {
                var firstCoil = condCoils[0];
                return prefix + $"本 Network 是 {rootDescCh}，触发表达式 {firstCoil.CoilKind} := {firstCoil.ExpressionText}";
            }

            if (calls.Count == 0 && coils.Count == 0)
                return prefix + $"本 Network 是 {rootDescCh}，无 FB 调用、无 Coil 输出，疑似纯数据搬运或参数初始化。";
            if (calls.Count == 0)
                return prefix + $"本 Network 是 {rootDescCh}，直接以 Coil 驱动 {coils.Count} 个目标信号。";

            var sample = calls.Take(3).Select(c => $"{c.CallInstanceName}（{c.CallTypeName}）");
            var sampleText = string.Join("、", sample);
            return prefix + $"本 Network 是 {rootDescCh}，调用 {calls.Count} 个 FB/FC（如 {sampleText}），完成对应的输出驱动与反馈处理。";
        }

        private static (List<(string Name, string Addr, string? Comment)> Q,
                        List<(string Name, string Addr, string? Comment)> I,
                        List<(string Name, string Addr, string? Comment)> M,
                        List<string> DB) ClassifySignals(
            HashSet<string> signals,
            Dictionary<string, (string Address, string? Comment)> ioInfo)
        {
            var q = new List<(string, string, string?)>();
            var i = new List<(string, string, string?)>();
            var m = new List<(string, string, string?)>();
            var db = new List<string>();

            foreach (var s in signals.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (ioInfo.TryGetValue(s, out var info))
                {
                    var addr = info.Address ?? "";
                    if (addr.StartsWith("%Q", StringComparison.OrdinalIgnoreCase)) q.Add((s, addr, info.Comment));
                    else if (addr.StartsWith("%I", StringComparison.OrdinalIgnoreCase)) i.Add((s, addr, info.Comment));
                    else if (addr.StartsWith("%M", StringComparison.OrdinalIgnoreCase)) m.Add((s, addr, info.Comment));
                    else db.Add(s);
                }
                else
                {
                    db.Add(s);
                }
            }
            return (q, i, m, db);
        }

        private static List<string> FilterKeyDbVariables(List<string> all)
        {
            // 只保留末段命中关键引脚名白名单的 DB 变量。
            // 不按命名前后缀做项目特定过滤；末段引脚名是博图标准（项目无关）。
            return all.Where(s => IsModeSelectionPin(LastSegment(s)) ||
                                  IsSafetyPin(LastSegment(s)) ||
                                  IsResetPin(LastSegment(s)) ||
                                  IsPermitPin(LastSegment(s)) ||
                                  IsFbStatusPin(LastSegment(s)))
                      .Distinct(StringComparer.OrdinalIgnoreCase)
                      .ToList();
        }

        private static string LastSegment(string sym)
        {
            if (string.IsNullOrWhiteSpace(sym)) return string.Empty;
            // 去掉数组下标 [N]
            var clean = System.Text.RegularExpressions.Regex.Replace(sym, @"\[[^\]]*\]", "");
            var idx = clean.LastIndexOf('.');
            return idx >= 0 ? clean.Substring(idx + 1) : clean;
        }

        // 西门子标准 FB 引脚命名（项目无关）
        private static bool IsModeSelectionPin(string seg) =>
            string.Equals(seg, "ManualMod", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(seg, "AutoMod", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(seg, "AutoRunMod", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(seg, "DebugMod", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(seg, "SemiAutoMod", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(seg, "Model", StringComparison.OrdinalIgnoreCase);

        private static bool IsSafetyPin(string seg) =>
            string.Equals(seg, "E_StopStatus", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(seg, "SafetyStatus", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(seg, "SafeStop", StringComparison.OrdinalIgnoreCase);

        private static bool IsResetPin(string seg) =>
            string.Equals(seg, "ResetStatus", StringComparison.OrdinalIgnoreCase);

        private static bool IsPermitPin(string seg) =>
            string.Equals(seg, "FwdPmt", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(seg, "BkdPmt", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(seg, "PmtDebug", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(seg, "FwdAuto", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(seg, "BkdAuto", StringComparison.OrdinalIgnoreCase);

        private static bool IsFbStatusPin(string seg) =>
            string.Equals(seg, "NeedSafeAckFwd", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(seg, "NeedSafeAckBkd", StringComparison.OrdinalIgnoreCase);

        // FB 标准输出引脚 / 反馈引脚（项目无关）
        private static bool IsQFwdOutputPin(string seg) => string.Equals(seg, "QFwd", StringComparison.OrdinalIgnoreCase);
        private static bool IsQBkdOutputPin(string seg) => string.Equals(seg, "QBkd", StringComparison.OrdinalIgnoreCase);
        private static bool IsIWPInputPin(string seg) => string.Equals(seg, "IWP", StringComparison.OrdinalIgnoreCase);
        private static bool IsIHPInputPin(string seg) => string.Equals(seg, "IHP", StringComparison.OrdinalIgnoreCase);

        private static List<QIPair> BuildQIPairs(List<CallInfo> calls, Dictionary<string, (string Address, string? Comment)> ioInfo)
        {
            var result = new List<QIPair>();
            foreach (var ci in calls)
            {
                // QFwd → IWP 绑定的 I 信号
                var qfwd = ci.OutputPins.FirstOrDefault(p => IsQFwdOutputPin(p.PinName));
                var iwp = ci.InputPins.FirstOrDefault(p => IsIWPInputPin(p.PinName));
                if (qfwd != null && !string.IsNullOrWhiteSpace(qfwd.DirectSignal) && iwp != null && iwp.BoundSignals.Count > 0)
                {
                    var qAddr = ioInfo.TryGetValue(qfwd.DirectSignal!, out var qi) ? qi.Address : "";
                    var iSigs = iwp.BoundSignals.Where(s => ioInfo.TryGetValue(s, out var ii) && ii.Address.StartsWith("%I", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (qAddr.StartsWith("%Q", StringComparison.OrdinalIgnoreCase) && iSigs.Count > 0)
                    {
                        var topology = iSigs.Count == 1 ? "单对单 Pairing" : $"一对多 AND 同步到位（AllRequired，{iSigs.Count} 个 I 信号互相不互斥）";
                        var iDesc = iSigs.Count == 1 ? iSigs[0] : "(" + string.Join(" AND ", iSigs) + ")";
                        result.Add(new QIPair { QSignal = qfwd.DirectSignal!, IDescription = iDesc, CallInstance = ci.CallInstanceName, QPin = "QFwd", IPin = "IWP", Topology = topology });
                    }
                }

                // QBkd → IHP 绑定的 I 信号
                var qbkd = ci.OutputPins.FirstOrDefault(p => IsQBkdOutputPin(p.PinName));
                var ihp = ci.InputPins.FirstOrDefault(p => IsIHPInputPin(p.PinName));
                if (qbkd != null && !string.IsNullOrWhiteSpace(qbkd.DirectSignal) && ihp != null && ihp.BoundSignals.Count > 0)
                {
                    var qAddr = ioInfo.TryGetValue(qbkd.DirectSignal!, out var qi) ? qi.Address : "";
                    var iSigs = ihp.BoundSignals.Where(s => ioInfo.TryGetValue(s, out var ii) && ii.Address.StartsWith("%I", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (qAddr.StartsWith("%Q", StringComparison.OrdinalIgnoreCase) && iSigs.Count > 0)
                    {
                        var topology = iSigs.Count == 1 ? "单对单 Pairing" : $"一对多 AND 同步到位（AllRequired，{iSigs.Count} 个 I 信号互相不互斥）";
                        var iDesc = iSigs.Count == 1 ? iSigs[0] : "(" + string.Join(" AND ", iSigs) + ")";
                        result.Add(new QIPair { QSignal = qbkd.DirectSignal!, IDescription = iDesc, CallInstance = ci.CallInstanceName, QPin = "QBkd", IPin = "IHP", Topology = topology });
                    }
                }
            }
            return result;
        }

        private static string BuildITopology(List<CallInfo> calls, Dictionary<string, (string Address, string? Comment)> ioInfo)
        {
            var lines = new List<string>();
            foreach (var ci in calls)
            {
                foreach (var pin in ci.InputPins.Where(p => IsIWPInputPin(p.PinName) || IsIHPInputPin(p.PinName)))
                {
                    var iSigs = pin.BoundSignals.Where(s => ioInfo.TryGetValue(s, out var ii) && ii.Address.StartsWith("%I", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (iSigs.Count >= 2)
                    {
                        // 表达式中含 AND 的就是串联（同步到位）
                        var expr = pin.ExpressionText ?? "";
                        if (expr.Contains(" AND "))
                            lines.Add($"- {ci.CallInstanceName}.{pin.PinName} 串联（AND 同步到位）：{string.Join(", ", iSigs)} → AllRequired，互相不互斥");
                        else if (expr.Contains(" OR "))
                            lines.Add($"- {ci.CallInstanceName}.{pin.PinName} 并联（OR 任一）：{string.Join(", ", iSigs)}");
                        else
                            lines.Add($"- {ci.CallInstanceName}.{pin.PinName} 多 I 信号：{string.Join(", ", iSigs)}");
                    }
                }
            }
            return string.Join("\n", lines);
        }

        private static List<string> BuildMutexes(List<CallInfo> calls, Dictionary<string, (string Address, string? Comment)> ioInfo)
        {
            var lines = new List<string>();
            foreach (var ci in calls)
            {
                // 同 FB 反向 Q 输出对：QFwd 绑定 vs QBkd 绑定（必须都是 %Q）
                var qfwd = ci.OutputPins.FirstOrDefault(p => IsQFwdOutputPin(p.PinName));
                var qbkd = ci.OutputPins.FirstOrDefault(p => IsQBkdOutputPin(p.PinName));
                if (qfwd != null && qbkd != null
                    && !string.IsNullOrWhiteSpace(qfwd.DirectSignal) && !string.IsNullOrWhiteSpace(qbkd.DirectSignal)
                    && ioInfo.TryGetValue(qfwd.DirectSignal!, out var qfwdAddr) && qfwdAddr.Address.StartsWith("%Q", StringComparison.OrdinalIgnoreCase)
                    && ioInfo.TryGetValue(qbkd.DirectSignal!, out var qbkdAddr) && qbkdAddr.Address.StartsWith("%Q", StringComparison.OrdinalIgnoreCase))
                {
                    lines.Add($"同一 FB 实例 {ci.CallInstanceName} 反向 Q 输出对互斥：{qfwd.DirectSignal} ↔ {qbkd.DirectSignal}（Mutex/AT_MOST_ONE）");
                }

                // 同 FB 反向 I 到位反馈对：IWP 绑定 vs IHP 绑定（仅当各自只绑定 1 个 %I 信号时才生成 Pairwise Mutex，
                // 多 I 同步组的不互相互斥）
                var iwp = ci.InputPins.FirstOrDefault(p => IsIWPInputPin(p.PinName));
                var ihp = ci.InputPins.FirstOrDefault(p => IsIHPInputPin(p.PinName));
                if (iwp != null && ihp != null)
                {
                    var iwpI = iwp.BoundSignals.Where(s => ioInfo.TryGetValue(s, out var ii) && ii.Address.StartsWith("%I", StringComparison.OrdinalIgnoreCase)).ToList();
                    var ihpI = ihp.BoundSignals.Where(s => ioInfo.TryGetValue(s, out var ii) && ii.Address.StartsWith("%I", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (iwpI.Count == 1 && ihpI.Count == 1)
                    {
                        lines.Add($"同一 FB 实例 {ci.CallInstanceName} 反向到位反馈互斥：{iwpI[0]} ↔ {ihpI[0]}（Mutex/AT_MOST_ONE）");
                    }
                    else if (iwpI.Count > 0 && ihpI.Count > 0)
                    {
                        // 多 I 同步组：W 组互相不互斥，H 组互相不互斥，W 组所有信号 ↔ H 组所有信号成对互斥
                        lines.Add($"同一 FB 实例 {ci.CallInstanceName} 多缸同步反向反馈互斥：W 组 [{string.Join(", ", iwpI)}] ↔ H 组 [{string.Join(", ", ihpI)}] 成对互斥（每对一条 Mutex/AT_MOST_ONE）");
                    }
                }
            }

            // 模式互斥：本 Network 出现 2+ 模式选择信号
            var modeSigs = calls.SelectMany(c => c.InputPins)
                .Where(p => IsModeSelectionPin(p.PinName))
                .SelectMany(p => p.BoundSignals)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (modeSigs.Count >= 2)
            {
                lines.Add($"模式互斥：[{string.Join(", ", modeSigs)}] 互为 Member（Mutex/AT_MOST_ONE）");
            }

            return lines;
        }

        private static List<string> BuildSafetyInterlocks(List<CallInfo> calls)
        {
            var lines = new List<string>();
            foreach (var ci in calls)
            {
                // E_StopStatus → 该 FB 输出的 Q 信号必为 false
                var estop = ci.InputPins.FirstOrDefault(p => IsSafetyPin(p.PinName));
                if (estop != null && estop.BoundSignals.Count > 0)
                {
                    var qfwd = ci.OutputPins.FirstOrDefault(p => IsQFwdOutputPin(p.PinName));
                    var qbkd = ci.OutputPins.FirstOrDefault(p => IsQBkdOutputPin(p.PinName));
                    var qOutputs = new List<string>();
                    if (qfwd != null && !string.IsNullOrWhiteSpace(qfwd.DirectSignal)) qOutputs.Add(qfwd.DirectSignal!);
                    if (qbkd != null && !string.IsNullOrWhiteSpace(qbkd.DirectSignal)) qOutputs.Add(qbkd.DirectSignal!);
                    foreach (var src in estop.BoundSignals)
                    {
                        foreach (var qOut in qOutputs)
                        {
                            lines.Add($"安全联锁：{src} → {qOut} 必为 false（Safety/REQUIRES_0）");
                        }
                    }
                }

                // ResetStatus → 该 FB 输出的 Q 信号必为 false（复位时）
                var reset = ci.InputPins.FirstOrDefault(p => IsResetPin(p.PinName));
                if (reset != null && reset.BoundSignals.Count > 0)
                {
                    var qfwd = ci.OutputPins.FirstOrDefault(p => IsQFwdOutputPin(p.PinName));
                    var qbkd = ci.OutputPins.FirstOrDefault(p => IsQBkdOutputPin(p.PinName));
                    var qOutputs = new List<string>();
                    if (qfwd != null && !string.IsNullOrWhiteSpace(qfwd.DirectSignal)) qOutputs.Add(qfwd.DirectSignal!);
                    if (qbkd != null && !string.IsNullOrWhiteSpace(qbkd.DirectSignal)) qOutputs.Add(qbkd.DirectSignal!);
                    foreach (var src in reset.BoundSignals)
                    {
                        foreach (var qOut in qOutputs)
                        {
                            lines.Add($"复位联锁：{src} → {qOut} 必为 false（Safety/REQUIRES_0）");
                        }
                    }
                }
            }
            return lines;
        }

        // ===== PLD 构建内部数据类 =====
        private sealed class CallInfo
        {
            public string CallTypeName { get; set; } = string.Empty;
            public string CallInstanceName { get; set; } = string.Empty;
            public List<PinBinding> InputPins { get; } = new();
            public List<PinBinding> OutputPins { get; } = new();
        }

        private sealed class PinBinding
        {
            public string PinName { get; set; } = string.Empty;
            public string? DirectSignal { get; set; }
            public string? ExpressionText { get; set; }
            public List<string> BoundSignals { get; set; } = new();
        }

        private sealed class CoilOutput
        {
            public string TargetSignal { get; set; } = string.Empty;
            public string ExpressionText { get; set; } = string.Empty;
            public List<string> InputSignals { get; set; } = new();
        }

        private sealed class ConditionCoil
        {
            public string CoilKind { get; set; } = string.Empty; // TrCoil / IlCoil / SvCoil
            public string ExpressionText { get; set; } = string.Empty;
            public List<string> InputSignals { get; set; } = new();
        }

        private sealed class QIPair
        {
            public string QSignal { get; set; } = string.Empty;
            public string IDescription { get; set; } = string.Empty;
            public string CallInstance { get; set; } = string.Empty;
            public string QPin { get; set; } = string.Empty;
            public string IPin { get; set; } = string.Empty;
            public string Topology { get; set; } = string.Empty;
        }
    }
}
