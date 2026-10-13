using System;
using System.Collections.Generic;
using System.Text;

namespace HSJM.AI.IDbService
{
    /// <summary>
    /// IGW 注册设备类型：S7（经典地址）/ S7Plus（符号式，逻辑设备 S1500_{ip}）/ Graph（GRAPH 工步容器，逻辑设备 S7Plus_{ip}）。
    /// 三者对应 §全局约束「设备逻辑名」规则，见 <see cref="IgwTagNaming.LogicalDeviceName"/>。
    /// </summary>
    public enum IgwDeviceKind
    {
        S7,
        S7Plus,
        Graph,
    }

    /// <summary>
    /// IGW 点位命名 / 读别名的纯函数集合（无副作用，注册与读链共用）。
    /// 规则来源：sub-project B task-1-brief（约束值/测试用例）+ global-constraints（契约细节）。
    /// </summary>
    public static class IgwTagNaming
    {
        /// <summary>IGW 契约：组名/点名/单段最长 128 字符</summary>
        public const int MaxNameLength = 128;

        /// <summary>IGW 契约：标记组嵌套深度上限</summary>
        public const int MaxGroupDepth = 16;

        /// <summary>IO 表点位读别名固定根组名</summary>
        public const string IoRootGroup = "IO";

        /// <summary>GRAPH 工步未挂工位分组时的占位分组名</summary>
        public const string UngroupedStation = "_未分组";

        /// <summary>
        /// 内部拼接键分隔符（不可见控制字符 U+0001，合法组名/点名已排除控制字符，不会与其冲突）。
        /// 两处共用同一个值，避免各自定义不一致（AppLayer 依赖本项目，可直接引用本常量；反向不行）：
        /// - <c>HSJM.AI.AppLayer.PLC.PLC_Upload.Igw.IgwRegistrationPlanner</c> 内部拼接"组路径+点名"去重键、replaceGroups 键；
        /// - 台账 RenamesJson（<see cref="PLC_IgwRegistrationEntity.RenamesJson"/>）RootKind=Io 行，
        ///   拼接 (表名, 点名) 复合键，见 <see cref="IgwLedgerSnapshotBuilder"/>。
        /// </summary>
        public const char KeySeparator = '';

        /// <summary>S7（经典地址）驱动名 / 逻辑设备名前缀</summary>
        public const string S7DeviceName = "S7";

        /// <summary>S7Plus（符号式）驱动名（注意：逻辑设备名前缀是 S1500，不是本常量，见 <see cref="LogicalDeviceName"/>）</summary>
        public const string S7PlusDeviceName = "S7Plus";

        /// <summary>GRAPH 工步容器驱动名（注意：逻辑设备名前缀是 S7Plus，不是本常量，见 <see cref="LogicalDeviceName"/>）</summary>
        public const string GraphDeviceName = "GRAPH";

        /// <summary>读别名固定前缀</summary>
        private const string ReadAliasPrefix = "通用";

        /// <summary>
        /// 逻辑设备名：S7 → S7_{ip}；S7Plus → S1500_{ip}；Graph → S7Plus_{ip}
        /// （S7Plus 沿用 IGW 内部 S1500 命名；GRAPH 独立逻辑名 S7Plus_{ip}，避免与 S7Plus 设备前缀撞名，见 DcsIgwConsts.GraphPlusDeviceLogicalName）。
        /// </summary>
        public static string LogicalDeviceName(IgwDeviceKind kind, string ip)
        {
            var trimmedIp = (ip ?? string.Empty).Trim();
            var prefix = kind switch
            {
                IgwDeviceKind.S7 => "S7",
                IgwDeviceKind.S7Plus => "S1500",
                IgwDeviceKind.Graph => "S7Plus",
                _ => "S7",
            };
            return $"{prefix}_{trimmedIp}";
        }

        /// <summary>
        /// 通道名：产线名与 PLC 编码都非空 → {line}_{plcCode}；否则退回 projectName；结果再经 <see cref="SanitizeName"/>。
        /// 三者皆空/清洗后为空 → 返回 null（无可用名）。
        /// </summary>
        public static string ChannelName(string productionLineName, string plcCode, string projectName)
        {
            var line = (productionLineName ?? string.Empty).Trim();
            var code = (plcCode ?? string.Empty).Trim();
            string raw;
            if (line.Length > 0 && code.Length > 0)
            {
                raw = $"{line}_{code}";
            }
            else
            {
                raw = projectName;
            }
            return SanitizeName(raw);
        }

        /// <summary>
        /// 清洗单段名：去控制字符 → `.` 替换为 `_` → Trim → 截断到 <see cref="MaxNameLength"/>；
        /// 清洗结果为空返回 null（null/空白输入同样返回 null）。
        /// </summary>
        public static string SanitizeName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var builder = new StringBuilder(raw.Length);
            foreach (var ch in raw)
            {
                if (char.IsControl(ch))
                {
                    continue;
                }
                builder.Append(ch == '.' ? '_' : ch);
            }

            var trimmed = builder.ToString().Trim();
            if (trimmed.Length == 0)
            {
                return null;
            }

            return trimmed.Length > MaxNameLength ? trimmed.Substring(0, MaxNameLength) : trimmed;
        }

