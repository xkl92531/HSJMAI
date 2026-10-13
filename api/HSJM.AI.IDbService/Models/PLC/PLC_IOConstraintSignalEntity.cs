using SqlSugar;
using HSJM.AI.Core;

namespace HSJM.AI.IDbService
{
    /// <summary>
    /// IO信号约束明细表（从表）
    /// 记录约束规则中涉及的每个信号及其角色
    /// </summary>
    [SugarTable("AI_PLC_IOConstraintSignal", tableDescription: "IO信号约束明细表")]
    public class PLC_IOConstraintSignalEntity : BaseField
    {
        public PLC_IOConstraintSignalEntity()
        {
            Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        }

        [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
        public long Id { get; set; }

        /// <summary>
        /// 关联的规则ID（外键 → AI_PLC_IOConstraintRule.Id）
        /// </summary>
        [SugarColumn(IsNullable = false, ColumnDescription = "关联的规则ID（外键）")]
        public long RuleId { get; set; }

        /// <summary>
        /// 信号名称（如：I_OP010_CYL01_FwdOK）
        /// </summary>
        [SugarColumn(IsNullable = false, Length = 200, ColumnDescription = "信号名称")]
        public string SignalName { get; set; } = string.Empty;

        /// <summary>
        /// 信号物理地址（如：%I4.0）
        /// </summary>
        [SugarColumn(IsNullable = true, Length = 50, ColumnDescription = "信号物理地址")]
        public string? SignalAddress { get; set; }

        /// <summary>
        /// 信号角色：Source（驱动方）, Target（被约束方）, Member（对等成员）
        /// </summary>
        [SugarColumn(IsNullable = false, Length = 20, ColumnDescription = "信号角色：Source/Target/Member")]
        public string SignalRole { get; set; } = "Member";

        /// <summary>
        /// 排序序号（Sequence 类型时表示时序顺序，其他类型可忽略）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "排序序号")]
        public int? SortOrder { get; set; }
    }
}
