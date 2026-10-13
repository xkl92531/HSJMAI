using MAF.DEMO.Core;
using SqlSugar;

namespace MAF.DEMO.IDbService;

/// <summary>
/// 操作日志
/// </summary>
[SugarTable("LOG_DataLog", tableDescription: "操作日志")]
public class DataLogEntity : BaseField
{
    public DataLogEntity()
    {
        Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
    }

    [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
    public long Id { get; set; }

    /// <summary>
    ///  操作类型
    /// </summary>
    [SugarColumn(IsNullable = true, ColumnDescription = "操作类型", ColumnDataType = "nvarchar", Length = 500)]
    public string DiffType { get; set; }

    /// <summary>
    ///  表名称
    /// </summary>
    [SugarColumn(IsNullable = true, ColumnDescription = "表名称", ColumnDataType = "nvarchar", Length = 500)]
    public string TableName { get; set; }

    /// <summary>
    ///  表描述
    /// </summary>
    [SugarColumn(IsNullable = true, ColumnDescription = "表描述", ColumnDataType = "nvarchar", Length = 500)]
    public string TableDesc { get; set; }

    /// <summary>
    ///  操作内容
    /// </summary>
    [SugarColumn(IsNullable = true, ColumnDescription = "操作内容", ColumnDataType = "nvarchar", Length = -1)]
    public string LogContent { get; set; }
}