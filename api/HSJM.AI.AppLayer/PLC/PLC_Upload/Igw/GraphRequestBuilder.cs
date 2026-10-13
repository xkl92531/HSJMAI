using HSJM.AI.Core;
using HSJM.AI.IDbService;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HSJM.AI.AppLayer.PLC.PLC_Upload.Igw
{
    /// <summary>
    /// 一个 GRAPH 步进实例 DB 的注册目标：DB 名、所属工站（空 = 未绑定，落 <see cref="IgwTagNaming.UngroupedStation"/>）、
    /// EXEC_BITS 最大 0 基下标（null = 无 EXEC_BIT 行，不注册数组）、是否注册 S_NO。
    /// </summary>
    public sealed record GraphTargetSpec(string DbName, string StationCode, int? MaxBitIndex, bool IncludeStepNo);

    /// <summary>一个待发送的 GRAPH 设备批量请求；DbNames = 本请求承载（点 + replaceGroups）的 DB</summary>
    public sealed record GraphPlannedRequest(IgwBulkRequest Request, IReadOnlyList<string> DbNames)
    {
        /// <summary>本请求仅清除（replaceGroups，无点）的失效 DB（台账有、本次目标已无）</summary>
        public IReadOnlyList<string> StaleDbNames { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// 构建结果：Requests 按 DB 边界切分（通常只有 1 个）；SkippedDbs = 无法注册的 DB（DB 名 → 原因）；
    /// TagCountByDb = 每个 DB 生成的点数（EXEC_BITS 数组算 1 点）。
    /// </summary>
    public sealed record GraphBuildResult(IReadOnlyList<GraphPlannedRequest> Requests, IReadOnlyDictionary<string, string> SkippedDbs,
        IReadOnlyDictionary<string, int> TagCountByDb);

    /// <summary>
    /// GRAPH 台账 → IGW GRAPH 设备批量请求（纯函数，sub-project B spec §3 / §4.4）：
    /// 每个 DB 两点——<c>[工站|_未分组, DB, RT_DATA]/EXEC_BITS</c>（bool 数组，维度 = 最大下标 + 1）与
    /// <c>[工站|_未分组, DB]/S_NO</c>（int16），符号地址 <c>{DB}.RT_DATA.EXEC_BITS</c> / <c>{DB}.S_NO</c>；
    /// replaceGroups = 每个 DB 在旧工站（台账）与新工站下的 <c>[工站, DB]</c>（去重），工站改绑时旧位置被清除。
    /// 组名/点名与 <see cref="IgwTagNaming.GraphReadAlias"/> 使用同一清洗规则，物理路径与读别名逐字一致。
    /// </summary>
    public static class GraphRequestBuilder
    {
        /// <summary>EXEC_BITS 数组所在子组</summary>
        public const string ExecBitsGroup = "RT_DATA";

        /// <summary>EXEC_BITS 数组点名</summary>
        public const string ExecBitsName = "EXEC_BITS";

        /// <summary>S_NO 点名</summary>
        public const string StepNoName = "S_NO";

        /// <summary>读别名 member：EXEC_BITS 数组（<see cref="IgwTagNaming.GraphReadAlias"/> 用）</summary>
        public const string ExecBitsMember = ExecBitsGroup + "." + ExecBitsName;

        /// <summary>读别名 member：S_NO</summary>
        public const string StepNoMember = StepNoName;

        /// <summary>工站根组名：清洗后工站代码，空 → <see cref="IgwTagNaming.UngroupedStation"/>（与读别名一致）</summary>
        public static string StationGroup(string stationCode) =>
            IgwTagNaming.SanitizeName(stationCode) ?? IgwTagNaming.UngroupedStation;

        /// <summary>台账 RootName（RootKind=Graph）："{工站根组}{U+0001}{DB 名}"（分隔符见 IgwLedgerSnapshotBuilder.GraphRootNameSeparator）</summary>
        public static string LedgerRootName(string stationCode, string dbName) =>
            $"{StationGroup(stationCode)}{IgwLedgerSnapshotBuilder.GraphRootNameSeparator}{(dbName ?? string.Empty).Trim()}";

        /// <summary>
        /// 构建 GRAPH 设备批量请求。previousStationsByDb：DB 名 → 旧工站集合（空值=未绑定 → _未分组），参与 replaceGroups。
        /// staleRoots：需从 GRAPH 设备清除的失效 DB（台账工站, DB 名），只生成 replaceGroups <c>[工站, DB]</c>、不生成点；
        /// 与本次目标同名（大小写不敏感）的项忽略。
        /// </summary>
        public static GraphBuildResult Build(string channelName, string ip, int port, IReadOnlyList<GraphTargetSpec> targets,
            IReadOnlyDictionary<string, IReadOnlyCollection<string>> previousStationsByDb, IgwRegistrationConfig config,
            IReadOnlyCollection<(string Station, string DbName)> staleRoots = null)
        {
            config ??= new IgwRegistrationConfig();
            var maxItems = Math.Max(1, config.MaxItemsPerRequest);
            var skipped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var tagCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 每个 DB 一个单元：点 + replaceGroups（切分只在 DB 边界发生）
            var units = new List<(string DbName, List<IgwBulkItem> Items, List<List<string>> Replace, string StaleDb)>();
            foreach (var target in targets ?? Array.Empty<GraphTargetSpec>())
            {
                var dbName = (target?.DbName ?? string.Empty).Trim();
                if (dbName.Length == 0 || !seen.Add(dbName))
                {
                    continue;
                }
                var dbGroup = IgwTagNaming.SanitizeName(dbName);
                var execAddress = $"{dbName}.{ExecBitsMember}";
                var stepNoAddress = $"{dbName}.{StepNoMember}";
                if (dbGroup == null
                    || !IgwRegistrationPlanner.IsValidAddress(execAddress, symbolic: true)
                    || !IgwRegistrationPlanner.IsValidAddress(stepNoAddress, symbolic: true))
                {
                    skipped[dbName] = "DB 名无法构成合法的 IGW 组名/符号地址（含空白、控制字符或超长）";
                    continue;
                }

                var station = StationGroup(target.StationCode);
                var items = new List<IgwBulkItem>();
                if (target.MaxBitIndex is int maxBit && maxBit >= 0)
                {
                    items.Add(new IgwBulkItem
                    {
                        GroupPath = new List<string> { station, dbGroup, ExecBitsGroup },
                        Name = ExecBitsName,
                        Address = execAddress,
                        // IGW 契约：数组点 dataType 必须等于 array.elementType
                        DataType = "bool",
                        Access = "readWrite",
                        ScanRateMs = config.GraphScanRateMs,
                        Array = new IgwBulkArray { ElementType = "bool", Dimensions = new[] { maxBit + 1 } },
                    });
                }
                if (target.IncludeStepNo)
                {
                    items.Add(new IgwBulkItem
                    {
                        GroupPath = new List<string> { station, dbGroup },
                        Name = StepNoName,
                        Address = stepNoAddress,
                        DataType = "int16",
                        Access = "readWrite",
                        ScanRateMs = config.GraphScanRateMs,
                    });
                }

                // replaceGroups：旧工站（可能多个）+ 新工站，按大小写不敏感去重（IGW 组名判重 NOCASE）
                var replace = new List<List<string>>();
                var replaceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var previous = previousStationsByDb != null && previousStationsByDb.TryGetValue(dbName, out var prev)
                    ? prev ?? Array.Empty<string>()
                    : (IReadOnlyCollection<string>)Array.Empty<string>();
                foreach (var stationGroup in previous.Select(StationGroup).Append(station))
                {
                    if (replaceKeys.Add(stationGroup))
                    {
                        replace.Add(new List<string> { stationGroup, dbGroup });
                    }
                }

                tagCounts[dbName] = items.Count;
                units.Add((dbName, items, replace, null));
            }

            // 失效 DB：只清除其台账工站下的 [工站, DB] 组（与目标同名的忽略——目标自身的 replaceGroups 已覆盖）
            var targetNames = new HashSet<string>(seen, StringComparer.OrdinalIgnoreCase);
            var staleSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (station, rawDb) in staleRoots ?? Array.Empty<(string, string)>())
            {
                var staleDb = (rawDb ?? string.Empty).Trim();
                var staleGroup = IgwTagNaming.SanitizeName(staleDb);
                if (staleDb.Length == 0 || staleGroup == null || targetNames.Contains(staleDb) || !staleSeen.Add(staleDb))
                {
                    continue;
                }
                units.Add((null, new List<IgwBulkItem>(),
                    new List<List<string>> { new List<string> { StationGroup(station), staleGroup } }, staleDb));
            }

            // 切分：每请求点数 ≤ MaxItemsPerRequest、replaceGroups ≤ IGW 上限；单个 DB 不拆
            var requests = new List<GraphPlannedRequest>();
            IgwBulkRequest current = null;
            List<string> currentDbs = null;
            List<string> currentStale = null;
            foreach (var unit in units)
            {
                if (current != null
                    && (current.Items.Count + unit.Items.Count > maxItems
                        || current.ReplaceGroups.Count + unit.Replace.Count > IgwRegistrationPlanner.MaxReplaceGroupsPerRequest))
                {
                    requests.Add(new GraphPlannedRequest(current, currentDbs) { StaleDbNames = currentStale });
                    current = null;
                }
                if (current == null)
                {
                    current = new IgwBulkRequest
                    {
                        Channel = IgwRegistrationPlanner.ChannelSpec(channelName),
                        Device = IgwRegistrationPlanner.DeviceSpec(IgwDeviceKind.Graph, ip, port, config.PlcModel),
                    };
                    currentDbs = new List<string>();
                    currentStale = new List<string>();
                }
                current.Items.AddRange(unit.Items);
                current.ReplaceGroups.AddRange(unit.Replace);
                if (unit.StaleDb != null)
                {
                    currentStale.Add(unit.StaleDb);
                }
                else
                {
                    currentDbs.Add(unit.DbName);
                }
            }
            if (current != null)
            {
                requests.Add(new GraphPlannedRequest(current, currentDbs) { StaleDbNames = currentStale });
            }

            return new GraphBuildResult(requests, skipped, tagCounts);
        }
    }
}
