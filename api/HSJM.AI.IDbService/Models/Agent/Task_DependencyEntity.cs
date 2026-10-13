
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:36:03
using SqlSugar;
using HSJM.AI.Core;
namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 任务依赖关系表
    /// </summary>
    [SugarTable("AI_Task_Dependency",tableDescription:"任务依赖关系表")]
    public class Task_DependencyEntity: BaseField
    {

       public  Task_DependencyEntity()
       {
                    Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                         }
         [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
       public long Id{get;set;}
            /// <summary>
        ///  所属工作流ID(冗余字段，方便查询)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="所属工作流ID(冗余字段，方便查询)")]
        public long? WorkflowID {get;set;}
        
          /// <summary>
        ///  前置任务ID
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="前置任务ID")]
        public long? PredecessorTaskID {get;set;}
        
          /// <summary>
        ///  后置任务ID
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="后置任务ID")]
        public long? SuccessorTaskID {get;set;}
        
          /// <summary>
        ///  依赖类型: 0-FinishToStart(完成即开始), 1-Conditional(条件触发)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="依赖类型: 0-FinishToStart(完成即开始), 1-Conditional(条件触发)")]
        public int? DependencyType {get;set;}
        
     

    }

   
}
