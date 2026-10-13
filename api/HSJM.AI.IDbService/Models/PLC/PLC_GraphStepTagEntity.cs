//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/6/15
using SqlSugar;
using HSJM.AI.Core;
namespace HSJM.AI.IDbService
{
    /// <summary>
    /// GRAPH 步位注册台账表（EXEC_BITS 逐位 → 网关注册结果）
    /// 来源：GRAPH 块绑定的实例 DB（ai_plc_dbfbinding）按绑定表驱动判定展开
    /// RT_DATA.EXEC_BITS.EXEC_BITS[n-1] 逐位清单，后台任务逐位注册网关 Tag 并回写状态。
    /// 状态机：Pending → Registered → Verified / RegFailed / VerifyFailed。
    /// </summary>
    [SugarTable("AI_PLC_GraphStepTag", tableDescription: "GRAPH步位注册台账表")]
    public class PLC_GraphStepTagEntity : BaseField
    {
        public PLC_GraphStepTagEntity()
        {
            Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        }

        [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
        public long Id { get; set; }

        /// <summary>
        /// 关联的项目ID（项目隔离）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "关联的项目ID")]
        public long? ProjectId { get; set; }

        /// <summary>
        /// 关联的 DB 块ID（ai_plc_block_new.id）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "关联的DB块ID（ai_plc_block_new.id）")]
        public long? DbBlockId { get; set; }

        /// <summary>
        /// 实例 DB 块名（如 DB161_HomeGraph）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "实例DB块名（如 DB161_HomeGraph）")]
        public string DbName { get; set; }

        /// <summary>
        /// 绑定的 GRAPH FB 类型名（如 FB161_HomeGraph）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "绑定的GRAPH FB类型名（如 FB161_HomeGraph）")]
        public string FbName { get; set; }

        /// <summary>
        /// 完整符号路径（如 DB161_HomeGraph.RT_DATA.EXEC_BITS.EXEC_BITS[3]）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "完整符号路径（如 DB161_HomeGraph.RT_DATA.EXEC_BITS.EXEC_BITS[3]）")]
        public string Symbol { get; set; }

        /// <summary>
        /// 注册到网关的实际点位地址（新 IGW：EXEC_BIT 行=数组整体符号、STEP_NO 行=DB.S_NO；老网关：逐位符号=Symbol）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "注册到网关的点位地址")]
        public string DbAddress { get; set; }

        /// <summary>
        /// 工站代码（ai_plc_stationblockbinding 按 FB 名匹配；无绑定为空）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "工站代码（stationblockbinding按FB名匹配）")]
        public string StationCode { get; set; }

        /// <summary>
        /// 工站名称（ai_plc_station.stationname；主数据缺失为空）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "工站名称（ai_plc_station）")]
        public string StationName { get; set; }

        // 工位（workcell）暂不落库（2026-08-26 决策）：主数据未建，列已从表中移除。

        /// <summary>
        /// 标签类别：EXEC_BIT（工步执行位）/ STEP_NO（工步号）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "标签类别（EXEC_BIT/STEP_NO）")]
        public string TagCategory { get; set; }

        /// <summary>
        /// 1 基工步号（仅 EXEC_BIT 类；EXEC_BITS[n-1] 的 n）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "1基工步号（仅EXEC_BIT类，EXEC_BITS[n-1]的n）")]
        public int? StepNumber { get; set; }

        /// <summary>
        /// 0 基数组下标 = StepNumber-1（仅 EXEC_BIT 类）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "0基数组下标=StepNumber-1（仅EXEC_BIT类）")]
        public int? BitIndex { get; set; }

        /// <summary>
        /// 顺控链编号（P1 留空）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "顺控链编号（P1留空）")]
        public string SequenceId { get; set; }

        /// <summary>
        /// 并发支路标签（P1 留空）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "并发支路标签（P1留空）")]
        public string BranchLabel { get; set; }

        /// <summary>
        /// 注册状态：Pending（待注册）/ Registered（已注册）/ RegFailed（注册失败）/ Verified（已验证）/ VerifyFailed（验证失败）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "注册状态（Pending/Registered/RegFailed/Verified/VerifyFailed）")]
        public string RegStatus { get; set; }

        /// <summary>
        /// 验证结果详情（≤500 字符）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "验证结果详情")]
        public string VerifyDetail { get; set; }

        /// <summary>
        /// 注册时间
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "注册时间")]
        public DateTime? RegTime { get; set; }

        /// <summary>
        /// 验证时间
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "验证时间")]
        public DateTime? VerifyTime { get; set; }

        /// <summary>
        /// 溯源上传任务ID（ai_plc_block_new.parserunid 同源）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "溯源上传任务ID")]
        public string ParseRunId { get; set; }
    }
}
