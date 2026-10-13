using HSJM.AI.IDbService.LineUp;

namespace HSJM.AI.IDbService;

public interface ILineUpPlcMetadataRepository
{
    Task<LineUpPlcResolutionData> LoadResolutionDataAsync(
        string projectName,
        string graphBlockName,
        int graphBlockNumber,
        CancellationToken cancellationToken);

    /// <summary>
    /// 按设备绑定装载解析数据：项目取 ai_plc_project_new.plcdeviceid = deviceId 的未删项目
    /// （项目详情页「绑定 PLC 设备」写入的 ID 关联，优先于设备 ProjectName 名字匹配）。
    /// </summary>
    Task<LineUpPlcResolutionData> LoadResolutionDataByDeviceIdAsync(
        long deviceId,
        string graphBlockName,
        int graphBlockNumber,
        CancellationToken cancellationToken);

    Task<LineUpVariableLookupData> LoadVariableDataAsync(
        long plcProjectId,
        long instanceDbBlockId,
        IReadOnlyCollection<string> canonicalVariablePaths,
        CancellationToken cancellationToken);

    Task<LineUpGraphSymbolLookupData> LoadGraphSymbolDataAsync(
        long plcProjectId,
        IReadOnlyCollection<LineUpGraphSymbolLookupRequest> symbols,
        CancellationToken cancellationToken);

    Task<LineUpGraphStepLedgerLookup> LoadGraphStepLedgerAsync(
        long plcProjectId,
        string graphBlockName,
        CancellationToken cancellationToken);
}
