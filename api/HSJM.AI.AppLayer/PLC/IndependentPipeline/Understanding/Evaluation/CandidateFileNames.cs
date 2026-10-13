#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

/// <summary>candidates 命令为抽样程序段生成的材料文件名 {块名}_{网络号}，在 Windows 上可用且互不重名。</summary>
public static class CandidateFileNames
{
    // Windows 在所有平台下都视为非法的字符（Path.GetInvalidFileNameChars 在非 Windows 上只含 '/' 和 '\0'）
    static readonly HashSet<char> Invalid = [.. Path.GetInvalidFileNameChars(), '<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    static readonly HashSet<string> Reserved = new(
        ["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(1, 9).Select(i => "COM" + i), .. Enumerable.Range(1, 9).Select(i => "LPT" + i)],
        StringComparer.OrdinalIgnoreCase);

    public static string Of(string block, int? network) =>
        AvoidReserved(new string($"{block}_{network}".Select(c => Invalid.Contains(c) || c < 32 ? '_' : c).ToArray()));

    /// <summary>Windows 保留设备名（第一个 '.' 之前的部分为 CON/PRN/AUX/NUL/COM1-9/LPT1-9，不分大小写）后追加 '_'。</summary>
    public static string AvoidReserved(string name)
    {
        var dot = name.IndexOf('.');
        var stem = dot < 0 ? name : name[..dot];
        return Reserved.Contains(stem) ? stem + "_" + name[stem.Length..] : name;
    }

    /// <summary>按给定顺序分配文件名，清洗后重名（不分大小写）时追加 _2、_3……</summary>
    public static Dictionary<long, string> Assign(IEnumerable<(long Id, string Block, int? Network)> units)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<long, string>();
        foreach (var (id, block, network) in units)
        {
            var baseName = Of(block, network);
            var name = baseName;
            for (var i = 2; !used.Add(name); i++) name = $"{baseName}_{i}";
            names[id] = name;
        }
        return names;
    }
}
