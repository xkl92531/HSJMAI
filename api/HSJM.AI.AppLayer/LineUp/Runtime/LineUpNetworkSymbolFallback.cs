#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.IDbService.LineUp;

namespace HSJM.AI.AppLayer.LineUp.Runtime;

/// <summary>
/// 2026-09-11 LAD 深层符号解析回退（生成侧 BindNetworkPartsAsync 与运行时时效校验 ResolveGraphOutcome 共用同口径）。
/// 背景：ai_plc_blockinterface 普遍只展开到 UDT/Struct 容器层或数组元素层，GRAPH 网络引用的深层叶子
/// （如 DB281_EPSONGlue1.Robot_To_PLC.Status.SafetyZone6、DB210_OP080_User.Status.Robot.Robot[1].HomeComplete）
/// 在接口表无精确行 → InterfaceMissing → 条件永久 Unknown（LAD 图上大量变量"未知"）。
/// 但老网关 S7 Plus 设备实测可按完整符号路径直读这些深层叶子（SafetyZone6 / Robot[1] / AllowSync[3] 均 Good）。
/// 两级回退均不需要重新导入 PLC 工程：
/// 1) 数组同名展开：GRAPH XML 把「成员 X 内的同名数组 X[i]」压成一个带索引组件 X[i]（解析器输出单层），
///    而接口表与 PLC 的真实可读路径都是双层 X.X[i]——归一化后可精确命中接口行，按双层路径读值。
/// 2) 最长前缀容器：归一化后仍未命中且存在最长前缀容器行（如 Robot_To_PLC.Status）时，
///    若能按 LAD 拓扑推断叶子类型（喂触点 = Bool、喂比较器 = SrcType），则以原路径绑定，接口行取容器行。
/// 两级都失败才维持 InterfaceMissing（容忍码，不进展示）。
/// </summary>
internal static class LineUpNetworkSymbolFallback
{
    private static readonly HashSet<string> ComparatorPartNames = new(StringComparer.Ordinal)
    {
        "Eq", "Ge", "Gt", "Le", "Lt", "Ne"
    };

    private static readonly HashSet<string> BoolCoilPartNames = new(StringComparer.Ordinal)
    {
        "Coil", "SCoil", "RCoil", "TrCoil", "IlCoil", "SvCoil"
    };

    /// <summary>跨 PLC 通信块名特征（如 DB182_PLC1&lt;-&gt;PLC2）：引用对端工程的块，本 PLC 网关必然读不到。</summary>
    internal static bool IsExternalReferenceBlockName(string blockName) =>
        blockName.Contains("<->", StringComparison.Ordinal);

    /// <summary>
    /// 数组同名展开：把路径中每个带索引段 X[i] 展开为 X.X[i]（仅当上一段不是同名 X——XML 有时本来就带双层形态）。
    /// 例：Status.Robot[1].HomeComplete → Status.Robot.Robot[1].HomeComplete（接口表与 PLC 的真实形式）。
    /// </summary>
    internal static string ExpandIndexedSegments(string variablePath)
    {
        if (!variablePath.Contains('[', StringComparison.Ordinal))
        {
            return variablePath;
        }

        var segments = variablePath.Split('.');
        var changed = false;
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            var open = segment.IndexOf('[');
            if (open <= 0 || !segment.EndsWith("]", StringComparison.Ordinal))
            {
                continue;
            }

            var name = segment[..open];
            if (name.Contains('['))
            {
                continue;
            }

            // XML 里已是双层同名形态（…/X/X[i]）则不再重复展开
            if (i > 0 && string.Equals(segments[i - 1], name, StringComparison.Ordinal))
            {
                continue;
            }

            segments[i] = name + "." + segment;
            changed = true;
        }

