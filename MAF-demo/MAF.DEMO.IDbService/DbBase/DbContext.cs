using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SqlSugar;
using MAF.DEMO.Core;
using MAF.DEMO.Utility;
using Microsoft.Extensions.DependencyInjection;
using MAF.DEMO.IDbService.DbBase;
using System.Data;
using DbType = SqlSugar.DbType;
using System.Reflection;

namespace MAF.DEMO.IDbService
{
    /// <summary>
    /// 数据库上下文
    /// </summary>
    public class DbContext
    {
        /// <summary>
        /// 用来处理Db操作
        /// </summary>
        protected ISqlSugarClient Db;




        public DbTransaction BeginTran(IsolationLevel isolationLevel = IsolationLevel.ReadCommitted) {
            return new DbTransaction(Db, isolationLevel);
        }
        public void CommitTran()
        {
            Db.AsTenant().CommitTran();
        }
        public void RollbackTran()
        {
            Db.AsTenant().RollbackTran();
        }
        /// <summary>
        /// 获取某个对象中的属性值
        /// </summary>
        /// <param name="info"></param>
        /// <param name="field"></param>
        /// <returns></returns>
        public static object GetPropertyValue(object info, string field)
        {
            if (info == null) return null;
            Type t = info.GetType();
            IEnumerable<PropertyInfo> property = from pi in t.GetProperties() where pi.Name.ToLower() == field.ToLower() select pi;
            return property.First().GetValue(info, null);
        }

        /// <summary>
        /// 初始化数据库上下文实例
        /// </summary>
        public DbContext()
        {
            this.Db=CommonServiceProvider.ServiceProvider.GetService<ISqlSugarClient>();
        }

        /// <summary>
        /// 获取数据库链接集合
        /// </summary>
        /// <returns></returns>
        public List<DBConConnection> GetListConnectionConfig()
        {
            List<DBConConnection> dBConConnections = new List<DBConConnection>();
            var list = AppsettingsConfig.DBList;
            foreach (var item in list)
            {
                var conStr2 = item;
                DBConConnection dBConConnection = new DBConConnection();
                dBConConnection.ConfigId = item.ConfigId;
                ConnectionConfig con = new ConnectionConfig();
                string? connectionString = item.ConnectionString;
                //DBstring 支持加密识别
                if (connectionString != null && connectionString.Trim().Contains("Server=")) //表示DBstring没有加密
                {
                    connectionString = connectionString?.Trim();
                }
             
                if (string.IsNullOrEmpty(connectionString))
                {
                    throw new ArgumentNullException("数据库连接字符串为空");
                }

                con.ConnectionString = connectionString;

                con.DbType = UPdateType(item.DatabaseType);

                con.IsAutoCloseConnection = true;
                dBConConnection.ConnectionConfig = con;
                dBConConnections.Add(dBConConnection);
            }

            return dBConConnections;
        }

        /// <summary>
        /// 判断数据库类型
        /// </summary>
        /// <param name="type">数据库类型</param>
        /// <returns></returns>
        public DbType UPdateType(string type)
        {
            switch (type.ToLower())
            {
                case "mysql":
                    return DbType.MySql;
                case "sqlserver":
                    return DbType.SqlServer;
                case "sqlite":
                    return DbType.Sqlite;
                case "oracle":
                    return DbType.Oracle;
                case "postgresql":
                    return DbType.PostgreSQL;
                case "questdb":
                    return DbType.QuestDB;
                default:
                    return DbType.SqlServer;
            }
        }

        /// <summary>
        /// 获取数据库处理对象
        /// </summary>
        /// <typeparam name="T">数据库模型</typeparam>
        /// <returns>返回值</returns>
        public DbSet<T> GetDbSet<T>() where T : class, new()
        {
            return new DbSet<T>(Db);
        }

      
    }
}