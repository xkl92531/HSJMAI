
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/02/29 10:05:46
using SqlSugar;
using HSJM.AI.Core;
namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 用户登录日志
    /// </summary>
    [SugarTable("LOG_Login")]
    public class LoginLogEntity : BaseField
    {

        public LoginLogEntity()
        {
            Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        }
        [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
        public long Id { get; set; }
        /// <summary>
        ///  用户ID
        /// </summary>
        [SugarColumn(IsNullable = true)]
        public long? UserID { get; set; }

        /// <summary>
        ///  用户名
        /// </summary>
        [SugarColumn(IsNullable = true)]
        public string Account { get; set; }

        /// <summary>
        ///  IP地址
        /// </summary>
        [SugarColumn(IsNullable = true)]
        public string LoginIP { get; set; }

        /// <summary>
        ///  登录类型（浏览器，手机端）
        /// </summary>
        [SugarColumn(IsNullable = true)]
        public string LoginType { get; set; }

        /// <summary>
        ///  操作系统
        /// </summary>
        [SugarColumn(IsNullable = true)]
        public string OSVersion { get; set; }

        /// <summary>
        ///  登录时间
        /// </summary>
        [SugarColumn(IsNullable = true)]
        public DateTime? LoginDate { get; set; }

        /// <summary>
        ///  地点（省份，城市）
        /// </summary>
        [SugarColumn(IsNullable = true)]
        public string Location { get; set; }

        /// <summary>
        ///  地理信息
        /// </summary>
        [SugarColumn(IsNullable = true)]
        public string Address { get; set; }



    }


}
