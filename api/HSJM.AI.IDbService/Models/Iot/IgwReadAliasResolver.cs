using System;
using System.Collections.Generic;

namespace HSJM.AI.IDbService
{
    /// <summary>读链请求类别（决定走哪台 IGW 设备、按哪套台账规则拼读别名）</summary>
    public enum IgwReadRequestKind
    {
        /// <summary>IO 变量表点位（候选校验命中 ai_plc_iotag：TableName==块名 且 TagName==变量路径）</summary>
        IoTag,

        /// <summary>DB 块变量（S7 或 S7Plus 设备，按台账 DeviceKind）</summary>
        DbVariable,

        /// <summary>GRAPH 工步容器（{db}.S_NO / {db}.RT_DATA.EXEC_BITS，GRAPH 设备）</summary>
        Graph,
    }

    /// <summary>
    /// 读链别名解析（sub-project B task-8，spec §4.6）：按 IGW 注册台账快照（<see cref="IgwLedgerSnapshot"/>）
    /// 把读请求映射为 IGW 读别名。纯函数、无副作用；与注册侧共用 <see cref="IgwTagNaming"/> 的清洗/路径还原/拼接规则，
    /// 保证「规划器产出的组路径 + 点名」与本类解析结果逐字一致（见 IgwReadAliasResolverTests）。
    /// <para>
    /// 回退规则：
    /// - DB 块台账无记录（项目尚未按新结构注册 / 台账被清空）→ S7Plus 逻辑设备 S1500_{ip} + 清洗后块名根组 + 还原路径。
    ///   这与旧的扁平注册（<c>DcsIgwConsts.BuildTagReadAlias</c>：通用.S1500_{ip}.{块名}.{原始路径}）只在
    ///   块名/路径无需清洗、无解析器数组占位段时恰好一致——属可接受的过渡期行为，新注册完成后即按台账切换。
    /// - GRAPH 工站：台账 Graph 行工站优先 → 调用方给出的回退工站（AI_PLC_GraphStepTag.StationCode）→ <c>_未分组</c>。
    /// </para>
    /// </summary>
    public static class IgwReadAliasResolver
    {
        /// <summary>GRAPH 容器 member：工步号</summary>
        public const string GraphStepNoMember = "S_NO";

        /// <summary>GRAPH 容器 member：执行位数组</summary>
        public const string GraphExecBitsMember = "RT_DATA.EXEC_BITS";

        /// <summary>按类别分派：IoTag → (表名, 点名)；DbVariable → (块名, 变量路径)；Graph → (DB 名, member)。</summary>
        public static string Resolve(string ip, IgwLedgerSnapshot? snapshot, IgwReadRequestKind kind,
            string first, string second, string? fallbackStation = null) =>
            kind switch
            {
                IgwReadRequestKind.IoTag => ResolveIo(ip, snapshot, first, second),
                IgwReadRequestKind.Graph => ResolveGraph(ip, snapshot, first, second, fallbackStation),
                _ => ResolveDb(ip, snapshot, first, second),
            };

        /// <summary>
        /// IO 点：通用.S7_{ip}.IO.{表名}.{点名}；台账 IoRenames 有 (原表名, 原点名) 映射时点名用映射后的最终名。
        /// </summary>
        public static string ResolveIo(string ip, IgwLedgerSnapshot? snapshot, string tableName, string tagName)
        {
            var finalTag = tagName;
            if (snapshot != null && tableName != null && tagName != null
                && snapshot.IoRenames.TryGetValue((tableName, tagName), out var renamed)
                && !string.IsNullOrEmpty(renamed))
            {
                finalTag = renamed;
            }
            return IgwTagNaming.IoReadAlias(ip, tableName, finalTag);
        }

