
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/2/7 15:05:13
using SqlSugar;
using HSJM.AI.Core;
namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 块调用关系表
    /// </summary>
    [SugarTable("AI_PLC_BlockCall",tableDescription:"块调用关系表")]
    public class PLC_BlockCallEntity: BaseField
    {

       public  PLC_BlockCallEntity()
       {
                    Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                         }
         [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
       public long Id{get;set;}
            /// <summary>
        ///  关联的项目ID（外键）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="关联的项目ID（外键）")]
        public long? ProjectId {get;set;}
        
          /// <summary>
        ///  发起调用的程序块ID（调用者）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="发起调用的程序块ID（调用者）")]
        public long? CallerBlockId {get;set;}
        
          /// <summary>
        ///  调用者块名称（冗余字段）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="调用者块名称（冗余字段）")]
        public string CallerBlockName {get;set;}
        
          /// <summary>
        ///  被调用的程序块名称（被调用者）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="被调用的程序块名称（被调用者）")]
        public string CalleeBlockName {get;set;}
        
          /// <summary>
        ///  被调用块的类型（如：FB, FC）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="被调用块的类型（如：FB, FC）")]
        public string CalleeBlockType {get;set;}
        
          /// <summary>
        ///  发生调用的逻辑单元ID
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="发生调用的逻辑单元ID")]
        public long? LogicUnitId {get;set;}
        
          /// <summary>
        ///  发生调用的Network编号
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="发生调用的Network编号")]
        public int? NetworkId {get;set;}
        
 
        
     

    }

   
}
