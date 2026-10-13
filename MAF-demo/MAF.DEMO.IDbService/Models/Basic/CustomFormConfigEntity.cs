
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/3/7 11:29:06
using SqlSugar;
using MAF.DEMO.Core;
namespace MAF.DEMO.IDbService
{
    /// <summary>
    /// 用户表单列配置
    /// </summary>
    [SugarTable("Basic_CustomFormConfig", TableDescription = "用户表单列配置 ")]
    public class CustomFormConfigEntity : BaseField
    {

        public CustomFormConfigEntity()
        {
            Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        }
        [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
        public long Id { get; set; }
        /// <summary>
        ///  表单Code
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "表单Code")]
        public string FormCode { get; set; }

        /// <summary>
        ///  用户ID
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "用户ID")]
        public long? UserId { get; set; }

        /// <summary>
        ///  表单Code
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "表单Code")]
        public string FormColumnJson { get; set; }

        /// <summary>
        ///  预留1
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "预留1")]
        public string Attr1 { get; set; }

        /// <summary>
        ///  预留2
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "预留2")]
        public string Attr2 { get; set; }

        /// <summary>
        ///  预留3
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "预留3")]
        public string Attr3 { get; set; }

        /// <summary>
        ///  预留4
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "预留4")]
        public string Attr4 { get; set; }



    }


}
