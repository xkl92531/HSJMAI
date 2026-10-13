//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/2/7 15:05:38

using SqlSugar;
using HSJM.AI.Core;

namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 逻辑单元表
    /// </summary>
    [SugarTable("AI_PLC_LogicUnit", tableDescription: "逻辑单元表")]
    public class PLC_LogicUnitEntity : BaseField
    {
        public PLC_LogicUnitEntity()
        {
            Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        }

        [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
        public long Id { get; set; }

        /// <summary>
        ///  关联的程序块ID（外键）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "关联的程序块ID（外键）")]
        public long? BlockId { get; set; }

        /// <summary>
        ///  逻辑单元类型（如：NETWORK, TRANSITION, STEP_ACTION）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "逻辑单元类型（如：NETWORK, TRANSITION, STEP_ACTION）")]
        public string UnitType { get; set; }

        /// <summary>
        ///  单元编号（对应Network号或Transition号）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "单元编号（对应Network号或Transition号）")]
        public int? NetworkId { get; set; }

        /// <summary>
        ///  单元标题/摘要
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "单元标题/摘要")]
        public string Title { get; set; }

        /// <summary>
        ///  单元注释
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "单元注释")]
        public string Comment { get; set; }

        /// <summary>
        ///  该单元使用的编程语言（如：LAD, FBD）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "该单元使用的编程语言（如：LAD, FBD）")]
        public string ProgrammingLanguage { get; set; }

        /// <summary>
        ///  原始逻辑代码/XML片段（AI诊断的直接输入）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "原始逻辑代码/XML片段（AI诊断的直接输入）")]
        public string RawContent { get; set; }

        /// <summary>
        ///  UID到变量名的映射字典（如：{"21": "OP070_CYL01_W"}）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "UID到变量名的映射字典")]
        public string UidSymbolMapJson { get; set; }

        /// <summary>
        ///  该单元读取的不同变量数量
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "该单元读取的不同变量数量")]
        public int? ReadVarsCount { get; set; }

        /// <summary>
        ///  该单元写入的不同变量数量
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "该单元写入的不同变量数量")]
        public int? WriteVarsCount { get; set; }

        /// <summary>
        ///  该单元包含的函数调用数量
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "该单元包含的函数调用数量")]
        public int? CallsCount { get; set; }

        /// <summary>
        /// 简化表达式
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "简化表达式")]
        public string Compact_logic_xml { set; get; }

        /// <summary>
        /// FB/FC调用引脚绑定 (Formal-to-Actual映射 JSON数组)，用于跨边界AST展平
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "FB/FC调用引脚绑定JSON（跨边界展平用）")]
        public string CallPinBindingsJson { get; set; }

        /// <summary>
        /// GRAPH Step Action 单元的出口 Transition 编号 JSON 数组（如 [7, 33]）。
        /// 运行时用于定位对应 Transition 的 LogicUnit 并评估其转移条件。
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "GRAPH Step出口Transition编号JSON（停机诊断用）")]
        public string GraphOutgoingTransitionsJson { get; set; }

        /// <summary>
        /// AI 解析后的结构化逻辑描述（Step1 XML 预解析结果）。
        /// 由 IOConstraintPreprocessService 全局预热阶段写入。
        /// 上传新 XML 时 LogicUnit 会被删除重建，此字段自然为 null，触发重新解析。
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "AI解析的结构化逻辑描述（XML预解析结果）")]
        public string ParsedLogicDescription { get; set; }
    }
}