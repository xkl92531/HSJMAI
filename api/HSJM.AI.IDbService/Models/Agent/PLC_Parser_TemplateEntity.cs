
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/26
using SqlSugar;
using HSJM.AI.Core;
namespace HSJM.AI.IDbService
{
    /// <summary>
    /// PLC解析器模版表
    /// 存储动态生成的解析器代码和元数据
    /// </summary>
    [SugarTable("AI_PLC_Parser_Template", tableDescription: "PLC解析器模版表")]
    public class PLC_Parser_TemplateEntity : BaseField
    {
        public PLC_Parser_TemplateEntity()
        {
            Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        }

        [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
        public long Id { get; set; }

        /// <summary>
        /// 模版名称（如：Siemens_S7_1500_XML_Parser_v1）
        /// </summary>
        [SugarColumn(IsNullable = false, Length = 200, ColumnDescription = "模版名称")]
        public string TemplateName { get; set; }

        /// <summary>
        /// 模版编码（唯一标识）
        /// </summary>
        [SugarColumn(IsNullable = false, Length = 100, ColumnDescription = "模版编码")]
        public string TemplateCode { get; set; }

        /// <summary>
        /// PLC品牌（Siemens/Mitsubishi/Omron/ABB等）
        /// </summary>
        [SugarColumn(IsNullable = false, Length = 50, ColumnDescription = "PLC品牌")]
        public string PlcBrand { get; set; }

        /// <summary>
        /// 适用型号正则（如：S7-(1200|1500)）
        /// </summary>
        [SugarColumn(IsNullable = true, Length = 200, ColumnDescription = "适用型号正则")]
        public string ModelPattern { get; set; }

        /// <summary>
        /// 文件格式（XML/AWL/SCL/STL等）
        /// </summary>
        [SugarColumn(IsNullable = false, Length = 50, ColumnDescription = "文件格式")]
        public string FileFormat { get; set; }

        /// <summary>
        /// 解析器语言（Python/CSharp）
        /// </summary>
        [SugarColumn(IsNullable = false, Length = 20, ColumnDescription = "解析器语言")]
        public string ParserLanguage { get; set; }

        /// <summary>
        /// 解析器代码（完整代码）
        /// </summary>
        [SugarColumn(IsNullable = false, ColumnDataType = "nvarchar(max)", ColumnDescription = "解析器代码")]
        public string ParserCode { get; set; }

        /// <summary>
        /// 解析器版本号
        /// </summary>
        [SugarColumn(IsNullable = true, Length = 20, ColumnDescription = "版本号")]
        public string Version { get; set; }

        /// <summary>
        /// 结构信息JSON（BlockPatterns/VariablePatterns等）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDataType = "nvarchar(max)", ColumnDescription = "结构信息JSON")]
        public string StructureInfoJson { get; set; }

        /// <summary>
        /// 测试用例JSON
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDataType = "nvarchar(max)", ColumnDescription = "测试用例JSON")]
        public string TestCasesJson { get; set; }

        /// <summary>
        /// 测试通过率（0-100）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "测试通过率")]
        public decimal? TestPassRate { get; set; }

        /// <summary>
        /// 最后验证时间
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "最后验证时间")]
        public DateTime? LastValidationTime { get; set; }

        /// <summary>
        /// 使用次数
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "使用次数")]
        public int? UsageCount { get; set; }

        /// <summary>
        /// 成功次数
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "成功次数")]
        public int? SuccessCount { get; set; }

        /// <summary>
        /// 平均解析时间（毫秒）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "平均解析时间(ms)")]
        public int? AvgParseTimeMs { get; set; }

        /// <summary>
        /// 生成来源任务ID
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "生成来源任务ID")]
        public long? GeneratedByTaskId { get; set; }

        /// <summary>
        /// 生成来源智能体ID
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "生成来源智能体ID")]
        public long? GeneratedByAgentId { get; set; }

        /// <summary>
        /// 审批状态（Pending/Approved/Rejected）
        /// </summary>
        [SugarColumn(IsNullable = true, Length = 20, ColumnDescription = "审批状态")]
        public string ApprovalStatus { get; set; }

        /// <summary>
        /// 审批人ID
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "审批人ID")]
        public long? ApprovedByUserId { get; set; }

        /// <summary>
        /// 是否激活（同品牌同格式可能有多个解析器）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "是否激活")]
        public bool? IsActive { get; set; }

        /// <summary>
        /// 优先级（同品牌多解析器时的优先级，数值越大优先）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "优先级")]
        public int? Priority { get; set; }

        /// <summary>
        /// 模版描述
        /// </summary>
        [SugarColumn(IsNullable = true, Length = 500, ColumnDescription = "模版描述")]
        public string Description { get; set; }

        /// <summary>
        /// 代码文件保存路径
        /// </summary>
        [SugarColumn(IsNullable = true, Length = 500, ColumnDescription = "代码文件保存路径")]
        public string CodeFilePath { get; set; }
    }
}
