
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:34:18
using SqlSugar;
using HSJM.AI.Core;
namespace HSJM.AI.IDbService
{
    /// <summary>
    /// MCP服务配置表
    /// </summary>
    [SugarTable("AI_Mcp_Server",tableDescription:"MCP服务配置表")]
    public class Mcp_ServerEntity: BaseField
    {

       public  Mcp_ServerEntity()
       {
                    Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                         }
         [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
       public long Id{get;set;}
            /// <summary>
        ///  服务名称
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="服务名称")]
        public string ServerName {get;set;}
        
          /// <summary>
        ///  服务类型(stdio/sse)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="服务类型(stdio/sse)")]
        public string ServerType {get;set;}
        
          /// <summary>
        ///  连接配置JSON(cmd+args 或 url)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="连接配置JSON(cmd+args 或 url)")]
        public string ConnectionConfig {get;set;}
        
          /// <summary>
        ///  描述
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="描述")]
        public string Description {get;set;}
        
          /// <summary>
        ///  是否启用
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="是否启用")]
        public bool? IsEnabled {get;set;}
        
     

    }

   
}
