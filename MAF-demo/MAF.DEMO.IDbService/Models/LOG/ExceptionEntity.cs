
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/02/29 11:10:31
using SqlSugar;
using MAF.DEMO.Core;
namespace MAF.DEMO.IDbService
{
    /// <summary>
    /// 异常日志
    /// </summary>
    [SugarTable("LOG_Exception")]
    public class ExceptionEntity: BaseField
    {

       public  ExceptionEntity()
       {
                    Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                         }
         [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
       public long Id{get;set;}
            /// <summary>
        ///  名称
        /// </summary>
                   [SugarColumn(IsNullable = true)]
        public string Name {get;set;}
        
          /// <summary>
        ///  类名称
        /// </summary>
                   [SugarColumn(IsNullable = true)]
        public string ClassName {get;set;}
        
          /// <summary>
        ///  方法名称
        /// </summary>
                   [SugarColumn(IsNullable = true)]
        public string MethodName {get;set;}
        
          /// <summary>
        ///  异常名称
        /// </summary>
                   [SugarColumn(IsNullable = true)]
        public string ExceptionName {get;set;}
        
          /// <summary>
        ///  异常信息
        /// </summary>
                   [SugarColumn(IsNullable = true)]
        public string ExceptionMsg {get;set;}
        
          /// <summary>
        ///  异常源
        /// </summary>
                   [SugarColumn(IsNullable = true)]
        public string ExceptionSource {get;set;}
        
          /// <summary>
        ///  堆栈信息
        /// </summary>
                   [SugarColumn(IsNullable = true)]
        public string StackTrace {get;set;}
        
          /// <summary>
        ///  参数对象
        /// </summary>
                   [SugarColumn(IsNullable = true)]
        public string ParamsObj {get;set;}
        
          /// <summary>
        ///  异常时间
        /// </summary>
                   [SugarColumn(IsNullable = true)]
        public DateTime? ExceptionTime {get;set;}
        
          /// <summary>
        ///  操作人
        /// </summary>
                   [SugarColumn(IsNullable = true)]
        public string Account {get;set;}
        
     

    }

   
}
