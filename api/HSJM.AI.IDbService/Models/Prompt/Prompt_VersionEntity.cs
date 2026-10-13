
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:35:26
using SqlSugar;
using HSJM.AI.Core;
namespace HSJM.AI.IDbService
{
    /// <summary>
    /// Prompt版本管理表
    /// </summary>
    [SugarTable("AI_Prompt_Version",tableDescription:"Prompt版本管理表")]
    public class Prompt_VersionEntity: BaseField
    {

       public  Prompt_VersionEntity()
       {
                    Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                         }
         [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
       public long Id{get;set;}
            /// <summary>
        ///  关联的角色ID
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="关联的角色ID")]
        public long? RoleID {get;set;}
        
          /// <summary>
        ///  版本号
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="版本号")]
        public string VersionNumber {get;set;}
        
          /// <summary>
        ///  Prompt内容
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="Prompt内容")]
        public string SystemPromptContent {get;set;}
        
          /// <summary>
        ///  变更说明
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="变更说明")]
        public string ChangeLog {get;set;}
        
          /// <summary>
        ///  是否当前激活版本
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="是否当前激活版本")]
        public bool? IsActive {get;set;}
        
          /// <summary>
        ///  是否参与A/B测试
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="是否参与A/B测试")]
        public bool? IsABTest {get;set;}
        
          /// <summary>
        ///  流量分配比例
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="流量分配比例")]
        public int? TrafficRatio {get;set;}
        
          /// <summary>
        ///  该版本的平均评分
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="该版本的平均评分")]
        public decimal? AverageScore {get;set;}
        
          /// <summary>
        ///  使用次数
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="使用次数")]
        public int? UsageCount {get;set;}
        
     

    }

   
}
