using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.Core;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace HSJM.AI.AppLayer.LineUp;

/// <summary>
/// PLC 项目删除 → LineUp 域派生数据级联软删（2026-09-04）。
/// 实现IPlcProjectRemovedHandler；PLC 模块经抽象接口调用，本类在 LineUp 域内清理：
/// 直接携带 PlcProjectId 的表（GraphAsset/GraphSequence/ImportBatch/SubStationGraphBinding）
/// + 经挂靠键传递的表（Revision/StationBinding←资产，Step/Transition←序列，Action←步，
///   RuntimeEvent←监控绑定，NodeBom/NodeImage←导入批次）。
/// 幂等：只更新 IsDeleted=false 的行；主数据（Basic_*/ai_plc_*）不动。
/// </summary>
public class LineUpProjectCascadeApp : IPlcProjectRemovedHandler, IAutoInject, ISelfScopedAutoInject
{
    private readonly ISqlSugarClient _db;
    private readonly ILogger<LineUpProjectCascadeApp> _logger;

    public LineUpProjectCascadeApp(ISqlSugarClient db, ILogger<LineUpProjectCascadeApp> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task HandleProjectsRemovedAsync(IReadOnlyCollection<long> plcProjectIds, CancellationToken cancellationToken = default)
    {
        var ids = plcProjectIds.Where(x => x > 0).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        var idParams = ids.Select((value, index) => new SugarParameter($"@p{index}", value)).ToArray();
        var idList = string.Join(", ", idParams.Select(p => p.ParameterName));

        // 挂靠键参数组：资产 / 序列 / 批次 / 结构绑定 / 监控绑定 / 步
        var assetKeys = await CollectKeysAsync("\"Id\"", "\"AI_LineUp_GraphAsset\"", "\"PlcProjectId\"", idList, idParams, cancellationToken);
        var sequenceKeys = await CollectKeysAsync("\"Id\"", "\"AI_LineUp_GraphSequence\"", "\"PlcProjectId\"", idList, idParams, cancellationToken);
        var batchKeys = await CollectKeysAsync("\"Id\"", "\"AI_LineUp_ImportBatch\"", "\"PlcProjectId\"", idList, idParams, cancellationToken);
        var stationBindingKeys = assetKeys.Length > 0
            ? await CollectKeysAsync("\"Id\"", "\"AI_LineUp_GraphStationBinding\"", "\"GraphAssetId\"", BuildList(assetKeys, "a"), BuildParams(assetKeys, "a"), cancellationToken)
            : [];
        var stepKeys = sequenceKeys.Length > 0
            ? await CollectKeysAsync("\"Id\"", "\"AI_LineUp_GraphStep\"", "\"SequenceId\"", BuildList(sequenceKeys, "s"), BuildParams(sequenceKeys, "s"), cancellationToken)
            : [];

        var affected = 0;
        // ① 直接携带 PlcProjectId
        affected += await SoftDeleteAsync("\"AI_LineUp_GraphAsset\"", "\"PlcProjectId\"", idList, idParams, cancellationToken);
        affected += await SoftDeleteAsync("\"AI_LineUp_GraphSequence\"", "\"PlcProjectId\"", idList, idParams, cancellationToken);
        affected += await SoftDeleteAsync("\"AI_LineUp_ImportBatch\"", "\"PlcProjectId\"", idList, idParams, cancellationToken);
        affected += await SoftDeleteAsync("\"AI_LineUp_SubStationGraphBinding\"", "\"PlcProjectId\"", idList, idParams, cancellationToken);
        // ② 经挂靠键传递
        if (assetKeys.Length > 0)
        {
            affected += await SoftDeleteAsync("\"AI_LineUp_GraphRevision\"", "\"GraphAssetId\"", BuildList(assetKeys, "a"), BuildParams(assetKeys, "a"), cancellationToken);
            affected += await SoftDeleteAsync("\"AI_LineUp_GraphStationBinding\"", "\"GraphAssetId\"", BuildList(assetKeys, "a"), BuildParams(assetKeys, "a"), cancellationToken);
        }
        if (sequenceKeys.Length > 0)
        {
            affected += await SoftDeleteAsync("\"AI_LineUp_GraphStep\"", "\"SequenceId\"", BuildList(sequenceKeys, "s"), BuildParams(sequenceKeys, "s"), cancellationToken);
            affected += await SoftDeleteAsync("\"AI_LineUp_GraphTransition\"", "\"SequenceId\"", BuildList(sequenceKeys, "s"), BuildParams(sequenceKeys, "s"), cancellationToken);
        }
        if (stepKeys.Length > 0)
        {
            affected += await SoftDeleteAsync("\"AI_LineUp_GraphAction\"", "\"StepId\"", BuildList(stepKeys, "st"), BuildParams(stepKeys, "st"), cancellationToken);
        }
        if (stationBindingKeys.Length > 0)
        {
            affected += await SoftDeleteAsync("\"AI_LineUp_RuntimeEvent\"", "\"BindingId\"", BuildList(stationBindingKeys, "b"), BuildParams(stationBindingKeys, "b"), cancellationToken);
        }
        if (batchKeys.Length > 0)
        {
            affected += await SoftDeleteAsync("\"AI_LineUp_GraphNodeBom\"", "\"BatchId\"", BuildList(batchKeys, "bt"), BuildParams(batchKeys, "bt"), cancellationToken);
            affected += await SoftDeleteAsync("\"AI_LineUp_GraphNodeImage\"", "\"BatchId\"", BuildList(batchKeys, "bt"), BuildParams(batchKeys, "bt"), cancellationToken);
        }

        _logger.LogInformation(
            "[LineUp级联] PLC项目删除联动清理完成: ProjectIds=[{ProjectIds}], 软删 {Rows} 行派生数据",
            string.Join(",", ids), affected);
    }

    private async Task<long[]> CollectKeysAsync(
        string keyColumn, string table, string filterColumn, string filterList, SugarParameter[] filterParams, CancellationToken ct)
    {
        var rows = await _db.Ado.SqlQueryAsync<long>(
                $"SELECT {keyColumn} FROM {table} WHERE {filterColumn} IN ({filterList})",
                filterParams)
            .WaitAsync(ct);
        return rows.ToArray();
    }

    private async Task<int> SoftDeleteAsync(
        string table, string filterColumn, string filterList, SugarParameter[] filterParams, CancellationToken ct)
    {
        return await _db.Ado.ExecuteCommandAsync(
                $"UPDATE {table} SET \"IsDeleted\" = TRUE, \"ModifyTime\" = now() " +
                $"WHERE \"IsDeleted\" = FALSE AND {filterColumn} IN ({filterList})",
                filterParams)
            .WaitAsync(ct);
    }

    private static string BuildList(long[] keys, string prefix) =>
        string.Join(", ", keys.Select((_, index) => $"@{prefix}{index}"));

    private static SugarParameter[] BuildParams(long[] keys, string prefix) =>
        keys.Select((value, index) => new SugarParameter($"@{prefix}{index}", value)).ToArray();
}
