using MAF.DEMO.Core.CustomAttribute;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MAF.DEMO.Core
{
    /// <summary>
    /// 数据字典
    /// </summary>
    public class DataDictionary
    {
        /// <summary>
        /// 代码
        /// </summary>
        public class Code
        {
            /// <summary>
            /// 通用状态
            /// </summary>
            public class Status
            {
                /// <summary>
                /// 正常
                /// </summary>
                public const int 正常 = 1;
                /// <summary>
                /// 停用
                /// </summary>
                public const int 停用 = 2;

            }

            /// <summary>
            /// 组织类别
            /// </summary>
            public class OUType
            {
                /// <summary>
                /// 公司
                /// </summary>
                public const int 公司 = 1;
                /// <summary>
                /// 部门
                /// </summary>
                public const int 部门 = 2;

            }

            /// <summary>
            /// 性别
            /// </summary>
            public class Sex
            {
                /// <summary>
                /// 男
                /// </summary>
                public const int 男 = 1;
                /// <summary>
                /// 女
                /// </summary>
                public const int 女 = 2;
                /// <summary>
                /// 保密
                /// </summary>
                public const int 保密 = 3;
            }

            /// <summary>
            /// 语言
            /// </summary>
            public class Language
            {
                /// <summary>
                /// 中文
                /// </summary>
                public const string ZHCN = "zh-cn";
                /// <summary>
                /// 英文
                /// </summary>
                public const string ZHEN = "en";
                /// <summary>
                /// 繁体
                /// </summary>
                public const string ZHTW = "zh-tw";
            }

        }
    }


    /// <summary>
    /// 角色类型
    /// </summary>
    public enum AccountTypeEnum
    {

        /// <summary>
        /// 其他
        /// </summary>
        [Description("其他")]
        None = 0,
        /// <summary>
        /// 普通管理员
        /// </summary>
        [Description("普通管理员")]
        GeneralAdministrator = 1,
        /// <summary>
        /// 普通管理员
        /// </summary>
        [Description("系统用户")]
        SystemUser = 2,
        /// <summary>
        /// 超级管理员
        /// </summary>
        [Description("超级管理员")]
        SuperAdministrator = 999
    }

    /// <summary>
    /// 员工状态
    /// </summary>
    public enum EmpStatusEnum
    {
        /// <summary>
        /// 在职
        /// </summary> 
        [MyLanguage("正式员工", "Regular employee", "正式員工")]
        正式员工 = 1,
        /// <summary>
        /// 在职
        /// </summary> 
        [MyLanguage("实习期", "Internship period", "實習期")]
        实习期 = 2,
        /// <summary>
        /// 在职
        /// </summary> 
        [MyLanguage("试用期", "probation period", "試用期")]
        试用期 = 3,
        /// <summary>
        /// 离职
        /// </summary>
        [MyLanguage("离职", "Depart", "離職")]
        离职 = 4
    }

    /// <summary>
    /// 员工状态
    /// </summary>
    public enum SexEnum
    {
        /// <summary>
        /// 在职
        /// </summary> 
        [MyLanguage("男", "Man", "男")]
        男 = 1,
        /// <summary>
        /// 在职
        /// </summary> 
        [MyLanguage("女", "Girl", "女")]
        女 = 2,
        /// <summary>
        /// 在职
        /// </summary> 
        [MyLanguage("保密", "Secrecy", "保密")]
        保密 = 3,
    }

    /// <summary>
    /// 操作类型
    /// </summary>
    public class OperateType
    {
        /// <summary>
        /// 新增
        /// </summary>
        public const string Add = "Add";
        /// <summary>
        /// 编辑
        /// </summary>
        public const string Edit = "Edit";
        /// <summary>
        /// 删除
        /// </summary>
        public const string Del = "Del";
    }
}
