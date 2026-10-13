#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.Agent.Services.Plc_diagnosis;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>
    /// 按项目构建一键排查用的报警定义，补报警文本与工位。
    /// 来源（新旧并行期，spec §9，计划 5 下线旧提取）：报警清单（plc_independent.alarm_catalog，XMLV2 发布后整体替换；
    /// 种类 Timeout / PositionConflict / ExpectedState（FB 契约代入）与 Latched（全局 DB 锁存报警位，已知问题第三批），不按引脚名）∪ 旧提取（FB 调用网络 → 解析锚点 → 四个标准报警引脚），
    /// 按报警位忽略大小写去重、清单优先；项目没有清单时只有旧提取。
    /// 独立管道未启用 / 连接配置不合法（配置状态）→ 视为没有清单；清单读库失败仍整体抛出（读不到 ≠ 没有）。
    /// 进程内只缓存与工位无关的部分（去重后的定义 + 报警文本），
    /// 指纹 = 项目 AI_PLC_BlockCall（带 LogicUnitId）与 IO 点各自的「行数:最大Id」（旧提取与清单定位都依赖），有清单时前面再加「catalog:清单版本」，
    /// 重新发布 / 重新上传 / 导入 IO 点后自动重建。
    /// 工位归属每次调用现查：工位绑定由全量分析 / 手工绑定写入，不随上传变化，不能进缓存。
    /// </summary>
    public sealed class ValveAlarmCatalogService : ISelfScopedAutoInject
    {
        private static readonly ConcurrentDictionary<long, (string Fingerprint, IReadOnlyList<ValveAlarmDefinition> Items)> Cache = new();
        private static int _pipelineUnavailableLogged;

        private readonly IPLC_BlockCallService _blockCalls;
        private readonly IPLC_LogicUnitService _logicUnits;
        private readonly IPLC_BlockService _blocks;
        private readonly IPLC_BlockInterfaceService _interfaces;
        private readonly IPLC_IOTagService _ioTags;
        private readonly IPLC_StationLogicUnitBindingService _luBindings;
        private readonly IPLC_StationBlockBindingService _blockBindings;
        private readonly StationPlcScopeService _scope;
        private readonly PostgresPipelineStore _pipelineStore;
        private readonly ILogger<ValveAlarmCatalogService> _logger;

        /// <summary>构造。</summary>
        public ValveAlarmCatalogService(
            IPLC_BlockCallService blockCalls,
            IPLC_LogicUnitService logicUnits,
            IPLC_BlockService blocks,
            IPLC_BlockInterfaceService interfaces,
            IPLC_IOTagService ioTags,
            IPLC_StationLogicUnitBindingService luBindings,
            IPLC_StationBlockBindingService blockBindings,
            StationPlcScopeService scope,
            PostgresPipelineStore pipelineStore,
            ILogger<ValveAlarmCatalogService> logger)
        {
            _blockCalls = blockCalls;
            _logicUnits = logicUnits;
            _blocks = blocks;
            _interfaces = interfaces;
            _ioTags = ioTags;
            _luBindings = luBindings;
            _blockBindings = blockBindings;
            _scope = scope;
            _pipelineStore = pipelineStore;
            _logger = logger;
        }

        /// <summary>返回项目内已归属工位的阀门报警定义（每条 StationCode 非空，Text 可能为 null）。</summary>
        public async Task<IReadOnlyList<ValveAlarmDefinition>> GetAsync(long projectId, CancellationToken ct)
        {
            var (catalogRows, catalogVersion) = await LoadCatalogAsync(
                () => _pipelineStore.TryGetConnectionString(out var cs, out var reason) ? (true, cs, null) : (false, "", reason),
                (cs, token) => new AlarmCatalogStore(cs).LoadAsync(projectId, token),
                reason =>
                {
                    if (Interlocked.Exchange(ref _pipelineUnavailableLogged, 1) == 0)
                        _logger.LogInformation("[阀门报警] 独立 PLC 管道未启用或配置不可用（{Reason}），不读报警清单，只用旧提取", reason);
                },
                ct);
            var fingerprint = await ComputeFingerprintAsync(projectId);
            // 清单版本之外仍带上调用 / IO 点指纹：清单定位用的逻辑单元 Id 与点表变量表 / 地址会随上传、导入 IO 点变化
            if (catalogRows.Count > 0) fingerprint = "catalog:" + catalogVersion + "|" + fingerprint;
            IReadOnlyList<ValveAlarmDefinition> definitions;
            if (Cache.TryGetValue(projectId, out var cached) && cached.Fingerprint == fingerprint)
            {
                definitions = cached.Items;
            }
            else
            {
                var legacy = await BuildDefinitionsAsync(projectId, ct);
                if (catalogRows.Count > 0)
                {
                    var fromCatalog = await BuildFromCatalogAsync(projectId, catalogRows, ct);
                    var merged = AlarmCatalogMapping.MergeWithLegacy(fromCatalog, legacy);
                    _logger.LogInformation("[阀门报警] 项目 {ProjectId} 报警清单 {Catalog} 条 + 旧提取 {Legacy} 条，按报警位去重后 {Merged} 条（旧提取补入 {Added} 条）",
                        projectId, fromCatalog.Count, legacy.Count, merged.Count, merged.Count - fromCatalog.Count);
                    definitions = merged;
                }
                else
                {
                    _logger.LogInformation("项目 {ProjectId} 无报警清单，只用旧提取（计划 5 下线）", projectId);
                    definitions = legacy;
                }
                Cache[projectId] = (fingerprint, definitions);
            }

            if (definitions.Count == 0) return Array.Empty<ValveAlarmDefinition>();
            ct.ThrowIfCancellationRequested();
            return await AssignStationsAsync(projectId, definitions);
        }

        /// <summary>
        /// 读报警清单。tryConnection 返回 false（管道未启用 / 连接配置不合法）→ 视为没有清单（只回调 onUnavailable，不抛）；
        /// 连接可用时读库的任何异常照常抛出，由调用方按整体报警读取失败处理。
        /// </summary>
        public static async Task<(IReadOnlyList<AlarmCatalogRow> Rows, string? Version)> LoadCatalogAsync(
            Func<(bool Ok, string ConnectionString, string? Reason)> tryConnection,
            Func<string, CancellationToken, Task<(IReadOnlyList<AlarmCatalogRow> Rows, string? Version)>> load,
            Action<string?> onUnavailable,
            CancellationToken ct)
        {
            var (ok, connectionString, reason) = tryConnection();
            if (!ok)
            {
                onUnavailable(reason);
                return (Array.Empty<AlarmCatalogRow>(), null);
            }
            return await load(connectionString, ct);
        }

        /// <summary>指纹只用聚合查询，不拉整行。</summary>
        private async Task<string> ComputeFingerprintAsync(long projectId)
        {
            var callCount = await _blockCalls.GetDb().AsQueryable()
                .Where(c => c.ProjectId == projectId && c.IsDeleted == false && c.LogicUnitId != null)
                .CountAsync();
            var callMaxId = callCount == 0
                ? 0L
                : await _blockCalls.GetDb().AsQueryable()
                    .Where(c => c.ProjectId == projectId && c.IsDeleted == false && c.LogicUnitId != null)
                    .MaxAsync(c => c.Id);

            var tagCount = await _ioTags.GetDb().AsQueryable()
                .Where(t => t.ProjectId == projectId && t.IsDeleted == false)
                .CountAsync();
            var tagMaxId = tagCount == 0
                ? 0L
                : await _ioTags.GetDb().AsQueryable()
                    .Where(t => t.ProjectId == projectId && t.IsDeleted == false)
                    .MaxAsync(t => t.Id);

            return $"{callCount}:{callMaxId}|{tagCount}:{tagMaxId}";
        }

        /// <summary>
        /// 报警清单路径：清单行 → 定义（逻辑单元按清单行发布时回查到的 LogicUnitId 定位，不按网络号现查）+ 报警文本，不带 StationCode。
        /// 清单按报警位主键唯一，不需再去重。
        /// </summary>
        private async Task<IReadOnlyList<ValveAlarmDefinition>> BuildFromCatalogAsync(
            long projectId, IReadOnlyList<AlarmCatalogRow> rows, CancellationToken ct)
        {
            var blocks = await _blocks.GetListAsync(b => b.ProjectId == projectId && b.IsDeleted == false);
            var ioTags = await LoadIoTagsByNameAsync(projectId);

            // 只查清单行指向的逻辑单元，且只取三列（RawContent 很大）；逻辑单元须在本项目未删的块下
            var blockNameById = blocks
                .Where(b => !string.IsNullOrWhiteSpace(b.BlockName))
                .ToDictionary(b => b.Id, b => b.BlockName.Trim());
            var wanted = rows.Where(r => r.LogicUnitId.HasValue).Select(r => r.LogicUnitId!.Value).Distinct().ToList();
            var units = new Dictionary<long, LogicUnitLocation>();
            foreach (var chunk in wanted.Chunk(500))
            {
                ct.ThrowIfCancellationRequested();
                var ids = chunk.ToList();
                var found = await _logicUnits.GetDb().AsQueryable()
                    .Where(l => ids.Contains(l.Id) && l.IsDeleted == false)
                    .Select(l => new { l.Id, l.BlockId, l.NetworkId })
                    .ToListAsync();
                foreach (var lu in found)
                {
                    if (lu.BlockId is not long blockId || !blockNameById.TryGetValue(blockId, out var blockName)) continue;
                    units[lu.Id] = new LogicUnitLocation(lu.Id, blockId, blockName, lu.NetworkId);
                }
            }

            var definitions = AlarmCatalogMapping.ToDefinitions(projectId, rows, ioTags, units);
            // 全局 DB 成员注释优先；没有时用清单行带的后备文本（经 FB 输出引脚的锁存报警位：FB 形参注释）；读取写法换成解析器写法
            if (definitions.Count > 0) definitions = await WithMemberInfoAsync(definitions, blocks);

            // 清单行无 LogicUnitId（旧清单 / 发布时回查不唯一）或指向的逻辑单元已不存在：只按块名归属工位，需重新发布
            _logger.LogInformation("[阀门报警] 项目 {ProjectId} 按报警清单重建报警定义：清单 {Rows} 行，带逻辑单元 Id {WithId} 行，定位到逻辑单元 {Located} 条",
                projectId, rows.Count, wanted.Count == 0 ? 0 : rows.Count(r => r.LogicUnitId.HasValue), definitions.Count(d => d.LogicUnitId != 0));
            return definitions;
        }

        /// <summary>项目点表按点名（忽略大小写、去首尾空格）建字典，重名取第一条。</summary>
        private async Task<Dictionary<string, PLC_IOTagEntity>> LoadIoTagsByNameAsync(long projectId) =>
            (await _ioTags.GetListAsync(t => t.ProjectId == projectId && t.IsDeleted == false))
                .Where(t => !string.IsNullOrWhiteSpace(t.TagName))
                .GroupBy(t => t.TagName.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        /// <summary>旧提取路径（无报警清单的项目，并行期保留）：去重后的定义 + 报警文本，不带 StationCode。</summary>
        private async Task<IReadOnlyList<ValveAlarmDefinition>> BuildDefinitionsAsync(long projectId, CancellationToken ct)
        {
            var calls = await _blockCalls.GetListAsync(c => c.ProjectId == projectId && c.IsDeleted == false && c.LogicUnitId != null);
            var luIds = calls.Select(c => c.LogicUnitId!.Value).Distinct().ToList();
            if (luIds.Count == 0) return Array.Empty<ValveAlarmDefinition>();

            var blocks = await _blocks.GetListAsync(b => b.ProjectId == projectId && b.IsDeleted == false);
            var blockById = blocks.ToDictionary(b => b.Id);
            var ioTags = await LoadIoTagsByNameAsync(projectId);

            var definitions = new List<ValveAlarmDefinition>();
            var parseFailures = 0;
            string? firstParseError = null;
            foreach (var chunk in luIds.Chunk(500))
            {
                ct.ThrowIfCancellationRequested();
                var ids = chunk.ToList();
                var units = await _logicUnits.GetListAsync(l => ids.Contains(l.Id) && l.IsDeleted == false);
                foreach (var unit in units)
                {
                    if (unit.BlockId is not long blockId || !blockById.TryGetValue(blockId, out var block)) continue;
                    if (string.IsNullOrWhiteSpace(unit.RawContent)) continue;
                    List<PldAnchorBlock> anchors;
                    try
                    {
                        anchors = SiemensFlgNetConstraintExtractor.ExtractAnchorBlocks(unit.RawContent);
                    }
                    catch (Exception ex)
                    {
                        // 解析不了的网络不产报警，不影响其它网络；但要计数，避免解析器回归时清单静默变空
                        parseFailures++;
                        firstParseError ??= $"LU {unit.Id}: {ex.Message}";
                        continue;
                    }
                    definitions.AddRange(ValveAlarmExtractor.Extract(
                        projectId, unit.Id, blockId, block.BlockName ?? string.Empty, unit.NetworkId, anchors, ioTags));
                }
            }

            if (parseFailures > 0)
            {
                _logger.LogWarning("[阀门报警] 项目 {ProjectId} 有 {ParseFailures} 个网络锚点解析失败，这些网络不产报警。首个错误：{FirstError}",
                    projectId, parseFailures, firstParseError);
            }

            // 先排序再去重：同一报警位被多处调用时，取哪一条不能依赖数据库返回顺序
            var unique = definitions
                .OrderBy(d => d.LogicUnitId)
                .ThenBy(d => d.ValveInstance, StringComparer.Ordinal)
                .GroupBy(d => d.AlarmVariable, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            if (unique.Count > 0) unique = await WithMemberInfoAsync(unique, blocks);

            _logger.LogInformation("[阀门报警] 项目 {ProjectId} 报警定义重建完成：调用网络 {Units} 个，去重后报警 {Total} 条，锚点解析失败 {ParseFailures} 个",
                projectId, luIds.Count, unique.Count, parseFailures);
            return unique;
        }

        /// <summary>每次调用现查工位范围、工位绑定与块工位，返回归属到工位的定义。</summary>
        private async Task<IReadOnlyList<ValveAlarmDefinition>> AssignStationsAsync(long projectId, IReadOnlyList<ValveAlarmDefinition> definitions)
        {
            var allowed = await _scope.GetAllowedStationsAsync(projectId);
            var luBindings = await _luBindings.GetListAsync(b => b.ProjectId == projectId && b.IsDeleted == false);
            var blockBindings = await _blockBindings.GetListAsync(b => b.ProjectId == projectId && b.IsDeleted == false);

            var blockIds = definitions.Where(d => d.BlockId.HasValue).Select(d => d.BlockId!.Value).Distinct().ToList();
            var blockStationById = new Dictionary<long, string?>();
            foreach (var chunk in blockIds.Chunk(500))
            {
                var ids = chunk.ToList();
                var rows = await _blocks.GetListAsync(b => ids.Contains(b.Id) && b.ProjectId == projectId && b.IsDeleted == false);
                foreach (var row in rows) blockStationById[row.Id] = row.StationCode;
            }

            var result = new List<ValveAlarmDefinition>(definitions.Count);
            var skipped = 0;
            foreach (var d in definitions)
            {
                var blockStation = d.BlockId.HasValue && blockStationById.TryGetValue(d.BlockId.Value, out var s) ? s : null;
                var station = ValveAlarmStationResolver.Resolve(d.LogicUnitId, d.BlockId, d.BlockName, luBindings, blockBindings, blockStation, allowed);
                if (station == null)
                {
                    skipped++;
                    continue;
                }
                result.Add(d with { StationCode = station });
            }

            _logger.LogInformation("[阀门报警] 项目 {ProjectId} 报警清单：共 {Total} 条，归属工位 {Assigned} 条，无工位跳过 {Skipped} 条",
                projectId, definitions.Count, result.Count, skipped);
            return result;
        }

        /// <summary>按报警位所在 DB 的接口行补报警文本与读取写法（见 <see cref="ApplyMemberInfo"/>）。</summary>
        private async Task<List<ValveAlarmDefinition>> WithMemberInfoAsync(List<ValveAlarmDefinition> definitions, List<PLC_BlockEntity> blocks)
        {
            var blockIdByName = blocks
                .Where(b => !string.IsNullOrWhiteSpace(b.BlockName))
                .GroupBy(b => b.BlockName.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
            var result = new List<ValveAlarmDefinition>(definitions.Count);
            foreach (var group in definitions.GroupBy(d => d.AlarmVariable.Split('.', 2)[0], StringComparer.OrdinalIgnoreCase))
            {
                if (!blockIdByName.TryGetValue(group.Key, out var dbId)) { result.AddRange(group); continue; }
                var members = await _interfaces.GetListAsync(i => i.BlockId == dbId && i.IsDeleted == false);
                result.AddRange(ApplyMemberInfo(group.ToList(), members.Select(m => (m.VariablePath, (string?)m.Comment))));
            }
            // 保持原顺序
            var order = definitions.Select((d, i) => (d, i)).GroupBy(x => x.d.AlarmVariable, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Min(x => x.i), StringComparer.OrdinalIgnoreCase);
            return result.OrderBy(d => order.TryGetValue(d.AlarmVariable, out var i) ? i : int.MaxValue).ToList();
        }

        /// <summary>
        /// 同一 DB 的报警定义 + 该 DB 的接口行（解析器 VariablePath 与注释）：
        /// 清单写法按 <see cref="AlarmVariablePaths"/> 换成解析器写法，记为 ReadVariable（读别名、台账改名映射、老网关点名都以它为准）；
        /// 报警文本取该行注释（HMI 显示文字），没有时保留定义原有文本（清单行的 FB 形参注释后备 / null）。对不上接口行的照原写法读。
        /// </summary>
        public static List<ValveAlarmDefinition> ApplyMemberInfo(IReadOnlyList<ValveAlarmDefinition> definitions,
            IEnumerable<(string? Path, string? Comment)> members)
        {
            var rows = members.Where(m => !string.IsNullOrWhiteSpace(m.Path)).Select(m => (Path: m.Path!.Trim(), m.Comment)).ToList();
            var index = AlarmVariablePaths.Index(rows.Select(r => r.Path));
            var commentByPath = rows.Where(r => !string.IsNullOrWhiteSpace(r.Comment))
                .GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Comment!.Trim(), StringComparer.OrdinalIgnoreCase);
            var result = new List<ValveAlarmDefinition>(definitions.Count);
            foreach (var d in definitions)
            {
                var parts = d.AlarmVariable.Split('.', 2);
                if (parts.Length < 2) { result.Add(d); continue; } // 清单里的报警位不一定是 DB 路径
                var parserPath = AlarmVariablePaths.Find(index, parts[1]);
                if (parserPath == null) { result.Add(d); continue; }
                result.Add(d with
                {
                    ReadVariable = parts[0] + "." + parserPath,
                    Text = commentByPath.TryGetValue(parserPath, out var comment) ? comment : d.Text,
                });
            }
            return result;
        }
    }
}
