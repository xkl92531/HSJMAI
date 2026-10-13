using HSJM.AI.Core;
using SqlSugar;

namespace HSJM.AI.IDbService;

[SugarTable("SubStation_ActionErrSolutions", tableDescription: "LineUp action error solutions")]
public sealed class LineUpActionErrSolutionEntity : BaseField
{
    public LineUpActionErrSolutionEntity() => Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
    [SugarColumn(IsPrimaryKey = true)] public long Id { get; set; }
    [SugarColumn(IsNullable = true)] public long? ActionId { get; set; }
    [SugarColumn(IsNullable = true)] public string? ActionName { get; set; }
    [SugarColumn(IsNullable = true)] public string? ErrSolutions { get; set; }
    [SugarColumn(IsNullable = true)] public string? Remark { get; set; }
}

[SugarTable("SubStation_Actions", tableDescription: "LineUp substation actions")]
public sealed class LineUpActionEntity : BaseField
{
    public LineUpActionEntity() => Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
    [SugarColumn(IsPrimaryKey = true)] public long Id { get; set; }
    [SugarColumn(IsNullable = true)] public long? SubStationId { get; set; }
    [SugarColumn(IsNullable = true)] public int? ActionNum { get; set; }
    [SugarColumn(IsNullable = true)] public string? ActionCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? ActionName { get; set; }
    [SugarColumn(IsNullable = true)] public string? FlowJson { get; set; }
    [SugarColumn(IsNullable = true)] public int? ExcuteTimeOut { get; set; }
    [SugarColumn(IsNullable = true)] public int? StepCount { get; set; }
    [SugarColumn(IsNullable = true)] public string? Remark { get; set; }
    public long PlcID { get; set; }
    [SugarColumn(IsNullable = true)] public string? PlcName { get; set; }
    [SugarColumn(IsNullable = true)] public string? AutoModeTagCode { get; set; }
}

[SugarTable("SubStation_ActionSteps", tableDescription: "LineUp action steps")]
public sealed class LineUpActionStepEntity : BaseField
{
    public LineUpActionStepEntity() => Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
    [SugarColumn(IsPrimaryKey = true)] public long Id { get; set; }
    [SugarColumn(IsNullable = true)] public string? PlcStepCode { get; set; }
    [SugarColumn(IsNullable = true)] public long? StationActionId { get; set; }
    [SugarColumn(IsNullable = true)] public int? StepNo { get; set; }
    [SugarColumn(IsNullable = true)] public int? StepNum { get; set; }
    [SugarColumn(IsNullable = true)] public int? StepBratherNum { get; set; }
    [SugarColumn(IsNullable = true)] public string? StepName { get; set; }
    [SugarColumn(IsNullable = true)] public string? NormalStatusValuesbyEdage { get; set; }
    [SugarColumn(IsNullable = true)] public int? OprType { get; set; }
    [SugarColumn(IsNullable = true)] public string? OprContent { get; set; }
    [SugarColumn(IsNullable = true)] public string? Remark { get; set; }
    public long PlcID { get; set; }
    [SugarColumn(IsNullable = true)] public string? PlcName { get; set; }
    [SugarColumn(IsNullable = true)] public string? StepErrInfo { get; set; }
    [SugarColumn(IsNullable = true)] public string? StepErrInfoManual { get; set; }
    [SugarColumn(IsNullable = true)] public string? ParentIds { get; set; }
    [SugarColumn(IsNullable = true)] public int? DurationThreshold { get; set; }
}

[SugarTable("SubStation_ConstTagInfos", tableDescription: "LineUp constant tags")]
public sealed class LineUpConstTagInfoEntity : BaseField
{
    public LineUpConstTagInfoEntity() => Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
    [SugarColumn(IsPrimaryKey = true)] public long Id { get; set; }
    [SugarColumn(IsNullable = true)] public long? PlcID { get; set; }
    [SugarColumn(IsNullable = true)] public string? PlcName { get; set; }
    [SugarColumn(IsNullable = true)] public string? ConstCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? ConstValue { get; set; }
    [SugarColumn(IsNullable = true)] public string? Remark { get; set; }
}

