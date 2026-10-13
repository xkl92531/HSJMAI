using HSJM.AI.Core;
using SqlSugar;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
namespace HSJM.AI.Core
{
    public class BaseField
    {
        /// <summary>
        /// 删除标记
        /// </summary>
        public bool IsDeleted { get; set; }

        /// <summary>
        /// 添加人
        /// </summary>
        /// 
        [MyComputedColumn(ComputedColumnType = MyComputedColumnTypeEnum.ComputedOnUpdate)]
        [SugarColumn(ColumnDescription = "添加人ID")]
        public long? CreaterId { get; set; } = 0;

        /// <summary>
        /// 添加人
        /// </summary>
        /// 
        [MyComputedColumn(ComputedColumnType = MyComputedColumnTypeEnum.ComputedOnUpdate)]
        [SugarColumn(ColumnDescription = "添加人", ColumnDataType = "nvarchar", Length = 50)]
        public string CreaterName { get; set; }

        /// <summary>
        /// 添加时间
        /// </summary>
        /// 
        [MyComputedColumn(ComputedColumnType = MyComputedColumnTypeEnum.ComputedOnUpdate)]
        [SugarColumn(ColumnDescription = "添加人")]
        public DateTime? CreateTime { get; set; }

        /// <summary>
        /// 修改人
        /// </summary>
        [SugarColumn(ColumnDescription = "修改人ID")]
        public long? ModifierId { get; set; } = 0;

        /// <summary>
        /// 修改时间
        /// </summary>
        [SugarColumn(ColumnDescription = "修改时间")]
        public DateTime? ModifyTime { get; set; }

        /// <summary>
        /// 修改人
        /// </summary>
        [SugarColumn(ColumnDescription = "修改人", ColumnDataType = "nvarchar", Length = 50)]
        public string ModifierName { get; set; }
    }
}
