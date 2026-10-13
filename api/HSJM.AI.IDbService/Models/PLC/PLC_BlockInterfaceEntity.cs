
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/2/7 15:05:22
using SqlSugar;
using HSJM.AI.Core;
namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 程序块接口变量表
    /// </summary>
    [SugarTable("AI_PLC_BlockInterface",tableDescription:"程序块接口变量表")]
    public class PLC_BlockInterfaceEntity: BaseField
    {

       public  PLC_BlockInterfaceEntity()
       {
                    Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                         }
         [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
       public long Id{get;set;}
            /// <summary>
        ///  关联的程序块ID（外键）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="关联的程序块ID（外键）")]
        public long? BlockId {get;set;}
        
          /// <summary>
        ///  变量名称（如：General）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="变量名称（如：General）")]
        public string VariableName {get;set;}
        
          /// <summary>
        ///  变量完整路径（如：Static.General.StepNum）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="变量完整路径（如：Static.General.StepNum）")]
        public string VariablePath {get;set;}
        
          /// <summary>
        ///  数据类型（如：Int, Bool, UDT_General）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="数据类型（如：Int, Bool, UDT_General）")]
        public string DataType {get;set;}
        
          /// <summary>
        ///  变量作用域（如：IN, OUT, INOUT, STAT, TEMP, CONSTANT）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="变量作用域（如：IN, OUT, INOUT, STAT, TEMP, CONSTANT）")]
        public string Scope {get;set;}
        
          /// <summary>
        ///  变量注释
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="变量注释")]
        public string Comment {get;set;}
        
          /// <summary>
        ///  变量初始值
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="变量初始值")]
        public string InitialValue {get;set;}
        
          /// <summary>
        ///  保持性设置（如：Retain, NonRetain）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="保持性设置（如：Retain, NonRetain）")]
        public string Remanence {get;set;}
        
          /// <summary>
        ///  父级变量ID（用于表示结构体/UDT的层级关系）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="父级变量ID（用于表示结构体/UDT的层级关系）")]
        public long? ParentId {get;set;}
        
          /// <summary>
        ///  嵌套层级深度（0表示顶层）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="嵌套层级深度（0表示顶层）")]
        public int? NestingLevel {get;set;}
        
          /// <summary>
        ///  数组维度信息（如：[1..10, 0..5]）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="数组维度信息（如：[1..10, 0..5]）")]
        public string ArrayInfo {get;set;}
        
        /// <summary>
        ///  字节偏移量（DB变量的绝对字节偏移，用于S7通讯）
        /// </summary>
        [SugarColumn(IsNullable = true,ColumnDescription ="字节偏移量（DB变量的绝对字节偏移，用于S7通讯）")]
        public decimal? OffsetByte {get;set;}
        
        /// <summary>
        ///  位偏移量（0-7，仅Bool类型有效）
        /// </summary>
        [SugarColumn(IsNullable = true,ColumnDescription ="位偏移量（0-7，仅Bool类型有效）")]
        public decimal? OffsetBit {get;set;}
        
        /// <summary>
        ///  变量占用的大小（位数，Bool=1，Byte=8，Int=16等）
        /// </summary>
        [SugarColumn(IsNullable = true,ColumnDescription ="变量占用的大小（位数）")]
        public decimal? SizeBits {get;set;}
        
        /// <summary>
        ///  引用的UDT/FB类型名（当DataType引用UDT时记录原始类型名，如UDT_StationAutoStatus）
        /// </summary>
        [SugarColumn(IsNullable = true,ColumnDescription ="引用的UDT/FB类型名")]
        public string ReferencedTypeName {get;set;}

        /// <summary>
        /// 偏移量地址
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "偏移量地址")]
        public string Offset { get; set; }
        
    }

   
}
