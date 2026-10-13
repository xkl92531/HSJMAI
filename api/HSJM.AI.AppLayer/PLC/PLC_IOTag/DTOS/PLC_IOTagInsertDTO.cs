

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/2/7 15:05:32
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace HSJM.AI.AppLayer.PLC.DTOS
{
    /// <summary>
    /// I/O点位/全局变量表
    /// </summary>
    public class PLC_IOTagInsertDTO
    {
        /// <summary>
        ///  关联的项目ID（外键）
        /// </summary>
        [Description("关联的项目ID（外键）")]
        [Required]
        [Range(0, long.MaxValue)]
        public long? ProjectId { get; set; }

        /// <summary>
        ///  来源文件ID（如：PLC_Tags.csv）
        /// </summary>
        [Description("来源文件ID（如：PLC_Tags.csv）")]
        [Range(0, long.MaxValue)]
        public long? SourceFileId { get; set; }

        /// <summary>
        ///  变量标签名（如：OP070_CYL01_W）
        /// </summary>
        [Description("变量标签名（如：OP070_CYL01_W）")]
        [Required]
        [MinLength(0)]
        [MaxLength(200)]
        public string TagName { get; set; }

        /// <summary>
        ///  物理地址（如：%I4.0, %Q8.1, %MW100）
        /// </summary>
        [Description("物理地址（如：%I4.0, %Q8.1, %MW100）")]
        [MinLength(0)]
        [MaxLength(50)]
        public string Address { get; set; }

        /// <summary>
        ///  数据类型（如：Bool, Int, Real）
        /// </summary>
        [Description("数据类型（如：Bool, Int, Real）")]
        [MinLength(0)]
        [MaxLength(50)]
        public string DataType { get; set; }

        /// <summary>
        ///  变量所属标签表名称
        /// </summary>
        [Description("变量所属标签表名称")]
        [MinLength(0)]
        [MaxLength(100)]
        public string TableName { get; set; }

        /// <summary>
        ///  变量层级路径（如：Root）
        /// </summary>
        [Description("变量层级路径（如：Root）")]
        [MinLength(0)]
        [MaxLength(200)]
        public string Path { get; set; }

        /// <summary>
        ///  变量注释或功能描述
        /// </summary>
        [Description("变量注释或功能描述")]
        [MinLength(0)]
        [MaxLength(500)]
        public string Comment { get; set; }

        /// <summary>
        ///  是否为安全相关变量（True/False）
        /// </summary>
        [Description("是否为安全相关变量（True/False）")]
        public bool? IsSafety { get; set; }

        /// <summary>
        ///  是否具有断电保持性（True/False）
        /// </summary>
        [Description("是否具有断电保持性（True/False）")]
        public bool? IsRetentive { get; set; }

        /// <summary>
        ///  HMI画面是否可见
        /// </summary>
        [Description("HMI画面是否可见")]
        public bool? HmiVisible { get; set; }

        /// <summary>
        ///  HMI画面是否可写
        /// </summary>
        [Description("HMI画面是否可写")]
        public bool? HmiWritable { get; set; }

        /// <summary>
        ///  变量业务分类（如：Sensor, Actuator, Status, Alarm）
        /// </summary>
        [Description("变量业务分类（如：Sensor, Actuator, Status, Alarm）")]
        [MinLength(0)]
        [MaxLength(50)]
        public string TagCategory { get; set; }

        /// <summary>
        ///  关联的设备或机构ID
        /// </summary>
        [Description("关联的设备或机构ID")]
        [MinLength(0)]
        [MaxLength(50)]
        public string EquipmentId { get; set; }

        /// <summary>
        ///  标签正常状态下数据值
        /// </summary>
        [Description("标签正常状态下数据值")]
        [MinLength(0)]
        [MaxLength(50)]
        public string Normal_Value { get; set; }

        /// <summary>
        ///  标签正常状态下数值注解
        /// </summary>
        [Description("标签正常状态下数值注解")]
        [MinLength(0)]
        [MaxLength(1000)]
        public string Annotation { get; set; }

        /// <summary>
        ///  标签所属控制大类
        /// </summary>
        [Description("标签所属控制大类")]
        [MinLength(0)]
        [MaxLength(200)]
        public string TagType { get; set; }

        /// <summary>
        ///  标签所起作用
        /// </summary>
        [Description("标签所起作用")]
        public string TagEffect { get; set; }

        public string IoMapping { set; get; }
    }
}
