using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace HSJM.AI.AppLayer.PLC.PLC_Upload.Igw
{
    /// <summary>
    /// 西门子 PLC 类型 / 地址 → IGW 批量契约的映射（纯函数）。
    /// 输出的数据类型字符串是 IGW IgwDataType 枚举的 camelCase 名（与 IGW JsonStringEnumConverter(CamelCase) 一致）。
    /// </summary>
    public static class IgwTypeMapper
    {
        /// <summary>S7 String / WString 缺省容量</summary>
        public const int DefaultStringLength = 254;

        private static readonly Regex StringTypeRegex = new(
            @"^(?<kind>W?String)\s*(\[\s*(?<len>\d+)\s*\])?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex IoAddressRegex = new(
            @"^(I|Q|M|E|A)(X|B|W|D)?\d+(\.[0-7])?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>
        /// PLC 数据类型 → IGW dataType；不可映射（UDT/Struct/Array/多维数组元素/Array[*] 等）返回 null。
        /// 2026-09-21 起时间与长整型（Time/LTime/Date/TOD/LTOD/DTL/DT/LDT/S5Time/LInt/ULInt/LWord）已放开。
        /// String/WString 通过 <paramref name="stringLength"/> 带出容量（缺省 254）。
        /// </summary>
        public static string MapDataType(string plcType, out int? stringLength)
        {
            stringLength = null;
            if (string.IsNullOrWhiteSpace(plcType))
            {
                return null;
            }

            var type = plcType.Trim();
            switch (type.ToLowerInvariant())
            {
                case "bool": return "bool";
                case "byte":
                case "usint": return "byte";
                case "sint": return "sInt";
                case "int": return "int16";
                case "uint":
                case "word": return "uInt16";
                case "dint": return "int32";
                case "udint":
                case "dword": return "uInt32";
                case "real": return "float";
                case "lreal": return "double";
                case "char": return "s7Char";
                // 时间 / 长整型（2026-09-21 放开）：IGW 全部支持，读出来是可读文本
                // （time → T#3723000ms、dtl → 2026-09-20 10:11:12.123456789），写回自动还原物理表示。
                // 放开前这些一律按 unmapped_type 跳过：现场 PLC1 导出里共 6,399 个成员读不到，
                // 其中 Time 6,359 个是 G7_StepPlus_V6 步骤结构的 T/U/T_MAX/T_WARN，
                // 正是判断「哪一步卡住、超没超时」要看的数据。
                case "time": return "time";
                case "ltime": return "ltime";
                case "date": return "date";
                case "tod":
                case "time_of_day": return "tod";
                case "ltod":
                case "ltime_of_day": return "ltod";
                case "dtl": return "dtl";
                case "dt":
                case "date_and_time": return "dt";
                case "ldt": return "ldt";
                case "s5time": return "s5Time";
                case "lint": return "int64";
                case "ulint": return "uInt64";
                case "lword": return "lWord";
            }

            var match = StringTypeRegex.Match(type);
            if (match.Success)
            {
                var length = DefaultStringLength;
                if (match.Groups["len"].Success
                    && !int.TryParse(match.Groups["len"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out length))
                {
                    return null;
                }
                stringLength = length;
                return match.Groups["kind"].Value.StartsWith("W", StringComparison.OrdinalIgnoreCase) ? "wString" : "string";
            }

            return null;
        }

        /// <summary>
        /// 经典 S7 绝对地址（DB{n}.DBX/DBB/DBW/DBD）；类型不支持绝对寻址或偏移非法 → null。
        /// Bool 缺位偏移时按 0 处理（偏移来自 Python 解析器，有字节偏移的 Bool 行均带位偏移）。
        /// </summary>
        public static string ClassicAddress(int blockNumber, string plcType, decimal offsetByte, decimal? offsetBit)
        {
            if (blockNumber <= 0 || string.IsNullOrWhiteSpace(plcType)
                || offsetByte < 0 || offsetByte != decimal.Truncate(offsetByte))
            {
                return null;
            }

            var b = ((long)offsetByte).ToString(CultureInfo.InvariantCulture);
            switch (plcType.Trim().ToLowerInvariant())
            {
                case "bool":
                    var bit = offsetBit ?? 0m;
                    if (bit < 0 || bit > 7 || bit != decimal.Truncate(bit))
                    {
                        return null;
                    }
                    return $"DB{blockNumber}.DBX{b}.{(int)bit}";
                case "byte":
                case "usint":
                case "char":
                    return $"DB{blockNumber}.DBB{b}";
                case "int":
                case "uint":
                case "word":
                // 2 字节时间类型：保留经典寻址，含它们的块不会因此被判去 S7Plus
                case "date":
                case "s5time":
                    return $"DB{blockNumber}.DBW{b}";
                case "dint":
                case "udint":
                case "dword":
                case "real":
                // 4 字节时间类型：同上
                case "time":
                case "tod":
                case "time_of_day":
                    return $"DB{blockNumber}.DBD{b}";
                // 注意：LTime/LTOD/DTL/DT/LDT/LInt/ULInt/LWord 是 8/12 字节，DBX/DBB/DBW/DBD 都放不下，
                // 没有经典形式 → 落到 default 返回 null，含它们的块按「整块二选一」归 S7Plus（现场 5 个块）。
                default:
                    return null;
            }
        }

        /// <summary>IO 地址："%I4057.4" → "I4057.4"；不符合 I/Q/M/E/A 区地址格式 → null</summary>
        public static string IoAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                return null;
            }

            var raw = address.Trim().TrimStart('%');
            return IoAddressRegex.IsMatch(raw) ? raw.ToUpperInvariant() : null;
        }

        /// <summary>
        /// IO 变量表数据类型：去掉尾部连续 `"` 后映射；只接受 Bool/Byte/Word/Int/DWord/DInt/Real，其它（PD_TEL* 等）→ null。
        /// </summary>
        public static string IoDataType(string plcType)
        {
            if (string.IsNullOrWhiteSpace(plcType))
            {
                return null;
            }

            var type = plcType.Trim().TrimEnd('"').Trim();
            switch (type.ToLowerInvariant())
            {
                case "bool":
                case "byte":
                case "word":
                case "int":
                case "dword":
                case "dint":
                case "real":
                    return MapDataType(type, out _);
                default:
                    return null;
            }
        }
    }
}
