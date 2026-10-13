using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Models;
using HSJM.AI.Core;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>
    /// 变量传播关系构建服务。
    /// 当前实现包含三层传播：
    /// 1. 同 LogicUnit 内 READ/IOTag -> WRITE
    /// 2. 块调用 + 块接口传播
    /// 3. 共享 DB 根节点跨块传播
    /// </summary>
    public class VariablePropagationBuilderService : ISelfScopedAutoInject
    {
        private static readonly Regex SharedDbRootRegex = new Regex(@"^(DB\d+(?:_[^\.\[]+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly IPLC_VariableAccessService _variableAccessService;
        private readonly IPLC_BlockCallService _blockCallService;
        private readonly IPLC_BlockInterfaceService _blockInterfaceService;
        private readonly IPLC_BlockService _blockService;

        public VariablePropagationBuilderService(
            IPLC_VariableAccessService variableAccessService,
            IPLC_BlockCallService blockCallService,
            IPLC_BlockInterfaceService blockInterfaceService,
            IPLC_BlockService blockService)
        {
            _variableAccessService = variableAccessService;
            _blockCallService = blockCallService;
            _blockInterfaceService = blockInterfaceService;
            _blockService = blockService;
        }

        public async Task<List<VariablePropagationLink>> BuildAsync(long projectId, IEnumerable<AnchorCandidate> anchors)
        {
            var variableNames = anchors?
                .Where(it => !string.IsNullOrWhiteSpace(it?.VariableName))
                .Select(it => it.VariableName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();

            if (variableNames.Count == 0)
            {
                return new List<VariablePropagationLink>();
            }

            var allAccesses = await _variableAccessService.GetListAsync(it =>
                it.ProjectId == projectId &&
                it.IsDeleted == false &&
                it.LogicUnitId.HasValue &&
                !string.IsNullOrEmpty(it.VariableName));

            var blockCalls = await _blockCallService.GetListAsync(it =>
                it.ProjectId == projectId &&
                it.IsDeleted == false);

            var allBlockIds = allAccesses
                .Where(it => it.BlockId.HasValue)
                .Select(it => it.BlockId.Value)
                .Distinct()
                .ToList();

            var blocks = allBlockIds.Count > 0
                ? await _blockService.GetListAsync(it => allBlockIds.Contains(it.Id) && it.IsDeleted == false)
                : new List<PLC_BlockEntity>();

            var blockMapById = blocks.ToDictionary(it => it.Id, it => it);
            var blockMapByName = blocks
                .Where(it => !string.IsNullOrWhiteSpace(it.BlockName))
                .GroupBy(it => it.BlockName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var interfaceBlockIds = blockCalls
                .Where(it => !string.IsNullOrWhiteSpace(it.CalleeBlockName) && blockMapByName.ContainsKey(it.CalleeBlockName))
                .Select(it => blockMapByName[it.CalleeBlockName].Id)
                .Distinct()
                .ToList();

            var blockInterfaces = interfaceBlockIds.Count > 0
                ? await _blockInterfaceService.GetListAsync(it => it.IsDeleted == false && it.BlockId.HasValue && interfaceBlockIds.Contains(it.BlockId.Value))
                : new List<PLC_BlockInterfaceEntity>();

            var interfacesByBlock = blockInterfaces
                .Where(it => it.BlockId.HasValue)
                .GroupBy(it => it.BlockId.Value)
                .ToDictionary(g => g.Key, g => g.ToList());

            var accessesByLogicUnit = allAccesses
                .GroupBy(it => it.LogicUnitId.Value)
                .ToDictionary(g => g.Key, g => g.ToList());

            var links = new List<VariablePropagationLink>();

            foreach (var logicGroup in accessesByLogicUnit)
            {
                var accesses = logicGroup.Value;
                var reads = accesses
                    .Where(IsReadAccess)
                    .GroupBy(it => it.VariableName, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();

                var writes = accesses
                    .Where(IsWriteAccess)
                    .GroupBy(it => it.VariableName, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();

                foreach (var write in writes)
                {
                    foreach (var read in reads)
                    {
                        if (string.Equals(read.VariableName, write.VariableName, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        // 当前阶段只保留与已关注锚点变量相邻的传播边，避免全量噪音过大。
                        var isAnchorRelated = variableNames.Contains(write.VariableName, StringComparer.OrdinalIgnoreCase)
                            || variableNames.Contains(read.VariableName, StringComparer.OrdinalIgnoreCase);
                        if (!isAnchorRelated)
                        {
                            continue;
                        }

                        var confidence = read.IsIOTag == true ? 0.90m : 0.60m;
                        var propagationType = read.IsIOTag == true ? "IOTagToWrite" : "ReadToWrite";

                        if (read.IsIOTag != true && write.IsIOTag == true)
                        {
                            continue;
                        }

                        links.Add(new VariablePropagationLink
                        {
                            FromVariable = read.VariableName,
                            ToVariable = write.VariableName,
                            LogicUnitId = write.LogicUnitId,
                            BlockId = write.BlockId,
                            PropagationType = propagationType,
                            Confidence = confidence,
                            Evidence = new List<string>
                            {
                                $"LogicUnit: {logicGroup.Key}",
                                $"READ: {read.VariableName}",
                                $"WRITE: {write.VariableName}",
                                read.IsIOTag == true ? "源变量为物理 IOTag" : "同网络读取变量传播到写入变量"
                            }
                        });
                    }
                }
            }

            // 块调用 + 接口传播：把调用点上下文变量映射到被调块接口变量，再从接口 OUT 反向映射回调用网络写变量。
            foreach (var blockCall in blockCalls)
            {
                if (!blockCall.LogicUnitId.HasValue || string.IsNullOrWhiteSpace(blockCall.CalleeBlockName))
                {
                    continue;
                }

                if (!accessesByLogicUnit.TryGetValue(blockCall.LogicUnitId.Value, out var callerAccesses))
                {
                    continue;
                }

                if (!blockMapByName.TryGetValue(blockCall.CalleeBlockName, out var calleeBlock))
                {
                    continue;
                }

                if (!interfacesByBlock.TryGetValue(calleeBlock.Id, out var calleeInterfaces))
                {
                    continue;
                }

                var callerReads = callerAccesses
                    .Where(IsReadAccess)
                    .GroupBy(it => it.VariableName, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();

                var callerWrites = callerAccesses
                    .Where(IsWriteAccess)
                    .GroupBy(it => it.VariableName, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();

                foreach (var blockInterface in calleeInterfaces)
                {
                    var interfaceName = FirstNonEmpty(blockInterface.VariablePath, blockInterface.VariableName);
                    if (string.IsNullOrWhiteSpace(interfaceName))
                    {
                        continue;
                    }

                    var interfaceVariable = $"{calleeBlock.BlockName}.{interfaceName}";
                    var scope = (blockInterface.Scope ?? string.Empty).Trim().ToUpperInvariant();

                    if (scope == "IN" || scope == "INOUT")
                    {
                        foreach (var read in callerReads.Where(it => IsAnchorRelated(it.VariableName, variableNames)))
                        {
                            links.Add(new VariablePropagationLink
                            {
                                FromVariable = read.VariableName,
                                ToVariable = interfaceVariable,
                                LogicUnitId = blockCall.LogicUnitId,
                                BlockId = calleeBlock.Id,
                                PropagationType = "BlockCallInput",
                                Confidence = read.IsIOTag == true ? 0.72m : 0.52m,
                                Evidence = new List<string>
                                {
                                    $"CallerBlock: {blockCall.CallerBlockName}",
                                    $"CalleeBlock: {calleeBlock.BlockName}",
                                    $"InterfaceScope: {blockInterface.Scope}",
                                    $"READ: {read.VariableName}",
                                    $"INTERFACE: {interfaceVariable}"
                                }
                            });
                        }
                    }

                    if (scope == "OUT" || scope == "INOUT")
                    {
                        foreach (var write in callerWrites.Where(it => IsAnchorRelated(it.VariableName, variableNames)))
                        {
                            links.Add(new VariablePropagationLink
                            {
                                FromVariable = interfaceVariable,
                                ToVariable = write.VariableName,
                                LogicUnitId = blockCall.LogicUnitId,
                                BlockId = calleeBlock.Id,
                                PropagationType = "BlockCallOutput",
                                Confidence = 0.46m,
                                Evidence = new List<string>
                                {
                                    $"CallerBlock: {blockCall.CallerBlockName}",
                                    $"CalleeBlock: {calleeBlock.BlockName}",
                                    $"InterfaceScope: {blockInterface.Scope}",
                                    $"INTERFACE: {interfaceVariable}",
                                    $"WRITE: {write.VariableName}"
                                }
                            });
                        }
                    }
                }
            }

            // 共享 DB 流：同一 DB 根下，跨 LogicUnit/Block 的写入 -> 读取视作共享状态传播链。
            var accessesByDbRoot = allAccesses
                .Select(it => new { Access = it, DbRoot = ExtractSharedDbRoot(it.VariableName) })
                .Where(it => !string.IsNullOrWhiteSpace(it.DbRoot))
                .GroupBy(it => it.DbRoot, StringComparer.OrdinalIgnoreCase);

            foreach (var dbGroup in accessesByDbRoot)
            {
                var writes = dbGroup
                    .Select(it => it.Access)
                    .Where(IsWriteAccess)
                    .GroupBy(it => it.VariableName, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();

                var reads = dbGroup
                    .Select(it => it.Access)
                    .Where(IsReadAccess)
                    .GroupBy(it => it.VariableName, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();

                foreach (var write in writes)
                {
                    foreach (var read in reads)
                    {
                        if (string.Equals(write.VariableName, read.VariableName, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (write.LogicUnitId == read.LogicUnitId)
                        {
                            continue;
                        }

                        var isAnchorRelated = IsAnchorRelated(write.VariableName, variableNames)
                            || IsAnchorRelated(read.VariableName, variableNames);
                        if (!isAnchorRelated)
                        {
                            continue;
                        }

                        links.Add(new VariablePropagationLink
                        {
                            FromVariable = write.VariableName,
                            ToVariable = read.VariableName,
                            LogicUnitId = read.LogicUnitId,
                            BlockId = read.BlockId,
                            PropagationType = "SharedDbFlow",
                            Confidence = 0.40m,
                            Evidence = new List<string>
                            {
                                $"SharedDbRoot: {dbGroup.Key}",
                                $"WRITE: {write.VariableName}",
                                $"READ: {read.VariableName}",
                                "跨 LogicUnit 共享 DB 传播"
                            }
                        });
                    }
                }
            }

            return links
                .GroupBy(it => BuildLinkKey(it), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(it => it.Confidence).First())
                .OrderByDescending(it => it.Confidence)
                .ThenBy(it => it.ToVariable)
                .ToList();
        }

        private static string ExtractSharedDbRoot(string variableName)
        {
            if (string.IsNullOrWhiteSpace(variableName))
            {
                return string.Empty;
            }

            var match = SharedDbRootRegex.Match(variableName.Trim());
            return match.Success ? match.Value.ToUpperInvariant() : string.Empty;
        }

        private static bool IsAnchorRelated(string variableName, IReadOnlyCollection<string> anchorVariables)
        {
            if (string.IsNullOrWhiteSpace(variableName))
            {
                return false;
            }

            return anchorVariables.Contains(variableName, StringComparer.OrdinalIgnoreCase);
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values.FirstOrDefault(it => !string.IsNullOrWhiteSpace(it)) ?? string.Empty;
        }

        private static string BuildLinkKey(VariablePropagationLink link)
        {
            return string.Join("|",
                link.FromVariable?.Trim().ToUpperInvariant() ?? string.Empty,
                link.ToVariable?.Trim().ToUpperInvariant() ?? string.Empty,
                link.LogicUnitId?.ToString() ?? string.Empty,
                link.BlockId?.ToString() ?? string.Empty,
                link.PropagationType?.Trim().ToUpperInvariant() ?? string.Empty);
        }

        private static bool IsReadAccess(PLC_VariableAccessEntity entity)
        {
            return string.Equals(entity.AccessType, "READ", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(entity.AccessType, "READWRITE", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsWriteAccess(PLC_VariableAccessEntity entity)
        {
            return string.Equals(entity.AccessType, "WRITE", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(entity.AccessType, "READWRITE", StringComparison.OrdinalIgnoreCase);
        }
    }
}