using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Magicodes.ExporterAndImporter.Core;

namespace HSJM.AI.AppLayer.SYS.SysUser.DTOS
{
    /// <summary>
    /// 用户账号
    /// </summary>
    public class SysUserView : ViewBaseField
    {
        /// <summary>
        ///  ID
        /// </summary>
        [ImporterHeader(Name = "ID", Format = ""), ExporterHeader("ID")]
        public long ID { get; set; }

        /// <summary>
        ///  账号
        /// </summary>
        [ImporterHeader(Name = "账号", Format = ""), ExporterHeader("账号")]
        public string Account { get; set; }

        /// <summary>
        ///  密码
        /// </summary>
        [ImporterHeader(Name = "密码", Format = ""), ExporterHeader("密码")]
        public string Password { get; set; }

        /// <summary>
        ///  工号
        /// </summary>
        [ImporterHeader(Name = "工号", Format = ""), ExporterHeader("工号")]
        public string Code { get; set; }

        /// <summary>
        ///  姓名
        /// </summary>
        [ImporterHeader(Name = "姓名", Format = ""), ExporterHeader("姓名")]
        public string Name { get; set; }

        /// <summary>
        ///  性别
        /// </summary>
        [ImporterHeader(Name = "性别", Format = ""), ExporterHeader("性别")]
        public int? Sex { get; set; }


        /// <summary>
        ///  头像地址
        /// </summary>
        [ImporterHeader(Name = "头像地址", Format = ""), ExporterHeader("头像地址")]
        public string HeadUrl { get; set; }

        /// <summary>
        ///  手机号
        /// </summary>
        [ImporterHeader(Name = "手机号", Format = ""), ExporterHeader("手机号")]
        public string MobilePhone { get; set; }

        /// <summary>
        ///  邮箱
        /// </summary>
        [ImporterHeader(Name = "邮箱", Format = ""), ExporterHeader("邮箱")]
        public string EMail { get; set; }

        /// <summary>
        ///  职位
        /// </summary>
        [ImporterHeader(Name = "职位", Format = ""), ExporterHeader("职位")]
        public long? PositionID { get; set; }

        /// <summary>
        ///  职位名称
        /// </summary>
        [ImporterHeader(Name = "职位名称", Format = ""), ExporterHeader("职位名称")]
        public string PositionName { get; set; }

        /// <summary>
        ///  备注
        /// </summary>
        [ImporterHeader(Name = "备注", Format = ""), ExporterHeader("备注")]
        public string Remarks { get; set; }

        /// <summary>
        ///  最后登录IP
        /// </summary>
        [ImporterHeader(Name = "最后登录IP", Format = ""), ExporterHeader("最后登录IP")]
        public string LastLoginIp { get; set; }

        /// <summary>
        ///  最后登录时间
        /// </summary>
        [ImporterHeader(Name = "最后登录时间", Format = ""), ExporterHeader("最后登录时间")]
        public DateTime? LastLoginTime { get; set; }

        /// <summary>
        ///  账号类型（超级管理员_1、非管理员_2）
        /// </summary>
        [ImporterHeader(Name = "账号类型（超级管理员_1、非管理员_2）", Format = ""), ExporterHeader("账号类型（超级管理员_1、非管理员_2）")]
        public int? AdminType { get; set; }

        /// <summary>
        ///  状态（正常_1、停用_2）
        /// </summary>
        [ImporterHeader(Name = "状态（正常_1、停用_2）", Format = ""), ExporterHeader("状态（正常_1、停用_2）")]
        public int? Status { get; set; }

        /// <summary>
        ///  组织ID
        /// </summary>
        [ImporterHeader(Name = "组织ID", Format = ""), ExporterHeader("组织ID")]
        public long? OUID { get; set; }

        /// <summary>
        ///  组织名称
        /// </summary>
        [ImporterHeader(Name = "组织名称", Format = ""), ExporterHeader("组织名称")]
        public string OUName { get; set; }

        /// <summary>
        ///  组织全称
        /// </summary>
        [ImporterHeader(Name = "组织全称", Format = ""), ExporterHeader("组织全称")]
        public string OUFullName { get; set; }


        /// <summary>
        /// 用户权限列表
        /// </summary>
        public List<long> UserRoleList { get; set; }
    }
}
