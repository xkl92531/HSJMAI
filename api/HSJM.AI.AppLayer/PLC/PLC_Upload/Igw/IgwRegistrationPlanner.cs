using System;
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.Core;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.PLC.PLC_Upload.Igw
{
    /// <summary>DB 块接口行（BlockInterface 叶子变量；OffsetByte/OffsetBit 来自 Python 解析器，仅 Standard 且偏移可信的块才有）</summary>
    public sealed record PlannerDbRow(string BlockName, int BlockNumber, string VariablePath, string DataType,
        decimal? OffsetByte, decimal? OffsetBit, string Comment);

    /// <summary>IO 变量表点位</summary>
    public sealed record PlannerIoTag(string TableName, string TagName, string Address, string DataType, string Comment);

    /// <summary>
    /// 规划输入。Full=true 全量（DbRows 中全部块 ∪ DeletedBlocks + IO 全部替换）；
    /// 否则增量：只为 ChangedBlocks 生成点，ChangedBlocks ∪ DeletedBlocks 在两台设备上替换，IoRefreshed 时替换 IO。
    /// <para>
    /// ⚠️ 调用方必须以稳定顺序传入 DbRows / IoTags / ChangedBlocks / DeletedBlocks（如按 Id 排序）：
    /// 同组重名后缀（_2/_3）与块根组改名（{root}_2）都按输入顺序确定，顺序变化会导致点名/读别名漂移。
    /// 增量模式下 DbRows 可以只含变更块的行，但必须用 KnownBlocks 传入项目全部存活 DB 块名（按块 Id 等稳定顺序），
    /// 块根组分配才能看到全部块、与全量一致（未变更块沿用台账根组，新块不会抢占其根组）。
    /// </para>
    /// <para>
    /// PreviousBlockRoots：台账记录的上次根组（块名 → 根组名，来自 IgwLedgerSnapshot.RootGroupByBlock）。
    /// 块的上次根组只要合法且不与其它块本次已保留的根组冲突就原样沿用，优先于「先到先得」改名——
    /// 存活块的根组（即物理路径与读别名）不会因其它块增删或输入顺序变化而漂移。
    /// </para>
    /// </summary>
    public sealed record PlannerInput(string ChannelName, string PlcIp, int PlcPort, IReadOnlyList<PlannerDbRow> DbRows,
        IReadOnlyList<PlannerIoTag> IoTags, IReadOnlyCollection<string> ChangedBlocks, IReadOnlyCollection<string> DeletedBlocks,
        bool IoRefreshed, bool Full, IgwRegistrationConfig Config,
        IReadOnlyDictionary<string, string> PreviousBlockRoots = null,
        IReadOnlyCollection<string> KnownBlocks = null,
        IReadOnlyCollection<string> FastScanBlocks = null,
        bool AppendOnly = false,
        // 只追加模式下额外要清除旧组的根（台账 PendingClear / FailedPendingClear 欠着的义务）。
        // 台账不记是哪台设备待清（两台都可能），所以这些根在**两台设备上都发**清除组。
        // 这些块本轮若又变更，义务不能跟着整块重做一起被丢掉——重做已经不带 replaceGroups 了。
        IReadOnlyCollection<string> PendingClearRoots = null,
        IReadOnlyDictionary<string, PlannerRootState> RootStates = null);

    /// <summary>一个待发送的批量请求（一台设备）；BlockNames = 本请求中带点或带 replaceGroups 的块</summary>
    public sealed record PlannedRequest(IgwDeviceKind Device, IgwBulkRequest Request, IReadOnlyList<string> BlockNames);

    /// <summary>块级规划结果：落在哪台设备、生成多少点、跳过多少行；SkipReasons = 本块跳过原因计数（台账 SkipSummary 用）</summary>
    /// <summary>
    /// 台账里记着的某个根上次注册成什么样（2026-09-22 只追加注册）。
    /// Fingerprint 为 null/空 = 没记过 ⇒ **必须重发**；当成"一致"的话整个功能从第一天起就是死的。
    /// </summary>
    public sealed record PlannerRootState(IgwDeviceKind Device, string Fingerprint);

    public sealed record PlannedBlock(string BlockName, IgwDeviceKind Device, int TagCount, int Skipped)
    {
        public IReadOnlyDictionary<string, int> SkipReasons { get; init; } = new Dictionary<string, int>();

        /// <summary>本轮算出的根组内容指纹（去重改名之后）。runner 成功后写回台账，下轮据此判跳过。</summary>
        public string Fingerprint { get; init; }

        /// <summary>指纹与台账一致且设备没变 ⇒ 本轮整根跳过，一个点都没发。台账那行原样保留。</summary>
        public bool SkippedUnchanged { get; init; }
    }

    /// <summary>
    /// 规划结果。SkipReasons 键：unmapped_type / too_deep / empty_name / invalid_address / io_address / io_type / io_name /
    /// renamed_duplicate（仅计数，不跳过）。
    /// IoRenames：同组重名被追加后缀的 IO 点 (原表名, 原点名) → 最终点名（读别名需按此映射）。
    /// DbRenames：同组重名被追加后缀的 DB 点 (原块名, 原变量路径) → 最终点名。
    /// BlockRootRenames：根组名不等于清洗后块名的块 (原块名 → 根组名)：与 IO 根组冲突 → IO_DB；与先出现的块清洗后同名 → {root}_2…。
    /// </summary>
    public sealed record PlanResult(IReadOnlyList<PlannedRequest> Requests, IReadOnlyList<PlannedBlock> Blocks, int IoTagCount,
        IReadOnlyDictionary<string, int> SkipReasons, IReadOnlyDictionary<(string Table, string Tag), string> IoRenames,
        IReadOnlyDictionary<(string Block, string VariablePath), string> DbRenames,
        IReadOnlyDictionary<string, string> BlockRootRenames)
    {
        /// <summary>本次规划涉及的全部块（含只清除的块）→ 实际根组名（大小写不敏感）。R-1：注册任务按根组单元回写台账</summary>
        public IReadOnlyDictionary<string, string> BlockRoots { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// IGW 自动注册规划器（纯函数）：把 PLC 解析结果转换为按设备切分的 IGW 批量注册请求。
    /// 组名/点名与 <see cref="IgwTagNaming.DbReadAlias"/> / <see cref="IgwTagNaming.IoReadAlias"/> 使用同一清洗规则，保证物理路径与读别名一致；
    /// 规划中的改名（重名后缀、块根组改名）通过 <see cref="PlanResult"/> 的映射字典暴露给读链。
    /// </summary>
    public static class IgwRegistrationPlanner
    {
        public const string ReasonUnmappedType = "unmapped_type";
        public const string ReasonTooDeep = "too_deep";
        public const string ReasonEmptyName = "empty_name";
        public const string ReasonInvalidAddress = "invalid_address";
        public const string ReasonIoAddress = "io_address";
        public const string ReasonIoType = "io_type";
        public const string ReasonIoName = "io_name";
        public const string ReasonRenamedDuplicate = "renamed_duplicate";

        /// <summary>IGW 单请求点数硬上限</summary>
        private const int IgwMaxItems = 200_000;

        /// <summary>IGW 单请求 replaceGroups 条数硬上限</summary>
        public const int MaxReplaceGroupsPerRequest = 10_000;

        /// <summary>请求信封（channel/device/固定字段）的估算字节数，按字节预算切分时先计入</summary>
        private const int RequestEnvelopeBytes = 1024;

        /// <summary>
        /// 估算一个字符串序列化进 JSON 后的字节数。
        /// 注意：IgwBulkJson.Options 未自定义 Encoder，走 System.Text.Json 默认转义——非 ASCII 会被转义成
        /// 6 字节的 \uXXXX 形式，不是 UTF-8 的 3 字节；按 UTF-8 估会严重低估中文描述，导致请求超预算。
        /// </summary>
        private static int EstimateStringBytes(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return 0;
            }
            var bytes = 0;
            foreach (var c in value)
            {
                // 34=双引号，92=反斜杠，连同控制字符一样会被转义成 6 字节
                bytes += c < 128 ? (c < 32 || c == 34 || c == 92 ? 6 : 1) : 6;
            }
            return bytes;
        }

        /// <summary>
        /// 单个点位序列化后的估算字节数（2026-09-20）。宁可高估：低估会让请求超出服务端上限被掐连接，
        /// 高估只是多切一个请求。
        /// </summary>
        private static int EstimateItemBytes(IgwBulkItem item)
        {
            // {"groupPath":[],"name":"","address":"","dataType":"","access":"readWrite","scanRateMs":1000,"description":""}
            var bytes = 128;
            if (item.GroupPath != null)
            {
                foreach (var seg in item.GroupPath)
                {
                    bytes += EstimateStringBytes(seg) + 4; // 引号 + 逗号
                }
            }
            bytes += EstimateStringBytes(item.Name);
            bytes += EstimateStringBytes(item.Address);
            bytes += EstimateStringBytes(item.DataType);
            bytes += EstimateStringBytes(item.Description);
            if (item.Array != null)
            {
                bytes += 96; // elementType + dimensions + lowerBounds
            }
            return bytes;
        }

        /// <summary>replaceGroups 里一条根组路径的估算字节数</summary>
        private static int EstimateGroupBytes(IReadOnlyList<string> group)
        {
            var bytes = 8;
            foreach (var seg in group)
            {
                bytes += EstimateStringBytes(seg) + 4;
            }
            return bytes;
        }

        /// <summary>IGW 点位地址最大字符数（超出整单被拒）</summary>
        public const int MaxAddressLength = 512;

        /// <summary>块名清洗后与 IO 根组冲突时改用的根组名</summary>
        public const string IoClashBlockRoot = "IO_DB";

        /// <summary>入库 description 截断长度</summary>
        private const int MaxDescriptionLength = 480;

        /// <summary>组路径 + 点名拼接键的分隔符（与 IgwLedgerSnapshotBuilder 共用同一常量，见 IgwTagNaming.KeySeparator）</summary>
        private const char KeySeparator = IgwTagNaming.KeySeparator;

        /// <summary>规划中的一个切分单元：一个块在某设备上的全部点，或 IO 全部点</summary>
        private sealed class Unit
        {
            public string BlockName;            // IO 单元为 null
            public List<string> ReplaceGroup;   // 本单元携带的 replaceGroups 项（仅首片）
            public List<IgwBulkItem> Items = new();
            /// <summary>与 Items 平行：IO 为 (表名, 点名)，DB 为 (块名, 变量路径)，用于记录重名映射</summary>
            public List<(string A, string B)> Origins = new();
        }

        /// <summary>构建中的请求</summary>
        private sealed class Draft
        {
            public IgwBulkRequest Request;
            /// <summary>已累计的估算字节数（含通道/设备头与 replaceGroups），用于字节预算切分</summary>
            public int EstimatedBytes = RequestEnvelopeBytes;
            public List<string> BlockNames = new();
            private readonly HashSet<string> _blockNameSet = new(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _replaceKeys = new(StringComparer.OrdinalIgnoreCase);

            public void AddBlockName(string blockName)
            {
                if (blockName != null && _blockNameSet.Add(blockName))
                {
                    BlockNames.Add(blockName);
                }
            }

            public void AddReplace(List<string> group)
            {
                if (_replaceKeys.Add(string.Join(KeySeparator, group)))
                {
                    Request.ReplaceGroups.Add(new List<string>(group));
                    EstimatedBytes += EstimateGroupBytes(group);
                }
            }
        }

        public static PlanResult Plan(PlannerInput input)
        {
            ArgumentNullException.ThrowIfNull(input);
            var config = input.Config ?? new IgwRegistrationConfig();
            var maxItems = config.MaxItemsPerRequest > 0 ? Math.Min(config.MaxItemsPerRequest, IgwMaxItems) : IgwMaxItems;
            var maxBytes = config.MaxRequestBytes > 0 ? config.MaxRequestBytes : new IgwRegistrationConfig().MaxRequestBytes;
            var skipReasons = new Dictionary<string, int>();
            var ioRenames = new Dictionary<(string Table, string Tag), string>();
            var dbRenames = new Dictionary<(string Block, string VariablePath), string>();
            var changedBlocks = input.ChangedBlocks ?? Array.Empty<string>();
            var deletedBlocks = input.DeletedBlocks ?? Array.Empty<string>();
            var units = new Dictionary<IgwDeviceKind, List<Unit>>
            {
                [IgwDeviceKind.S7] = new List<Unit>(),
                [IgwDeviceKind.S7Plus] = new List<Unit>(),
            };

            // ---------- DB 行按块分组（保持输入顺序） ----------
            var rowsByBlock = new List<(string Block, List<PlannerDbRow> Rows)>();
            var blockIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in input.DbRows ?? Array.Empty<PlannerDbRow>())
            {
                var key = row.BlockName ?? string.Empty;
                if (!blockIndex.TryGetValue(key, out var idx))
                {
                    idx = rowsByBlock.Count;
                    blockIndex[key] = idx;
                    rowsByBlock.Add((key, new List<PlannerDbRow>()));
                }
                rowsByBlock[idx].Rows.Add(row);
            }

            // ---------- 块根组名（唯一化，按输入顺序：DbRows 块 → 其余已知存活块 → 变更块 → 删除块） ----------
            var (blockRoots, blockRootRenames) = AssignBlockRoots(
                rowsByBlock.Select(x => x.Block)
                    .Concat(input.KnownBlocks ?? Array.Empty<string>())
                    .Concat(changedBlocks).Concat(deletedBlocks), input.PreviousBlockRoots);

            // ---------- IO（放 S7 设备，排在块之前） ----------
            var includeIo = input.IoRefreshed || input.Full;
            if (includeIo)
            {
                // 只追加注册下 IO 根也不带 replaceGroups——它不走下面的 replaceBlocks，是独立一处。
                var ioUnit = new Unit
                {
                    ReplaceGroup = input.AppendOnly
                        ? null
                        : new List<string> { IgwTagNaming.IoRootGroup },
                };
                foreach (var tag in input.IoTags ?? Array.Empty<PlannerIoTag>())
                {
                    var item = BuildIoItem(tag, config, skipReasons);
                    if (item != null)
                    {
                        ioUnit.Items.Add(item);
                        ioUnit.Origins.Add((tag.TableName, tag.TagName));
                    }
                }
                units[IgwDeviceKind.S7].Add(ioUnit);
            }

            // ---------- DB 块 ----------
            var changed = new HashSet<string>(changedBlocks, StringComparer.OrdinalIgnoreCase);
            // 工步相关块（GRAPH FB 的实例 DB）用更快的扫描率，其余 DB 点保持 DbScanRateMs。
            // 按块而不是整体提速：几百个符号点都按 100ms 轮会把 S7-1500 的连接压断（现场实测
            // 93 点/请求必断），只让工步块那几十个点加速，压力涨幅可控。
            var fastScan = input.FastScanBlocks == null || input.FastScanBlocks.Count == 0
                ? null
                : new HashSet<string>(input.FastScanBlocks, StringComparer.OrdinalIgnoreCase);
            var plannedBlocks = new List<(string Block, IgwDeviceKind Device, Unit Unit, int Skipped, Dictionary<string, int> Reasons)>();
            foreach (var (block, rows) in rowsByBlock)
            {
                if (!input.Full && !changed.Contains(block))
                {
                    continue;
                }
                blockRoots.TryGetValue(block, out var root);
                var blockReasons = new Dictionary<string, int>();
                var (device, unit, skipped) = BuildBlockUnit(block, root, rows, config, blockReasons, fastScan);
                foreach (var (reason, n) in blockReasons)
                {
                    Count(skipReasons, reason, n);
                }
                plannedBlocks.Add((block, device, unit, skipped, blockReasons));
                if (unit.Items.Count > 0)
                {
                    units[device].Add(unit);
                }
            }

            // ---------- 同组重名去重（每台设备：先保留所有原名，再只给真正重复者加后缀） ----------
            foreach (var deviceUnits in units.Values)
            {
                DedupeNames(deviceUnits, skipReasons, ioRenames, dbRenames);
            }

            // ---------- 只追加注册（2026-09-22）：指纹一致的根整根跳过 ----------
            // 指纹必须在**去重改名之后**算——DedupeNames 会给同组重名点加 _2 后缀，
            // 改名本身就是 IGW 侧内容变化，算在前面会把改名判成"没变"。
            var fingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var skippedUnchanged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in plannedBlocks)
            {
                var fingerprint = IgwRootFingerprint.Compute(b.Unit.Items);
                fingerprints[b.Block] = fingerprint;
                if (!input.AppendOnly || input.RootStates == null) continue;
                if (!input.RootStates.TryGetValue(b.Block, out var state) || state == null) continue;
                // 设备变了（块在 S7 / S7Plus 之间搬家）⇒ 新设备上什么都没有，指纹再一致也必须整块重发
                if (state.Device != b.Device) continue;
                if (string.IsNullOrEmpty(state.Fingerprint)) continue;   // 没记过 ⇒ 必须重发
                if (!string.Equals(state.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)) continue;
                skippedUnchanged.Add(b.Block);
                units[b.Device].Remove(b.Unit);
            }

            // ---------- replaceGroups（块级，两台设备都替换） ----------
            // ⚠️ 只追加注册下一个都不发：上传解析这条链路只负责新增，PLC 里删掉的东西留在 IGW 上不管它
            //    （用户 2026-09-22 拍板）。现场实测整组删光再重写在 29 万点的设备上要 519 秒。
            var replaceBlocks = new List<string>();
            // 只追加模式下"哪个根允许在哪台设备上发清除组"；null = 不是只追加模式，走原有全量/增量逻辑
            Dictionary<string, HashSet<IgwDeviceKind>> clearOn = null;
            if (input.AppendOnly)
            {
                // 自己那台设备上的旧组一个都不替换，也不跟随 PLC 侧的删除。
                // 唯一例外：块在 S7 / S7Plus 之间**搬了家**，旧设备上那一份要清掉——
                // 那不是"PLC 里删了东西"，而是同一个块被注册了两份（重复注册），且只是个空请求，没有整组重写的代价。
                clearOn = new Dictionary<string, HashSet<IgwDeviceKind>>(StringComparer.OrdinalIgnoreCase);
                void Owe(string block, params IgwDeviceKind[] devices)
                {
                    if (string.IsNullOrEmpty(block)) return;
                    if (!clearOn.TryGetValue(block, out var set))
                    {
                        clearOn[block] = set = new HashSet<IgwDeviceKind>();
                        replaceBlocks.Add(block);
                    }
                    foreach (var d in devices) set.Add(d);
                }

                if (input.RootStates != null)
                {
                    foreach (var b in plannedBlocks)
                    {
                        if (!input.RootStates.TryGetValue(b.Block, out var st) || st == null) continue;
                        if (st.Device == b.Device) continue;
                        Owe(b.Block, st.Device);   // 搬家：只清它搬离的那台
                    }
                }
                // 台账里还欠着的旧组清除（上一次没清成功）：不记是哪台，两台都发
                if (input.PendingClearRoots != null)
                {
                    foreach (var block in input.PendingClearRoots)
                    {
                        Owe(block, IgwDeviceKind.S7, IgwDeviceKind.S7Plus);
                    }
                }
            }
            else if (input.Full)
            {
                replaceBlocks.AddRange(rowsByBlock.Select(x => x.Block));
            }
            else
            {
                replaceBlocks.AddRange(changedBlocks);
            }
            if (!input.AppendOnly)
            {
                replaceBlocks.AddRange(deletedBlocks);
            }

            // ---------- 组装请求 ----------
            var requests = new List<PlannedRequest>();
            foreach (var device in new[] { IgwDeviceKind.S7, IgwDeviceKind.S7Plus })
            {
                requests.AddRange(BuildDeviceRequests(device, units[device], replaceBlocks, clearOn, blockRoots, input, config, maxItems, maxBytes));
            }

            var blocks = plannedBlocks
                .Select(b => new PlannedBlock(b.Block, b.Device, b.Unit.Items.Count, b.Skipped)
                {
                    SkipReasons = b.Reasons,
                    Fingerprint = fingerprints[b.Block],
                    SkippedUnchanged = skippedUnchanged.Contains(b.Block),
                })
                .ToList();
            var ioTagCount = includeIo ? units[IgwDeviceKind.S7][0].Items.Count : 0;
            return new PlanResult(requests, blocks, ioTagCount, skipReasons, ioRenames, dbRenames, blockRootRenames)
            {
                BlockRoots = blockRoots,
            };
        }

        /// <summary>IGW 通道向导默认 settings（Siemens.S7 驱动）</summary>
        public static IgwBulkChannel ChannelSpec(string channelName) => new()
        {
            Name = channelName,
            DriverId = "Siemens.S7",
            Settings = new Dictionary<string, string>
            {
                ["wordOrder"] = "ABCD",
                ["timeoutMs"] = "2000",
                ["batchingMode"] = "auto",
                ["writeOptimization"] = "仅写入非布尔型标记的最新值",
                ["dutyCycle"] = "10",
                ["nonNormalFloat"] = "替换为零",
                ["diagnostics"] = "禁用",
                ["networkAdapter"] = "默认值",
            },
        };

        /// <summary>IGW 设备向导默认 settings + dcsLogicalDevice（端口 0 → 102）</summary>
        public static IgwBulkDevice DeviceSpec(IgwDeviceKind kind, string ip, int port, string plcModel)
        {
            var trimmedIp = (ip ?? string.Empty).Trim();
            var name = kind switch
            {
                IgwDeviceKind.S7Plus => IgwTagNaming.S7PlusDeviceName,
                IgwDeviceKind.Graph => IgwTagNaming.GraphDeviceName,
                _ => IgwTagNaming.S7DeviceName,
            };
            return new IgwBulkDevice
            {
                Name = name,
                Endpoint = $"{trimmedIp}:{(port == 0 ? 102 : port)}",
                Settings = new Dictionary<string, string>
                {
                    // ⚠️ 必须是这个中文字面量，别换成 "active"：≤1.6.85 的 IGW `ScanModeKey`
                    // 只认中文标签，其余值一律 `_ => "all"` —— 那会让整台设备几十万点全量轮询。
                    // 1.6.86 起两者都认，但中文标签是唯一跨版本安全的值。详见 CLAUDE.md §六。
                    ["scanMode"] = "遵循客户端指定的扫描速率",
                    ["initialFromCache"] = "禁用",
                    ["connTimeout"] = "3",
                    ["timeoutMs"] = "2000",
                    ["retries"] = "2",
                    ["requestDelay"] = "0",
                    ["dataCollection"] = "启用",
                    ["demote"] = "禁用",
                    ["demoteAfter"] = "3",
                    ["demotePeriodMs"] = "10000",
                    ["model"] = plcModel,
                    ["rack"] = "0",
                    ["slot"] = "1",
                    ["byteOrder"] = "big",
                    ["dcsLogicalDevice"] = IgwTagNaming.LogicalDeviceName(kind, trimmedIp),
                },
            };
        }

        /// <summary>
        /// 地址守卫（对齐 IGW）：非空、≤512 字符；符号地址（S7Plus）还必须含 `.` 且不含任何空白
        /// （IGW SiemensAddressDescriptor.Parse 遇空白抛异常，会导致整台 S7Plus 设备启动失败；超长会让整单被拒）。
        /// </summary>
        public static bool IsValidAddress(string address, bool symbolic)
        {
            if (string.IsNullOrEmpty(address) || address.Length > MaxAddressLength)
            {
                return false;
            }
            if (!symbolic)
            {
                return true;
            }
            if (address.IndexOf('.') < 0)
            {
                return false;
            }
            foreach (var ch in address)
            {
                if (char.IsWhiteSpace(ch))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 块根组名唯一化：
        /// ① 先按输入顺序沿用台账上次根组（<paramref name="previousRoots"/>）：名称合法（清洗后不变）、不等于 IO 根组、
        ///    且未被先处理的块占用 → 原样沿用（存活块根组不漂移）；
        /// ② 其余块按输入顺序保留「清洗后块名」（首个出现者）；
        /// ③ 最后为冲突者分配新根：与 IO 根组冲突 → IO_DB（仍冲突 → IO_DB_2…）；与已保留名同名（忽略大小写）→
        ///    {root}_2、{root}_3…（跳过已被占用的名字）。
        /// </summary>
        private static (Dictionary<string, string> Roots, Dictionary<string, string> Renames) AssignBlockRoots(
            IEnumerable<string> blocks, IReadOnlyDictionary<string, string> previousRoots)
        {
            var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { IgwTagNaming.IoRootGroup };
            var pending = new List<(string Block, string Base)>();
            var seenBlocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ordered = new List<string>();
            foreach (var block in blocks)
            {
                if (block != null && seenBlocks.Add(block))
                {
                    ordered.Add(block);
                }
            }

            // ① 台账上次根组优先（键大小写不敏感）
            if (previousRoots is { Count: > 0 })
            {
                var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (key, value) in previousRoots)
                {
                    if (key != null)
                    {
                        lookup.TryAdd(key, value);
                    }
                }
                foreach (var block in ordered)
                {
                    if (IgwTagNaming.SanitizeName(block) == null
                        || !lookup.TryGetValue(block, out var previous)
                        || string.IsNullOrEmpty(previous)
                        || !string.Equals(IgwTagNaming.SanitizeName(previous), previous, StringComparison.Ordinal)
                        || !used.Add(previous))
                    {
                        continue;
                    }
                    roots[block] = previous;
                }
            }

            foreach (var block in ordered)
            {
                if (roots.ContainsKey(block))
                {
                    continue;
                }
                var sanitized = IgwTagNaming.SanitizeName(block);
                if (sanitized == null)
                {
                    continue;
                }
                if (string.Equals(sanitized, IgwTagNaming.IoRootGroup, StringComparison.OrdinalIgnoreCase))
                {
                    pending.Add((block, IoClashBlockRoot));
                }
                else if (used.Add(sanitized))
                {
                    roots[block] = sanitized;
                }
                else
                {
                    pending.Add((block, sanitized));
                }
            }

            foreach (var (block, baseName) in pending)
            {
                var candidate = baseName;
                for (var n = 2; used.Contains(candidate); n++)
                {
                    candidate = WithSuffix(baseName, n);
                }
                used.Add(candidate);
                roots[block] = candidate;
                renames[block] = candidate;
            }

            // 沿用的上次根组若不等于清洗后块名，同样属于改名（读链须按此映射）
            foreach (var (block, root) in roots)
            {
                if (!renames.ContainsKey(block) && !string.Equals(root, IgwTagNaming.SanitizeName(block), StringComparison.Ordinal))
                {
                    renames[block] = root;
                }
            }
            return (roots, renames);
        }

        /// <summary>IO 点：GroupPath=[IO, 表名]，Name=点名；地址/类型/名称不可用 → null 并计原因</summary>
        private static IgwBulkItem BuildIoItem(PlannerIoTag tag, IgwRegistrationConfig config, Dictionary<string, int> skipReasons)
        {
            var table = IgwTagNaming.SanitizeName(tag.TableName);
            var name = IgwTagNaming.SanitizeName(tag.TagName);
            if (table == null || name == null)
            {
                Count(skipReasons, ReasonIoName);
                return null;
            }
            var address = IgwTypeMapper.IoAddress(tag.Address);
            if (address == null || !IsValidAddress(address, symbolic: false))
            {
                Count(skipReasons, ReasonIoAddress);
                return null;
            }
            var dataType = IgwTypeMapper.IoDataType(tag.DataType);
            if (dataType == null)
            {
                Count(skipReasons, ReasonIoType);
                return null;
            }
            return new IgwBulkItem
            {
                GroupPath = new List<string> { IgwTagNaming.IoRootGroup, table },
                Name = name,
                Address = address,
                DataType = dataType,
                ScanRateMs = config.IoScanRateMs,
                Description = Truncate(tag.Comment),
            };
        }

        /// <summary>
        /// 单块规划：求可注册行 → 判协议（全部可注册行都有合法经典地址 → S7，否则整块 S7Plus）→ 生成点；
        /// 按所选协议的地址过守卫，不合格行跳过（invalid_address）。点名此处用原名，重名去重在 <see cref="DedupeNames"/> 统一处理。
        /// </summary>
        private static (IgwDeviceKind Device, Unit Unit, int Skipped) BuildBlockUnit(
            string block, string root, List<PlannerDbRow> rows, IgwRegistrationConfig config,
            Dictionary<string, int> skipReasons, HashSet<string> fastScan)
        {
            var unit = new Unit { BlockName = block };
            if (root == null)
            {
                Count(skipReasons, ReasonEmptyName, rows.Count);
                return (IgwDeviceKind.S7, unit, rows.Count);
            }

            var registrable = new List<(PlannerDbRow Row, IReadOnlyList<string> Segments, string DataType, int? StringLength)>();
            var skipped = 0;
            foreach (var row in rows)
            {
                var dataType = IgwTypeMapper.MapDataType(row.DataType, out var stringLength);
                if (dataType == null)
                {
                    Count(skipReasons, ReasonUnmappedType);
                    skipped++;
                    continue;
                }
                var segments = IgwTagNaming.SplitSegments(IgwTagNaming.NormalizeVariablePath(row.VariablePath));
                if (segments.Count == 0)
                {
                    Count(skipReasons, ReasonEmptyName);
                    skipped++;
                    continue;
                }
                if (segments.Count + 1 > IgwTagNaming.MaxGroupDepth)
                {
                    Count(skipReasons, ReasonTooDeep);
                    skipped++;
                    continue;
                }
                registrable.Add((row, segments, dataType, stringLength));
            }

            var classic = registrable.All(r => IsValidAddress(ClassicAddressOf(r.Row), symbolic: false));
            var device = classic ? IgwDeviceKind.S7 : IgwDeviceKind.S7Plus;

            foreach (var (row, segments, dataType, stringLength) in registrable)
            {
                var address = classic
                    ? ClassicAddressOf(row)
                    : row.BlockName + "." + IgwTagNaming.NormalizeVariablePath(row.VariablePath);
                if (!IsValidAddress(address, symbolic: !classic))
                {
                    Count(skipReasons, ReasonInvalidAddress);
                    skipped++;
                    continue;
                }
                var groupPath = new List<string>(segments.Count) { root };
                groupPath.AddRange(segments.Take(segments.Count - 1));
                unit.Items.Add(new IgwBulkItem
                {
                    GroupPath = groupPath,
                    Name = segments[segments.Count - 1],
                    Address = address,
                    DataType = dataType,
                    ScanRateMs = fastScan != null && fastScan.Contains(row.BlockName)
                        ? config.GraphDbScanRateMs
                        : config.DbScanRateMs,
                    Description = Truncate(row.Comment),
                    StringLength = stringLength,
                });
                unit.Origins.Add((row.BlockName, row.VariablePath));
            }
            return (device, unit, skipped);
        }

        private static string ClassicAddressOf(PlannerDbRow row) =>
            row.OffsetByte == null ? null : IgwTypeMapper.ClassicAddress(row.BlockNumber, row.DataType, row.OffsetByte.Value, row.OffsetBit);

        /// <summary>
        /// 单设备同组重名去重（忽略大小写）：先把所有点的原名登记为已占用，按输入顺序只给第 2 个起的真正重复者分配
        /// {name}_n（n 从 2 起，跳过任何已占用名——所以真实存在的 X_2 保留原名，重复的 X 变成 X_3）。
        /// </summary>
        private static void DedupeNames(List<Unit> units, Dictionary<string, int> skipReasons,
            Dictionary<(string Table, string Tag), string> ioRenames, Dictionary<(string Block, string VariablePath), string> dbRenames)
        {
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var unit in units)
            {
                foreach (var item in unit.Items)
                {
                    reserved.Add(Key(item.GroupPath, item.Name));
                }
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var unit in units)
            {
                for (var i = 0; i < unit.Items.Count; i++)
                {
                    var item = unit.Items[i];
                    if (seen.Add(Key(item.GroupPath, item.Name)))
                    {
                        continue;
                    }

                    string candidate = null;
                    for (var n = 2; ; n++)
                    {
                        candidate = WithSuffix(item.Name, n);
                        if (reserved.Add(Key(item.GroupPath, candidate)))
                        {
                            break;
                        }
                    }
                    seen.Add(Key(item.GroupPath, candidate));
                    item.Name = candidate;
                    Count(skipReasons, ReasonRenamedDuplicate);
                    var origin = unit.Origins[i];
                    if (unit.BlockName == null)
                    {
                        ioRenames[(origin.A, origin.B)] = candidate;
                    }
                    else
                    {
                        dbRenames[(origin.A, origin.B)] = candidate;
                    }
                }
            }
        }

        /// <summary>
        /// 单设备组装：按单元累加，点数超上限或 replaceGroups 达上限开新请求；单元超限拆片，replaceGroups 只挂首片；
        /// 本设备无点的替换块依次填入已有请求的剩余 replaceGroups 容量，不够再开只含 replaceGroups 的请求。
        /// </summary>
        private static List<PlannedRequest> BuildDeviceRequests(IgwDeviceKind device, List<Unit> units,
            List<string> replaceBlocks, Dictionary<string, HashSet<IgwDeviceKind>> clearOn,
            Dictionary<string, string> blockRoots, PlannerInput input,
            IgwRegistrationConfig config, int maxItems, int maxBytes)
        {
            // 本设备待替换组（按根组名去重）
            var pendingReplace = new List<(List<string> Group, string BlockName)>();
            var pendingIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var block in replaceBlocks)
            {
                // 只追加模式下 replaceBlocks 里只有欠着旧组清除的根，且只发给欠它的那台设备
                if (clearOn != null && (block == null || !clearOn.TryGetValue(block, out var owed) || !owed.Contains(device)))
                {
                    continue;
                }
                if (block != null && blockRoots.TryGetValue(block, out var root) && !pendingIndex.ContainsKey(root))
                {
                    pendingIndex[root] = pendingReplace.Count;
                    pendingReplace.Add((new List<string> { root }, block));
                }
            }

            var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var unit in units)
            {
                if (unit.BlockName == null)
                {
                    continue; // IO 单元自带 ReplaceGroup
                }
                if (blockRoots.TryGetValue(unit.BlockName, out var root) && pendingIndex.TryGetValue(root, out var idx) && consumed.Add(root))
                {
                    unit.ReplaceGroup = pendingReplace[idx].Group;
                }
            }

            var drafts = new List<Draft>();
            Draft current = null;
            Draft NewDraft()
            {
                var draft = new Draft
                {
                    Request = new IgwBulkRequest
                    {
                        Channel = ChannelSpec(input.ChannelName),
                        Device = DeviceSpec(device, input.PlcIp, input.PlcPort, config.PlcModel),
                    },
                };
                drafts.Add(draft);
                return draft;
            }

            // 逐点预估字节，与点数上限一起决定切分位置（见 EstimateItemBytes）
            var unitBytes = new Dictionary<Unit, int[]>();
            int[] ItemBytesOf(Unit u)
            {
                if (!unitBytes.TryGetValue(u, out var arr))
                {
                    arr = new int[u.Items.Count];
                    for (var i = 0; i < u.Items.Count; i++)
                    {
                        arr[i] = EstimateItemBytes(u.Items[i]);
                    }
                    unitBytes[u] = arr;
                }
                return arr;
            }

            foreach (var unit in units)
            {
                var itemBytes = ItemBytesOf(unit);
                if (current != null && current.Request.Items.Count > 0 && current.Request.Items.Count + unit.Items.Count > maxItems)
                {
                    current = null;
                }
                if (current != null && unit.ReplaceGroup != null && current.Request.ReplaceGroups.Count >= MaxReplaceGroupsPerRequest)
                {
                    current = null; // replaceGroups 已达 IGW 单请求上限
                }
                var offset = 0;
                var first = true;
                do
                {
                    current ??= NewDraft();
                    var room = maxItems - current.Request.Items.Count;
                    var take = Math.Min(room, unit.Items.Count - offset);
                    // 字节预算：逐点累加，超预算就在此处断开（至少放一个点，避免单点超预算时死循环）
                    var budgetRoom = maxBytes - current.EstimatedBytes;
                    var taken = 0;
                    var bytesAdded = 0;
                    while (taken < take)
                    {
                        var next = itemBytes[offset + taken];
                        if (bytesAdded + next > budgetRoom && (current.Request.Items.Count > 0 || taken > 0))
                        {
                            break;
                        }
                        bytesAdded += next;
                        taken++;
                    }
                    if (taken > 0)
                    {
                        current.Request.Items.AddRange(unit.Items.GetRange(offset, taken));
                        current.EstimatedBytes += bytesAdded;
                        offset += taken;
                    }
                    // 注意：replaceGroups / 块名必须无条件登记——只带 replaceGroups、没有点位的单元
                    // （典型是 IO 根与「只清除」单元）taken 恒为 0，放进 if 里会让这些根一个请求都进不去。
                    if (first && unit.ReplaceGroup != null)
                    {
                        current.AddReplace(unit.ReplaceGroup);
                    }
                    current.AddBlockName(unit.BlockName);
                    first = false;
                    if (offset < unit.Items.Count)
                    {
                        current = null; // 本片已满（点数或字节），剩余点开新请求
                    }
                }
                while (offset < unit.Items.Count);
            }

            var draftIndex = 0;
            foreach (var (group, blockName) in pendingReplace)
            {
                if (consumed.Contains(group[0]))
                {
                    continue;
                }
                while (draftIndex < drafts.Count && drafts[draftIndex].Request.ReplaceGroups.Count >= MaxReplaceGroupsPerRequest)
                {
                    draftIndex++;
                }
                var target = draftIndex < drafts.Count ? drafts[draftIndex] : NewDraft();
                target.AddReplace(group);
                target.AddBlockName(blockName);
            }

            return drafts.Select(d => new PlannedRequest(device, d.Request, d.BlockNames)).ToList();
        }

        /// <summary>{name}_n，超长时截断原名保证 ≤128</summary>
        private static string WithSuffix(string name, int n)
        {
            var suffix = "_" + n;
            var baseName = name.Length + suffix.Length > IgwTagNaming.MaxNameLength
                ? name.Substring(0, IgwTagNaming.MaxNameLength - suffix.Length)
                : name;
            return baseName + suffix;
        }

        private static string Key(List<string> groupPath, string name) =>
            string.Join(KeySeparator, groupPath) + KeySeparator + name;

        private static string Truncate(string comment)
        {
            if (string.IsNullOrEmpty(comment))
            {
                return comment;
            }
            return comment.Length > MaxDescriptionLength ? comment.Substring(0, MaxDescriptionLength) : comment;
        }

        private static void Count(Dictionary<string, int> skipReasons, string reason, int n = 1)
        {
            skipReasons.TryGetValue(reason, out var current);
            skipReasons[reason] = current + n;
        }
    }
}
