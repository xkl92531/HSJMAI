using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.Agent.Services.Plc_diagnosis
{
    /// <summary>
    /// 对零歧义的标准 FB 调用模式直接产出约束 JSON，补足 LLM 在结构性约束上的漏识
    /// （典型场景：IWP/IHP 反向反馈对 Mutex 93% 漏识率）。
    ///
    /// 设计原则（遵循 CLAUDE.md §4.2 铁律）：
    /// 1. 仅基于结构证据判定（FB 引脚名 / Wire 拓扑 / 地址前缀），不依赖命名前后缀
    /// 2. 仅识别零歧义模式，多触点复杂模式留给 LLM
    /// 3. 输出 JSON 与 LLM 输出格式一致，由调用方合并到主流程，统一走 SaveIOConstraintsAsync 入库过滤
    ///
    /// **数据来源**：本类的所有方法直接消费 <see cref="PldAnchorBlock"/> 结构数据
    /// （由 <see cref="SiemensFlgNetConstraintExtractor.ExtractAnchorBlocks"/> 从厂商 XML 抽出）。
    /// 不读 ParsedLogicDescription Markdown 文本，不做任何文本正则解析。
    ///
    /// 换厂商支持时只需提供「XML → <see cref="PldAnchorBlock"/>」适配器，
    /// 本类的约束推理逻辑完全复用，无需任何修改。
    /// </summary>
    public static class PldStructuralConstraintAugmenter
    {
        private static readonly Regex AddrBitByteRe = new(@"^%[QIM](\d+)\.(\d+)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // === 公共能力 ===

        /// <summary>
        /// 消费 LogicUnit 锚点结构，输出结构性约束 JSON 数组（与 LLM 输出格式一致）。
        /// 调用 <see cref="SiemensFlgNetConstraintExtractor.ExtractAnchorBlocks"/> 从 XML 取结构。
        /// </summary>
        public static string Augment(
            string? rawContent,
            Dictionary<string, PLC_IOTagEntity> ioTagByName,
            long luId,
            string? blockName)
        {
            if (string.IsNullOrWhiteSpace(rawContent))
            {
                return "[]";
            }

            var allAnchors = SiemensFlgNetConstraintExtractor.ExtractAnchorBlocks(rawContent);
            return AugmentFromBlocks(allAnchors, ioTagByName);
        }

        /// <summary>
        /// 用 LogicUnit 锚点结构推理结构性约束（与 <see cref="Augment"/> 等价的「已拿到结构」版本，避免上游重复解析 XML）。
        /// </summary>
        public static string AugmentFromBlocks(
            List<PldAnchorBlock> allAnchors,
            Dictionary<string, PLC_IOTagEntity> ioTagByName)
        {
            var array = new JsonArray();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // === 同步驱动对（SAME_STATE 约束）正向补全 ===
            // 识别两个独立 Coil 锚点子树触点完全相同 → 物理上同步通电 → 状态必须一致
            // 用 SAME_STATE operator 表达："全部 Member 状态必须一致（同 true 或同 false），用于诊断继电器/接线故障"
            //
            // 排除 RCoil（复位线圈）：RCoil 触发条件相同只代表「被同一信号复位」，
            // 不代表它们"状态一定同步"——它们的 SCoil（置位逻辑）可能完全独立（如本 LU
            // FZ91→SCoil Error1、FZ94→SCoil Error3 但 RCoil 都被 ResetStatus 复位）。
            // 只有 SCoil/普通 Coil 触发条件相同才意味着同步通电状态。
            var coilAnchors = allAnchors
                .Where(b => b.Type == PldAnchorType.Coil
                            && b.DirectContacts.Count > 0
                            && !(b.TypeText ?? string.Empty).Contains("RCoil", StringComparison.OrdinalIgnoreCase))
                .ToList();
            // 把触点集合相同的 Coil 锚点分组（不只 2 元，N 元同步对也支持）
            var coilGroupsByContacts = coilAnchors
                .GroupBy(b => string.Join("|", b.DirectContacts
                    .OrderBy(c => c.Polarity).ThenBy(c => c.Name)
                    .Select(c => $"{c.Polarity}:{c.Name}")))
                .Where(g => g.Count() >= 2)  // 至少 2 个 Coil 同步才有约束意义
                .ToList();
            foreach (var grp in coilGroupsByContacts)
            {
                var members = grp.Select(b => b.Anchor)
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (members.Count < 2) continue;
                // 仅当所有 Member 都在 IO Tag 表（能采集到值）时才产 SAME_STATE 约束。
                // 否则典型如 DB 内部状态信号（MOP_AUTO/MOP_HALT/AxisStatusControl.* 等）
                // 诊断引擎采集不到值 → 即使产了约束也无法被实时校验 → 等于无效约束污染表。
                if (!members.All(m => ioTagByName.ContainsKey(m.Trim()))) continue;
                var key = "SAMESTATE|" + string.Join("|", members);
                if (seen.Add(key))
                    array.Add(BuildSyncState(members, "独立 Coil 锚点同步驱动（触点表达式完全相同，状态必须一致）"));
            }

            var fbBlocks = allAnchors.Where(b => b.Type == PldAnchorType.FbCall).ToList();
            if (fbBlocks.Count == 0)
            {
                return array.ToJsonString(new JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
            }

            foreach (var blk in fbBlocks)
            {
                blk.OutPins.TryGetValue("QFwd", out var qFwd);
                blk.OutPins.TryGetValue("QBkd", out var qBkd);
                bool qFwdIsQ = IsQAddr(GetAddr(ioTagByName, qFwd));
                bool qBkdIsQ = IsQAddr(GetAddr(ioTagByName, qBkd));

                string? iwpI = SoleIInputSignal(blk.InPinContacts, "IWP", ioTagByName);
                string? ihpI = SoleIInputSignal(blk.InPinContacts, "IHP", ioTagByName);

                // 1) Mutex(QFwd, QBkd)
                if (qFwdIsQ && qBkdIsQ && !string.Equals(qFwd, qBkd, StringComparison.OrdinalIgnoreCase))
                {
                    var key = BuildMutexKey(qFwd!, qBkd!);
                    if (seen.Add(key))
                        array.Add(BuildMutex(qFwd!, qBkd!, "同 FB 反向 Q 输出对（结构性补全）"));
                }

                // 2) Mutex(IWP_I, IHP_I) — 零歧义路径
                if (iwpI != null && ihpI != null
                    && !string.Equals(iwpI, ihpI, StringComparison.OrdinalIgnoreCase))
                {
                    var key = BuildMutexKey(iwpI, ihpI);
                    if (seen.Add(key))
                        array.Add(BuildMutex(iwpI, ihpI, "同 FB 反向 I 反馈对（结构性补全）"));
                }
                else
                {
                    // 2b) Mutex(IWP_I, IHP_I) — 多 %I 触点场景，按「地址同字节相邻 bit」配对
                    var iwpAll = blk.InPinContacts.TryGetValue("IWP", out var iwpC)
                        ? iwpC.Where(c => c.Polarity == "NO").Select(c => c.Name).ToList()
                        : new List<string>();
                    var ihpAll = blk.InPinContacts.TryGetValue("IHP", out var ihpC)
                        ? ihpC.Where(c => c.Polarity == "NO").Select(c => c.Name).ToList()
                        : new List<string>();
                    var iwpISignals = iwpAll.Where(c => IsIAddr(GetAddr(ioTagByName, c))).ToList();
                    var ihpISignals = ihpAll.Where(c => IsIAddr(GetAddr(ioTagByName, c))).ToList();
                    if (iwpISignals.Count >= 1 && ihpISignals.Count >= 1
                        && (iwpISignals.Count > 1 || ihpISignals.Count > 1))
                    {
                        foreach (var w in iwpISignals)
                        {
                            var wAddr = ParseAddrBitByte(GetAddr(ioTagByName, w));
                            if (wAddr == null) continue;
                            foreach (var h in ihpISignals)
                            {
                                if (string.Equals(w, h, StringComparison.OrdinalIgnoreCase)) continue;
                                var hAddr = ParseAddrBitByte(GetAddr(ioTagByName, h));
                                if (hAddr == null) continue;
                                // 严格 W_bit == H_bit + 1（H 偶 W 奇）防跨缸误配
                                if (wAddr.Value.Byte != hAddr.Value.Byte) continue;
                                if (wAddr.Value.Bit != hAddr.Value.Bit + 1) continue;
                                var key2 = BuildMutexKey(w, h);
                                if (seen.Add(key2))
                                    array.Add(BuildMutex(w, h, "同 FB 反向 I 反馈对（多触点同字节相邻 bit 配对补全）"));
                            }
                        }
                    }
                }

                // 3) Pairing(QFwd → IWP_I)
                if (qFwdIsQ && iwpI != null)
                {
                    var key = $"PAIRING|{qFwd}|{iwpI}";
                    if (seen.Add(key))
                        array.Add(BuildPairing(qFwd!, iwpI, "QFwd→IWP 反馈（结构性补全）"));
                }

                // 4) Pairing(QBkd → IHP_I)
                if (qBkdIsQ && ihpI != null)
                {
                    var key = $"PAIRING|{qBkd}|{ihpI}";
                    if (seen.Add(key))
                        array.Add(BuildPairing(qBkd!, ihpI, "QBkd→IHP 反馈（结构性补全）"));
                }

                // 5) 位置传感器组 Mutex（V90/S210 伺服 FB 标准模式）：
                //    同 FB 实例下，≥3 个不同输入引脚各自只有 1 个 %I 常开触点 + 地址同字节
                //    → 这些 %I 是同一伺服轴的多个位置传感器（典型：负向限位/原点/正向限位）
                //    → 物理上 AT_MOST_ONE（任一时刻轴只能在一个位置）
                //
                //    判定基于纯结构证据：本 FB 调用上只接 1 个常开 %I 的输入引脚「恰好 3 个」且同字节
                //    （这三个就是该轴的全部离散输入）。4 个以上说明输入属于多台设备或多种状态，不是一个机构的
                //    互斥位置——2026-09-30 实测误报：FB3100_ValveControl 一次调用带两只气缸（4 个）、
                //    FB3430_GearSync 主从两根轴（6 个）、FB6040_TOS9301 耐压仪状态（7~8 个）、
                //    FB12350_Gate_Display_FB 按钮+门栓（4 个）；FB7010_V90PN（3 个）正确。
                //    跨厂商/跨项目通用，不依赖具体引脚命名（Stop_cam_minus 等是博图标准但不强求）
                var positionPinSignals = new List<(string PinName, string SignalName, int Byte, int Bit)>();
                foreach (var kv in blk.InPinContacts)
                {
                    var soleI = SoleIInputSignal(blk.InPinContacts, kv.Key, ioTagByName);
                    if (soleI == null) continue;
                    var addr = ParseAddrBitByte(GetAddr(ioTagByName, soleI));
                    if (addr == null) continue;
                    positionPinSignals.Add((kv.Key, soleI, addr.Value.Byte, addr.Value.Bit));
                }
                if (positionPinSignals.Count == 3)
                {
                    var byByte = positionPinSignals
                        .GroupBy(p => p.Byte)
                        .Where(g => g.Count() >= 3)
                        .ToList();
                    foreach (var grp in byByte)
                    {
                        var members = grp.Select(p => p.SignalName)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        if (members.Count < 3) continue;
                        var key = "POSGRP|" + string.Join("|", members);
                        if (!seen.Add(key)) continue;
                        var memberArr = new JsonArray();
                        foreach (var m in members)
                            memberArr.Add(new JsonObject { ["signalName"] = m, ["signalRole"] = "Member" });
                        array.Add(new JsonObject
                        {
                            ["ruleName"] = $"{ShortName(members[0])}_位置组互斥",
                            ["ruleType"] = "Mutex",
                            ["operator"] = "AT_MOST_ONE",
                            ["confidence"] = 0.95,
                            ["priority"] = "High",
                            ["source"] = "Structural",
                            ["description"] = $"同 FB 实例下 {members.Count} 个位置传感器组（同字节）互斥：任一时刻只能有 1 个 %I 信号为 true（典型伺服轴负向限位/原点/正向限位）",
                            ["signals"] = memberArr
                        });
                    }
                }
            }

            return array.ToJsonString(new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        }

        /// <summary>
        /// 从 LogicUnit XML 中提取**合法 Mutex 信号对**集合（用作白名单）。
        ///
        /// **判定纯结构+地址证据，不依赖具体引脚名**（不假设 QFwd/QBkd/IWP/IHP 等命名）：
        ///   - 反向 Q 对：同 FB 实例**任意两个**输出引脚映射的 %Q 信号 + **地址同字节相邻 bit**
        ///   - 反向 I 对：同 FB 实例**不同**输入引脚下各的 %I 触点 + **地址同字节相邻 bit**
        ///
        /// 判定基于博图工程师标准布线习惯（反向对总是布在同字节相邻 bit），跨 FB 类型/跨厂商通用。
        /// 不在此集合的 Mutex 仍可能合法（如模式信号 ManualMod↔AutoRunMod），调用方需额外白名单兼容。
        /// 返回 frozenset 形式（`"MUTEX|A|B"` 字典序排序）。
        /// </summary>
        public static HashSet<string> ExtractValidMutexPairs(
            string? rawContent,
            Dictionary<string, PLC_IOTagEntity> ioTagByName)
        {
            if (string.IsNullOrWhiteSpace(rawContent)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var blocks = SiemensFlgNetConstraintExtractor.ExtractAnchorBlocks(rawContent);
            return ExtractValidMutexPairsFromBlocks(blocks, ioTagByName);
        }

        public static HashSet<string> ExtractValidMutexPairsFromBlocks(
            List<PldAnchorBlock> blocks,
            Dictionary<string, PLC_IOTagEntity> ioTagByName)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var fbBlocks = blocks.Where(b => b.Type == PldAnchorType.FbCall).ToList();

            foreach (var blk in fbBlocks)
            {
                // === 反向 Q 对：同 FB 任意两个输出引脚映射 %Q 信号 + 同字节相邻 bit ===
                var qOutputs = blk.OutPins.Values
                    .Where(s => !string.IsNullOrWhiteSpace(s) && IsQAddr(GetAddr(ioTagByName, s)))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                for (int i = 0; i < qOutputs.Count; i++)
                {
                    var ai = ParseAddrBitByte(GetAddr(ioTagByName, qOutputs[i]));
                    if (ai == null) continue;
                    for (int j = i + 1; j < qOutputs.Count; j++)
                    {
                        var bj = ParseAddrBitByte(GetAddr(ioTagByName, qOutputs[j]));
                        if (bj == null) continue;
                        if (ai.Value.Byte == bj.Value.Byte && Math.Abs(ai.Value.Bit - bj.Value.Bit) == 1)
                        {
                            result.Add(BuildMutexKey(qOutputs[i], qOutputs[j]));
                        }
                    }
                }

                // === 反向 I 对：同 FB 不同输入引脚各的 %I 触点 + 同字节相邻 bit ===
                // 按输入引脚分组收集 %I 触点（常开）
                var iByPin = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in blk.InPinContacts)
                {
                    var iSigs = kv.Value
                        .Where(c => c.Polarity == "NO")
                        .Select(c => c.Name)
                        .Where(c => IsIAddr(GetAddr(ioTagByName, c)))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (iSigs.Count > 0) iByPin[kv.Key] = iSigs;
                }

                // 不同输入引脚两两组合
                var pinNames = iByPin.Keys.ToList();
                for (int i = 0; i < pinNames.Count; i++)
                {
                    var listA = iByPin[pinNames[i]];
                    for (int j = i + 1; j < pinNames.Count; j++)
                    {
                        var listB = iByPin[pinNames[j]];
                        // 笛卡尔积找同字节相邻 bit 对
                        foreach (var a in listA)
                        {
                            var pa = ParseAddrBitByte(GetAddr(ioTagByName, a));
                            if (pa == null) continue;
                            foreach (var b in listB)
                            {
                                if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) continue;
                                var pb = ParseAddrBitByte(GetAddr(ioTagByName, b));
                                if (pb == null) continue;
                                if (pa.Value.Byte == pb.Value.Byte && Math.Abs(pa.Value.Bit - pb.Value.Bit) == 1)
                                {
                                    result.Add(BuildMutexKey(a, b));
                                }
                            }
                        }
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// 从 LogicUnit XML 中提取**合法 Pairing** (Source→Target) 信号对集合（用作白名单）。
        ///
        /// **判定纯结构+地址证据**：同 FB 实例的任一输出引脚映射 %Q × 任一输入引脚下 %I 触点 = 合法 Pairing 候选。
        /// 用途：入库前对 LLM 产的 Pairing 做"白名单校验"——不在此集合中的 Pairing 整条丢，
        /// 消除 LLM 凭命名启发式（跨方向 / 独立 Coil 信号拼对）产生的错 Pairing。
        /// </summary>
        public static HashSet<string> ExtractValidPairings(
            string? rawContent,
            Dictionary<string, PLC_IOTagEntity> ioTagByName)
        {
            if (string.IsNullOrWhiteSpace(rawContent)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var blocks = SiemensFlgNetConstraintExtractor.ExtractAnchorBlocks(rawContent);
            return ExtractValidPairingsFromBlocks(blocks, ioTagByName);
        }

        public static HashSet<string> ExtractValidPairingsFromBlocks(
            List<PldAnchorBlock> blocks,
            Dictionary<string, PLC_IOTagEntity> ioTagByName)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var fbBlocks = blocks.Where(b => b.Type == PldAnchorType.FbCall).ToList();

            foreach (var blk in fbBlocks)
            {
                // 不依赖具体引脚名（不假设 QFwd/QBkd/IWP/IHP）：
                // 1) 收集本 FB 所有输出引脚映射的 %Q 信号 → 候选 Source（必须是 FB 真实绑定的输出，不是凭命名）
                // 2) 收集本 FB 所有输入引脚下的 %I 触点 → 候选 Target
                // 3) 笛卡尔积：所有 Source × 所有 Target = 合法 Pairing 候选
                //
                // 关键约束「Source 必须是 FB 真实输出」：LLM 凭命名误把外部独立 Coil 信号
                // （如 OP080_YV08_W 这种在 FB QFwd/QBkd 未绑定情况下的独立 Coil）当 FB 输出 Source —— 不在白名单。
                var qSources = blk.OutPins.Values
                    .Where(s => !string.IsNullOrWhiteSpace(s) && IsQAddr(GetAddr(ioTagByName, s)))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var iTargets = new List<string>();
                foreach (var kv in blk.InPinContacts)
                {
                    foreach (var c in kv.Value.Where(c => c.Polarity == "NO"))
                    {
                        if (IsIAddr(GetAddr(ioTagByName, c.Name))) iTargets.Add(c.Name);
                    }
                }
                iTargets = iTargets.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                foreach (var src in qSources)
                    foreach (var tgt in iTargets)
                    {
                        if (string.Equals(src, tgt, StringComparison.OrdinalIgnoreCase)) continue;
                        result.Add($"{src}→{tgt}");
                    }
            }
            return result;
        }

        /// <summary>
        /// 从 LogicUnit XML 中识别「同步驱动对」——两个独立 Coil 锚点，子树触点（含常开/常闭极性）完全相同。
        /// 例：OP080_YV08_W 和 OP080_YV08_H 都被 `Valve[8].QBkd` 驱动 → 同时通电 → 不是反向对 → Mutex(W,H) 应被拒绝。
        ///
        /// 返回去重后的「同步对」集合，每对用 `"A|B"`（按字典序排序）作为 key。
        /// 调用方在 LLM 输出 JSON 后用此集合过滤命中的 Mutex(A,B)。
        ///
        /// 严格基于结构（锚点 + 子树触点集合），不依赖命名前后缀（遵守 §4.2 铁律）。
        /// </summary>
        /// <summary>
        /// 识别屏蔽态 SCoil 报警网络 → 对该 SCoil 触发条件里的**常开** %I 传感器各产 1 条 <c>MUST_BE_FALSE</c> 单 Target 约束。
        ///
        /// **逐 SCoil 锚点判定**（不再无脑收 LU 全局 %I）：
        /// 1. 仅处理 <c>TypeText</c> 含 "SCoil" 的 Coil 锚点（不处理 RCoil / 普通 Coil）
        /// 2. 该 SCoil 的 <c>DirectContacts</c> 必须含 <c>AlwaysTRUE</c>/<c>AlwaysFALSE</c>/<c>Null</c>
        ///    占位符触点 → 是「屏蔽态」（被全局开关绕过）
        /// 3. 收集同 SCoil <c>DirectContacts</c> 里 <c>Polarity="NO"（常开）</c> + 命中 IO Tag 表 +
        ///    地址 %I 的信号 → 各产 1 条 MUST_BE_FALSE
        ///
        /// **关键改进**（NEW 2026-05-27 修复入库后质量检测发现的 5 条反向约束）：
        /// - 旧实现「LU 含 SCoil + 含占位符 → 收 LU 全局 %I Access」太粗暴：
        ///   * 把不在 SCoil 触发链路上的 %I 信号也产了约束（如 FC190 Net12 的 FZ104/109/110）
        ///   * 把「触点(常闭)」的 %I 信号也产了约束（如 SafeDoor*_Estop_PB 急停按钮，按下=false 触发报警，
        ///     MUST_BE_FALSE 语义完全反了）
        /// - 新实现严格用 SCoil <c>DirectContacts</c> + 极性筛选，仅常开 %I 触点产 MUST_BE_FALSE
        ///
        /// **TON 阻断问题已解决**：依赖 <see cref="SiemensFlgNetConstraintExtractor.BuildExpressionForTarget"/>
        /// 的 TON 穿透分支（IsTimerPartName），TON 后游 %I 信号能正确出现在 SCoil.DirectContacts。
        ///
        /// 输出 JSON 与其它 Augment 方法格式一致，可与 LLM 输出合并入库。
        /// 单 Target 形态由 <see cref="HSJM.AI.AppLayer.Agent.Services.Plc_diagnosis.PlcDiagnosisService"/>
        /// 的 §5.1 2b-2 结构破损校验对 <c>MUST_BE_FALSE</c> 放宽支持。
        /// </summary>
        public static string AugmentMaskedAlarm(
            string? rawContent,
            Dictionary<string, PLC_IOTagEntity> ioTagByName)
        {
            if (string.IsNullOrWhiteSpace(rawContent)) return "[]";
            var blocks = SiemensFlgNetConstraintExtractor.ExtractAnchorBlocks(rawContent);
            return AugmentMaskedAlarmFromBlocks(blocks, ioTagByName);
        }

        public static string AugmentMaskedAlarmFromBlocks(
            List<PldAnchorBlock> blocks,
            Dictionary<string, PLC_IOTagEntity> ioTagByName)
        {
            var array = new JsonArray();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var placeholderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AlwaysTRUE", "AlwaysFALSE", "Null" };

            foreach (var blk in blocks)
            {
                if (blk.Type != PldAnchorType.Coil) continue;
                // 仅 SCoil（置位线圈）的屏蔽态报警网络才走 MUST_BE_FALSE
                if (string.IsNullOrEmpty(blk.TypeText)
                    || blk.TypeText.IndexOf("SCoil", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                // 该 SCoil 触发条件必须含屏蔽态占位符（AlwaysTRUE/AlwaysFALSE/Null）
                bool isMasked = blk.DirectContacts.Any(c =>
                    placeholderNames.Contains(c.Name?.Trim() ?? string.Empty));
                if (!isMasked) continue;
                // 收集同 SCoil 触发条件里「常开 %I 触点」的信号 → 产 MUST_BE_FALSE
                // 常闭触点（如急停按钮 SafeDoor*_Estop_PB 按下=false 触发报警）跳过 — 语义相反
                foreach (var c in blk.DirectContacts)
                {
                    if (c == null) continue;
                    if (!string.Equals(c.Polarity, "NO", StringComparison.OrdinalIgnoreCase)) continue;
                    var name = c.Name?.Trim();
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!ioTagByName.TryGetValue(name, out var tag)) continue;
                    if (string.IsNullOrWhiteSpace(tag?.Address)) continue;
                    if (!tag!.Address!.StartsWith("%I", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!seen.Add(name)) continue;
                    array.Add(BuildMustBeFalse(name));
                }
            }
            return array.ToJsonString(new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        }

        public static HashSet<string> ExtractSyncDrivenPairs(string? rawContent)
        {
            if (string.IsNullOrWhiteSpace(rawContent)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var blocks = SiemensFlgNetConstraintExtractor.ExtractAnchorBlocks(rawContent);
            return ExtractSyncDrivenPairsFromBlocks(blocks);
        }

        public static HashSet<string> ExtractSyncDrivenPairsFromBlocks(List<PldAnchorBlock> blocks)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // 排除 RCoil（复位线圈）：「被同一信号复位」≠「状态同步」（SCoil 置位逻辑可能独立）
            var coilBlocks = blocks
                .Where(b => b.Type == PldAnchorType.Coil
                            && b.DirectContacts.Count > 0
                            && !(b.TypeText ?? string.Empty).Contains("RCoil", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (coilBlocks.Count < 2) return result;

            for (int i = 0; i < coilBlocks.Count; i++)
            {
                for (int j = i + 1; j < coilBlocks.Count; j++)
                {
                    var a = coilBlocks[i];
                    var b = coilBlocks[j];
                    if (string.IsNullOrWhiteSpace(a.Anchor) || string.IsNullOrWhiteSpace(b.Anchor)) continue;
                    if (string.Equals(a.Anchor, b.Anchor, StringComparison.OrdinalIgnoreCase)) continue;
                    var aSet = a.DirectContacts.ToHashSet();
                    var bSet = b.DirectContacts.ToHashSet();
                    if (aSet.SetEquals(bSet))
                    {
                        var pair = new[] { a.Anchor, b.Anchor }
                            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase);
                        result.Add(string.Join("|", pair));
                    }
                }
            }
            return result;
        }

        // === 内部工具 ===

        private static string? SoleIInputSignal(
            Dictionary<string, List<ContactRef>> inPinContacts,
            string pinName,
            Dictionary<string, PLC_IOTagEntity> ioTagByName)
        {
            if (!inPinContacts.TryGetValue(pinName, out var contacts) || contacts == null) return null;
            var iSignals = contacts
                .Where(c => c.Polarity == "NO")  // 仅常开触点算正向信号引用
                .Select(c => c.Name)
                .Where(c => IsIAddr(GetAddr(ioTagByName, c)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return iSignals.Count == 1 ? iSignals[0] : null;
        }

        private static string? GetAddr(Dictionary<string, PLC_IOTagEntity> ioTagByName, string? signal)
        {
            if (string.IsNullOrWhiteSpace(signal)) return null;
            return ioTagByName.TryGetValue(signal.Trim(), out var tag) ? tag.Address : null;
        }

        private static bool IsQAddr(string? addr)
            => !string.IsNullOrWhiteSpace(addr) && addr!.StartsWith("%Q", StringComparison.OrdinalIgnoreCase);

        private static bool IsIAddr(string? addr)
            => !string.IsNullOrWhiteSpace(addr) && addr!.StartsWith("%I", StringComparison.OrdinalIgnoreCase);

        private static (int Byte, int Bit)? ParseAddrBitByte(string? addr)
        {
            if (string.IsNullOrWhiteSpace(addr)) return null;
            var m = AddrBitByteRe.Match(addr);
            if (!m.Success) return null;
            return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
        }

        private static string BuildMutexKey(string a, string b)
        {
            var pair = new[] { a, b }.OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
            return "MUTEX|" + string.Join("|", pair);
        }

        private static JsonObject BuildMutex(string a, string b, string desc)
        {
            return new JsonObject
            {
                ["ruleName"] = $"{ShortName(a)}_互斥",
                ["ruleType"] = "Mutex",
                ["operator"] = "AT_MOST_ONE",
                ["confidence"] = 0.99,
                ["priority"] = "Critical",
                ["source"] = "Structural",
                ["description"] = desc,
                ["signals"] = new JsonArray(
                    new JsonObject { ["signalName"] = a, ["signalRole"] = "Member" },
                    new JsonObject { ["signalName"] = b, ["signalRole"] = "Member" })
            };
        }

        private static JsonObject BuildSyncState(List<string> members, string desc)
        {
            var memberArr = new JsonArray();
            foreach (var m in members)
                memberArr.Add(new JsonObject { ["signalName"] = m, ["signalRole"] = "Member" });
            return new JsonObject
            {
                ["ruleName"] = $"{ShortName(members[0])}_同步",
                ["ruleType"] = "Synchronous",
                ["operator"] = "SAME_STATE",
                ["confidence"] = 0.99,
                ["priority"] = "High",
                ["source"] = "Structural",
                ["description"] = desc,
                ["signals"] = memberArr
            };
        }

        private static JsonObject BuildPairing(string source, string target, string desc)
        {
            return new JsonObject
            {
                ["ruleName"] = $"{ShortName(source)}->{ShortName(target)}",
                ["ruleType"] = "Pairing",
                ["operator"] = "IMPLIES",
                ["confidence"] = 0.99,
                ["priority"] = "Critical",
                ["source"] = "Structural",
                ["description"] = desc,
                ["signals"] = new JsonArray(
                    new JsonObject { ["signalName"] = source, ["signalRole"] = "Source" },
                    new JsonObject { ["signalName"] = target, ["signalRole"] = "Target" })
            };
        }

        private static JsonObject BuildMustBeFalse(string signal)
        {
            return new JsonObject
            {
                ["ruleName"] = $"{ShortName(signal)}_必须为false",
                ["ruleType"] = "Safety",
                ["operator"] = "MUST_BE_FALSE",
                ["confidence"] = 0.99,
                ["priority"] = "Critical",
                ["source"] = "Structural",
                ["description"] = $"屏蔽态报警传感器 {signal} 永远不应触发；为 true 即真实异常（任一 SCoil 报警网络含 AlwaysTRUE/AlwaysFALSE 屏蔽开关）",
                ["signals"] = new JsonArray(
                    new JsonObject { ["signalName"] = signal, ["signalRole"] = "Target" })
            };
        }

        private static string ShortName(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName)) return fullName ?? string.Empty;
            var lastDot = fullName.LastIndexOf('.');
            return lastDot >= 0 ? fullName[(lastDot + 1)..] : fullName;
        }
    }
}
