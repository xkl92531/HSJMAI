using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.PLC.PLC_Upload
{
    /// <summary>
    /// S7 DB 变量偏移量计算器
    /// 
    /// 功能：
    /// 1. 遍历 DB 块的 BlockInterface 层级结构
    /// 2. 解析 UDT 引用（从数据库中查找 UDT 定义并展开）
    /// 3. 按照 S7 内存对齐规则计算每个变量的绝对偏移量 (Byte.Bit)
    /// 
    /// S7 对齐规则：
    /// - Bool: 1 bit, bit-aligned
    /// - Byte/SInt/USInt/Char: 1 byte, byte-aligned  
    /// - Int/UInt/Word/Date/S5Time: 2 bytes, word-aligned (偶地址)
    /// - DInt/UDInt/DWord/Real/Time/TOD: 4 bytes, word-aligned
    /// - LInt/ULInt/LWord/LReal/LTime/DT: 8 bytes, word-aligned
    /// - DTL: 12 bytes, word-aligned
    /// - String[n]: n+2 bytes, word-aligned
    /// - WString[n]: 2n+4 bytes, word-aligned
    /// - Struct/UDT: word-aligned start, word-aligned end
    /// - Array: element alignment × count
    /// </summary>
    public class S7OffsetCalculator : ISelfScopedAutoInject
    {
        private readonly ILogger<S7OffsetCalculator> _logger;
        private readonly IPLC_BlockService _blockService;
        private readonly IPLC_BlockInterfaceService _interfaceService;

        // S7 基础类型 -> (size_in_bits, requires_word_alignment)
        private static readonly Dictionary<string, (int SizeBits, bool WordAlign)> TypeMap =
            new(StringComparer.OrdinalIgnoreCase)
        {
            // 1 bit
            ["bool"] = (1, false),
            // 1 byte
            ["byte"] = (8, false),
            ["char"] = (8, false),
            ["sint"] = (8, false),
            ["usint"] = (8, false),
            // 2 bytes
            ["int"] = (16, true),
            ["uint"] = (16, true),
            ["word"] = (16, true),
            ["date"] = (16, true),
            ["s5time"] = (16, true),
            // 4 bytes
            ["dint"] = (32, true),
            ["udint"] = (32, true),
            ["dword"] = (32, true),
            ["real"] = (32, true),
            ["time"] = (32, true),
            ["time_of_day"] = (32, true),
            ["tod"] = (32, true),
            // 8 bytes
            ["lint"] = (64, true),
            ["ulint"] = (64, true),
            ["lword"] = (64, true),
            ["lreal"] = (64, true),
            ["ltime"] = (64, true),
            ["date_and_time"] = (64, true),
            ["dt"] = (64, true),
            // 12 bytes
            ["dtl"] = (96, true),
        };

        // 系统 FB 类型（定时器/计数器）的内部结构
        private static readonly Dictionary<string, List<(string Name, string Type)>> SystemTypes =
            new(StringComparer.OrdinalIgnoreCase)
        {
            ["ton_time"] = new() { ("IN", "Bool"), ("PT", "Time"), ("Q", "Bool"), ("ET", "Time") },
            ["tof_time"] = new() { ("IN", "Bool"), ("PT", "Time"), ("Q", "Bool"), ("ET", "Time") },
            ["tp_time"] = new() { ("IN", "Bool"), ("PT", "Time"), ("Q", "Bool"), ("ET", "Time") },
            ["iec_timer"] = new() { ("IN", "Bool"), ("PT", "Time"), ("Q", "Bool"), ("ET", "Time") },
            ["r_trig"] = new() { ("CLK", "Bool"), ("Q", "Bool") },
            ["f_trig"] = new() { ("CLK", "Bool"), ("Q", "Bool") },
            ["ctu"] = new() { ("CU", "Bool"), ("R", "Bool"), ("PV", "Int"), ("Q", "Bool"), ("CV", "Int") },
            ["ctd"] = new() { ("CD", "Bool"), ("LD", "Bool"), ("PV", "Int"), ("Q", "Bool"), ("CV", "Int") },
        };

        public S7OffsetCalculator(
            ILogger<S7OffsetCalculator> logger,
            IPLC_BlockService blockService,
            IPLC_BlockInterfaceService interfaceService)
        {
            _logger = logger;
            _blockService = blockService;
            _interfaceService = interfaceService;
        }

        /// <summary>
        /// 为项目中所有 DB 块的 BlockInterface 计算偏移量
        /// </summary>
        public async Task CalculateOffsetsForProjectAsync(long projectId)
        {
            // 1. 获取所有 DB 块（GlobalDB, InstanceDB, DB）
            var dbBlocks = await _blockService.GetListAsync(x =>
                x.ProjectId == projectId &&
                !x.IsDeleted &&
                (x.BlockType == "GlobalDB" || x.BlockType == "InstanceDB" || x.BlockType == "DB"));

            if (dbBlocks.Count == 0)
            {
                _logger.LogInformation("[S7Offset] 项目 {ProjectId} 无 DB 块，跳过偏移计算", projectId);
                return;
            }

            // 2. 预加载所有 UDT 定义（BlockType = "UDT"），建立 Name -> Members 缓存
            var udtBlocks = await _blockService.GetListAsync(x =>
                x.ProjectId == projectId &&
                !x.IsDeleted &&
                x.BlockType == "UDT");

            var udtCache = new Dictionary<string, List<PLC_BlockInterfaceEntity>>(StringComparer.OrdinalIgnoreCase);
            foreach (var udt in udtBlocks)
            {
                var members = await _interfaceService.GetListAsync(x =>
                    x.BlockId == udt.Id && !x.IsDeleted);
                udtCache[udt.BlockName] = members.OrderBy(m => m.NestingLevel).ThenBy(m => m.Id).ToList();
            }

            _logger.LogInformation("[S7Offset] 项目 {ProjectId}: {DbCount} 个 DB，{UdtCount} 个 UDT 定义",
                projectId, dbBlocks.Count, udtCache.Count);

            // 3. 对每个 DB 计算偏移
            var allUpdates = new List<PLC_BlockInterfaceEntity>();

            foreach (var db in dbBlocks)
            {
                try
                {
                    var interfaces = await _interfaceService.GetListAsync(x =>
                        x.BlockId == db.Id && !x.IsDeleted);

                    if (interfaces.Count == 0) continue;

                    // 检查是否已有 Python 解析器预计算的偏移量
                    // 如果该 DB 的 interface 记录已有 OffsetByte 值，说明 Python 已计算过，跳过
                    bool hasPreCalculatedOffsets = interfaces.Any(i => i.OffsetByte != null);
                    if (hasPreCalculatedOffsets)
                    {
                        _logger.LogInformation("[S7Offset] DB {DbName}: 已有预计算偏移量（来自Python解析器），跳过重复计算", db.BlockName);
                        continue;
                    }

                    // 构建树结构（按 ParentId 分组）
                    var topLevel = interfaces
                        .Where(i => i.ParentId == null || i.ParentId == 0)
                        .OrderBy(i => i.Id)
                        .ToList();

                    var childMap = interfaces
                        .Where(i => i.ParentId != null && i.ParentId != 0)
                        .GroupBy(i => i.ParentId!.Value)
                        .ToDictionary(g => g.Key, g => g.OrderBy(i => i.Id).ToList());

                    // 偏移计算
                    var state = new OffsetState();
                    CalculateOffsets(topLevel, childMap, udtCache, state);

                    // 收集需要更新的记录
                    allUpdates.AddRange(interfaces.Where(i =>
                        i.OffsetByte != null || i.SizeBits != null));

                    // 更新 DB 的总大小到 MetadataJson（可选）
                    _logger.LogInformation("[S7Offset] DB {DbName}: 计算完成，总大小 {Size} bytes",
                        db.BlockName, state.GetTotalSize());
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[S7Offset] DB {DbName} 偏移计算失败", db.BlockName);
                }
            }

            // 4. 批量更新
            if (allUpdates.Count > 0)
            {
                await _interfaceService.BulkUpdateAsync(allUpdates);
                _logger.LogInformation("[S7Offset] 已更新 {Count} 条偏移记录", allUpdates.Count);
            }
        }

        /// <summary>
        /// 递归计算偏移量
        /// </summary>
        private void CalculateOffsets(
            List<PLC_BlockInterfaceEntity> members,
            Dictionary<long, List<PLC_BlockInterfaceEntity>> childMap,
            Dictionary<string, List<PLC_BlockInterfaceEntity>> udtCache,
            OffsetState state)
        {
            foreach (var member in members)
            {
                var dataType = (member.DataType ?? "").Trim();
                var cleanType = dataType.Trim('"');

                // 1. 检查是否为数组
                var arrayMatch = Regex.Match(cleanType,
                    @"Array\s*\[(\d+)\.\.(\d+)\]\s+of\s+""?(.+?)""?\s*$", RegexOptions.IgnoreCase);
                if (arrayMatch.Success)
                {
                    int lower = int.Parse(arrayMatch.Groups[1].Value);
                    int upper = int.Parse(arrayMatch.Groups[2].Value);
                    string elementType = arrayMatch.Groups[3].Value.Trim('"');

                    state.FlushBits();
                    state.AlignToWord();

                    member.OffsetByte = state.CurrentByte;
                    member.OffsetBit = 0;
                    member.ReferencedTypeName = elementType;
                    int startByte = state.CurrentByte;

                    // 计算单个元素大小
                    int elementStartByte = state.CurrentByte;
                    ProcessSingleElement(elementType, childMap, udtCache, state,
                        member.Id, isArrayElement: true);
                    int elementSize = state.CurrentByte - elementStartByte;

                    // 剩余元素直接按大小推进（假设每个元素结构相同）
                    int count = upper - lower + 1;
                    if (count > 1)
                    {
                        state.CurrentByte += elementSize * (count - 1);
                    }

                    member.SizeBits = (state.CurrentByte - startByte) * 8;
                    member.ModifyTime = DateTime.Now;
                    continue;
                }

                // 2. 检查是否为 Struct
                if (cleanType.Equals("Struct", StringComparison.OrdinalIgnoreCase))
                {
                    state.FlushBits();
                    state.AlignToWord();

                    member.OffsetByte = state.CurrentByte;
                    member.OffsetBit = 0;
                    int startByte = state.CurrentByte;

                    // 递归子成员
                    if (childMap.TryGetValue(member.Id, out var children))
                    {
                        CalculateOffsets(children, childMap, udtCache, state);
                    }

                    state.FlushBits();
                    state.AlignToWord();

                    member.SizeBits = (state.CurrentByte - startByte) * 8;
                    member.ModifyTime = DateTime.Now;
                    continue;
                }

                // 3. 检查是否为系统类型（TON_TIME 等）
                string sysKey = cleanType.Replace(" ", "_");
                if (SystemTypes.TryGetValue(sysKey, out var sysMembers))
                {
                    state.FlushBits();
                    state.AlignToWord();

                    member.OffsetByte = state.CurrentByte;
                    member.OffsetBit = 0;
                    member.ReferencedTypeName = cleanType;
                    int startByte = state.CurrentByte;

                    // 如果有子成员（XML 内联展开），用子成员计算
                    if (childMap.TryGetValue(member.Id, out var children) && children.Count > 0)
                    {
                        CalculateOffsets(children, childMap, udtCache, state);
                    }
                    else
                    {
                        // 使用预定义结构
                        foreach (var (name, type) in sysMembers)
                        {
                            ProcessBasicType(type, state);
                        }
                    }

                    state.FlushBits();
                    state.AlignToWord();

                    member.SizeBits = (state.CurrentByte - startByte) * 8;
                    member.ModifyTime = DateTime.Now;
                    continue;
                }

                // 4. 检查是否为 UDT 引用
                if (udtCache.TryGetValue(cleanType, out var udtMembers))
                {
                    state.FlushBits();
                    state.AlignToWord();

                    member.OffsetByte = state.CurrentByte;
                    member.OffsetBit = 0;
                    member.ReferencedTypeName = cleanType;
                    int startByte = state.CurrentByte;

                    // 如果有子成员（XML 内联展开），优先使用
                    if (childMap.TryGetValue(member.Id, out var children) && children.Count > 0)
                    {
                        CalculateOffsets(children, childMap, udtCache, state);
                    }
                    else
                    {
                        // 使用 UDT 缓存中的模板结构来计算大小（不展开到当前 DB）
                        // 只推进偏移，不创建新记录
                        var udtTopLevel = udtMembers
                            .Where(m => m.ParentId == null || m.ParentId == 0)
                            .OrderBy(m => m.Id).ToList();
                        var udtChildMap = udtMembers
                            .Where(m => m.ParentId != null && m.ParentId != 0)
                            .GroupBy(m => m.ParentId!.Value)
                            .ToDictionary(g => g.Key, g => g.OrderBy(m => m.Id).ToList());

                        // 虚拟计算偏移（不修改 UDT 模板记录）
                        var tempState = new OffsetState();
                        CalculateOffsetsVirtual(udtTopLevel, udtChildMap, udtCache, tempState);
                        tempState.FlushBits();
                        tempState.AlignToWord();
                        state.CurrentByte += tempState.GetTotalSize();
                    }

                    state.FlushBits();
                    state.AlignToWord();

                    member.SizeBits = (state.CurrentByte - startByte) * 8;
                    member.ModifyTime = DateTime.Now;
                    continue;
                }

                // 5. 检查是否有内联子成员（未识别的复合类型）
                if (childMap.TryGetValue(member.Id, out var inlineChildren) && inlineChildren.Count > 0)
                {
                    state.FlushBits();
                    state.AlignToWord();

                    member.OffsetByte = state.CurrentByte;
                    member.OffsetBit = 0;
                    member.ReferencedTypeName = cleanType;
                    int startByte = state.CurrentByte;

                    CalculateOffsets(inlineChildren, childMap, udtCache, state);

                    state.FlushBits();
                    state.AlignToWord();

                    member.SizeBits = (state.CurrentByte - startByte) * 8;
                    member.ModifyTime = DateTime.Now;
                    continue;
                }

                // 6. 基础类型
                ProcessBasicTypeForMember(member, cleanType, state);
            }
        }

        /// <summary>
        /// 虚拟计算偏移（不修改实体，只推进 state）
        /// 用于 UDT 模板大小计算
        /// </summary>
        private void CalculateOffsetsVirtual(
            List<PLC_BlockInterfaceEntity> members,
            Dictionary<long, List<PLC_BlockInterfaceEntity>> childMap,
            Dictionary<string, List<PLC_BlockInterfaceEntity>> udtCache,
            OffsetState state)
        {
            foreach (var member in members)
            {
                var dataType = (member.DataType ?? "").Trim().Trim('"');

                // String
                var strMatch = Regex.Match(dataType, @"^String\s*\[(\d+)\]$", RegexOptions.IgnoreCase);
                if (strMatch.Success)
                {
                    int maxLen = int.Parse(strMatch.Groups[1].Value);
                    state.FlushBits();
                    state.AlignToWord();
                    state.CurrentByte += maxLen + 2;
                    continue;
                }

                // WString
                var wstrMatch = Regex.Match(dataType, @"^WString\s*\[(\d+)\]$", RegexOptions.IgnoreCase);
                if (wstrMatch.Success)
                {
                    int maxLen = int.Parse(wstrMatch.Groups[1].Value);
                    state.FlushBits();
                    state.AlignToWord();
                    state.CurrentByte += maxLen * 2 + 4;
                    continue;
                }

                // Array
                var arrayMatch = Regex.Match(dataType,
                    @"Array\s*\[(\d+)\.\.(\d+)\]\s+of\s+""?(.+?)""?\s*$", RegexOptions.IgnoreCase);
                if (arrayMatch.Success)
                {
                    int lower = int.Parse(arrayMatch.Groups[1].Value);
                    int upper = int.Parse(arrayMatch.Groups[2].Value);
                    string elementType = arrayMatch.Groups[3].Value.Trim('"');

                    state.FlushBits();
                    state.AlignToWord();

                    int elementStart = state.CurrentByte;
                    ProcessSingleElementVirtual(elementType, childMap, udtCache, state);
                    int elementSize = state.CurrentByte - elementStart;

                    int count = upper - lower + 1;
                    if (count > 1)
                        state.CurrentByte += elementSize * (count - 1);

                    continue;
                }

                // Struct
                if (dataType.Equals("Struct", StringComparison.OrdinalIgnoreCase))
                {
                    state.FlushBits();
                    state.AlignToWord();
                    if (childMap.TryGetValue(member.Id, out var ch))
                        CalculateOffsetsVirtual(ch, childMap, udtCache, state);
                    state.FlushBits();
                    state.AlignToWord();
                    continue;
                }

                // System type
                string sysKey = dataType.Replace(" ", "_");
                if (SystemTypes.TryGetValue(sysKey, out var sysM))
                {
                    state.FlushBits();
                    state.AlignToWord();
                    if (childMap.TryGetValue(member.Id, out var ch) && ch.Count > 0)
                        CalculateOffsetsVirtual(ch, childMap, udtCache, state);
                    else
                        foreach (var (_, t) in sysM) ProcessBasicType(t, state);
                    state.FlushBits();
                    state.AlignToWord();
                    continue;
                }

                // UDT reference
                if (udtCache.TryGetValue(dataType, out var udtM))
                {
                    state.FlushBits();
                    state.AlignToWord();
                    if (childMap.TryGetValue(member.Id, out var ch) && ch.Count > 0)
                    {
                        CalculateOffsetsVirtual(ch, childMap, udtCache, state);
                    }
                    else
                    {
                        var top = udtM.Where(m => m.ParentId == null || m.ParentId == 0)
                            .OrderBy(m => m.Id).ToList();
                        var cm = udtM.Where(m => m.ParentId != null && m.ParentId != 0)
                            .GroupBy(m => m.ParentId!.Value)
                            .ToDictionary(g => g.Key, g => g.OrderBy(m => m.Id).ToList());
                        CalculateOffsetsVirtual(top, cm, udtCache, state);
                    }
                    state.FlushBits();
                    state.AlignToWord();
                    continue;
                }

                // Inline children
                if (childMap.TryGetValue(member.Id, out var inCh) && inCh.Count > 0)
                {
                    state.FlushBits();
                    state.AlignToWord();
                    CalculateOffsetsVirtual(inCh, childMap, udtCache, state);
                    state.FlushBits();
                    state.AlignToWord();
                    continue;
                }

                // Basic
                ProcessBasicType(dataType, state);
            }
        }

        private void ProcessSingleElement(string elementType, 
            Dictionary<long, List<PLC_BlockInterfaceEntity>> childMap,
            Dictionary<string, List<PLC_BlockInterfaceEntity>> udtCache,
            OffsetState state, long parentId, bool isArrayElement)
        {
            // 对单个数组元素推进偏移
            ProcessSingleElementVirtual(elementType, childMap, udtCache, state);
        }

        private void ProcessSingleElementVirtual(string elementType,
            Dictionary<long, List<PLC_BlockInterfaceEntity>> childMap,
            Dictionary<string, List<PLC_BlockInterfaceEntity>> udtCache,
            OffsetState state)
        {
            string clean = elementType.Trim('"');

            // System type
            string sysKey = clean.Replace(" ", "_");
            if (SystemTypes.TryGetValue(sysKey, out var sysM))
            {
                state.FlushBits();
                state.AlignToWord();
                foreach (var (_, t) in sysM) ProcessBasicType(t, state);
                state.FlushBits();
                state.AlignToWord();
                return;
            }

            // UDT
            if (udtCache.TryGetValue(clean, out var udtM))
            {
                state.FlushBits();
                state.AlignToWord();
                var top = udtM.Where(m => m.ParentId == null || m.ParentId == 0)
                    .OrderBy(m => m.Id).ToList();
                var cm = udtM.Where(m => m.ParentId != null && m.ParentId != 0)
                    .GroupBy(m => m.ParentId!.Value)
                    .ToDictionary(g => g.Key, g => g.OrderBy(m => m.Id).ToList());
                CalculateOffsetsVirtual(top, cm, udtCache, state);
                state.FlushBits();
                state.AlignToWord();
                return;
            }

            // Basic
            ProcessBasicType(clean, state);
        }

        /// <summary>
        /// 处理基础类型 - 更新实体字段
        /// </summary>
        private void ProcessBasicTypeForMember(PLC_BlockInterfaceEntity member, string cleanType, OffsetState state)
        {
            // String[n]
            var strMatch = Regex.Match(cleanType, @"^String\s*\[(\d+)\]$", RegexOptions.IgnoreCase);
            if (strMatch.Success)
            {
                int maxLen = int.Parse(strMatch.Groups[1].Value);
                state.FlushBits();
                state.AlignToWord();
                member.OffsetByte = state.CurrentByte;
                member.OffsetBit = 0;
                int sizeBits = (maxLen + 2) * 8;
                member.SizeBits = sizeBits;
                state.CurrentByte += maxLen + 2;
                member.ModifyTime = DateTime.Now;
                return;
            }

            // WString[n]
            var wstrMatch = Regex.Match(cleanType, @"^WString\s*\[(\d+)\]$", RegexOptions.IgnoreCase);
            if (wstrMatch.Success)
            {
                int maxLen = int.Parse(wstrMatch.Groups[1].Value);
                state.FlushBits();
                state.AlignToWord();
                member.OffsetByte = state.CurrentByte;
                member.OffsetBit = 0;
                int sizeBits = (maxLen * 2 + 4) * 8;
                member.SizeBits = sizeBits;
                state.CurrentByte += maxLen * 2 + 4;
                member.ModifyTime = DateTime.Now;
                return;
            }

            if (TypeMap.TryGetValue(cleanType, out var info))
            {
                if (info.SizeBits == 1)
                {
                    // Bool - bit aligned
                    member.OffsetByte = state.CurrentByte;
                    member.OffsetBit = state.CurrentBit;
                    member.SizeBits = 1;
                    state.CurrentBit++;
                    if (state.CurrentBit >= 8)
                    {
                        state.CurrentByte++;
                        state.CurrentBit = 0;
                    }
                }
                else
                {
                    // 非 Bool
                    state.FlushBits();
                    if (info.WordAlign)
                        state.AlignToWord();

                    member.OffsetByte = state.CurrentByte;
                    member.OffsetBit = 0;
                    member.SizeBits = info.SizeBits;
                    state.CurrentByte += info.SizeBits / 8;
                }
                member.ModifyTime = DateTime.Now;
            }
            else
            {
                // 未知类型 - 按 2 字节处理
                state.FlushBits();
                state.AlignToWord();
                member.OffsetByte = state.CurrentByte;
                member.OffsetBit = 0;
                member.SizeBits = 16;
                state.CurrentByte += 2;
                member.ModifyTime = DateTime.Now;
                _logger.LogWarning("[S7Offset] 未知数据类型: {Type}，按 2 字节处理", cleanType);
            }
        }

        /// <summary>
        /// 处理基础类型 - 仅推进偏移（不修改实体）
        /// </summary>
        private void ProcessBasicType(string cleanType, OffsetState state)
        {
            // String
            var strMatch = Regex.Match(cleanType, @"^String\s*\[(\d+)\]$", RegexOptions.IgnoreCase);
            if (strMatch.Success)
            {
                state.FlushBits();
                state.AlignToWord();
                state.CurrentByte += int.Parse(strMatch.Groups[1].Value) + 2;
                return;
            }

            // WString
            var wstrMatch = Regex.Match(cleanType, @"^WString\s*\[(\d+)\]$", RegexOptions.IgnoreCase);
            if (wstrMatch.Success)
            {
                state.FlushBits();
                state.AlignToWord();
                state.CurrentByte += int.Parse(wstrMatch.Groups[1].Value) * 2 + 4;
                return;
            }

            if (TypeMap.TryGetValue(cleanType, out var info))
            {
                if (info.SizeBits == 1)
                {
                    state.CurrentBit++;
                    if (state.CurrentBit >= 8)
                    {
                        state.CurrentByte++;
                        state.CurrentBit = 0;
                    }
                }
                else
                {
                    state.FlushBits();
                    if (info.WordAlign)
                        state.AlignToWord();
                    state.CurrentByte += info.SizeBits / 8;
                }
            }
            else
            {
                // 未知 → 2 bytes
                state.FlushBits();
                state.AlignToWord();
                state.CurrentByte += 2;
            }
        }

        /// <summary>
        /// 偏移状态追踪器
        /// </summary>
        private class OffsetState
        {
            public int CurrentByte { get; set; }
            public int CurrentBit { get; set; }

            /// <summary>Flush 未满的 bit 到下一个完整字节</summary>
            public void FlushBits()
            {
                if (CurrentBit > 0)
                {
                    CurrentByte++;
                    CurrentBit = 0;
                }
            }

            /// <summary>对齐到偶数字节（Word 边界）</summary>
            public void AlignToWord()
            {
                if (CurrentByte % 2 != 0)
                    CurrentByte++;
            }

            /// <summary>获取总大小（向偶数对齐）</summary>
            public int GetTotalSize()
            {
                FlushBits();
                AlignToWord();
                return CurrentByte;
            }
        }
    }
}
