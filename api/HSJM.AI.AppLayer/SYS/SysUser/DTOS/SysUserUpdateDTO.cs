using SqlSugar;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.SYS.SysUser.DTOS
{
    /// <summary>
    /// 用户账号
    /// </summary> 
    public class SysUserUpdateDTO
    {
        /// <summary>
        ///  ID
        /// </summary>
        [Description("ID")]
        [Required]
        [Range(0, long.MaxValue)]
        public long ID { get; set; }

        /// <summary>
        ///  账号
        /// </summary>
        [Description("账号")]
        [MinLength(0)]
        [MaxLength(20)]
        public string Account { get; set; }

        /// <summary>
        ///  密码
        /// </summary>
        [Description("密码")]
        [MinLength(0)]
        [MaxLength(500)]
        public string Password { get; set; }

        /// <summary>
        ///  工号
        /// </summary>
        [Description("工号")]
        [MinLength(0)]
        [MaxLength(20)]
        public string Code { get; set; }

        /// <summary>
        ///  姓名
        /// </summary>
        [Description("姓名")]
        [MinLength(0)]
        [MaxLength(50)]
        public string Name { get; set; }

        /// <summary>
        ///  性别
        /// </summary>
        [Description("性别")]
        [Range(0, int.MaxValue)]
        public int? Sex { get; set; }


        /// <summary>
        ///  头像地址
        /// </summary>
        [Description("头像地址")]
        [MinLength(0)]
        [MaxLength(500)]
        public string HeadUrl { get; set; }

        /// <summary>
        ///  手机号
        /// </summary>
        [Description("手机号")]
        [MinLength(0)]
        [MaxLength(20)]
        public string MobilePhone { get; set; }

        /// <summary>
        ///  邮箱
        /// </summary>
        [Description("邮箱")]
        [MinLength(0)]
        [MaxLength(100)]
        public string EMail { get; set; }

        /// <summary>
        ///  职位
        /// </summary>
        [Description("职位")]
        [Range(0, long.MaxValue)]
        public long? PositionID { get; set; }

        /// <summary>
        ///  职位名称
        /// </summary>
        [Description("职位名称")]
        [MinLength(0)]
        [MaxLength(50)]
        public string PositionName { get; set; }

        /// <summary>
        ///  备注
        /// </summary>
        [Description("备注")]
        [MinLength(0)]
        [MaxLength(500)]
        public string Remarks { get; set; }

        /// <summary>
        ///  最后登录IP
        /// </summary>
        [Description("最后登录IP")]
        [MinLength(0)]
        [MaxLength(50)]
        public string LastLoginIp { get; set; }

        /// <summary>
        ///  最后登录时间
        /// </summary>
        [Description("最后登录时间")]
        public DateTime? LastLoginTime { get; set; }

        /// <summary>
        ///  账号类型（超级管理员_1、非管理员_2）
        /// </summary>
        [Description("账号类型（超级管理员_1、非管理员_2）")]
        [Range(0, int.MaxValue)]
        public int? AdminType { get; set; }

        /// <summary>
        ///  状态（正常_1、停用_2）
        /// </summary>
        [Description("状态（正常_1、停用_2）")]
        [Range(0, int.MaxValue)]
        public int? Status { get; set; }

        /// <summary>
        ///  组织ID
        /// </summary>
        [Description("组织ID")]
        [Range(0, long.MaxValue)]
        public long? OUID { get; set; }

        /// <summary>
        ///  组织名称
        /// </summary>
        [Description("组织名称")]
        [MinLength(0)]
        [MaxLength(50)]
        public string OUName { get; set; }

        /// <summary>
        ///  组织全称
        /// </summary>
        [Description("组织全称")]
        [MinLength(0)]
        [MaxLength(500)]
        public string OUFullName { get; set; }

        /// <summary>
        /// 用户权限列表
        /// </summary>
        public List<long> UserRoleList { get; set; }
    }


    /// <summary>
    /// 修改密码
    /// </summary>
    public class UserChangePwdDTO
    {
        /// <summary>
        /// 原密码
        /// </summary>
        public string OldPwd { get; set; }
        /// <summary>
        /// 新密码
        /// </summary>
        public string NewPwd { get; set; }
        /// <summary>
        /// 确认密码
        /// </summary>
        public string ConfirmPwd { get; set; }
    }
}
