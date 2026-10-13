
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/2/7 15:05:03
using SqlSugar;
using HSJM.AI.Core;
namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 程序块表
    /// </summary>
    [SugarTable("AI_PLC_Block_new",tableDescription:"程序块表")]
    public class PLC_BlockEntity: BaseField
    {

       public  PLC_BlockEntity()
       {
                    Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                         }
         [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
       public long Id{get;set;}
            /// <summary>
        ///  关联的源文件ID（外键）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="关联的源文件ID（外键）")]
        public long? SourceFileId {get;set;}
        
          /// <summary>
        ///  关联的项目ID（冗余字段，便于直接查询）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="关联的项目ID（冗余字段，便于直接查询）")]
        public long? ProjectId {get;set;}
        
          /// <summary>
        ///  程序块名称（如：FB720_OP70Manual）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="程序块名称（如：FB720_OP70Manual）")]
        public string BlockName {get;set;}
        
          /// <summary>
        ///  程序块类型（如：FB, FC, OB, DB, UDT）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="程序块类型（如：FB, FC, OB, DB, UDT）")]
        public string BlockType {get;set;}
        
          /// <summary>
        ///  程序块编号（如：720，部分品牌可能无编号）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="程序块编号（如：720，部分品牌可能无编号）")]
        public int? BlockNumber {get;set;}
        
          /// <summary>
        ///  编程语言（如：LAD, FBD, STL, SCL, GRAPH）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="编程语言（如：LAD, FBD, STL, SCL, GRAPH）")]
        public string ProgrammingLanguage {get;set;}
        
          /// <summary>
        ///  程序块的功能描述或注释
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="程序块的功能描述或注释")]
        public string Description {get;set;}
        
          /// <summary>
        ///  是否为潜在入口块（True表示没有被其他块调用，如OB1）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="是否为潜在入口块（True表示没有被其他块调用，如OB1）")]
        public bool? IsPotentialEntry {get;set;}
        
          /// <summary>
        ///  该块调用其他块的数量（出度）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="该块调用其他块的数量（出度）")]
        public int? CallsOutCount {get;set;}
        
          /// <summary>
        ///  该块被其他块调用的数量（入度）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="该块被其他块调用的数量（入度）")]
        public int? CalledByCount {get;set;}
        
          /// <summary>
        ///  该块包含的Network/段落数量
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="该块包含的Network/段落数量")]
        public int? NetworksCount {get;set;}
        
          /// <summary>
        ///  扩展元数据（包含作者、版本、时间戳等信息的JSON对象）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="扩展元数据（包含作者、版本、时间戳等信息的JSON对象）")]
        public string MetadataJson {get;set;}

        [SugarColumn(IsNullable = true, ColumnDescription = "工位")]
        public string StationCode { get; set; }

        /// <summary>
        /// 本次上传解析任务ID（异步路径为 plc_upload_{projectId}_{时间戳}_{GUID}，同步路径为 plc_upload_sync_*）。
        /// 用于溯源该 Block 由哪次解析任务生成（PLC_UploadProgressService 的进度缓存是内存态，重启即丢，必须落库）。
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "上传解析任务ID（溯源用）")]
        public string ParseRunId { get; set; }

        /// <summary>
        /// GRAPH 内容 SHA256（graph-v1|块名|各GRAPH类LogicUnit的type/network_id/title/raw_content/outgoing_transition_numbers）。
        /// 仅 GRAPH 块计算（非 GRAPH 块为 null）。内容变更时哈希变化，供溯源与变更检测。
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "GRAPH内容SHA256哈希（仅GRAPH块）")]
        public string SourceHash { get; set; }

    }

   
}
