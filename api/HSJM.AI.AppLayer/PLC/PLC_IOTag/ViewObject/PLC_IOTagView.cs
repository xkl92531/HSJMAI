
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/2/7 15:05:32
using HSJM.AI.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace HSJM.AI.AppLayer.PLC.ViewObject
{
    /// <summary>
    /// I/O点位/全局变量表
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class PLC_IOTagView : ViewBaseField
    {
        /// <summary>
        ///  主键ID（雪花算法生成）
        /// </summary>
        [ImporterHeader(Name = "主键ID（雪花算法生成）", Format = ""), ExporterHeader("主键ID（雪花算法生成）")]
        public long Id { get; set; }

        /// <summary>
        ///  关联的项目ID（外键）
        /// </summary>
        [ImporterHeader(Name = "关联的项目ID（外键）", Format = ""), ExporterHeader("关联的项目ID（外键）")]
        public long? ProjectId { get; set; }

        /// <summary>
        ///  来源文件ID（如：PLC_Tags.csv）
        /// </summary>
        [ImporterHeader(Name = "来源文件ID（如：PLC_Tags.csv）", Format = ""), ExporterHeader("来源文件ID（如：PLC_Tags.csv）")]
        public long? SourceFileId { get; set; }

        /// <summary>
        ///  变量标签名（如：OP070_CYL01_W）
        /// </summary>
        [ImporterHeader(Name = "变量标签名（如：OP070_CYL01_W）", Format = ""), ExporterHeader("变量标签名（如：OP070_CYL01_W）")]
        public string TagName { get; set; }

        /// <summary>
        ///  物理地址（如：%I4.0, %Q8.1, %MW100）
        /// </summary>
        [ImporterHeader(Name = "物理地址（如：%I4.0, %Q8.1, %MW100）", Format = ""), ExporterHeader("物理地址（如：%I4.0, %Q8.1, %MW100）")]
        public string Address { get; set; }

        /// <summary>
        ///  数据类型（如：Bool, Int, Real）
        /// </summary>
        [ImporterHeader(Name = "数据类型（如：Bool, Int, Real）", Format = ""), ExporterHeader("数据类型（如：Bool, Int, Real）")]
        public string DataType { get; set; }

        /// <summary>
        ///  变量所属标签表名称
        /// </summary>
        [ImporterHeader(Name = "变量所属标签表名称", Format = ""), ExporterHeader("变量所属标签表名称")]
        public string TableName { get; set; }

        /// <summary>
        ///  变量层级路径（如：Root）
        /// </summary>
        [ImporterHeader(Name = "变量层级路径（如：Root）", Format = ""), ExporterHeader("变量层级路径（如：Root）")]
        public string Path { get; set; }

        /// <summary>
        ///  变量注释或功能描述
        /// </summary>
        [ImporterHeader(Name = "变量注释或功能描述", Format = ""), ExporterHeader("变量注释或功能描述")]
        public string Comment { get; set; }

        /// <summary>
        ///  是否为安全相关变量（True/False）
        /// </summary>
        [ImporterHeader(Name = "是否为安全相关变量（True/False）", Format = ""), ExporterHeader("是否为安全相关变量（True/False）")]
        public bool? IsSafety { get; set; }

        /// <summary>
        ///  是否具有断电保持性（True/False）
        /// </summary>
        [ImporterHeader(Name = "是否具有断电保持性（True/False）", Format = ""), ExporterHeader("是否具有断电保持性（True/False）")]
        public bool? IsRetentive { get; set; }

        /// <summary>
        ///  HMI画面是否可见
        /// </summary>
        [ImporterHeader(Name = "HMI画面是否可见", Format = ""), ExporterHeader("HMI画面是否可见")]
        public bool? HmiVisible { get; set; }

        /// <summary>
        ///  HMI画面是否可写
        /// </summary>
        [ImporterHeader(Name = "HMI画面是否可写", Format = ""), ExporterHeader("HMI画面是否可写")]
        public bool? HmiWritable { get; set; }

        /// <summary>
        ///  变量业务分类（如：Sensor, Actuator, Status, Alarm）
        /// </summary>
        [ImporterHeader(Name = "变量业务分类（如：Sensor, Actuator, Status, Alarm）", Format = ""), ExporterHeader("变量业务分类（如：Sensor, Actuator, Status, Alarm）")]
        public string TagCategory { get; set; }

        /// <summary>
        ///  关联的设备或机构ID
        /// </summary>
        [ImporterHeader(Name = "关联的设备或机构ID", Format = ""), ExporterHeader("关联的设备或机构ID")]
        public string EquipmentId { get; set; }

        /// <summary>
        ///  标签正常状态下数据值
        /// </summary>
        [ImporterHeader(Name = "标签正常状态下数据值", Format = ""), ExporterHeader("标签正常状态下数据值")]
        public string Normal_Value { get; set; }

        /// <summary>
        ///  标签正常状态下数值注解
        /// </summary>
        [ImporterHeader(Name = "标签正常状态下数值注解", Format = ""), ExporterHeader("标签正常状态下数值注解")]
        public string Annotation { get; set; }

        /// <summary>
        ///  标签所属控制大类
        /// </summary>
        [ImporterHeader(Name = "标签所属控制大类", Format = ""), ExporterHeader("标签所属控制大类")]
        public string TagType { get; set; }

        /// <summary>
        ///  标签所起作用
        /// </summary>
        [ImporterHeader(Name = "标签所起作用", Format = ""), ExporterHeader("标签所起作用")]
        public string TagEffect { get; set; }

    }
}
