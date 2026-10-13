using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>
    /// I 点反向索引服务：建立 I 点 → 关联 AST/Q 点的反向映射。
    /// 给定一个 I 点变量名，可快速查出哪些 AST 逻辑单元（及其输出 Q 点）依赖该 I 点。
    /// </summary>
    public class IPointReverseIndexService : ISelfScopedAutoInject
    {
        private readonly IPLC_LogicUnitAstService _astService;
        private readonly IPLC_IOTagService _ioTagService;
        private readonly ILogger<IPointReverseIndexService> _logger;

        /// <summary>
        /// 匹配标准 I 点地址：%I0.0, %IW100, %IB10, I0.0, E0.0(德语), %E0.0 等
        /// </summary>
        private static readonly Regex IPointAddressPattern = new(
            @"^%?[IE]\d|^%?[IE][BWD]\d", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public IPointReverseIndexService(
            IPLC_LogicUnitAstService astService,
            IPLC_IOTagService ioTagService,
            ILogger<IPointReverseIndexService> logger)
        {
            _astService = astService;
            _ioTagService = ioTagService;
            _logger = logger;
        }

        /// <summary>
        /// 为指定项目和工位构建 I 点反向索引。
        /// Key = I 点变量名（不区分大小写），Value = 依赖该 I 点的 AST 信息列表。
        /// </summary>
        public async Task<Dictionary<string, List<IPointDependencyInfo>>> BuildReverseIndexAsync(
            long projectId, string stationCode = null)
        {
            // 1. 查出所有已编译 AST 行
            var astRows = string.IsNullOrWhiteSpace(stationCode)
                ? await _astService.GetListAsync(it =>
                    it.IsDeleted == false
                    && it.ProjectId == projectId
                    && !string.IsNullOrEmpty(it.InputVariablesJson)
                    && !string.IsNullOrEmpty(it.OutputVariablesJson))
                : await _astService.GetListAsync(it =>
                    it.IsDeleted == false
                    && it.ProjectId == projectId
                    && it.StationCode == stationCode
                    && !string.IsNullOrEmpty(it.InputVariablesJson)
                    && !string.IsNullOrEmpty(it.OutputVariablesJson));

            // 2. 查出 IOTag 表建立 TagName → Address 映射（用于判断哪些输入变量是 I 点）
            var ioTags = await _ioTagService.GetListAsync(it =>
                it.IsDeleted == false
                && it.ProjectId == projectId
                && !string.IsNullOrEmpty(it.Address)
                && !string.IsNullOrEmpty(it.TagName));

            var tagNameToAddress = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var tagNameToComment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tag in ioTags)
            {
                var name = tag.TagName?.Trim();
                if (!string.IsNullOrEmpty(name) && !tagNameToAddress.ContainsKey(name))
                {
                    tagNameToAddress[name] = tag.Address?.Trim() ?? "";
                    tagNameToComment[name] = tag.Comment?.Trim() ?? "";
                }
            }

            // 3. 遍历 AST 行，提取输入变量中的 I 点，建立反向映射
            var index = new Dictionary<string, List<IPointDependencyInfo>>(StringComparer.OrdinalIgnoreCase);

            foreach (var ast in astRows)
            {
                var inputVars = DeserializeStringList(ast.InputVariablesJson);
                var outputVars = DeserializeStringList(ast.OutputVariablesJson);

                foreach (var inputVar in inputVars)
                {
                    if (!IsIPointVariable(inputVar, tagNameToAddress))
                        continue;

                    var normalizedName = inputVar.Trim();
                    if (!index.TryGetValue(normalizedName, out var deps))
                    {
                        deps = new List<IPointDependencyInfo>();
                        index[normalizedName] = deps;
                    }

                    tagNameToAddress.TryGetValue(normalizedName, out var iPointAddress);
                    tagNameToComment.TryGetValue(normalizedName, out var iPointComment);

                    deps.Add(new IPointDependencyInfo
                    {
                        IPointVariableName = normalizedName,
                        IPointAddress = iPointAddress,
                        IPointComment = iPointComment,
                        AstLogicUnitId = ast.LogicUnitId,
                        AstBlockName = ast.BlockName,
                        AstStationCode = ast.StationCode,
                        AstNetworkTitle = ast.Title,
                        AstUnitType = ast.UnitType,
                        OutputVariables = outputVars,
                    });
                }
            }

            _logger.LogInformation(
                "I点反向索引构建完成: ProjectId={ProjectId}, StationCode={StationCode}, " +
                "索引I点数={IPointCount}, 关联AST条目数={DepCount}",
                projectId, stationCode ?? "(全部)",
                index.Count,
                index.Values.Sum(v => v.Count));

            return index;
        }

        /// <summary>
        /// 查询指定 I 点变量影响的所有 Q 点/AST。
        /// </summary>
        public async Task<List<IPointDependencyInfo>> GetDependenciesAsync(
            long projectId, string stationCode, string iPointVariableName)
        {
            var index = await BuildReverseIndexAsync(projectId, stationCode);
            return index.TryGetValue(iPointVariableName?.Trim() ?? "", out var deps)
                ? deps
                : new List<IPointDependencyInfo>();
        }

        /// <summary>
        /// 批量查询：给定一组变化的 I 点信号，返回所有受影响的 AST/Q 点。
        /// </summary>
        public List<IPointDependencyInfo> LookupAffectedOutputs(
            Dictionary<string, List<IPointDependencyInfo>> reverseIndex,
            IEnumerable<string> changedIPointNames)
        {
            var result = new List<IPointDependencyInfo>();
            foreach (var name in changedIPointNames)
            {
                if (reverseIndex.TryGetValue(name?.Trim() ?? "", out var deps))
                {
                    result.AddRange(deps);
                }
            }
            return result;
        }

        /// <summary>
        /// 判断变量是否为 I 点（物理输入信号）。
        /// 优先用 IOTag 表的 Address 判断，降级用变量名模式匹配。
        /// </summary>
        private static bool IsIPointVariable(string variableName, Dictionary<string, string> tagNameToAddress)
        {
            if (string.IsNullOrWhiteSpace(variableName))
                return false;

            var name = variableName.Trim();

            // 方式1: IOTag 表有地址记录且地址为 %I 类型
            if (tagNameToAddress.TryGetValue(name, out var address) && !string.IsNullOrEmpty(address))
            {
                return IPointAddressPattern.IsMatch(address);
            }

            // 方式2: 变量名本身就像 I 点地址（如 %I0.0, I0.0）
            return IPointAddressPattern.IsMatch(name);
        }

        private static List<string> DeserializeStringList(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<string>();
            try
            {
                return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }
    }

    /// <summary>
    /// I 点反向索引条目：描述一个 I 点影响了哪个 AST 逻辑单元及其输出。
    /// </summary>
    public class IPointDependencyInfo
    {
        /// <summary>I 点变量名</summary>
        public string IPointVariableName { get; set; }

        /// <summary>I 点物理地址（如 %I4.0）</summary>
        public string IPointAddress { get; set; }

        /// <summary>I 点注释（如 传感器1到位）</summary>
        public string IPointComment { get; set; }

        /// <summary>受影响的 AST LogicUnitId</summary>
        public long? AstLogicUnitId { get; set; }

        /// <summary>受影响的 AST 所属块名</summary>
        public string AstBlockName { get; set; }

        /// <summary>受影响的 AST 所属工位</summary>
        public string AstStationCode { get; set; }

        /// <summary>AST 网络标题</summary>
        public string AstNetworkTitle { get; set; }

        /// <summary>AST 单元类型（Network/GraphStep 等）</summary>
        public string AstUnitType { get; set; }

        /// <summary>该 AST 的输出变量列表（Q 点或其他输出）</summary>
        public List<string> OutputVariables { get; set; } = new List<string>();
    }
}
