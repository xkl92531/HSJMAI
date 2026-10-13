using System;
using System.Collections.Generic;
using SqlSugar;

namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 数据库链接地址
    /// </summary>
    public static class DatabaseList
    {
        /// <summary>
        /// 链接地址集合
        /// </summary>
        public static List<DBConConnection> DbList { get; set; } = new List<DBConConnection>();
    }

    /// <summary>
    /// 
    /// </summary>
    public class DBConConnection
    {
        /// <summary>
        /// BaseKey
        /// </summary>
        public string ConfigId { get; set; }
        /// <summary>
        /// 数据库连接
        /// </summary>
        public ConnectionConfig ConnectionConfig { get; set; }
    }
}
