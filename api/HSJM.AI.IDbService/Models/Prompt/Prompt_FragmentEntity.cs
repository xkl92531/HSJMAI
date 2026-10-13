
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:35:04
using SqlSugar;
using HSJM.AI.Core;
namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 提示词片段
    /// </summary>
    [SugarTable("AI_Prompt_Fragment",tableDescription:"提示词片段")]
    public class Prompt_FragmentEntity: BaseField
    {

       public  Prompt_FragmentEntity()
       {
                    Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                         }
         [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
       public long Id{get;set;}
            /// <summary>
        ///  片段类型（Persona/Instruction 等）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="片段类型（Persona/Instruction 等）")]
        public string FragmentType {get;set;}
        
          /// <summary>
        ///  标签/用途标识
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="标签/用途标识")]
        public string Tag {get;set;}
        
          /// <summary>
        ///  片段内容
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="片段内容")]
        public string Content {get;set;}
        
          /// <summary>
        ///  片段版本号
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="片段版本号")]
        public string Version {get;set;}
        
          /// <summary>
        ///  是否启用
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="是否启用")]
        public bool? IsActive {get;set;}
        
          /// <summary>
        ///  扩展元数据 JSON
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="扩展元数据 JSON")]
        public string MetadataJson {get;set;}
        
     

    }

   
}
