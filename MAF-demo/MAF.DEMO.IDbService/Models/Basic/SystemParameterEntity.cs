
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2025/3/28 16:07:46
using SqlSugar;
using MAF.DEMO.Core;
namespace MAF.DEMO.IDbService
{
    /// <summary>
    /// 系统参数
    /// </summary>
    [SugarTable("Basic_SystemParameter",tableDescription:"系统参数")]
    public class SystemParameterEntity: BaseField
    {

       public  SystemParameterEntity()
       {
                    Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                         }
         [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
       public long Id{get;set;}
            /// <summary>
        ///  参数代码
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="参数代码")]
        public string ParamCode {get;set;}
        
          /// <summary>
        ///  参数名称
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="参数名称")]
        public string ParamName {get;set;}
        
          /// <summary>
        ///  参数值
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="参数值")]
        public string ParamValue {get;set;}
        
          /// <summary>
        ///  备注
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="备注")]
        public string Remarks {get;set;}
        
          /// <summary>
        ///  状态（正常_1、停用_2）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="状态（正常_1、停用_2）")]
        public int? Status {get;set;}
        
     

    }

   
}
