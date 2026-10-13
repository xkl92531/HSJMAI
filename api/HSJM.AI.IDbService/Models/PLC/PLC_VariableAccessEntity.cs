
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/2/7 15:05:56
using SqlSugar;
using HSJM.AI.Core;
namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 变量访问记录表
    /// </summary>
    [SugarTable("AI_PLC_VariableAccess",tableDescription:"变量访问记录表")]
    public class PLC_VariableAccessEntity: BaseField
    {

       public  PLC_VariableAccessEntity()
       {
                    Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                         }
         [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
       public long Id{get;set;}
            /// <summary>
        ///  关联的逻辑单元ID（外键）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="关联的逻辑单元ID（外键）")]
        public long? LogicUnitId {get;set;}
        
          /// <summary>
        ///  关联的程序块ID（冗余字段，加速查询）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="关联的程序块ID（冗余字段，加速查询）")]
        public long? BlockId {get;set;}
        
          /// <summary>
        ///  关联的项目ID（冗余字段，加速查询）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="关联的项目ID（冗余字段，加速查询）")]
        public long? ProjectId {get;set;}
        
          /// <summary>
        ///  变量全路径名（如：DB745.Control.FwdAuto）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="变量全路径名（如：DB745.Control.FwdAuto）")]
        public string VariableName {get;set;}
        
          /// <summary>
        ///  访问类型（READ:读取, WRITE:写入, READWRITE:读写）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="访问类型（READ:读取, WRITE:写入, READWRITE:读写）")]
        public string AccessType {get;set;}
        
          /// <summary>
        ///  变量在原始XML中的UID标识
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="变量在原始XML中的UID标识")]
        public string Uid {get;set;}
        
          /// <summary>
        ///  变量在访问处的特定注释
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="变量在访问处的特定注释")]
        public string Comment {get;set;}
        
          /// <summary>
        ///  变量作用域来源（Global, Local, Constant）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="变量作用域来源（Global, Local, Constant）")]
        public string VariableScope {get;set;}
        
          /// <summary>
        ///  是否为物理I/O点（True/False）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="是否为物理I/O点（True/False）")]
        public bool? IsIOTag {get;set;}
        
          /// <summary>
        ///  是否为关键步序变量（用于流程诊断）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="是否为关键步序变量（用于流程诊断）")]
        public bool? IsStepVariable {get;set;}
        
          /// <summary>
        ///  是否为报警变量（用于故障识别）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="是否为报警变量（用于故障识别）")]
        public bool? IsAlarmVariable {get;set;}
        
          /// <summary>
        ///  STL指令码（如 L/T/A/AN/OPN），SCL中为空
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="STL指令码（如 L/T/A/AN/OPN），SCL中为空")]
        public string Instruction {get;set;}
        
          /// <summary>
        ///  寻址模式（symbolic/absolute/indirect/pointer/constant）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="寻址模式（symbolic/absolute/indirect/pointer/constant）")]
        public string AddressMode {get;set;}
        


    }

   
}
