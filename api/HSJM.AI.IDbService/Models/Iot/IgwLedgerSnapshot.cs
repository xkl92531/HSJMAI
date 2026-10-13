using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace HSJM.AI.IDbService
{
    /// <summary>
    /// <see cref="PLC_IgwRegistrationEntity.RootKind"/> 取值常量。
    /// </summary>
    public static class IgwLedgerRootKind
    {
        /// <summary>DB 块（S7/S7Plus 两种设备之一）</summary>
        public const string DbBlock = "DbBlock";

        /// <summary>IO 变量表整体（固定落在 S7 设备）</summary>
        public const string Io = "Io";

        /// <summary>GRAPH 工站下的一个实例 DB（固定落在 Graph 设备）</summary>
        public const string Graph = "Graph";
    }

    /// <summary>
    /// <see cref="PLC_IgwRegistrationEntity.Status"/> 取值常量。
    /// </summary>
    public static class IgwLedgerStatus
    {
        public const string Registered = "Registered";
        public const string Failed = "Failed";

        /// <summary>
        /// IGW 按请求内容永久拒绝（HTTP 400/413，R-1 2026-09-20）：不随 Hangfire 重试 / 下次上传自动重发，
        /// 只在该块自身变更或全量注册时重发（IO 行：IO 刷新或全量时）。Failed 保持「瞬态失败，自动重试」语义。
        /// </summary>
        public const string Rejected = "Rejected";

        /// <summary>
        /// DB 块只待清除（H Task 4 fix round 4 / 5）：块本身的内容已全部写在台账 DeviceKind / RootGroup / 重名映射指向的位置
        /// （读链照常按它解析），只剩「DeviceKind 以外的另一台设备」（S7 ↔ S7Plus）上的旧组清除还没写入（旧点可能仍留在那台设备上）。
        /// 下次增量注册只对那台设备重发一个清除组请求（replaceGroups，不带点），块自身内容不重发——否则每次上传都会让
        /// 本设备重启一次；清除写入后转 Registered。清除被 IGW 永久拒绝时保持本状态，代价是每次上传一个小的清除请求。
        /// 不计入「注册成功」（不影响增量升级全量的判定）。
        /// </summary>
        public const string PendingClear = "PendingClear";

        /// <summary>
        /// DB 块内容未全部写入且有旧组清除待写入（H Task 4 fix round 5）：块自身内容没有全部写在台账位置（部分分片失败 / 被拒，
        /// 或一片都没写入而沿用旧位置），同时某台设备上仍可能留有待清除的旧组（包括块换回本设备时本设备上更早的旧组）。
        /// 与 Failed 一样随下次增量注册整块重做（规划器对变更块在两台设备上都发 replaceGroups，旧组随之清除），
        /// 即使本设备内容被按内容拒绝也继续自动重试（不能像 Rejected 那样停止，否则旧组永远留着）；并携带「旧组待清除」要求到下一轮。
        /// 不计入 FailedDbBlocks（增量升级全量只看瞬态 Failed 行）也不计入「注册成功」。
        /// </summary>
        public const string FailedPendingClear = "FailedPendingClear";
    }

    /// <summary>
    /// 台账快照：把一个项目下全部未删除的 <see cref="PLC_IgwRegistrationEntity"/> 行折叠为读链
    /// （spec §4.6，PLC_VariableAccessService.ReadExactViaIgwAsync / ReadGraphStepsViaIgwAsync 等）
    /// 直接可用的纯内存结构。由 <see cref="IgwLedgerSnapshotBuilder.Build"/> 构建，构建过程不访问数据库、无副作用。
    /// 块名/设备/根组/工站等单字符串键查找大小写不敏感（StringComparer.OrdinalIgnoreCase）；
    /// 重名映射 DbRenames / IoRenames 的 (原块名, 原变量路径) / (原表名, 原点名) 复合键按 Ordinal 精确比较——
    /// 重名正是由大小写变体引起（如 Flag / flag → flag_2），大小写不敏感会让 Flag 的读请求命中 flag 的映射而读到 flag_2。
    /// </summary>
    /// <param name="DeviceKindByBlock">RootKind=DbBlock 行：块名 → 设备类型（大小写不敏感）</param>
    /// <param name="RootGroupByBlock">RootKind=DbBlock 行：块名 → 规划器实际分配的根组名（大小写不敏感）</param>
    /// <param name="DbRenames">RootKind=DbBlock 行 RenamesJson 展开：(原块名, 原变量路径) → 最终点名（Ordinal 精确匹配）</param>
    /// <param name="IoRenames">RootKind=Io 行 RenamesJson 展开：(原表名, 原点名) → 最终点名（Ordinal 精确匹配）</param>
    /// <param name="StationByDb">RootKind=Graph 行：DB 名 → 工站代码（RootName 按最后一个分隔符拆分，新形式分隔符为
    /// <see cref="IgwLedgerSnapshotBuilder.GraphRootNameSeparator"/>，兼容旧 '|' 形式；大小写不敏感）</param>
    public sealed record IgwLedgerSnapshot(
        IReadOnlyDictionary<string, IgwDeviceKind> DeviceKindByBlock,
        IReadOnlyDictionary<string, string> RootGroupByBlock,
        IReadOnlyDictionary<(string Block, string VariablePath), string> DbRenames,
        IReadOnlyDictionary<(string Table, string Tag), string> IoRenames,
        IReadOnlyDictionary<string, string> StationByDb)
    {
        /// <summary>RootKind=Graph 行：DB 名 → 台账原始 RootName（大小写不敏感查找；台账替换/移除需按原键精确匹配）</summary>
        public IReadOnlyDictionary<string, string> GraphRootNameByDb { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 台账中出现过的 RootKind 集合（<see cref="IgwLedgerRootKind"/> 取值，Ordinal）：注册任务据此判断项目是否完成过
        /// DB/IO 首次注册（无 DbBlock/Io 行 → 增量自动升级为全量）、是否有过 GRAPH 注册（无 Graph 行且有 GRAPH 目标 → 触发 GRAPH 重注册）。
        /// </summary>
        public IReadOnlySet<string> RootKinds { get; init; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Status=Registered 的行出现过的 RootKind 集合（Ordinal）：无 Registered 的 DbBlock/Io 行 → 增量自动升级全量</summary>
        public IReadOnlySet<string> RegisteredRootKinds { get; init; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Status=Failed 的 DbBlock 行块名（大小写不敏感）：增量注册时并入变更范围重注册（Hangfire 重试 / 下次上传补齐）</summary>
        public IReadOnlySet<string> FailedDbBlocks { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Status=PendingClear 的 DbBlock 行块名（大小写不敏感）：内容已写入，只剩 DeviceKind 以外另一台设备上的旧组清除未写入——
        /// 增量注册时只重发那台设备上的清除组（不并入变更范围）；不计入 FailedDbBlocks（增量升级全量只看瞬态 Failed 行）也不计入 RegisteredRootKinds。
        /// </summary>
        public IReadOnlySet<string> PendingClearDbBlocks { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Status=FailedPendingClear 的 DbBlock 行块名（大小写不敏感）：内容未全部写入且有旧组待清除——增量注册时并入变更范围整块重做；
        /// 不计入 FailedDbBlocks 也不计入 RegisteredRootKinds。
        /// </summary>
        public IReadOnlySet<string> FailedPendingClearDbBlocks { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>RootKind=DbBlock 行：块名 → 台账记录的点数 / 跳过数 / 跳过摘要（大小写不敏感；只重发旧组清除的块改写台账状态时原样沿用）</summary>
        public IReadOnlyDictionary<string, IgwLedgerDbBlockStats> DbBlockStats { get; init; } =
            new Dictionary<string, IgwLedgerDbBlockStats>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Io 行 Status=Failed：增量注册时视为 IO 已刷新，整体重注册 IO</summary>
        public bool HasFailedIo { get; init; }

        /// <summary>Status=Rejected 的 DbBlock 行块名（大小写不敏感）：永久被拒，不自动重试（R-1）</summary>
        public IReadOnlySet<string> RejectedDbBlocks { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Io 行 Status=Rejected（R-1）</summary>
        public bool HasRejectedIo { get; init; }

        /// <summary>台账是否含指定 RootKind 的行</summary>
        public bool HasRootKind(string rootKind) => rootKind != null && RootKinds.Contains(rootKind);

        /// <summary>空快照（项目从未注册过，或台账被清空）：所有查表操作均返回"未命中"，调用方按各自的回退规则处理。</summary>
        public static readonly IgwLedgerSnapshot Empty = new(
            new Dictionary<string, IgwDeviceKind>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<(string, string), string>(),
            new Dictionary<(string, string), string>(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>台账 DbBlock 行的点数 / 跳过数 / 跳过摘要（原样取自台账列）</summary>
    public sealed record IgwLedgerDbBlockStats(int? TagCount, int? SkippedCount, string? SkipSummary)
    {
        /// <summary>
        /// 上次注册落在哪台设备（台账 DeviceKind 原文）。2026-09-22 只追加注册用：
        /// 块在 S7 / S7Plus 之间搬家时新设备上什么都没有，指纹再一致也必须整块重发。
        /// </summary>
        public string? DeviceKind { get; init; }

        /// <summary>
        /// 上次注册的根组内容指纹（<see cref="P:HSJM.AI.AppLayer.PLC.PLC_Upload.Igw.IgwRootFingerprint"/> 算出）。
        /// **null/空 = 没记过 ⇒ 必须重发**——当成"一致"的话整个跳过机制从第一天起就是死的，而且不报错。
        /// </summary>
        public string? Fingerprint { get; init; }
    }

    /// <summary>
    /// 纯函数：台账行 → <see cref="IgwLedgerSnapshot"/>。与 <see cref="IPLC_IgwRegistrationService"/> 的数据库读取解耦，
    /// 便于无数据库单测（见 sub-project B task-5 测试要求）。调用方需保证传入行已按 (ProjectId, IsDeleted=false) 过滤——
    /// 本函数只额外防御性跳过 IsDeleted=true 的行，不做项目隔离、不重复过滤。
    /// </summary>
    public static class IgwLedgerSnapshotBuilder
    {
        /// <summary>
        /// RootKind=Io 行 RenamesJson 键内 (表名, 点名) 的拼接分隔符：与 IgwRegistrationPlanner 内部拼接
        /// 组路径+点名键共用同一个不可见字符常量，见 <see cref="IgwTagNaming.KeySeparator"/>。
        /// </summary>
        public const char IoRenameKeySeparator = IgwTagNaming.KeySeparator;

        /// <summary>
        /// RootKind=Graph 行 RootName "{工站}{分隔符}{DB名}" 的分隔符：不可见控制字符 U+0001（<see cref="IgwTagNaming.KeySeparator"/>），
        /// 经 <see cref="IgwTagNaming.SanitizeName"/> 清洗的工站名不可能含它，拆分无歧义（工站名可含 '|'）。
        /// </summary>
        public const char GraphRootNameSeparator = IgwTagNaming.KeySeparator;

        /// <summary>旧版 Graph RootName 分隔符（'|'）：只读兼容，按最后一个 '|' 拆分。</summary>
        public const char LegacyGraphRootNameSeparator = '|';

        /// <summary>
        /// 台账行 → 快照。<paramref name="rows"/> 无需预先过滤 IsDeleted（本函数会跳过）。
        /// </summary>
        public static IgwLedgerSnapshot Build(IEnumerable<PLC_IgwRegistrationEntity>? rows) => Build(rows, out _);

        /// <summary>
        /// 同 <see cref="Build(IEnumerable{PLC_IgwRegistrationEntity})"/>，额外输出 RenamesJson 解析失败被丢弃的行数
        /// （调用方——通常是 <c>IPLC_IgwRegistrationService.GetLedgerAsync</c>——据此记一条告警日志，
        /// 便于发现台账数据被手工改坏/写入方序列化 bug 等问题）。
        /// </summary>
        public static IgwLedgerSnapshot Build(IEnumerable<PLC_IgwRegistrationEntity>? rows, out int malformedRenamesRowCount)
        {
            var deviceKindByBlock = new Dictionary<string, IgwDeviceKind>(StringComparer.OrdinalIgnoreCase);
            var rootGroupByBlock = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var dbRenames = new Dictionary<(string, string), string>();
            var ioRenames = new Dictionary<(string, string), string>();
            var stationByDb = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var graphRootNameByDb = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var rootKinds = new HashSet<string>(StringComparer.Ordinal);
            var registeredRootKinds = new HashSet<string>(StringComparer.Ordinal);
            var failedDbBlocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pendingClearDbBlocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var failedPendingClearDbBlocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dbBlockStats = new Dictionary<string, IgwLedgerDbBlockStats>(StringComparer.OrdinalIgnoreCase);
            var hasFailedIo = false;
            var rejectedDbBlocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hasRejectedIo = false;
            var malformedCount = 0;

            foreach (var row in rows ?? Enumerable.Empty<PLC_IgwRegistrationEntity>())
            {
                if (row == null || row.IsDeleted)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(row.RootKind) && !string.IsNullOrEmpty(row.RootName))
                {
                    rootKinds.Add(row.RootKind);
                    if (string.Equals(row.Status, IgwLedgerStatus.Registered, StringComparison.Ordinal))
                    {
                        registeredRootKinds.Add(row.RootKind);
                    }
                    else if (string.Equals(row.Status, IgwLedgerStatus.Failed, StringComparison.Ordinal))
                    {
                        if (string.Equals(row.RootKind, IgwLedgerRootKind.DbBlock, StringComparison.Ordinal))
                        {
                            failedDbBlocks.Add(row.RootName);
                        }
                        else if (string.Equals(row.RootKind, IgwLedgerRootKind.Io, StringComparison.Ordinal))
                        {
                            hasFailedIo = true;
                        }
                    }
                    else if (string.Equals(row.Status, IgwLedgerStatus.PendingClear, StringComparison.Ordinal))
                    {
                        if (string.Equals(row.RootKind, IgwLedgerRootKind.DbBlock, StringComparison.Ordinal))
                        {
                            pendingClearDbBlocks.Add(row.RootName);
                        }
                    }
                    else if (string.Equals(row.Status, IgwLedgerStatus.FailedPendingClear, StringComparison.Ordinal))
                    {
                        if (string.Equals(row.RootKind, IgwLedgerRootKind.DbBlock, StringComparison.Ordinal))
                        {
                            failedPendingClearDbBlocks.Add(row.RootName);
                        }
                    }
                    else if (string.Equals(row.Status, IgwLedgerStatus.Rejected, StringComparison.Ordinal))
                    {
                        if (string.Equals(row.RootKind, IgwLedgerRootKind.DbBlock, StringComparison.Ordinal))
                        {
                            rejectedDbBlocks.Add(row.RootName);
                        }
                        else if (string.Equals(row.RootKind, IgwLedgerRootKind.Io, StringComparison.Ordinal))
                        {
                            hasRejectedIo = true;
                        }
                    }
                }

                if (string.Equals(row.RootKind, IgwLedgerRootKind.DbBlock, StringComparison.Ordinal))
                {
                    if (string.IsNullOrEmpty(row.RootName))
                    {
                        continue;
                    }

                    if (Enum.TryParse<IgwDeviceKind>(row.DeviceKind, ignoreCase: true, out var kind))
                    {
                        deviceKindByBlock[row.RootName] = kind;
                    }

                    if (!string.IsNullOrEmpty(row.RootGroup))
                    {
                        rootGroupByBlock[row.RootName] = row.RootGroup;
                    }

                    dbBlockStats[row.RootName] = new IgwLedgerDbBlockStats(row.TagCount, row.SkippedCount, row.SkipSummary)
                    {
                        DeviceKind = row.DeviceKind,
                        Fingerprint = row.Fingerprint,
                    };

                    if (!TryParseRenames(row.RenamesJson, out var dbMap))
                    {
                        malformedCount++;
                        continue;
                    }

                    foreach (var (variablePath, finalName) in dbMap)
                    {
                        dbRenames[(row.RootName, variablePath)] = finalName;
                    }
                }
                else if (string.Equals(row.RootKind, IgwLedgerRootKind.Io, StringComparison.Ordinal))
                {
                    if (!TryParseRenames(row.RenamesJson, out var ioMap))
                    {
                        malformedCount++;
                        continue;
                    }

                    foreach (var (key, finalName) in ioMap)
                    {
                        var parts = key.Split(IoRenameKeySeparator, 2);
                        if (parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
                        {
                            ioRenames[(parts[0], parts[1])] = finalName;
                        }
                    }
                }
                else if (string.Equals(row.RootKind, IgwLedgerRootKind.Graph, StringComparison.Ordinal))
                {
                    if (string.IsNullOrEmpty(row.RootName))
                    {
                        continue;
                    }

                    // 新形式按最后一个 U+0001 拆；旧形式按最后一个 '|' 拆（DB 名不含 '|'，工站名可能含）
                    var separatorIndex = row.RootName.LastIndexOf(GraphRootNameSeparator);
                    if (separatorIndex < 0)
                    {
                        separatorIndex = row.RootName.LastIndexOf(LegacyGraphRootNameSeparator);
                    }
                    if (separatorIndex < 0)
                    {
                        continue;
                    }

                    var station = row.RootName.Substring(0, separatorIndex);
                    var dbName = row.RootName.Substring(separatorIndex + 1);
                    if (!string.IsNullOrEmpty(dbName))
                    {
                        stationByDb[dbName] = station;
                        graphRootNameByDb[dbName] = row.RootName;
                    }
                }
            }

            malformedRenamesRowCount = malformedCount;
            return new IgwLedgerSnapshot(deviceKindByBlock, rootGroupByBlock, dbRenames, ioRenames, stationByDb)
            {
                GraphRootNameByDb = graphRootNameByDb,
                RootKinds = rootKinds,
                RegisteredRootKinds = registeredRootKinds,
                FailedDbBlocks = failedDbBlocks,
                PendingClearDbBlocks = pendingClearDbBlocks,
                FailedPendingClearDbBlocks = failedPendingClearDbBlocks,
                DbBlockStats = dbBlockStats,
                HasFailedIo = hasFailedIo,
                RejectedDbBlocks = rejectedDbBlocks,
                HasRejectedIo = hasRejectedIo,
            };
        }

        /// <summary>
        /// 解析 RenamesJson（{key: finalName} 扁平字符串字典）。
        /// 空/空白 → 成功，返回空序列（"没有重名"是合法状态，不算解析失败）。
        /// JSON 语法错误/反序列化为 null → 失败（<paramref name="entries"/> 为空序列，返回 false 供调用方计数）。
        /// key/value 任一为空的条目静默跳过（不计入失败，只是脏数据条目）。
        /// </summary>
        private static bool TryParseRenames(string? renamesJson, out List<(string Key, string Value)> entries)
        {
            entries = new List<(string Key, string Value)>();
            if (string.IsNullOrWhiteSpace(renamesJson))
            {
                return true;
            }

            Dictionary<string, string>? map;
            try
            {
                map = JsonSerializer.Deserialize<Dictionary<string, string>>(renamesJson);
            }
            catch (JsonException)
            {
                return false;
            }

            if (map == null)
            {
                return false;
            }

            foreach (var kv in map)
            {
                if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(kv.Value))
                {
                    entries.Add((kv.Key, kv.Value));
                }
            }

            return true;
        }
    }
}
