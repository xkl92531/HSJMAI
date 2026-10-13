
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/2/7 15:05:49
using SqlSugar;
using HSJM.AI.Core;
namespace HSJM.AI.IDbService
{
    /// <summary>
    /// PLC源文件表
    /// </summary>
    [SugarTable("AI_PLC_SourceFile",tableDescription:"PLC源文件表")]
    public class PLC_SourceFileEntity: BaseField
    {

       public  PLC_SourceFileEntity()
       {
                    Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                         }
         [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
       public long Id{get;set;}
            /// <summary>
        ///  关联的项目ID（外键）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="关联的项目ID（外键）")]
        public long? ProjectId {get;set;}
        
          /// <summary>
        ///  源文件名（包含扩展名，如：MainProgram.xml）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="源文件名（包含扩展名，如：MainProgram.xml）")]
        public string FileName {get;set;}
        
          /// <summary>
        ///  文件存储的完整路径或URL
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="文件存储的完整路径或URL")]
        public string FilePath {get;set;}
        
          /// <summary>
        ///  文件类型标识（如：XML, JSON, CSV, AWL）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="文件类型标识（如：XML, JSON, CSV, AWL）")]
        public string FileType {get;set;}
        
          /// <summary>
        ///  文件大小（字节为单位）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="文件大小（字节为单位）")]
        public long? FileSize {get;set;}
        
          /// <summary>
        ///  文件编码格式（如：UTF-8, GB2312）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="文件编码格式（如：UTF-8, GB2312）")]
        public string Encoding {get;set;}
        
          /// <summary>
        ///  文件内容的SHA256哈希值（用于检测文件是否变更）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="文件内容的SHA256哈希值（用于检测文件是否变更）")]
        public string FileHash {get;set;}
        
          /// <summary>
        ///  解析状态：0-待解析，1-解析成功，2-解析失败
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="解析状态：0-待解析，1-解析成功，2-解析失败")]
        public int? ParseStatus {get;set;}
        
          /// <summary>
        ///  解析完成时间
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="解析完成时间")]
        public DateTime? ParseTime {get;set;}
        
          /// <summary>
        ///  解析失败时的错误日志（JSON格式）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="解析失败时的错误日志（JSON格式）")]
        public string ParseErrors {get;set;}

        /// <summary>
        ///  GRAPH 块源 XML 原文（仅 ProgrammingLanguage=GRAPH 的块保留；
        ///  上传解析窗口内归档，供 LineUp PreviewFromBlock 免上传生成预览）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="GRAPH块源XML原文（仅GRAPH块保留，供LineUp预览复用）")]
        public string FileContent {get;set;}
        

     

    }

   
}
