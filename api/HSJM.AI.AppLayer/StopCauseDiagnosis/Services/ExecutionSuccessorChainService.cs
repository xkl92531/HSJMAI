using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Models;
using HSJM.AI.Core;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>
    /// 基于离线变量传播图，为运行时诊断提取“当前激活锚点的后继目标”。
    /// 第一版直接复用已持久化的 VariablePropagation 图，避免新增表结构。
    /// </summary>
    public class ExecutionSuccessorChainService : ISelfScopedAutoInject
    {
        private readonly StationMapPersistenceService _stationMapPersistenceService;
        private readonly StationShadowStateCacheService _stationShadowStateCacheService;
        private readonly IPLC_StationShadowSignalService _stationShadowSignalService;

        public ExecutionSuccessorChainService(
            StationMapPersistenceService stationMapPersistenceService,
            StationShadowStateCacheService stationShadowStateCacheService,
            IPLC_StationShadowSignalService stationShadowSignalService)
        {
            _stationMapPersistenceService = stationMapPersistenceService;
            _stationShadowStateCacheService = stationShadowStateCacheService;
            _stationShadowSignalService = stationShadowSignalService;
        }

        public async Task<List<ExecutionSuccessorCandidate>> QueryCandidatesAsync(
            long projectId,
            string stationCode,
            int maxDepth = 3,
            int maxCandidates = 12)
        {
            var runtimeState = await LoadRuntimeStateAsync(projectId, stationCode);
            if (runtimeState == null || runtimeState.ActiveGraphAnchors.Count == 0)
            {
                return new List<ExecutionSuccessorCandidate>();
            }

            var links = await _stationMapPersistenceService.QueryVariablePropagationsAsync(projectId, runtimeState.StationCode);
            if (links == null || links.Count == 0)
            {
                return new List<ExecutionSuccessorCandidate>();
            }

            return ExecutionSuccessorSelectionPolicy.BuildCandidates(
                runtimeState.ActiveGraphAnchors,
                links,
                runtimeState.ShadowSignals,
                runtimeState.Mirrors,
                maxDepth,
                maxCandidates);
        }

        /// <summary>
        /// 非GRAPH场景：从所有运行时核心锚点（PhysicalAction、VirtualAction、HandshakeState）出发，
        /// 沿传播链寻找未激活的后继目标。
        /// </summary>
        public async Task<List<ExecutionSuccessorCandidate>> QueryNonGraphCandidatesAsync(
            long projectId,
            string stationCode,
            int maxDepth = 3,
            int maxCandidates = 12)
        {
            var runtimeState = await LoadRuntimeStateAsync(projectId, stationCode, graphOnly: false);
            if (runtimeState == null || runtimeState.ActiveGraphAnchors.Count == 0)
            {
                return new List<ExecutionSuccessorCandidate>();
            }

            var links = await _stationMapPersistenceService.QueryVariablePropagationsAsync(projectId, runtimeState.StationCode);
            if (links == null || links.Count == 0)
            {
                return new List<ExecutionSuccessorCandidate>();
            }

            return ExecutionSuccessorSelectionPolicy.BuildCandidates(
                runtimeState.ActiveGraphAnchors,
                links,
                runtimeState.ShadowSignals,
                runtimeState.Mirrors,
                maxDepth,
                maxCandidates);
        }

        /// <summary>
        /// 非GRAPH场景精确推导：以"最近才变为True的IO信号"为种子，沿传播链查找下游尚未激活的目标。
        /// 与 QueryNonGraphCandidatesAsync 的区别：种子不是所有当前为True的锚点（会产生大量噪音），
        /// 而是调用方传入的"最近发生过True变化"的信号子集，深度限制为2（只找直接后继）。
        /// 典型场景：OP030_YV12_W 刚置True → 传播链 → 找 DB345_INP.BkdPos 等下游应激活但未激活的节点。
        /// </summary>
        public async Task<List<ExecutionSuccessorCandidate>> QueryFromRecentlyActivatedIoAsync(
            long projectId,
            string stationCode,
            IEnumerable<string> recentlyActivatedVariables,
            int maxDepth = 2,
            int maxCandidates = 12)
        {
            var seedVars = (recentlyActivatedVariables ?? Enumerable.Empty<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (seedVars.Count == 0)
                return new List<ExecutionSuccessorCandidate>();

            var normalizedStationCode = stationCode?.Trim();
            if (projectId <= 0 || string.IsNullOrWhiteSpace(normalizedStationCode))
                return new List<ExecutionSuccessorCandidate>();

            var shadowSignals = await _stationShadowSignalService.GetListAsync(it =>
                it.IsDeleted == false && it.ProjectId == projectId && it.StationCode == normalizedStationCode);
            if (shadowSignals == null || shadowSignals.Count == 0)
                return new List<ExecutionSuccessorCandidate>();

            var mirrors = await _stationShadowStateCacheService.GetSignalMirrorsAsync(projectId, normalizedStationCode);
            if (mirrors == null || mirrors.Count == 0)
                return new List<ExecutionSuccessorCandidate>();

            var links = await _stationMapPersistenceService.QueryVariablePropagationsAsync(projectId, normalizedStationCode);
            if (links == null || links.Count == 0)
                return new List<ExecutionSuccessorCandidate>();

            // 把最近变True的变量构造为 ExecutionSuccessorSource（不要求一定在影子信号注册表里）
            var mirrorLookup = mirrors.Values
                .Where(m => !string.IsNullOrWhiteSpace(m.VariableName))
                .GroupBy(m => m.VariableName.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var seeds = seedVars.Select(v =>
            {
                mirrorLookup.TryGetValue(v, out var mirror);
                return new ExecutionSuccessorSource
                {
                    VariableName = v,
                    AnchorType = mirror?.AnchorType ?? "PhysicalAction",
                    DisplayLabel = mirror?.SemanticLabel ?? v,
                };
            }).ToList();

            return ExecutionSuccessorSelectionPolicy.BuildCandidates(
                seeds,
                links,
                shadowSignals,
                mirrors,
                maxDepth,
                maxCandidates);
        }

        public async Task<bool> HasActiveGraphAnchorsAsync(long projectId, string stationCode)
        {
            var runtimeState = await LoadRuntimeStateAsync(projectId, stationCode, graphOnly: true);
            return runtimeState != null && runtimeState.ActiveGraphAnchors.Count > 0;
        }

        /// <summary>
        /// 检测工位是否存在任何类型的激活运行时锚点（含非GRAPH）。
        /// </summary>
        public async Task<bool> HasActiveRuntimeAnchorsAsync(long projectId, string stationCode)
        {
            var runtimeState = await LoadRuntimeStateAsync(projectId, stationCode, graphOnly: false);
            return runtimeState != null && runtimeState.ActiveGraphAnchors.Count > 0;
        }

        private async Task<RuntimeState> LoadRuntimeStateAsync(long projectId, string stationCode, bool graphOnly = true)
        {
            var normalizedStationCode = stationCode?.Trim();
            if (projectId <= 0 || string.IsNullOrWhiteSpace(normalizedStationCode))
            {
                return null;
            }

            var shadowSignals = await _stationShadowSignalService.GetListAsync(it =>
                it.IsDeleted == false
                && it.ProjectId == projectId
                && it.StationCode == normalizedStationCode);

            if (shadowSignals == null || shadowSignals.Count == 0)
            {
                return null;
            }

            var mirrors = await _stationShadowStateCacheService.GetSignalMirrorsAsync(projectId, normalizedStationCode);
            if (mirrors == null || mirrors.Count == 0)
            {
                return null;
            }

            var activeGraphAnchors = graphOnly
                ? ExecutionSuccessorSelectionPolicy.SelectActiveRuntimeAnchors(shadowSignals, mirrors)
                : ExecutionSuccessorSelectionPolicy.SelectActiveNonGraphAnchors(shadowSignals, mirrors);
            return new RuntimeState
            {
                StationCode = normalizedStationCode,
                ShadowSignals = shadowSignals,
                Mirrors = mirrors,
                ActiveGraphAnchors = activeGraphAnchors,
            };
        }

        private sealed class RuntimeState
        {
            public string StationCode { get; set; }

            public List<PLC_StationShadowSignalEntity> ShadowSignals { get; set; } = new List<PLC_StationShadowSignalEntity>();

            public IReadOnlyDictionary<string, StationShadowSignalCacheModel> Mirrors { get; set; }

            public List<ExecutionSuccessorSource> ActiveGraphAnchors { get; set; } = new List<ExecutionSuccessorSource>();
        }
    }
}