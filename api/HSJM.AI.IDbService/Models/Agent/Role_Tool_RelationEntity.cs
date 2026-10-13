
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:35:38
using SqlSugar;
using HSJM.AI.Core;
namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 角色-工具关联表
    /// </summary>
    [SugarTable("AI_Role_Tool_Relation",tableDescription:"角色-工具关联表")]
    public class Role_Tool_RelationEntity: BaseField
    {

       public  Role_Tool_RelationEntity()
       {
                    Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                         }
         [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
       public long Id{get;set;}
            /// <summary>
        ///  角色ID
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="角色ID")]
        public long? RoleID {get;set;}
        
          /// <summary>
        ///  工具ID
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="工具ID")]
        public long? ToolID {get;set;}
        
          /// <summary>
        ///  针对该角色的特定配置覆盖(JSON)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="针对该角色的特定配置覆盖(JSON)")]
        public string OverrideConfig {get;set;}
        
     

    }

   
}
