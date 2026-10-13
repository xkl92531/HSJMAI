
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/2/7 15:05:32
using SqlSugar;
using HSJM.AI.Core;
namespace HSJM.AI.IDbService
{
    /// <summary>
    /// I/O点位/全局变量表
    /// </summary>
    [SugarTable("AI_PLC_IOTag", tableDescription: "I/O点位/全局变量表")]
    public class PLC_IOTagEntity : BaseField
    {

        public PLC_IOTagEntity()
        {
            Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        }
        [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
        public long Id { get; set; }
        /// <summary>
        ///  关联的项目ID（外键）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "关联的项目ID（外键）")]
        public long? ProjectId { get; set; }

        /// <summary>
        ///  来源文件ID（如：PLC_Tags.csv）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "来源文件ID（如：PLC_Tags.csv）")]
        public long? SourceFileId { get; set; }

        /// <summary>
        ///  变量标签名（如：OP070_CYL01_W）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "变量标签名（如：OP070_CYL01_W）")]
        public string TagName { get; set; }

        /// <summary>
        ///  物理地址（如：%I4.0, %Q8.1, %MW100）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "物理地址（如：%I4.0, %Q8.1, %MW100）")]
        public string Address { get; set; }

        /// <summary>
        ///  数据类型（如：Bool, Int, Real）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "数据类型（如：Bool, Int, Real）")]
        public string DataType { get; set; }

        /// <summary>
        ///  变量所属标签表名称
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "变量所属标签表名称")]
        public string TableName { get; set; }

        /// <summary>
        ///  变量层级路径（如：Root）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "变量层级路径（如：Root）")]
        public string Path { get; set; }

        /// <summary>
        ///  变量注释或功能描述
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "变量注释或功能描述")]
        public string Comment { get; set; }

        /// <summary>
        ///  是否为安全相关变量（True/False）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "是否为安全相关变量（True/False）")]
        public bool? IsSafety { get; set; }

        /// <summary>
        ///  是否具有断电保持性（True/False）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "是否具有断电保持性（True/False）")]
        public bool? IsRetentive { get; set; }

        /// <summary>
        ///  HMI画面是否可见
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "HMI画面是否可见")]
        public bool? HmiVisible { get; set; }

        /// <summary>
        ///  HMI画面是否可写
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "HMI画面是否可写")]
        public bool? HmiWritable { get; set; }

        /// <summary>
        ///  变量业务分类（如：Sensor, Actuator, Status, Alarm）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "变量业务分类（如：Sensor, Actuator, Status, Alarm）")]
        public string TagCategory { get; set; }

        /// <summary>
        ///  关联的设备或机构ID
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "关联的设备或机构ID")]
        public string EquipmentId { get; set; }


        /// <summary>
        ///  标签正常状态下数据值
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "标签正常状态下数据值")]
        public string Normal_Value { get; set; }

        /// <summary>
        ///  标签正常状态下数值注解
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "标签正常状态下数值注解")]
        public string Annotation { get; set; }

        /// <summary>
        ///  标签所属控制大类
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "标签所属控制大类")]
        public string TagType { get; set; }

        /// <summary>
        ///  标签所起作用
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "标签所起作用")]
        public string TagEffect { get; set; }

        /// <summary>
        ///  约束分析状态：0=未处理, 1=处理中, 2=已完成, 3=失败, 6=弃用
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "约束分析状态：0=未处理,1=处理中,2=已完成,3=失败,6=弃用")]
        public int? ConstraintStatus { get; set; }

        /// <summary>
        ///  最后约束分析时间
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "最后约束分析时间")]
        public DateTime? ConstraintAnalyzedAt { get; set; }

        /// <summary>
        /// IO映射地址（Step1 抽取的 category/sourceVar/block/label 等结构化字段，pipe 分隔多 entry）
        /// </summary>
        [SugarColumn(ColumnName = "iomapping", IsNullable = true, ColumnDescription = "IO映射地址")]
        public string IoMapping { set; get; }
    }


}
