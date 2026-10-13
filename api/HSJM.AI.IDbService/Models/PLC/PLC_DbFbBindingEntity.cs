//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/6/15
using SqlSugar;
using HSJM.AI.Core;
namespace HSJM.AI.IDbService
{
    /// <summary>
    /// DB块与FB绑定关系表（InstanceDB → 绑定的 FB/UDT 类型）
    /// 来源：博图 InstanceDB 导出 XML 的 &lt;InstanceOfName&gt;/&lt;InstanceOfType&gt;，
    /// 上传解析时随 DB 块一同写入。DB 块重传时按 (ProjectId, DbName) 替换。
    /// </summary>
    // 表名与 tools/create_dbfbinding.sql 及现网一致：ai_plc_dbfbinding（小写全拼）。
    // 原属性 "AI_PLC_DbFbBinding" 经 PG 大小写折叠会得到 ai_plc_dbfbbinding（多一个 b），
    // 与实际建的表 ai_plc_dbfbinding 不匹配，运行时 UndefinedTable。上传绑定 BulkCopy 同样受害。
    [SugarTable("ai_plc_dbfbinding", tableDescription: "DB块与FB绑定关系表")]
    public class PLC_DbFbBindingEntity : BaseField
    {
        public PLC_DbFbBindingEntity()
        {
            Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        }

        [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
        public long Id { get; set; }

        /// <summary>
        /// 关联的项目ID（plcProjectId）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "关联的项目ID")]
        public long? ProjectId { get; set; }

        /// <summary>
        /// 关联的 DB 块ID（ai_plc_block_new.id，即解析生成的 BlockId）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "关联的DB块ID（ai_plc_block_new.id）")]
        public long? BlockId { get; set; }

        /// <summary>
        /// DB 块名（如 DB161_HomeGraph）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "DB块名")]
        public string DbName { get; set; }

        /// <summary>
        /// 绑定的 FB/UDT 类型名（如 FB161_HomeGraph）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "绑定的FB/UDT类型名（InstanceOfName）")]
        public string InstanceOfName { get; set; }

        /// <summary>
        /// 绑定类型（如 "FB" / "UDT"）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "绑定类型（InstanceOfType）")]
        public string InstanceOfType { get; set; }
    }
}
