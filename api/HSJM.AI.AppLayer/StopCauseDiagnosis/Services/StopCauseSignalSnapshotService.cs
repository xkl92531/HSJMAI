using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Models;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.Utility;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>
    /// 负责根据符号全路径读取在线值并组装停机诊断快照。
    /// 当前先完成采样与上下文组装，后续在此基础上接入 AST 求值与诊断规则。
    /// </summary>
    public class StopCauseSignalSnapshotService : ISelfScopedAutoInject
    {
        // ── 进程内静态缓存：避免每 2 秒重复查询静态配置数据 ──
        private static readonly ConcurrentDictionary<long, (PLC_ProjectEntity Project, DateTime ExpiresAt)> _projectCache = new();
        private static readonly ConcurrentDictionary<long, (List<PLC_IOTagEntity> Tags, DateTime ExpiresAt)> _ioTagsCache = new();
        private const int ProjectCacheTtlSeconds = 300;
        private const int IoTagsCacheTtlSeconds = 300;
        private static readonly ConcurrentDictionary<string, (IReadOnlyCollection<long> Projects, DateTime ExpiresAt)> _blockOwnersCache = new(StringComparer.OrdinalIgnoreCase);
        private const int BlockOwnersCacheTtlSeconds = 300;
        private readonly IIotService _iotService;
        private readonly IPLC_ProjectService _projectService;
        private readonly IPLC_StationAnchorBindingService _stationAnchorBindingService;
        private readonly IPLC_IOTagService _ioTagService;
        private readonly IPLC_StationShadowSignalService _stationShadowSignalService;
        private readonly IPLC_VariableAccessService _variableAccessService;
        private readonly IPLC_BlockService _blockService;

        public StopCauseSignalSnapshotService(
            IIotService iotService,
            IPLC_ProjectService projectService,
            IPLC_StationAnchorBindingService stationAnchorBindingService,
            IPLC_IOTagService ioTagService,
            IPLC_StationShadowSignalService stationShadowSignalService,
            IPLC_VariableAccessService variableAccessService,
            IPLC_BlockService blockService = null)
        {
            _blockService = blockService;
            _iotService = iotService;
            _projectService = projectService;
            _stationAnchorBindingService = stationAnchorBindingService;
            _ioTagService = ioTagService;
            _stationShadowSignalService = stationShadowSignalService;
            _variableAccessService = variableAccessService;
        }

        public async Task<AcquireDiagnosticSnapshotResultDto> AcquireAsync(AcquireDiagnosticSnapshotRequestDto request)
        {
            // 项目缓存（5 分钟 TTL）
            PLC_ProjectEntity project;
            if (!_projectCache.TryGetValue(request.ProjectId, out var projEntry) || DateTime.Now >= projEntry.ExpiresAt)
            {
                project = _projectService.GetById(request.ProjectId);
                if (project != null)
                    _projectCache[request.ProjectId] = (project, DateTime.Now.AddSeconds(ProjectCacheTtlSeconds));
            }
            else
            {
                project = projEntry.Project;
            }

            if (project == null)
            {
                throw new InvalidOperationException($"未找到项目: {request.ProjectId}");
            }

            var requestedSymbols = await ResolveRequestedSymbolsAsync(request);
            var timestamp = DateTime.Now;
            var primaryDevice = BuildDeviceName(project.StationId, "S7");
            var fallbackDevice = BuildDeviceName(project.StationId, "Plus");

            List<PLC_ReadTagResult> liveResults;
            if (request.UseSimulation)
            {
                liveResults = requestedSymbols
                    .Select(symbol => new PLC_ReadTagResult
                    {
                        TagName = symbol,
                        Value = request.SimulatedCurrentValues != null && request.SimulatedCurrentValues.TryGetValue(symbol, out var simVal) ? simVal : null,
                        IsGood = true,
                    })
                    .ToList();
            }
            else
            {
                async Task<List<PLC_ReadTagResult>> ReadLiveAsync(IReadOnlyList<string> symbols)
                {
                    // 预计算一次，传入两次 ReadTagsAsync（primary + fallback）来复用
                    var ioTagMap = await BuildIoTagMapAsync(request.ProjectId, symbols);
                    var anchorTypeMap = request.AnchorTypeMap != null && request.AnchorTypeMap.Count > 0
                        ? request.AnchorTypeMap
                        : await BuildAnchorTypeMapAsync(request.ProjectId, request.StationCode, symbols);

                    var rows = await ReadWithFallbackAsync(
                        project.IotUrl,
                        primaryDevice,
                        fallbackDevice,
                        request.ProjectId,
                        request.StationCode,
                        symbols,
                        ioTagMap,
                        anchorTypeMap);
                    return await RerouteCrossPlcAsync(request.ProjectId, symbols, rows);
                }

                // 2026-09-23 停机诊断内同一批点只读一次（作用域由诊断入口开启，作用域外行为不变）
                var readScope = StopCauseSignalReadScope.Current;
                liveResults = readScope != null
                    ? await readScope.ReadAsync(request.ProjectId, requestedSymbols, ReadLiveAsync)
                    : await ReadLiveAsync(requestedSymbols);
            }

            var currentValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var signalQuality = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var symbol in requestedSymbols)
            {
                var result = liveResults.FirstOrDefault(it => string.Equals(it.TagName, symbol, StringComparison.OrdinalIgnoreCase));
                currentValues[symbol] = result?.Value;
                signalQuality[symbol] = result == null
                    ? "Missing"
                    : result.IsGood ? "Good" : (string.IsNullOrWhiteSpace(result.ErrorMessage) ? "Bad" : result.ErrorMessage);
            }

            var previousValues = new Dictionary<string, object>(request.PreviousValues ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            var lastChangeTimes = new Dictionary<string, DateTime?>(request.LastChangeTimes ?? new Dictionary<string, DateTime?>(), StringComparer.OrdinalIgnoreCase);
            foreach (var symbol in requestedSymbols)
            {
                currentValues.TryGetValue(symbol, out var currentValue);
                previousValues.TryGetValue(symbol, out var previousValue);
                if (!lastChangeTimes.ContainsKey(symbol))
                {
                    lastChangeTimes[symbol] = timestamp;
                    continue;
                }

                if (!ValuesEqual(previousValue, currentValue))
                {
                    lastChangeTimes[symbol] = timestamp;
                }
            }

            var relatedAnchors = (request.RelatedAnchors ?? new List<string>())
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new AcquireDiagnosticSnapshotResultDto
            {
                ProjectId = request.ProjectId,
                StationCode = request.StationCode,
                DeviceName = primaryDevice,
                FallbackDeviceName = fallbackDevice,
                RequestedSymbols = requestedSymbols,
                MissingSymbols = requestedSymbols.Where(symbol => !liveResults.Any(it => string.Equals(it.TagName, symbol, StringComparison.OrdinalIgnoreCase))).ToList(),
                ExecutionContext = new StopCauseExecutionContext
                {
                    Timestamp = timestamp,
                    StationMode = request.StationMode,
                    CurrentValues = currentValues,
                    PreviousValues = previousValues,
                    SignalQuality = signalQuality,
                    PreviousSignalQuality = new Dictionary<string, string>(request.PreviousSignalQuality ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
                    SourceSymbols = requestedSymbols.ToList()
                },
                Snapshot = new DiagnosticSnapshot
                {
                    StationCode = request.StationCode,
                    SnapshotTime = timestamp,
                    CurrentValues = currentValues,
                    PreviousValues = previousValues,
                    LastChangeTimes = lastChangeTimes,
                    RelatedAnchors = relatedAnchors,
                    SignalQuality = signalQuality,
                    SourceSymbols = requestedSymbols.ToList(),
                    MissingSymbols = requestedSymbols.Where(symbol => !liveResults.Any(it => string.Equals(it.TagName, symbol, StringComparison.OrdinalIgnoreCase))).ToList()
                }
            };
        }

        private async Task<List<string>> ResolveRequestedSymbolsAsync(AcquireDiagnosticSnapshotRequestDto request)
        {
            var symbols = new List<string>();
            if (request.SymbolPaths?.Count > 0)
            {
                symbols.AddRange(request.SymbolPaths);
            }

            if (request.IncludeStationAnchorVariables && !string.IsNullOrWhiteSpace(request.StationCode))
            {
                var bindings = await _stationAnchorBindingService.GetListAsync(it =>
                    it.IsDeleted == false &&
                    it.ProjectId == request.ProjectId &&
                    it.StationCode == request.StationCode);
                symbols.AddRange(bindings
                    .Select(it => it.VariableName)
                    .Where(it => !string.IsNullOrWhiteSpace(it)));
            }

            return symbols
                .Select(NormalizeSymbolPath)
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private async Task<List<PLC_ReadTagResult>> ReadWithFallbackAsync(
            string host,
            string primaryDevice,
            string fallbackDevice,
            long projectId,
            string stationCode,
            IReadOnlyList<string> symbols,
            Dictionary<string, List<string>> ioTagMap,
            Dictionary<string, string> anchorTypeMap)
        {
            if (symbols == null || symbols.Count == 0)
            {
                return new List<PLC_ReadTagResult>();
            }

            // 2026-09-18 全 IGW 迁移：ioturl 指向新 IGW（非 2500 老网关）时整链改走
            // ReadExactDataAsync 精确读——老 /DriverInfo/ReadTag 在 IGW 上无路由必 404
            // （响应壳 null 曾令本链 NRE，工位影子巡检两台 PLC 全部 11 个工位每轮崩溃、
            // 信号历史零写入、监控页满屏异常的根因）。精确读内部已处理：
            // 工步位 EXEC_BITS 元素路径→数组容器整组读回本地展开；普通符号路径→
            // 通用.S1500_{ip}.{path} 别名 ReadTagsAsList；缺失/坏点按 Unknown 降级。
            // 一次读全量，不再做 _S7/_Plus 两跳回退（IGW 下无此设备模型）。
            if (!DcsIgwConsts.IsLegacyIotGatewayUrl(host))
            {
                return await ReadExactViaIgwAsync(projectId, stationCode, symbols, ioTagMap);
            }

            Stopwatch watch = Stopwatch.StartNew();
            var primary = await ReadTagsAsync(host, primaryDevice, projectId, stationCode, symbols, ioTagMap, anchorTypeMap);
            watch.Stop();
            WriteLogHelper.WriteLogsAsync($"{stationCode},S7读取{symbols.Count}个 耗时{watch.ElapsedMilliseconds} ms,","读取信号计时");
            var merged = primary.Where(it => it.IsGood).ToList();
            var missing = symbols.Where(symbol => !primary.Any(it => string.Equals(it.TagName, symbol, StringComparison.OrdinalIgnoreCase) && it.IsGood)).ToList();
            if (missing.Count == 0)
            {
                return merged;
            }
            watch.Restart();
          
            var fallback = await ReadTagsAsync(host, fallbackDevice, projectId, stationCode, missing, ioTagMap, anchorTypeMap);
            watch.Stop();
            WriteLogHelper.WriteLogsAsync($"{stationCode},s7plus读取{symbols.Count}个 耗时{watch.ElapsedMilliseconds} ms,", "读取信号计时");
            merged.AddRange(fallback);
            return merged
                .GroupBy(it => it.TagName, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(it => it.IsGood).First())
                .ToList();
        }

        /// <summary>
        /// 2026-09-18 全 IGW 迁移：工位影子快照读链改走 ReadExactDataAsync 精确读。
        /// 符号按 {Block}.{Path} 拆分为精确读请求（RequestKey=canonical 符号），内部已完成：
        /// ①iotag/块接口候选校验（EXEC_BITS 运行时符号有块接口回退）；②工步元素路径→
        /// 数组容器整组读回本地展开；③普通符号→通用.S1500_{ip}.{path} 别名 ReadTagsAsList；
        /// ④缺失/坏点按 Unknown+ErrorCode 降级。无块段的 IOTag 短名按 I/O 表换成 TableName.TagName 再读；
        /// I/O 表里也没有的无块段符号才标坏（IGW 无绝对地址通道）。
        /// </summary>
        private async Task<List<PLC_ReadTagResult>> ReadExactViaIgwAsync(
            long projectId,
            string stationCode,
            IReadOnlyList<string> symbols,
            Dictionary<string, List<string>> ioTagMap)
        {
            var results = new List<PLC_ReadTagResult>();
            var requests = BuildIgwExactReadRequests(symbols, ioTagMap, out var noBlockSymbols);

            if (requests.Count > 0)
            {
                var exact = await _variableAccessService.ReadExactDataAsync(projectId, requests, CancellationToken.None);
                foreach (var item in exact)
                {
                    results.Add(new PLC_ReadTagResult
                    {
                        TagName = item.RequestKey,
                        Value = item.Value,
                        IsGood = item.Quality == PLC_ExactReadQuality.Good,
                        ErrorMessage = item.ErrorCode,
                    });
                }
            }

            foreach (var symbol in noBlockSymbols)
            {
                results.Add(new PLC_ReadTagResult
                {
                    TagName = symbol,
                    Value = null,
                    IsGood = false,
                    ErrorMessage = "IgwNoBlockSegment",
                });
            }

            // 与老路径同构：同一 canonical 取最好结果
            return results
                .GroupBy(it => it.TagName, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(it => it.IsGood).First())
                .ToList();
        }

        /// <summary>
        /// 跨 PLC 改道读（2026-10-14）：本工程读不到、块又只在另一个工程里的符号（如 PLC1 引用的
        /// DB182_PLC1&lt;-&gt;PLC2），改到块所在工程精确读一次，读到的好值替换原来的坏行。规则见 CrossPlcRules。
        /// </summary>
        private async Task<List<PLC_ReadTagResult>> RerouteCrossPlcAsync(
            long projectId,
            IReadOnlyList<string> symbols,
            List<PLC_ReadTagResult> rows)
        {
            rows ??= new List<PLC_ReadTagResult>();
            var good = rows
                .Where(r => r != null && r.IsGood && !string.IsNullOrWhiteSpace(r.TagName))
                .Select(r => r.TagName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unread = (symbols ?? Array.Empty<string>())
                .Select(NormalizeSymbolPath)
                .Where(s => !string.IsNullOrWhiteSpace(s) && !good.Contains(s) && CrossPlcRules.BlockNameOf(s) != null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            // 没有块服务（单测手工构造）时不做跨 PLC 改道，行为与改道前一致
            if (unread.Count == 0 || _blockService == null)
            {
                return rows;
            }

            try
            {
                var owners = await LoadBlockOwnersAsync(unread.Select(CrossPlcRules.BlockNameOf).Where(n => n != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
                var plan = CrossPlcRules.PlanReroute(unread, projectId, owners);
                foreach (var (ownerProjectId, ownerSymbols) in plan)
                {
                    var requests = ownerSymbols
                        .Select(s => new PLC_ExactReadRequest(s, CrossPlcRules.BlockNameOf(s), CrossPlcRules.MemberPathOf(s)))
                        .Where(r => !string.IsNullOrWhiteSpace(r.BlockName) && !string.IsNullOrWhiteSpace(r.VariablePath))
                        .ToList();
                    if (requests.Count == 0)
                    {
                        continue;
                    }

                    var exact = await _variableAccessService.ReadExactDataAsync(ownerProjectId, requests, CancellationToken.None);
                    var rerouted = exact.Where(item => item.Quality == PLC_ExactReadQuality.Good).ToList();
                    foreach (var item in rerouted)
                    {
                        rows.RemoveAll(r => string.Equals(r?.TagName, item.RequestKey, StringComparison.OrdinalIgnoreCase));
                        rows.Add(new PLC_ReadTagResult
                        {
                            TagName = item.RequestKey,
                            Value = item.Value,
                            IsGood = true,
                        });
                    }

                    if (rerouted.Count > 0)
                    {
                        WriteLogHelper.WriteLogsAsync(
                            $"[跨PLC读取] 项目{projectId} 读不到的 {requests.Count} 个对端块符号改由项目{ownerProjectId}读取，成功 {rerouted.Count} 个",
                            "读取信号计时");
                    }
                }
            }
            catch (Exception ex)
            {
                // 改道读失败不影响本工程已读到的值，读不到的照旧按未知处理
                WriteLogHelper.WriteLogsAsync($"[跨PLC读取] 项目{projectId} 改道读取失败: {ex.Message}", "读取信号计时");
            }

            return rows;
        }

        /// <summary>块名 → 含有该块的工程（未删除），5 分钟缓存（工位影子巡检每轮都会走到这里）</summary>
        private async Task<IReadOnlyDictionary<string, IReadOnlyCollection<long>>> LoadBlockOwnersAsync(IReadOnlyList<string> blockNames)
        {
            var result = new Dictionary<string, IReadOnlyCollection<long>>(StringComparer.OrdinalIgnoreCase);
            var missing = new List<string>();
            foreach (var name in blockNames)
            {
                if (_blockOwnersCache.TryGetValue(name, out var cached) && DateTime.Now < cached.ExpiresAt)
                {
                    result[name] = cached.Projects;
                }
                else
                {
                    missing.Add(name);
                }
            }

            if (missing.Count > 0)
            {
                var rows = await _blockService.GetListAsync(block =>
                    block.IsDeleted == false
                    && block.ProjectId != null
                    && missing.Contains(block.BlockName));
                foreach (var name in missing)
                {
                    var projects = rows
                        .Where(block => string.Equals(block.BlockName?.Trim(), name, StringComparison.OrdinalIgnoreCase))
                        .Select(block => block.ProjectId!.Value)
                        .Distinct()
                        .ToList();
                    _blockOwnersCache[name] = (projects, DateTime.Now.AddSeconds(BlockOwnersCacheTtlSeconds));
                    result[name] = projects;
                }
            }

            return result;
        }

        internal static List<PLC_ExactReadRequest> BuildIgwExactReadRequests(
            IReadOnlyList<string> symbols,
            Dictionary<string, List<string>> ioTagMap,
            out List<string> unresolvedSymbols)
        {
            var requests = new List<PLC_ExactReadRequest>();
            unresolvedSymbols = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var symbol in symbols ?? Array.Empty<string>())
            {
                var canonical = NormalizeSymbolPath(symbol);
                if (string.IsNullOrWhiteSpace(canonical) || !seen.Add(canonical))
                {
                    continue;
                }

                // 没有块名的全局 I/O 变量（如 OP030_CYL05_W）：按 I/O 表换成「表名.变量名」再读，
                // 与老网关路径（BuildReadTagCandidates）和 ReadTagsByFixedDeviceAsync 的 IGW 分支一致。
                // RequestKey 仍用原符号，读回的结果直接对得上 AST 里的叶子名
                // 2026-10-15：先查 I/O 表，再看有没有块段——IQ 表点名常带地址里的小数点（I106.2_SG），
                // 先按小数点拆会变成「块 I106 + 成员 2_SG」而读不到。I/O 表只按点名精确匹配，DB 路径不会误中
                var fullPath = canonical;
                if (ioTagMap != null
                    && ioTagMap.TryGetValue(canonical, out var tableTagCandidates)
                    && tableTagCandidates?.FirstOrDefault(it => !string.IsNullOrWhiteSpace(it)) is { } tableTag)
                {
                    fullPath = tableTag.Trim();
                }
                var firstDot = fullPath.IndexOf('.');

                if (firstDot <= 0 || firstDot == fullPath.Length - 1)
                {
                    unresolvedSymbols.Add(canonical);
                    continue;
                }

                requests.Add(new PLC_ExactReadRequest(
                    canonical,
                    fullPath.Substring(0, firstDot),
                    fullPath.Substring(firstDot + 1)));
            }

            return requests;
        }

        private async Task<List<PLC_ReadTagResult>> ReadTagsAsync(
            string host,
            string deviceName,
            long projectId,
            string stationCode,
            IReadOnlyList<string> symbols,
            Dictionary<string, List<string>> ioTagMap,
            Dictionary<string, string> anchorTypeMap)
        {
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(deviceName) || symbols == null || symbols.Count == 0)
            {
                return new List<PLC_ReadTagResult>();
            }

            // 依赖调用方预计算并传入，不再重复查询
            Stopwatch watch = Stopwatch.StartNew();
            var readTags = new List<string>();
            var tagToCanonicalMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var symbol in symbols)
            {
                var canonical = NormalizeSymbolPath(symbol);
                if (string.IsNullOrWhiteSpace(canonical))
                {
                    continue;
                }

                var candidates = BuildReadTagCandidates(canonical, ioTagMap, anchorTypeMap);

                // 如果候选是全路径变量名（非 IOTag 映射），尝试展开数组路径
                // 例如 DB210.Status.Robot[2].X → DB210.Status.Robot.Robot[2].X
                // 以匹配 IOT 中的完整 UDT 成员路径
                var expandedCandidates = new List<string>();
                foreach (var readTag in candidates)
                {
                    var expanded = ExpandArrayPath(readTag);
                    if (!string.Equals(expanded, readTag, StringComparison.OrdinalIgnoreCase))
                    {
                        expandedCandidates.Add(expanded);
                    }
                }
                if (expandedCandidates.Count > 0)
                {
                    candidates = expandedCandidates;
                }

                foreach (var readTag in candidates)
                {
                    if (!tagToCanonicalMap.ContainsKey(readTag))
                    {
                        tagToCanonicalMap[readTag] = canonical;
                        readTags.Add(readTag);
                    }
                }
            }

            if (readTags.Count == 0)
            {
                return new List<PLC_ReadTagResult>();
            }
            watch.Restart();
            var response = await _iotService.ReadTags(host, new PLC_ReadTagParame
            {
                DeviceName = deviceName,
                TagList = readTags
            });
            watch.Stop();
            WriteLogHelper.WriteLogsAsync($"{stationCode},读取{readTags.Count}个 耗时{watch.ElapsedMilliseconds} ms,", "读取信号计时");
            if (!response.IsSuccess || response.BackResult == null)
            {
                return new List<PLC_ReadTagResult>();
            }

            var normalizedResults = new List<PLC_ReadTagResult>();
            for (var index = 0; index < response.BackResult.Count && index < readTags.Count; index++)
            {
                var readTag = readTags[index];
                if (!tagToCanonicalMap.TryGetValue(readTag, out var canonicalTag))
                {
                    continue;
                }

                var item = response.BackResult[index];
                if (item == null)
                {
                    continue;
                }

                item.OldTagName = string.IsNullOrWhiteSpace(item.TagName) ? readTag : item.TagName;
                item.TagName = canonicalTag;
                normalizedResults.Add(item);
            }

            return normalizedResults
                .GroupBy(it => it.TagName, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(it => it.IsGood).First())
                .ToList();
        }

        private static string BuildDeviceName(string stationId, string suffix)
        {
            return string.IsNullOrWhiteSpace(stationId)
                ? string.Empty
                : $"{stationId}_{suffix}";
        }

        private static string NormalizeSymbolPath(string symbolPath)
        {
            return (symbolPath ?? string.Empty)
                .Trim()
                .Trim('"')
                .Replace("\"", string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        private async Task<Dictionary<string, List<string>>> BuildIoTagMapAsync(long projectId, IReadOnlyList<string> symbols)
        {
            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (projectId <= 0 || symbols == null || symbols.Count == 0)
            {
                return map;
            }

            // IO 标签列表缓存（5 分钟 TTL）
            List<PLC_IOTagEntity> ioTags;
            if (!_ioTagsCache.TryGetValue(projectId, out var ioEntry) || DateTime.Now >= ioEntry.ExpiresAt)
            {
                ioTags = await _ioTagService.GetDb().AsQueryable()
                    .Where(it => it.IsDeleted == false && it.ProjectId == projectId)
                    .Where(it => !string.IsNullOrWhiteSpace(it.TagName) && !string.IsNullOrWhiteSpace(it.TableName))
                    .ToListAsync();
                _ioTagsCache[projectId] = (ioTags, DateTime.Now.AddSeconds(IoTagsCacheTtlSeconds));
            }
            else
            {
                ioTags = ioEntry.Tags;
            }

            var canonicalSymbols = symbols
                .Select(NormalizeSymbolPath)
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var symbol in canonicalSymbols)
            {
                var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var tailName = symbol.Contains('.') ? symbol.Split('.').Last() : symbol;
                var allowTailMatch = !IsStructuredRuntimeSymbol(symbol);

                foreach (var tag in ioTags)
                {
                    if (string.Equals(tag.TagName?.Trim(), symbol, StringComparison.OrdinalIgnoreCase)
                        || (allowTailMatch && string.Equals(tag.TagName?.Trim(), tailName, StringComparison.OrdinalIgnoreCase)))
                    {
                        candidates.Add($"{tag.TableName.Trim()}.{tag.TagName.Trim()}");
                    }
                }

                if (candidates.Count > 0)
                {
                    map[symbol] = candidates.ToList();
                }
            }

            return map;
        }

        private async Task<Dictionary<string, string>> BuildAnchorTypeMapAsync(long projectId, string stationCode, IReadOnlyList<string> symbols)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (projectId <= 0 || string.IsNullOrWhiteSpace(stationCode) || symbols == null || symbols.Count == 0)
            {
                return map;
            }

            var canonicalSet = symbols
                .Select(NormalizeSymbolPath)
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var stationSignals = await _stationShadowSignalService.GetDb().AsQueryable()
                .Where(it => it.IsDeleted == false && it.ProjectId == projectId && it.StationCode == stationCode && it.Enabled == true)
                .ToListAsync();

            foreach (var signal in stationSignals)
            {
                var canonical = NormalizeSymbolPath(signal.VariableName);
                if (string.IsNullOrWhiteSpace(canonical) || !canonicalSet.Contains(canonical))
                {
                    continue;
                }

                if (!map.ContainsKey(canonical) && !string.IsNullOrWhiteSpace(signal.AnchorType))
                {
                    map[canonical] = signal.AnchorType.Trim();
                }
            }

            return map;
        }

        /// <summary>
        /// 读取候选策略（每个变量只选一种格式，避免重复采样）：
        /// 1) PhysicalAction/Auxiliary: 优先 TableName.TagName
        /// 2) VirtualAction/HandshakeState/结构化DB路径: 使用全路径变量名（如 DBXX.XX）
        /// 3) 未识别简单符号：优先 TableName.TagName，缺失时回退全路径
        /// </summary>
        private static List<string> BuildReadTagCandidates(
            string canonicalSymbol,
            Dictionary<string, List<string>> ioTagMap,
            Dictionary<string, string> anchorTypeMap)
        {
            var candidates = new List<string>();
            if (string.IsNullOrWhiteSpace(canonicalSymbol))
            {
                return candidates;
            }

            anchorTypeMap ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            anchorTypeMap.TryGetValue(canonicalSymbol, out var anchorType);

            var isVirtualOrHandshake = string.Equals(anchorType, "VirtualAction", StringComparison.OrdinalIgnoreCase)
                || string.Equals(anchorType, "HandshakeState", StringComparison.OrdinalIgnoreCase);
            var isPhysical = string.Equals(anchorType, "PhysicalAction", StringComparison.OrdinalIgnoreCase)
                || string.Equals(anchorType, "Auxiliary", StringComparison.OrdinalIgnoreCase);
            var isStructuredSymbol = IsStructuredRuntimeSymbol(canonicalSymbol);

            if (isStructuredSymbol && !isPhysical)
            {
                candidates.Add(canonicalSymbol);
                return candidates;
            }

            if (!isVirtualOrHandshake
                && ioTagMap != null
                && ioTagMap.TryGetValue(canonicalSymbol, out var tableTagCandidates)
                && tableTagCandidates?.Count > 0)
            {
                candidates.AddRange(tableTagCandidates);
            }

            if (isVirtualOrHandshake || candidates.Count == 0 || !isPhysical)
            {
                // 虚拟Q点/握手位走全路径；物理映射缺失时回退全路径，避免漏采样。
                candidates.Add(canonicalSymbol);
            }

            return candidates
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(1)
                .ToList();
        }

        private static bool IsStructuredRuntimeSymbol(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
            {
                return false;
            }

            return symbol.Contains('.', StringComparison.Ordinal)
                || symbol.Contains('[', StringComparison.Ordinal)
                || symbol.Contains(']', StringComparison.Ordinal);
        }

        /// <summary>
        /// 将数组短写法展开为 UDT 完整路径。
        /// Name[i] → Name.Name[i]，支持多级数组。
        /// 例如：DB210.Status.Robot[2].WorkDone → DB210.Status.Robot.Robot[2].WorkDone
        /// </summary>
        private static string ExpandArrayPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !path.Contains('['))
                return path;

            var parts = path.Split('.');
            var result = new List<string>();
            foreach (var part in parts)
            {
                var bracketIdx = part.IndexOf('[');
                if (bracketIdx > 0)
                {
                    var arrayName = part.Substring(0, bracketIdx);
                    // 避免重复展开：如果前一段已经是同名前缀则跳过插入
                    if (result.Count == 0 ||
                        !string.Equals(result[result.Count - 1], arrayName, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(arrayName);
                    }
                    result.Add(part);
                }
                else
                {
                    result.Add(part);
                }
            }
            return string.Join(".", result);
        }

        private static bool ValuesEqual(object left, object right)
        {
            if (left == null && right == null)
            {
                return true;
            }

            if (left == null || right == null)
            {
                return false;
            }

            return string.Equals(Convert.ToString(left), Convert.ToString(right), StringComparison.OrdinalIgnoreCase);
        }
    }
}