        /// <summary>
        /// DB 变量：设备按台账 DeviceKind（无记录 → S7Plus）；根组按台账 RootGroup（无记录 → 清洗后块名）；
        /// 路径经解析器数组路径还原后逐段清洗；台账 DbRenames 有 (原块名, 原变量路径) 映射时只替换最后一段（点名）。
        /// </summary>
        public static string ResolveDb(string ip, IgwLedgerSnapshot? snapshot, string blockName, string variablePath)
        {
            var kind = IgwDeviceKind.S7Plus;
            string? root = null;
            if (snapshot != null && blockName != null)
            {
                if (snapshot.DeviceKindByBlock.TryGetValue(blockName, out var ledgerKind))
                {
                    kind = ledgerKind;
                }
                if (snapshot.RootGroupByBlock.TryGetValue(blockName, out var ledgerRoot) && !string.IsNullOrEmpty(ledgerRoot))
                {
                    root = ledgerRoot;
                }
            }
            root ??= IgwTagNaming.SanitizeName(blockName);

            var segments = new List<string>(IgwTagNaming.SplitSegments(IgwTagNaming.NormalizeVariablePath(variablePath)));
            if (segments.Count > 0 && snapshot != null && blockName != null && variablePath != null
                && snapshot.DbRenames.TryGetValue((blockName, variablePath), out var renamed)
                && !string.IsNullOrEmpty(renamed))
            {
                segments[segments.Count - 1] = renamed;
            }

            if (segments.Count == 0)
            {
                return IgwTagNaming.ReadAlias(kind, ip, root == null ? Array.Empty<string>() : new[] { root }, null);
            }

            var groupPath = new List<string>(segments.Count);
            if (root != null)
            {
                groupPath.Add(root);
            }
            groupPath.AddRange(segments.GetRange(0, segments.Count - 1));
            return IgwTagNaming.ReadAlias(kind, ip, groupPath, segments[segments.Count - 1]);
        }

        /// <summary>
        /// GRAPH 容器：通用.S7Plus_{ip}.{工站|_未分组}.{DB}.{member}；工站取台账 StationByDb → <paramref name="fallbackStation"/> → _未分组。
        /// </summary>
        public static string ResolveGraph(string ip, IgwLedgerSnapshot? snapshot, string dbName, string member, string? fallbackStation)
        {
            var trimmedDb = (dbName ?? string.Empty).Trim();
            string? station = null;
            if (snapshot != null && trimmedDb.Length > 0
                && snapshot.StationByDb.TryGetValue(trimmedDb, out var ledgerStation)
                && !string.IsNullOrEmpty(ledgerStation))
            {
                station = ledgerStation;
            }
            station ??= fallbackStation;
            return IgwTagNaming.GraphReadAlias(ip, station!, trimmedDb, member);
        }

        /// <summary>
        /// GRAPH 工站回退表（AI_PLC_GraphStepTag 未删除行 → DB 名 → 工站代码），与注册侧取值口径一致：
        /// GraphStepTagRegistrationJob 每个 DB 只解析一个 target.StationCode，同时写入该 DB 的全部台账行，
        /// 并据此注册到 GraphRequestBuilder.StationGroup(target.StationCode)（清洗后工站，空 → _未分组）；
        /// 重注册时先软删该 (项目, DB) 旧行再插新行。因此按 DB 名（Trim、大小写不敏感，同 GraphRequestBuilder 的 DB 去重）
        /// 分组后取 Id 最大（最新写入）行的 StationCode（Trim；空 → 不入表，由 <see cref="ResolveGraph"/> 落到 _未分组）。
        /// </summary>
        public static IReadOnlyDictionary<string, string> BuildGraphStationFallback(
            IEnumerable<(long Id, string? DbName, string? StationCode)> rows)
        {
            var latest = new Dictionary<string, (long Id, string? Station)>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, dbName, stationCode) in rows ?? Array.Empty<(long, string?, string?)>())
            {
                var db = dbName?.Trim();
                if (string.IsNullOrEmpty(db))
                {
                    continue;
                }
                if (!latest.TryGetValue(db, out var current) || id > current.Id)
                {
                    latest[db] = (id, stationCode?.Trim());
                }
            }

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (db, entry) in latest)
            {
                if (!string.IsNullOrEmpty(entry.Station))
                {
                    result[db] = entry.Station!;
                }
            }
            return result;
        }

        /// <summary>变量路径（不含块名）是否 GRAPH 容器 member：S_NO 或 RT_DATA.EXEC_BITS（忽略大小写，与 DcsIgwConsts.IsGraphContainerPath 同口径）。</summary>
        public static bool IsGraphContainerMember(string? variablePath) =>
            !string.IsNullOrEmpty(variablePath)
            && (string.Equals(variablePath, GraphStepNoMember, StringComparison.OrdinalIgnoreCase)
                || string.Equals(variablePath, GraphExecBitsMember, StringComparison.OrdinalIgnoreCase));
    }
}