[SugarTable("Station_CurrentStepExt")]
public sealed class LineUpCurrentStepExtEntity : BaseField
{
    public LineUpCurrentStepExtEntity() => Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
    [SugarColumn(IsPrimaryKey = true)] public long Id { get; set; }
    [SugarColumn(IsNullable = true)] public long? SubStationId { get; set; }
    [SugarColumn(IsNullable = true)] public long? ActionId { get; set; }
    [SugarColumn(IsNullable = true)] public string? PlcStepCode { get; set; }
    [SugarColumn(IsNullable = true)] public int? MinInterval { get; set; }
    [SugarColumn(IsNullable = true)] public int? AgvInterval { get; set; }
    [SugarColumn(IsNullable = true)] public long? PlcID { get; set; }
    [SugarColumn(IsNullable = true)] public string? PlcName { get; set; }
    public bool FirstCycle { get; set; }
}

[SugarTable("Station_CurrentStepExtHistory")]
public sealed class LineUpCurrentStepExtHistoryEntity : BaseField
{
    public LineUpCurrentStepExtHistoryEntity() => Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
    [SugarColumn(IsPrimaryKey = true)] public long Id { get; set; }
    [SugarColumn(IsNullable = true)] public long? SubStationId { get; set; }
    [SugarColumn(IsNullable = true)] public long? ActionId { get; set; }
    [SugarColumn(IsNullable = true)] public string? PlcStepCode { get; set; }
    [SugarColumn(IsNullable = true)] public int? MinInterval { get; set; }
    [SugarColumn(IsNullable = true)] public int? AgvInterval { get; set; }
    [SugarColumn(IsNullable = true)] public long? PlcID { get; set; }
    [SugarColumn(IsNullable = true)] public string? PlcName { get; set; }
    [SugarColumn(IsNullable = true)] public bool? FirstCycle { get; set; }
    [SugarColumn(IsNullable = true)] public int? SpanNowInterval { get; set; }
}