        /// <summary>
        /// 归一化解析器产出的变量路径：按 `.` 切段（`[]` 内的 `.` 不算分隔符——本规则未用到但保持扫描一致性），
        /// 若段 i 为纯名 `X` 且段 i+1 以 `X[` 开头，则丢弃段 i（解析器重复输出的数组容器名占位段）。
        /// 例：`Sensor.Sensor[3]` → `Sensor[3]`；`Status.Robot.Robot[2].X` → `Status.Robot[2].X`。
        /// </summary>
        public static string NormalizeVariablePath(string variablePath)
        {
            if (string.IsNullOrEmpty(variablePath))
            {
                return variablePath;
            }

            var rawSegments = ScanDotSegments(variablePath);
            var kept = new List<string>(rawSegments.Count);
            for (var i = 0; i < rawSegments.Count; i++)
            {
                var current = rawSegments[i];
                if (i + 1 < rawSegments.Count)
                {
                    var next = rawSegments[i + 1];
                    if (next.StartsWith(current + "[", StringComparison.Ordinal))
                    {
                        // 段 i 是段 i+1 的数组容器占位名，丢弃段 i
                        continue;
                    }
                }
                kept.Add(current);
            }

            return string.Join(".", kept);
        }

        /// <summary>
        /// 对归一化后的路径切段（不含块名），并对每段调 <see cref="SanitizeName"/>（数组下标 `[3]` 随段一起保留）。
        /// </summary>
        public static IReadOnlyList<string> SplitSegments(string normalizedPath)
        {
            var segments = ScanDotSegments(normalizedPath ?? string.Empty);
            var result = new List<string>(segments.Count);
            foreach (var segment in segments)
            {
                var sanitized = SanitizeName(segment);
                if (sanitized != null)
                {
                    result.Add(sanitized);
                }
            }
            return result;
        }

        /// <summary>
        /// 手写扫描器：按 `.` 切分，`[` 到 `]` 之间的内容（含方括号）视为当前段的一部分，不作为分隔符处理。
        /// </summary>
        private static List<string> ScanDotSegments(string path)
        {
            var segments = new List<string>();
            if (string.IsNullOrEmpty(path))
            {
                return segments;
            }

            var current = new StringBuilder();
            var inBracket = false;
            foreach (var ch in path)
            {
                if (ch == '[')
                {
                    inBracket = true;
                    current.Append(ch);
                }
                else if (ch == ']')
                {
                    inBracket = false;
                    current.Append(ch);
                }
                else if (ch == '.' && !inBracket)
                {
                    segments.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(ch);
                }
            }
            segments.Add(current.ToString());
            return segments;
        }

        /// <summary>
        /// DB 变量读别名：通用.{LogicalDeviceName}.{块名}.{归一化后各段}。
        /// </summary>
        public static string DbReadAlias(string ip, IgwDeviceKind kind, string blockName, string variablePath)
        {
            var segments = new List<string>();
            var sanitizedBlock = SanitizeName(blockName);
            if (sanitizedBlock != null)
            {
                segments.Add(sanitizedBlock);
            }
            segments.AddRange(SplitSegments(NormalizeVariablePath(variablePath)));

            return BuildAlias(LogicalDeviceName(kind, ip), segments);
        }

        /// <summary>
        /// IO 变量表点位读别名：通用.{S7 逻辑设备名}.IO.{变量表名}.{点位名}（均经 SanitizeName）。
        /// 重名后缀规则见 Task 2，此处不处理。
        /// </summary>
        public static string IoReadAlias(string ip, string tableName, string tagName)
        {
            var segments = new List<string> { IoRootGroup };
            var sanitizedTable = SanitizeName(tableName);
            if (sanitizedTable != null)
            {
                segments.Add(sanitizedTable);
            }
            var sanitizedTag = SanitizeName(tagName);
            if (sanitizedTag != null)
            {
                segments.Add(sanitizedTag);
            }

            return BuildAlias(LogicalDeviceName(IgwDeviceKind.S7, ip), segments);
        }

        /// <summary>
        /// GRAPH 工步容器读别名：通用.{Graph 逻辑设备名}.{工位（空则 _未分组）}.{DB 名}.{member 按 . 切分}。
        /// member 形如 "RT_DATA.EXEC_BITS" 或 "S_NO"。
        /// </summary>
        public static string GraphReadAlias(string ip, string stationCode, string dbName, string member)
        {
            var segments = new List<string>();

            var sanitizedStation = SanitizeName(stationCode);
            segments.Add(sanitizedStation ?? UngroupedStation);

            var sanitizedDb = SanitizeName(dbName);
            if (sanitizedDb != null)
            {
                segments.Add(sanitizedDb);
            }

            foreach (var part in ScanDotSegments(member ?? string.Empty))
            {
                var sanitizedPart = SanitizeName(part);
                if (sanitizedPart != null)
                {
                    segments.Add(sanitizedPart);
                }
            }

            return BuildAlias(LogicalDeviceName(IgwDeviceKind.Graph, ip), segments);
        }

        /// <summary>
        /// 由注册项（组路径 + 点名，均为规划器已清洗/去重后的最终名）拼读别名：通用.{逻辑设备名}.{组1}...{组n}.{点名}。
        /// 不再清洗——调用方传入的即 IGW 中的实际组名/点名（读链 <see cref="IgwReadAliasResolver"/> 与注册侧共用本函数，保证逐字一致）。
        /// </summary>
        public static string ReadAlias(IgwDeviceKind kind, string ip, IEnumerable<string> groupPath, string name)
        {
            var segments = new List<string>();
            if (groupPath != null)
            {
                segments.AddRange(groupPath);
            }
            if (!string.IsNullOrEmpty(name))
            {
                segments.Add(name);
            }
            return BuildAlias(LogicalDeviceName(kind, ip), segments);
        }

        /// <summary>拼装 "通用.{logicalDevice}.{段1}.{段2}..." 形式的读别名</summary>
        private static string BuildAlias(string logicalDeviceName, IReadOnlyList<string> segments)
        {
            if (segments.Count == 0)
            {
                return $"{ReadAliasPrefix}.{logicalDeviceName}";
            }
            return $"{ReadAliasPrefix}.{logicalDeviceName}." + string.Join(".", segments);
        }
    }
}
