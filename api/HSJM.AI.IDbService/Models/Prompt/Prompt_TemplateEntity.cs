
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:35:18
using SqlSugar;
using HSJM.AI.Core;
namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 提示词模板
    /// </summary>
    [SugarTable("AI_Prompt_Template",tableDescription:"提示词模板")]
    public class Prompt_TemplateEntity: BaseField
    {

       public  Prompt_TemplateEntity()
       {
                    Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                         }
         [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
       public long Id{get;set;}
            /// <summary>
        ///  应用场景（Parsing/Diagnosis 等）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="应用场景（Parsing/Diagnosis 等）")]
        public string Scenario {get;set;}
        
          /// <summary>
        ///  适用品牌
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="适用品牌")]
        public string Brand {get;set;}
        
          /// <summary>
        ///  适用型号/系列
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="适用型号/系列")]
        public string Model {get;set;}
        
          /// <summary>
        ///  语言（指令/自然语言）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="语言（指令/自然语言）")]
        public string Language {get;set;}
        
          /// <summary>
        ///  适用块类型 OB/FB/FC/DB 等
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="适用块类型 OB/FB/FC/DB 等")]
        public string BlockType {get;set;}
        
          /// <summary>
        ///  操作/任务类型
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="操作/任务类型")]
        public string Action {get;set;}
        
          /// <summary>
        ///  模板唯一键
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="模板唯一键")]
        public string PromptKey {get;set;}
        
          /// <summary>
        ///  模板版本号
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="模板版本号")]
        public string Version {get;set;}
        
          /// <summary>
        ///  父模板ID
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="父模板ID")]
        public long? ParentTemplateID {get;set;}
        
          /// <summary>
        ///  回退层级（数字越大越靠后）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="回退层级（数字越大越靠后）")]
        public int? FallbackLevel {get;set;}
        
          /// <summary>
        ///  优先级（大值优先）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="优先级（大值优先）")]
        public int? Priority {get;set;}
        
          /// <summary>
        ///  状态 0-草稿 1-启用 2-停用
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="状态 0-草稿 1-启用 2-停用")]
        public int? Status {get;set;}
        
          /// <summary>
        ///  提示词正文内容
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="提示词正文内容")]
        public string Content {get;set;}
        
          /// <summary>
        ///  约束配置 JSON
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="约束配置 JSON")]
        public string ConstraintsJson {get;set;}
        
          /// <summary>
        ///  输出结构定义
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="输出结构定义")]
        public string OutputSchema {get;set;}
        
          /// <summary>
        ///  扩展元数据 JSON
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="扩展元数据 JSON")]
        public string MetadataJson {get;set;}
        
     

    }

   
}
