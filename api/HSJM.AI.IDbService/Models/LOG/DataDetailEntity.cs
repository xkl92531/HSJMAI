using HSJM.AI.Core;
using SqlSugar;

namespace HSJM.AI.IDbService;

/// <summary>
/// 操作数据明细
/// </summary>
[SugarTable("LOG_DataDetail", tableDescription: "操作数据明细")]
public class DataDetailEntity : BaseField
{
    public DataDetailEntity()
    {
        Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
    }

    [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
    public long Id { get; set; }

    /// <summary>
    ///  操作日志ID
    /// </summary>
    [SugarColumn(IsNullable = true, ColumnDescription = "操作日志ID")]
    public long? DataLogID { get; set; }

    /// <summary>
    ///  列描述
    /// </summary>
    [SugarColumn(IsNullable = true, ColumnDescription = "列描述", ColumnDataType = "nvarchar", Length = 500)]
    public string ColumnDesc { get; set; }

    /// <summary>
    ///  列字段
    /// </summary>
    [SugarColumn(IsNullable = true, ColumnDescription = "列字段", ColumnDataType = "nvarchar", Length = 500)]
    public string ColumnField { get; set; }

    /// <summary>
    ///  原值
    /// </summary>
    [SugarColumn(IsNullable = true, ColumnDescription = "原值", ColumnDataType = "nvarchar", Length = -1)]
    public string OldValue { get; set; }

    /// <summary>
    ///  新值
    /// </summary>
    [SugarColumn(IsNullable = true, ColumnDescription = "新值", ColumnDataType = "nvarchar", Length = -1)]
    public string NewValue { get; set; }
}