
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:24:25
using SqlSugar;
using HSJM.AI.Core;
namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 智能体关系表
    /// </summary>
    [SugarTable("AI_Agent_Relation",tableDescription:"智能体关系表")]
    public class Agent_RelationEntity: BaseField
    {

       public  Agent_RelationEntity()
       {
                    Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                         }
         [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
       public long Id{get;set;}
            /// <summary>
        ///  父智能体ID (Manager/Supervisor)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="父智能体ID (Manager/Supervisor)")]
        public long? ParentRoleID {get;set;}
        
          /// <summary>
        ///  子智能体ID (Worker/Subordinate)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="子智能体ID (Worker/Subordinate)")]
        public long? ChildRoleID {get;set;}
        
          /// <summary>
        ///  关系类型: Supervisor, Member, Delegate
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="关系类型: Supervisor, Member, Delegate")]
        public string RelationType {get;set;}
        
          /// <summary>
        ///  关系描述
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="关系描述")]
        public string Description {get;set;}
        
     

    }

   
}
