
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:34:50
using SqlSugar;
using HSJM.AI.Core;
namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 记录 Prompt 拼装过程，方便复现最终 Prompt 及所选模板/片段。
    /// </summary>
    [SugarTable("AI_Prompt_Assembly_Log",tableDescription:"记录 Prompt 拼装过程，方便复现最终 Prompt 及所选模板/片段。")]
    public class Prompt_Assembly_LogEntity: BaseField
    {

       public  Prompt_Assembly_LogEntity()
       {
                    Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                         }
         [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
       public long Id{get;set;}
            /// <summary>
        ///  场景标识，例如 Parsing、Diagnosis、Planning。
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="场景标识，例如 Parsing、Diagnosis、Planning。")]
        public string Scenario {get;set;}
        
          /// <summary>
        ///  品牌或客户筛选条件。
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="品牌或客户筛选条件。")]
        public string Brand {get;set;}
        
          /// <summary>
        ///  Prompt 输出语言。
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="Prompt 输出语言。")]
        public string Language {get;set;}
        
          /// <summary>
        ///  任务动作，例如 ParseBlock/Diagnose。
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="任务动作，例如 ParseBlock/Diagnose。")]
        public string Action {get;set;}
        
          /// <summary>
        ///  使用的模板 ID。
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="使用的模板 ID。")]
        public long? TemplateID {get;set;}
        
          /// <summary>
        ///  拼装的片段 ID 列表。
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="拼装的片段 ID 列表。")]
        public string FragmentIDs {get;set;}
        
          /// <summary>
        ///  Few-shot 示例 ID 列表。
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="Few-shot 示例 ID 列表。")]
        public string FewShotIDs {get;set;}
        
          /// <summary>
        ///  最终发送给模型的 Prompt 文本。
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="最终发送给模型的 Prompt 文本。")]
        public string FinalPrompt {get;set;}
        
          /// <summary>
        ///  最终 Prompt 的 token 数。
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="最终 Prompt 的 token 数。")]
        public int? PromptTokenCount {get;set;}
        
          /// <summary>
        ///  拼装耗时（毫秒）。
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="拼装耗时（毫秒）。")]
        public int? AssemblyTimeMs {get;set;}
        
          /// <summary>
        ///  回退层级：0 精确、1 一级、2 二级。
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="回退层级：0 精确、1 一级、2 二级。")]
        public int? FallbackLevel {get;set;}
        
          /// <summary>
        ///  关联的模型运行日志 ID。
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="关联的模型运行日志 ID。")]
        public long? RuntimeLogID {get;set;}
        
          /// <summary>
        ///  备注信息。
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="备注信息。")]
        public string Remark {get;set;}
        
     

    }

   
}
