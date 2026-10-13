using HSJM.AI.Core;
using SqlSugar;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HSJM.AI.IDbService.Models.System
{
    /// <summary>
    /// 用户账号表
    /// </summary>
    [SugarTable("SYS_User", TableDescription = "用户表")]
    public class SysUserEntity : BaseField
    {
        public SysUserEntity()
        {
            Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        }
        [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
        public long Id { get; set; }
        /// <summary>
        ///  账号
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "账号")]
        public string Account { get; set; }

        /// <summary>
        ///  密码
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "密码")]
        public string Password { get; set; }

        /// <summary>
        ///  工号
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "工号")]
        public string Code { get; set; }

        /// <summary>
        ///  姓名
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "姓名")]
        public string Name { get; set; }

        /// <summary>
        ///  性别
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "性别")]
        public int? Sex { get; set; }

        /// <summary>
        ///  头像地址
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "头像地址")]
        public string HeadUrl { get; set; }

        /// <summary>
        ///  手机号
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "手机号")]
        public string MobilePhone { get; set; }

        /// <summary>
        ///  邮箱
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "邮箱")]
        public string EMail { get; set; }

        /// <summary>
        ///  职位
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "职位")]
        public long? PositionID { get; set; }

        /// <summary>
        ///  职位名称
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "职位名称")]
        public string PositionName { get; set; }

        /// <summary>
        ///  备注
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "备注")]
        public string Remarks { get; set; }

        /// <summary>
        ///  最后登录IP
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "最后登录IP")]
        public string LastLoginIp { get; set; }

        /// <summary>
        ///  最后登录时间
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "最后登录时间")]
        public DateTime? LastLoginTime { get; set; }

        /// <summary>
        ///  账号类型（超级管理员_1、非管理员_2）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "账号类型（超级管理员_1、非管理员_2）")]
        public int? AdminType { get; set; }

        /// <summary>
        ///  状态（正常_1、停用_2）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "状态（正常_1、停用_2）")]
        public int? Status { get; set; }

        /// <summary>
        ///  组织ID
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "组织ID")]
        public long? OUID { get; set; }

        /// <summary>
        ///  组织名称
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "组织名称")]
        public string OUName { get; set; }

        /// <summary>
        ///  组织全称
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "组织全称")]
        public string OUFullName { get; set; }
    }
}