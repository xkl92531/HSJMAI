

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:36:03
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace HSJM.AI.AppLayer.Agent.DTOS
{
    /// <summary>
    /// 任务依赖关系表
    /// </summary>
    public class Task_DependencyUpdateDTO
    {
            /// <summary>
        ///  主键ID
        /// </summary>
        [Description("主键ID")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long ID {get;set;}
        
          /// <summary>
        ///  所属工作流ID(冗余字段，方便查询)
        /// </summary>
        [Description("所属工作流ID(冗余字段，方便查询)")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long? WorkflowID {get;set;}
        
          /// <summary>
        ///  前置任务ID
        /// </summary>
        [Description("前置任务ID")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long? PredecessorTaskID {get;set;}
        
          /// <summary>
        ///  后置任务ID
        /// </summary>
        [Description("后置任务ID")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long? SuccessorTaskID {get;set;}
        
          /// <summary>
        ///  依赖类型: 0-FinishToStart(完成即开始), 1-Conditional(条件触发)
        /// </summary>
        [Description("依赖类型: 0-FinishToStart(完成即开始), 1-Conditional(条件触发)")]
                  [Required]
                   [Range(0, int.MaxValue)]
                  public int? DependencyType {get;set;}
        
     

    }
}
