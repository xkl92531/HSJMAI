#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.IDbService.IService;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>
    /// FB/FC 内部 LocalVariable 的全局路径具体化器（2026-10-07）。
    ///
    /// 背景：GRAPH/LAD 块内 <c>#I_Mode.DryRunMode</c>、<c>#S_AutoRunMode</c> 这类 FB 接口变量
    /// 在解析产物（ai_plc_variableaccess / ParsedLogicDescription / AST finding）中只有裸名，
    /// 读值与白话解释都找不到真身。本组件按结构证据把裸名还原为全局可读路径，两条路径：
    ///
    /// 1) 形参链向上解析（Input/InOut 形参成员）：
    ///    FB150 的 <c>I_Mode.DryRunMode</c> → FC150 调用绑定实参 "Mode"（形参转发）
    ///    → FC100 调用绑定实参 "DB10050_Mode.M010_Mode"（全局）→ <c>DB10050_Mode.M010_Mode.DryRunMode</c>。
    ///    证据源：ai_plc_blockcall（调用边）+ ai_plc_logicunit.callpinbindingsjson（引脚绑定）。
    ///
    /// 2) STAT 镜像（FB 静态成员）：
    ///    FB150 的 <c>S_AutoRunMode</c> → ai_plc_dbfbinding 中该 FB 的唯一实例 DB
    ///    → <c>IDB150_OP1030_电芯上线扫码枪自动流程.S_AutoRunMode</c>，
    ///    且要求路径确实存在于实例 DB 接口表（结构校验，防止同名误配）。
    ///
    /// 严格模式（宁可漏不可误，对齐约束体系"结构证据优先"铁律）：
    /// 多调用点实参不一致 / 多调用者转发 / 多实例 DB / 链路断开 / 深度超限 / 成环
    /// / 字面量绑定 —— 一律返回 null，调用方保留原样。
    /// </summary>
    public class FbLocalVariableConcretizer : ISelfScopedAutoInject
    {
        private const int MaxDepth = 8;

        private readonly IPLC_BlockCallService _blockCallService;
        private readonly IPLC_LogicUnitService _logicUnitService;
        private readonly IPLC_DbFbBindingService _dbFbBindingService;
        private readonly IPLC_BlockService _blockService;
        private readonly IPLC_BlockInterfaceService _blockInterfaceService;
        private readonly ILogger<FbLocalVariableConcretizer> _logger;

        public FbLocalVariableConcretizer(
            IPLC_BlockCallService blockCallService,
            IPLC_LogicUnitService logicUnitService,
            IPLC_DbFbBindingService dbFbBindingService,
            IPLC_BlockService blockService,
            IPLC_BlockInterfaceService blockInterfaceService,
            ILogger<FbLocalVariableConcretizer> logger)
        {
            _blockCallService = blockCallService;
            _logicUnitService = logicUnitService;
            _dbFbBindingService = dbFbBindingService;
            _blockService = blockService;
            _blockInterfaceService = blockInterfaceService;
            _logger = logger;
        }

        /// <summary>
        /// 把 FB/FC 块内局部变量路径具体化为全局可读路径。
        /// 返回 null 表示不可唯一具体化（调用方应保留原样，不得猜测）。
        /// </summary>
        /// <param name="projectId">PLC 项目 Id</param>
        /// <param name="ownerBlockName">局部变量所属的 FB/FC 块名（如 FB150_OP1030_电芯上线扫码枪自动流程）</param>
        /// <param name="localPath">去 # 后的局部路径（如 I_Mode.DryRunMode / S_AutoRunMode）</param>
        public async Task<string?> TryConcretizeAsync(long projectId, string ownerBlockName, string localPath)
        {
            if (projectId <= 0 || string.IsNullOrWhiteSpace(ownerBlockName) || string.IsNullOrWhiteSpace(localPath))
            {
                return null;
            }

            localPath = localPath.Trim().TrimStart('#').Trim();
            ownerBlockName = ownerBlockName.Trim();
            if (localPath.Length == 0)
            {
                return null;
            }

            // A) 形参链向上解析（优先：能落到真源全局 DB 就不用镜像）
            var viaFormal = await ResolveFormalChainAsync(projectId, ownerBlockName, localPath).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(viaFormal))
            {
                _logger.LogInformation(
                    "[FB局部变量具体化·形参链] {Block}.{Path} => {GlobalPath}（项目 {ProjectId}）",
                    ownerBlockName, localPath, viaFormal, projectId);
                return viaFormal;
            }

            // B) STAT 镜像：owner 是 FB 且路径存在于其唯一实例 DB 接口
            var viaMirror = await ResolveInstanceDbMirrorAsync(projectId, ownerBlockName, localPath).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(viaMirror))
            {
                _logger.LogInformation(
                    "[FB局部变量具体化·STAT镜像] {Block}.{Path} => {GlobalPath}（项目 {ProjectId}）",
                    ownerBlockName, localPath, viaMirror, projectId);
            }
            return viaMirror;
        }

        /// <summary>
        /// 批量具体化（LAD 梯形图展示用）：对一批局部路径逐个具体化，
        /// 只返回成功项（失败路径不进字典，调用方按原样显示）。
        /// </summary>
        public async Task<Dictionary<string, string>> TryConcretizeRangeAsync(
            long projectId, string ownerBlockName, IEnumerable<string> localPaths)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (projectId <= 0 || string.IsNullOrWhiteSpace(ownerBlockName) || localPaths == null)
            {
                return map;
            }

            foreach (var raw in localPaths)
            {
                var localPath = raw?.Trim().TrimStart('#').Trim();
                if (string.IsNullOrWhiteSpace(localPath) || map.ContainsKey(localPath))
                {
                    continue;
                }

                var global = await TryConcretizeAsync(projectId, ownerBlockName, localPath).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(global))
                {
                    map[localPath] = global;
                }
            }

            return map;
        }

        #region 形参链向上解析

        private async Task<string?> ResolveFormalChainAsync(long projectId, string ownerBlockName, string localPath)
        {
            var current = ownerBlockName;
            var remaining = localPath;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ownerBlockName };

            for (var depth = 0; depth < MaxDepth; depth++)
            {
                var sites = await LoadCallSitesAsync(projectId, current).ConfigureAwait(false);
                if (sites.Count == 0)
                {
                    return null;
                }

                var step = ResolveFormalStep(sites, remaining);
                if (step == null)
                {
                    return null;
                }
                if (step.ResolvedGlobal != null)
                {
                    return step.ResolvedGlobal;
                }

                // 形参转发：上移到唯一 caller 继续
                if (step.MoveToCaller == null || step.MoveNextRemaining == null)
                {
                    return null;
                }
                if (!visited.Add(step.MoveToCaller))
                {
                    return null; // 环
                }
                current = step.MoveToCaller;
                remaining = step.MoveNextRemaining;
            }

            return null; // 深度超限
        }

        /// <summary>
        /// 单步解析：在 callee 的全部调用点绑定中取首段形参的实参。
        /// 实参唯一且为全局路径 → 直接返回拼接结果；实参是 caller 形参名 → 返回上移指令；
        /// 无绑定 / 实参不一致 / 字面量 / 多 caller 转发 → null（不可解析）。
        /// 纯函数，可单测。
        /// </summary>
        public static ChainStepResult? ResolveFormalStep(IReadOnlyList<CallSiteRecord> sites, string remaining)
        {
            var (firstSeg, rest) = SplitFirstSegment(remaining);
            if (string.IsNullOrWhiteSpace(firstSeg))
            {
                return null;
            }

            var actuals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var site in sites)
            {
                if (site.InputPins != null && site.InputPins.TryGetValue(firstSeg, out var inActual)
                    && !string.IsNullOrWhiteSpace(inActual))
                {
                    actuals.Add(inActual);
                }
                if (site.InOutPins != null && site.InOutPins.TryGetValue(firstSeg, out var ioActual)
                    && !string.IsNullOrWhiteSpace(ioActual))
                {
                    actuals.Add(ioActual);
                }
            }

            // 0 个 = 未绑定（可能是 STAT/Output 成员）；>1 个 = 多调用点实参不一致
            if (actuals.Count != 1)
            {
                return null;
            }

            var actual = actuals.First().Trim();
            if (IsLiteralActual(actual))
            {
                return null;
            }

            if (actual.StartsWith("%", StringComparison.Ordinal))
            {
                // 绝对地址实参：仅当无剩余路径（标量直连）时有效
                return rest.Length == 0
                    ? ChainStepResult.Done(actual)
                    : null;
            }

            if (actual.Contains('.'))
            {
                // 全局带点路径（如 DB10050_Mode.M010_Mode / M010_1_HMI.GraphMode[1]）——直接拼接
                return ChainStepResult.Done(rest.Length == 0 ? actual : actual + "." + rest);
            }

            // 无点：可能是 caller 的形参名（如 "Mode"），也可能恰好是全局块名
            var callers = sites
                .Select(s => s.CallerBlockName?.Trim())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (callers.Count != 1)
            {
                return null; // 多调用者转发：无法确定唯一上移目标
            }

            return ChainStepResult.MoveUp(callers[0]!, rest.Length == 0 ? actual : actual + "." + rest);
        }

        public sealed class ChainStepResult
        {
            public string? ResolvedGlobal { get; private init; }
            public string? MoveToCaller { get; private init; }
            public string? MoveNextRemaining { get; private init; }

            public static ChainStepResult Done(string global) => new() { ResolvedGlobal = global };
            public static ChainStepResult MoveUp(string caller, string remaining) =>
                new() { MoveToCaller = caller, MoveNextRemaining = remaining };
        }

        public static bool IsLiteralActual(string value)
        {
            var t = value.Trim();
            if (t.Length == 0) return true;
            if (t.Equals("true", StringComparison.OrdinalIgnoreCase)
                || t.Equals("false", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.All(char.IsDigit)) return true;
            if (t.Length >= 2
                && ((t.StartsWith("'") && t.EndsWith("'")) || (t.StartsWith("\"") && t.EndsWith("\"")))) return true;
            return false;
        }

        public static (string first, string rest) SplitFirstSegment(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return (string.Empty, string.Empty);
            var idx = path.IndexOf('.');
            return idx < 0
                ? (path, string.Empty)
                : (path.Substring(0, idx), path.Substring(idx + 1));
        }

        #endregion

        #region STAT 镜像（实例 DB）

        private async Task<string?> ResolveInstanceDbMirrorAsync(long projectId, string fbName, string localPath)
        {
            var bindings = await _dbFbBindingService.GetListAsync(b =>
                    b.IsDeleted == false && b.ProjectId == projectId
                    && b.InstanceOfName == fbName
                    && b.InstanceOfType != null && b.InstanceOfType.ToUpper() == "FB")
                .ConfigureAwait(false);

            var instanceDbs = bindings
                .Select(b => b.DbName?.Trim())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 实例 DB 必须唯一（多工位共用 FB 类型时不可唯一具体化，保留原样）
            if (instanceDbs.Count != 1)
            {
                return null;
            }

            var dbName = instanceDbs[0]!;

            // 结构校验：路径必须存在于该实例 DB 的接口表（防止同名 STAT 跨块误配）
            var idBlock = await _blockService.GetListAsync(b =>
                    b.IsDeleted == false && b.ProjectId == projectId && b.BlockName == dbName)
                .ConfigureAwait(false);
            var block = idBlock.FirstOrDefault();
            if (block == null)
            {
                return null;
            }

            var ifaces = await _blockInterfaceService.GetListAsync(i =>
                    i.IsDeleted == false && i.BlockId == block.Id
                    && !string.IsNullOrEmpty(i.VariablePath))
                .ConfigureAwait(false);
            if (ifaces.Count == 0)
            {
                return null;
            }

            var paths = ifaces
                .Select(i => i.VariablePath?.Trim())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!InstanceDbPathExists(paths, localPath))
            {
                return null;
            }

            return $"{dbName}.{localPath}";
        }

        /// <summary>纯函数：局部路径是否存在于实例 DB 接口路径集合（兼容 Static. 前缀与数组展开两种形态）。</summary>
        public static bool InstanceDbPathExists(IReadOnlySet<string> instanceDbPaths, string localPath)
        {
            if (string.IsNullOrWhiteSpace(localPath)) return false;
            var expanded = ExpandArrayPath(localPath);
            return instanceDbPaths.Contains(localPath)
                   || instanceDbPaths.Contains("Static." + localPath)
                   || instanceDbPaths.Contains(expanded)
                   || instanceDbPaths.Contains("Static." + expanded);
        }

        /// <summary>
        /// 数组路径展开：PLC 代码短写法 → BlockInterface 层级格式（与 StopCauseDiagnosisApp.ExpandArrayPath 语义一致）。
        /// Name[i] → Name.Name[i]，如 Sensor_On[37].X → Sensor_On.Sensor_On[37].X。
        /// </summary>
        public static string ExpandArrayPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !path.Contains('['))
            {
                return path;
            }

            var parts = path.Split('.');
            var result = new List<string>();
            foreach (var part in parts)
            {
                var bracketIdx = part.IndexOf('[');
                if (bracketIdx > 0)
                {
                    result.Add(part.Substring(0, bracketIdx));
                    result.Add(part);
                }
                else
                {
                    result.Add(part);
                }
            }
            return string.Join(".", result);
        }

        #endregion

        #region 调用点数据加载

        private async Task<List<CallSiteRecord>> LoadCallSitesAsync(long projectId, string calleeName)
        {
            var calls = await _blockCallService.GetListAsync(c =>
                    c.IsDeleted == false && c.ProjectId == projectId
                    && c.CalleeBlockName == calleeName
                    && !string.IsNullOrEmpty(c.CallerBlockName))
                .ConfigureAwait(false);
            if (calls.Count == 0)
            {
                return new List<CallSiteRecord>();
            }

            var luIds = calls.Select(c => c.LogicUnitId).Where(id => id > 0).Distinct().ToList();
            var luById = new Dictionary<long, PLC_LogicUnitEntity>();
            if (luIds.Count > 0)
            {
                var units = await _logicUnitService.GetListAsync(u =>
                        u.IsDeleted == false && luIds.Contains(u.Id))
                    .ConfigureAwait(false);
                foreach (var unit in units)
                {
                    luById[unit.Id] = unit;
                }
            }

            var sites = new List<CallSiteRecord>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var call in calls)
            {
                if (call.LogicUnitId == null || !luById.TryGetValue(call.LogicUnitId.Value, out var unit))
                {
                    continue;
                }
                var parsed = ParseCallSitesJson(unit.CallPinBindingsJson, calleeName, call.CallerBlockName!);
                foreach (var site in parsed)
                {
                    var key = $"{site.CallerBlockName}|{site.CallUid}";
                    if (seen.Add(key))
                    {
                        sites.Add(site);
                    }
                }
            }

            return sites;
        }

        /// <summary>
        /// 解析一个 LogicUnit 的 callpinbindingsjson，取出 callee_name 匹配的调用点绑定。
        /// 纯函数，可单测。JSON 契约与 PlcDiagnosisService.BlockCallPinBindingRecord 一致
        /// （Python parser 的 BlockCallPinBinding：callee_name/callee_block_type/instance_db/call_uid/input_pins/...）。
        /// </summary>
        public static List<CallSiteRecord> ParseCallSitesJson(string? json, string calleeName, string callerBlockName)
        {
            var result = new List<CallSiteRecord>();
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(calleeName))
            {
                return result;
            }

            try
            {
                var bindings = JsonSerializer.Deserialize<List<BindingRecord>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });
                if (bindings == null)
                {
                    return result;
                }

                foreach (var b in bindings)
                {
                    if (!string.Equals(b.callee_name, calleeName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    result.Add(new CallSiteRecord(
                        callerBlockName,
                        b.callee_name ?? string.Empty,
                        b.instance_db ?? string.Empty,
                        b.call_uid ?? string.Empty,
                        b.input_pins,
                        b.inout_pins));
                }
            }
            catch (JsonException)
            {
                // 坏 JSON：按无绑定处理（宁可漏不可误）
            }

            return result;
        }

        public sealed record CallSiteRecord(
            string CallerBlockName,
            string CalleeName,
            string InstanceDb,
            string CallUid,
            Dictionary<string, string>? InputPins,
            Dictionary<string, string>? InOutPins);

        private sealed class BindingRecord
        {
            public string? callee_name { get; set; }
            public string? callee_block_type { get; set; }
            public string? instance_db { get; set; }
            public string? call_uid { get; set; }
            public Dictionary<string, string>? input_pins { get; set; }
            public Dictionary<string, string>? output_pins { get; set; }
            public Dictionary<string, string>? inout_pins { get; set; }
        }

        #endregion
    }
}