        return changed ? string.Join(".", segments) : variablePath;
    }

    /// <summary>
    /// 最长前缀容器行：接口行路径是变量路径的段边界前缀（下一字符为 '.'，或数组元素形式的 '['）。
    /// 例：容器 Robot_To_PLC.Status ← 叶子 Robot_To_PLC.Status.SafetyZone6；
    /// 容器 CamSyncControl.LAnyAxis_MC_CamIn ← 叶子 CamSyncControl.LAnyAxis_MC_CamIn[1].Busy。
    /// </summary>
    internal static LineUpBlockInterfaceLookup? FindLongestPrefixContainer(
        IReadOnlyList<LineUpBlockInterfaceLookup> interfaces,
        long blockId,
        string variablePath)
    {
        LineUpBlockInterfaceLookup? best = null;
        var bestLength = -1;
        foreach (var row in interfaces)
        {
            if (row.Id <= 0 || row.BlockId != blockId)
            {
                continue;
            }

            var prefix = row.VariablePath?.Trim();
            if (string.IsNullOrEmpty(prefix) || prefix.Length >= variablePath.Length)
            {
                continue;
            }

            if (!variablePath.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var next = variablePath[prefix.Length];
            if (next != '.' && next != '[')
            {
                continue;
            }

            if (prefix.Length > bestLength)
            {
                best = row;
                bestLength = prefix.Length;
            }
        }

        return best;
    }

    /// <summary>
    /// 深层叶子解析回退：先试数组同名展开后的精确命中，再试最长前缀容器 + 推断类型。
    /// 返回 null 表示两级回退都不可行（维持 InterfaceMissing）。
    /// </summary>
    internal static (LineUpBlockInterfaceLookup Interface, string BoundVariablePath)? ResolveDeepLeaf(
        IReadOnlyList<LineUpBlockInterfaceLookup> interfaces,
        long blockId,
        string variablePath,
        string? inferredLeafDataType)
    {
        // 1) 数组同名展开：X[i] → X.X[i] 精确命中接口行（PLC 可读形式）
        var expandedPath = ExpandIndexedSegments(variablePath);
        if (!string.Equals(expandedPath, variablePath, StringComparison.Ordinal))
        {
            var expanded = interfaces.Where(row =>
                    row.Id > 0 && row.BlockId == blockId &&
                    string.Equals(row.VariablePath?.Trim(), expandedPath, StringComparison.Ordinal))
                .ToArray();
            if (expanded.Length == 1 && !string.IsNullOrWhiteSpace(expanded[0].DataType))
            {
                return (expanded[0], expandedPath);
            }
        }

        // 2) 最长前缀容器行 + 消费者推断类型：以原路径绑定（网关按完整符号路径直读）
        if (inferredLeafDataType is not null)
        {
            var container = FindLongestPrefixContainer(interfaces, blockId, variablePath);
            if (container is not null)
            {
                return (container with { DataType = inferredLeafDataType }, variablePath);
            }
        }

        return null;
    }

    /// <summary>
    /// 按 LAD 线拓扑推断 Access 叶子的数据类型：喂触点 operand / 线圈 in → Bool；
    /// 喂比较器 in1/in2 → 比较器模板 SrcType。多消费形态冲突或出现未知消费形态时保守放弃（返回 null）。
    /// 与 LineUpLadNetworkEvaluator 相同的线端点读法：端点[0] 为源头（IdentCon/NameCon），
    /// 其后 NameCon 为消费部件（UId + 引脚名）。
    /// </summary>
    internal static string? InferAccessDataType(
        LineUpPublishedLogicNetworkDefinition network,
        LineUpPublishedLogicPartDefinition access)
    {
        string? inferred = null;
        foreach (var wire in network.Wires)
        {
            if (wire.Endpoints.Count == 0)
            {
                continue;
            }

            var head = wire.Endpoints[0];
            // 与求值器相同口径：源头端可为 IdentCon 或 NameCon（均指向部件 UId）
            if (head.UId != access.UId ||
                (!string.Equals(head.Kind, "IdentCon", StringComparison.Ordinal) &&
                 !string.Equals(head.Kind, "NameCon", StringComparison.Ordinal)))
            {
                continue;
            }

            for (var i = 1; i < wire.Endpoints.Count; i++)
            {
                var endpoint = wire.Endpoints[i];
                if (!string.Equals(endpoint.Kind, "NameCon", StringComparison.Ordinal) || endpoint.UId is null)
                {
                    return null;
                }

                var consumer = network.Parts.FirstOrDefault(part => part.UId == endpoint.UId.Value);
                if (consumer is null)
                {
                    return null;
                }

                if (string.Equals(consumer.PartName, "Contact", StringComparison.Ordinal) &&
                    string.Equals(endpoint.Name, "operand", StringComparison.Ordinal))
                {
                    if (!MergeInferred(ref inferred, "Bool"))
                    {
                        return null;
                    }

                    continue;
                }

                if (BoolCoilPartNames.Contains(consumer.PartName) &&
                    string.Equals(endpoint.Name, "in", StringComparison.Ordinal))
                {
                    if (!MergeInferred(ref inferred, "Bool"))
                    {
                        return null;
                    }

                    continue;
                }

                if (ComparatorPartNames.Contains(consumer.PartName) &&
                    (endpoint.Name == "in1" || endpoint.Name == "in2"))
                {
                    if (!consumer.TemplateValues.TryGetValue("SrcType", out var srcType) ||
                        string.IsNullOrWhiteSpace(srcType))
                    {
                        return null;
                    }

                    if (!MergeInferred(ref inferred, srcType))
                    {
                        return null;
                    }

                    continue;
                }

                // 未知消费形态（FB 引脚、嵌套调用等），保守放弃推断
                return null;
            }
        }

        return inferred;
    }

    private static bool MergeInferred(ref string? inferred, string candidate)
    {
        if (inferred is null)
        {
            inferred = candidate;
            return true;
        }

        return string.Equals(inferred, candidate, StringComparison.Ordinal);
    }
}