[SugarTable("Station_CurrentStepStatus")]
public sealed class LineUpCurrentStepStatusEntity : BaseField
{
    public LineUpCurrentStepStatusEntity() => Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
    [SugarColumn(IsPrimaryKey = true)] public long Id { get; set; }
    [SugarColumn(IsNullable = true)] public long? SubStationId { get; set; }
    [SugarColumn(IsNullable = true)] public long? ActionId { get; set; }
    [SugarColumn(IsNullable = true)] public string? PlcStepCode { get; set; }
    [SugarColumn(IsNullable = true)] public int? StepNum { get; set; }
    [SugarColumn(IsNullable = true)] public int? StepStatus { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? StartTime { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? EndTime { get; set; }
    [SugarColumn(IsNullable = true)] public int? VStatus { get; set; }
    [SugarColumn(IsNullable = true)] public int? IsTimeOut { get; set; }
    [SugarColumn(IsNullable = true)] public string? ActionCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? ActionName { get; set; }
    [SugarColumn(IsNullable = true)] public string? SubStationCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? SubStationName { get; set; }
    [SugarColumn(IsNullable = true)] public string? StepName { get; set; }
    public long PlcID { get; set; }
    [SugarColumn(IsNullable = true)] public string? PlcName { get; set; }
}

[SugarTable("Station_CurrentTagValues")]
public sealed class LineUpCurrentTagValueEntity : BaseField
{
    public LineUpCurrentTagValueEntity() => Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
    [SugarColumn(IsPrimaryKey = true)] public long Id { get; set; }
    [SugarColumn(IsNullable = true)] public long? SubStationId { get; set; }
    [SugarColumn(IsNullable = true)] public long? ActionId { get; set; }
    [SugarColumn(IsNullable = true)] public string? PlcStepCode { get; set; }
    [SugarColumn(IsNullable = true)] public int? StepNum { get; set; }
    [SugarColumn(IsNullable = true)] public string? TagCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? TagDBAddress { get; set; }
    [SugarColumn(IsNullable = true)] public string? TagValue { get; set; }
    [SugarColumn(IsNullable = true)] public int? VStatus { get; set; }
    [SugarColumn(IsNullable = true)] public string? ActionCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? ActionName { get; set; }
    [SugarColumn(IsNullable = true)] public string? SubStationCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? SubStationName { get; set; }
    [SugarColumn(IsNullable = true)] public long? BomId { get; set; }
    [SugarColumn(IsNullable = true)] public string? BomCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? StepName { get; set; }
    [SugarColumn(IsNullable = true)] public string? ETValue { get; set; }
    public long PlcID { get; set; }
    [SugarColumn(IsNullable = true)] public string? PlcName { get; set; }
    public int ShowErrStatus { get; set; }
}

[SugarTable("SubStation_DeviceWarnInfo")]
public sealed class LineUpDeviceWarnInfoEntity : BaseField
{
    public LineUpDeviceWarnInfoEntity() => Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
    [SugarColumn(IsPrimaryKey = true)] public long Id { get; set; }
    [SugarColumn(IsNullable = true)] public long? SubStationId { get; set; }
    [SugarColumn(IsNullable = true)] public string? SubStationCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? SubStationName { get; set; }
    [SugarColumn(IsNullable = true)] public string? DeviceName { get; set; }
    [SugarColumn(IsNullable = true)] public string? StatusCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? StatusDes { get; set; }
    public long PlcID { get; set; }
    [SugarColumn(IsNullable = true)] public string? PlcName { get; set; }
    [SugarColumn(IsNullable = true)] public string? ProcessingMethod { get; set; }
    public int Status { get; set; }
    [SugarColumn(IsNullable = true)] public long? ProductionLineID { get; set; }
    [SugarColumn(IsNullable = true)] public string? ProductionLineCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? ProductionLineName { get; set; }
}

[SugarTable("log_errtagcode")]
public sealed class LineUpErrorTagCodeEntity : BaseField
{
    public LineUpErrorTagCodeEntity() => Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
    [SugarColumn(IsPrimaryKey = true)] public long Id { get; set; }
    [SugarColumn(IsNullable = true)] public string? tagcode { get; set; }
    [SugarColumn(IsNullable = true)] public long? actionid { get; set; }
    [SugarColumn(IsNullable = true)] public string? actioncode { get; set; }
    [SugarColumn(IsNullable = true)] public string? actionname { get; set; }
    [SugarColumn(IsNullable = true)] public string? plcstepcode { get; set; }
    [SugarColumn(IsNullable = true)] public string? tagvalue { get; set; }
    [SugarColumn(IsNullable = true)] public string? etvalue { get; set; }
    [SugarColumn(IsNullable = true)] public long? substationid { get; set; }
    [SugarColumn(IsNullable = true)] public string? substationcode { get; set; }
    [SugarColumn(IsNullable = true)] public string? substationname { get; set; }
    [SugarColumn(IsNullable = true)] public long? plcid { get; set; }
    [SugarColumn(IsNullable = true)] public string? plcname { get; set; }
    [SugarColumn(IsNullable = true)] public long? productionlineid { get; set; }
    [SugarColumn(IsNullable = true)] public string? productionlinecode { get; set; }
    [SugarColumn(IsNullable = true)] public string? productionlinename { get; set; }
}

[SugarTable("SubStation_OtherTagInfos", tableDescription: "LineUp additional tag information")]
public sealed class LineUpOtherTagInfoEntity : BaseField
{
    public LineUpOtherTagInfoEntity() => Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
    [SugarColumn(IsPrimaryKey = true)] public long Id { get; set; }
    [SugarColumn(IsNullable = true)] public long? SubStationId { get; set; }
    [SugarColumn(IsNullable = true)] public long? StationActionId { get; set; }
    [SugarColumn(IsNullable = true)] public string? TagCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? TagDBAdress { get; set; }
    [SugarColumn(IsNullable = true)] public string? PlcIp { get; set; }
    [SugarColumn(IsNullable = true)] public string? Remark { get; set; }
    [SugarColumn(IsNullable = true)] public int? TagType { get; set; }
    [SugarColumn(IsNullable = true)] public string? Details { get; set; }
    [SugarColumn(IsNullable = true)] public string? SubStationCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? SubStationName { get; set; }
    [SugarColumn(IsNullable = true)] public string? TagDefaultValue { get; set; }
    [SugarColumn(IsNullable = true)] public string? TagVarType { get; set; }
    public long PlcID { get; set; }
    [SugarColumn(IsNullable = true)] public string? PlcName { get; set; }
    [SugarColumn(IsNullable = true)] public string? Solutions { get; set; }
    public bool IsSolutions { get; set; }
    public bool Status { get; set; }
    [SugarColumn(IsNullable = true)] public long? WorkStationId { get; set; }
    [SugarColumn(IsNullable = true)] public string? WorkStationCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? WorkStationName { get; set; }
    [SugarColumn(IsNullable = true)] public long? ProductionLineID { get; set; }
    [SugarColumn(IsNullable = true)] public string? ProductionLineCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? ProductionLineName { get; set; }
    [SugarColumn(IsNullable = true)] public string? ReadSoure { get; set; }
    public int ShowErrStatus { get; set; }
}

[SugarTable("SubStation_StationHistory", tableDescription: "LineUp substation change history")]
public sealed class LineUpStationHistoryEntity : BaseField
{
    public LineUpStationHistoryEntity() => Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
    [SugarColumn(IsPrimaryKey = true)] public long Id { get; set; }
    public long ActionId { get; set; }
    [SugarColumn(IsNullable = true)] public string? ActionCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? ActionName { get; set; }
    [SugarColumn(IsNullable = true)] public string? FlowJson { get; set; }
    [SugarColumn(IsNullable = true)] public string? SubStationCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? SubStationName { get; set; }
    [SugarColumn(IsNullable = true)] public string? ProductionLineCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? ProductionLineName { get; set; }
}

[SugarTable("SubStation_VariableError")]
public sealed class LineUpStationVariableErrorEntity : BaseField
{
    public LineUpStationVariableErrorEntity() => Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
    [SugarColumn(IsPrimaryKey = true)] public long Id { get; set; }
    [SugarColumn(IsNullable = true)] public string? VariableCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? DBName { get; set; }
    [SugarColumn(IsNullable = true)] public string? ErrorMessage { get; set; }
    public long ActionId { get; set; }
    [SugarColumn(IsNullable = true)] public string? ActionCode { get; set; }
    public long SubStationId { get; set; }
    [SugarColumn(IsNullable = true)] public string? SubStationCode { get; set; }
}

[SugarTable("SubStation_StepDecision", tableDescription: "LineUp step decision")]
public sealed class LineUpStepDecisionEntity : BaseField
{
    public LineUpStepDecisionEntity() => Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
    [SugarColumn(IsPrimaryKey = true)] public long Id { get; set; }
    [SugarColumn(IsNullable = true)] public long? StationActionId { get; set; }
    [SugarColumn(IsNullable = true)] public int? StepNum { get; set; }
    public int DecisionType { get; set; }
    [SugarColumn(IsNullable = true)] public int? SubStepNum { get; set; }
    [SugarColumn(IsNullable = true)] public string? DecisionStr { get; set; }
    [SugarColumn(IsNullable = true)] public string? PlcStepCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? DParameters { get; set; }
    public long PlcID { get; set; }
    [SugarColumn(IsNullable = true)] public string? PlcName { get; set; }
}

[SugarTable("SubStation_StepErrSolutions", tableDescription: "LineUp step error solutions")]
public sealed class LineUpStepErrSolutionEntity : BaseField
{
    public LineUpStepErrSolutionEntity() => Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
    [SugarColumn(IsPrimaryKey = true)] public long Id { get; set; }
    [SugarColumn(IsNullable = true)] public long? SubStationId { get; set; }
    [SugarColumn(IsNullable = true)] public long? ActionId { get; set; }
    [SugarColumn(IsNullable = true)] public long? StepNum { get; set; }
    [SugarColumn(IsNullable = true)] public int? SubStepNum { get; set; }
    [SugarColumn(IsNullable = true)] public string? PlcStepCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? Tags { get; set; }
    [SugarColumn(IsNullable = true)] public string? ErrSolutions { get; set; }
    [SugarColumn(IsNullable = true)] public string? Remark { get; set; }
    [SugarColumn(IsNullable = true)] public string? ActionCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? ActionName { get; set; }
    [SugarColumn(IsNullable = true)] public string? SubStationCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? SubStationName { get; set; }
}

[SugarTable("SubStation_TagValueHistory")]
public sealed class LineUpTagValueHistoryEntity : BaseField
{
    public LineUpTagValueHistoryEntity() => Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
    [SugarColumn(IsPrimaryKey = true)] public long Id { get; set; }
    [SugarColumn(IsNullable = true)] public long? StationId { get; set; }
    [SugarColumn(IsNullable = true)] public long? ActionId { get; set; }
    [SugarColumn(IsNullable = true)] public string? PlcStepCode { get; set; }
    [SugarColumn(IsNullable = true)] public int? StepNum { get; set; }
    [SugarColumn(IsNullable = true)] public int? StepBratherNum { get; set; }
    [SugarColumn(IsNullable = true)] public string? TagCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? TagDBAddress { get; set; }
    [SugarColumn(IsNullable = true)] public string? TagValue { get; set; }
    public long PlcID { get; set; }
    [SugarColumn(IsNullable = true)] public string? PlcName { get; set; }
}
