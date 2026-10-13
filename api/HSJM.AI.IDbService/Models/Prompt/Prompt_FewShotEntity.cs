
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:34:56
using SqlSugar;
using HSJM.AI.Core;
namespace HSJM.AI.IDbService
{
    /// <summary>
    /// Few-shot 样例
    /// </summary>
    [SugarTable("AI_Prompt_FewShot",tableDescription:"Few-shot 样例")]
    public class Prompt_FewShotEntity: BaseField
    {

       public  Prompt_FewShotEntity()
       {
                    Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                         }
         [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
       public long Id{get;set;}
            /// <summary>
        ///  应用场景
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="应用场景")]
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
        ///  语言
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="语言")]
        public string Language {get;set;}
        
          /// <summary>
        ///  块类型
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="块类型")]
        public string BlockType {get;set;}
        
          /// <summary>
        ///  操作/任务类型
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="操作/任务类型")]
        public string Action {get;set;}
        
          /// <summary>
        ///  样例类型（正例/反例等）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="样例类型（正例/反例等）")]
        public string ExampleType {get;set;}
        
          /// <summary>
        ///  输入摘要
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="输入摘要")]
        public string InputSummary {get;set;}
        
          /// <summary>
        ///  输出摘要
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="输出摘要")]
        public string OutputSummary {get;set;}
        
          /// <summary>
        ///  样例 JSON 内容
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="样例 JSON 内容")]
        public string ExampleJson {get;set;}
        
          /// <summary>
        ///  Embedding 模型
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="Embedding 模型")]
        public string EmbeddingModel {get;set;}
        
          /// <summary>
        ///  向量库ID/标识
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="向量库ID/标识")]
        public string VectorID {get;set;}
        
          /// <summary>
        ///  相似度哈希/指纹
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="相似度哈希/指纹")]
        public string SimilarityHash {get;set;}
        
          /// <summary>
        ///  扩展元数据 JSON
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="扩展元数据 JSON")]
        public string MetadataJson {get;set;}
        
     

    }

   
}
