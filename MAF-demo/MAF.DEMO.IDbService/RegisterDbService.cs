using MAF.DEMO.Core;
using MAF.DEMO.Utility;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace MAF.DEMO.IDbService
{
    public class IDbRegisterService
    {
        public static void RegisterService(IServiceCollection services)
        {
            services.AddScoped<ISqlSugarClient>(s =>
            {
                ConnectionConfig connectionConfig = new ConnectionConfig();
                //判断DbList是否为空
                if (DatabaseList.DbList.Count > 0)
                {
                    connectionConfig = DatabaseList.DbList.First(a => a.ConfigId == "DefultDataBase")
                        .ConnectionConfig; //注入默认数据库连接
                }
                else
                {
                    //获取appsettings.json中的链接数据库数组
                    DatabaseList.DbList = ListGetConnectionConfig();
                    //选择默认数据库
                    connectionConfig = DatabaseList.DbList.First(a => a.ConfigId == "DefultDataBase")
                        .ConnectionConfig; //注入默认数据库连接
                }

                SqlSugarScope sqlSugar = new SqlSugarScope(connectionConfig,
                    db =>
                    {
                        // db.CodeFirst.InitTables(typeof(DataDetailEntity));
                        // db.CodeFirst.InitTables(typeof(DataLogEntity));
                        // 如果需要更多的初始化操作，比如创建索引，可以在这里添加
                        db.Aop.DataExecuting = (oldValue, entityInfo) =>
                        {
                            if (entityInfo.OperationType == DataFilterType.InsertByObject)
                            {
                                if (entityInfo.PropertyName == "CreateTime")
                                {
                                    entityInfo.SetValue(DateTime.Now);
                                }

                                if (entityInfo.PropertyName == "CreaterName")
                                {
                                    entityInfo.SetValue(CurrentUserInfo.Instance.UserName);
                                }

                                if (entityInfo.PropertyName == "CreaterId")
                                {
                                    entityInfo.SetValue(CurrentUserInfo.Instance.UserID);
                                }
                            }

                            if (entityInfo.OperationType == DataFilterType.UpdateByObject)
                            {
                                if (entityInfo.PropertyName == "ModifyTime")
                                {
                                    entityInfo.SetValue(DateTime.Now);
                                }

                                if (entityInfo.PropertyName == "ModifierName")
                                {
                                    entityInfo.SetValue(CurrentUserInfo.Instance.UserName);
                                }

                                if (entityInfo.PropertyName == "ModifierId")
                                {
                                    entityInfo.SetValue(CurrentUserInfo.Instance.UserID);
                                }
                            }


                            //if (entityInfo.PropertyName == "CreateTime" && entityInfo.OperationType == DataFilterType.InsertByObject)
                            //{
                            //    var propNameVal = GetPropertyValue(entityInfo.EntityValue, entityInfo.PropertyName);
                            //    if (propNameVal == null)
                            //    {
                            //        entityInfo.SetValue(DateTime.Now);//修改UpdateTime字段
                            //    }
                            //    /*当前列获取特性*/
                            //    //5.1.3.23 +
                            //    //entityInfo.IsAnyAttribute<特性>()
                            //    //entityInfo.GetAttribute<特性>()
                            //}
                            //// 更新创建人 和 更新人信息,从请求token获取对应的登录人信息
                            ///*** 列级别事件 ：更新的每一列都会进事件 ***/
                            //if (entityInfo.PropertyName == "ModifyTime")
                            //{
                            //    entityInfo.SetValue(DateTime.Now);//修改UpdateTime字段
                            //}


                            ///*** 行级别事件 ：更新一条记录只进一次 ***/
                            //if (entityInfo.EntityColumnInfo.IsPrimarykey)
                            //{
                            //    //entityInfo.EntityValue 拿到单条实体对象
                            //}

                            /***  根据当前列修改另一列 ***/
                            //if(当前列逻辑==XXX)
                            //var properyDate = entityInfo.EntityValue.GetType().GetProperty("Date");
                            //if(properyDate!=null)
                            //properyDate.SetValue(entityInfo.EntityValue,1);


                            //可以写多个IF
                        };
                        //执行SQL前触发
                        db.Aop.OnLogExecuting = (sql, pars) =>
                        {
                            //Console.WriteLine(sql + "\r\n" +
                            //Db.Utilities.SerializeObject(pars.ToDictionary(it => it.ParameterName, it => it.Value)));
                            //Console.WriteLine();
                        };
                        //执行SQL完触发
                        db.Aop.OnLogExecuted = (sql, pars) =>
                        {
                            //SQL执行时间
                            //TimeSpan excuteTime = Db.Ado.SqlExecutionTime;
                            //StringBuilder sb = new StringBuilder();
                            //sb.AppendLine($"SQL：{sql}");
                            //sb.AppendLine($"SQL参数：{Db.Utilities.SerializeObject(pars.ToDictionary(it => it.ParameterName, it => it.Value))}");
                            //sb.AppendLine($"SQL执行时间：{excuteTime}");

                            //LogUtil.Info(sb.ToString(), "SQL执行日志");
                        };
                        //执行SQL出现错误事件
                        db.Aop.OnError = (exp) =>
                        {
                            // StringBuilder sb = new StringBuilder();
                            // sb.AppendLine($"SQL：{exp.Sql}");
                            // sb.AppendLine($"SQL参数：{exp.Parametres}");
                            // LogUtil.Error(sb.ToString(), "SQL执行错误日志", exp);
                        };
                        // 差异日志功能（审计）
                        db.Aop.OnDiffLogEvent = (it) =>
                        {
                            ////操作前记录  包含： 字段描述 列名 值 表名 表描述
                            //var editBeforeData = it.BeforeData;//插入Before为null，之前还没进库
                            ////操作后记录   包含： 字段描述 列名 值  表名 表描述
                            //var editAfterData = it.AfterData;
                            //var sql = it.Sql;
                            //var parameter = it.Parameters;
                            //var data = it.BusinessData;//这边会显示你传进来的对象
                            //var time = it.Time;
                            //var diffType = it.DiffType;//enum insert 、update and delete

                            DiffLog(db, it, CurrentUserInfo.Instance.UserID, CurrentUserInfo.Instance.UserName);
                        };
                    });
                return sqlSugar;
            });
        }

        private static List<DBConConnection> ListGetConnectionConfig()
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

                con.MoreSettings = new ConnMoreSettings()
                {
                    //禁用掉提升初始化第一次性能
                    IsNoReadXmlDescription = true //禁止读取XML中备注,true是禁用
                };
                dBConConnection.ConnectionConfig = con;
                dBConConnections.Add(dBConConnection);
            }

            return dBConConnections;
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
            // IEnumerable<System.Reflection.PropertyInfo> property = from pi in t.GetProperties() where pi.Name.ToLower() == field.ToLower() select pi;

            PropertyInfo property = t.GetProperties().FirstOrDefault(p => p.Name.ToLower() == field.ToLower());
            return property.GetValue(info, null);
        }

        /// <summary>
        /// 判断数据库类型
        /// </summary>
        /// <param name="type">数据库类型</param>
        /// <returns></returns>
        public static DbType UPdateType(string type)
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
        /// 记录审计日志 - 优化版本
        /// </summary>
        private static void DiffLog(SqlSugarClient db, DiffLogModel diffLog, long userid, string userName)
        {
            try
            {
                // 使用后台任务处理日志，避免阻塞主业务流程
                Task.Factory.StartNew(() =>
                {
                    string name = userName;
                    long useid = userid;
                    // 跳过不需要记录的表和操作
                    if (ShouldSkipLogging(diffLog))
                        return;

                    List<DataLogEntity> dataLogLs = new List<DataLogEntity>();
                    List<DataDetailEntity> dataDetailLs = new List<DataDetailEntity>();

                    switch (diffLog.DiffType)
                    {
                        case DiffType.delete:
                            {
                                DataLogEntity dataLogEntity = CreateBasicLogEntity(db, diffLog, useid, name);
                                dataLogLs.Add(dataLogEntity);
                                break;
                            }
                        case DiffType.update when diffLog.BeforeData?.Count > 0 && diffLog.AfterData?.Count > 0:
                            {
                                var sql = UtilMethods.GetSqlString(DbType.SqlServer, diffLog.Sql, diffLog.Parameters);
                                foreach (var table in diffLog.BeforeData)
                                {
                                    var index = diffLog.BeforeData.IndexOf(table);
                                    DataLogEntity dataLogEntity = CreateLogEntityForTable(table, sql, diffLog.DiffType, useid, name);
                                    var alfterTableEntity = diffLog.AfterData[index];
                                    if (alfterTableEntity != null)
                                    {
                                        // 只记录主键和变更的字段，提高性能
                                        foreach (var column in table.Columns.Where(c => c.IsPrimaryKey ||
                                                                                        alfterTableEntity.Columns.Any(ac => ac.ColumnName.Trim() == c.ColumnName.Trim() && !ac.Value.Equals(c.Value))))
                                        {
                                            if (alfterTableEntity.Columns.Exists(it => it.ColumnName.Trim() == column.ColumnName.Trim()))
                                            {
                                                var alterColumnsEntity = alfterTableEntity.Columns.First(it => it.ColumnName.Trim() == column.ColumnName.Trim());

                                                // 只添加主键或已变更的字段
                                                if (column.IsPrimaryKey || !alterColumnsEntity.Value.Equals(column.Value))
                                                {
                                                    DataDetailEntity dataDetailEntity = CreateDetailEntity(dataLogEntity.Id, column, alterColumnsEntity, useid, name);
                                                    dataDetailLs.Add(dataDetailEntity);
                                                }
                                            }
                                        }
                                    }

                                    dataLogLs.Add(dataLogEntity);
                                }

                                break;
                            }
                    }

                    // 只有当真正有数据需要记录时才进行写入操作
                    if (dataLogLs.Count > 0)
                    {
                        using (var newDb = new SqlSugarClient(db.CurrentConnectionConfig))
                        {
                            newDb.Fastest<DataLogEntity>().BulkCopy(dataLogLs);

                            if (dataDetailLs.Count > 0)
                            {
                                newDb.Fastest<DataDetailEntity>().BulkCopy(dataDetailLs);
                            }
                        }
                    }
                }, TaskCreationOptions.LongRunning);
            }
            catch (Exception)
            {
                // 日志记录异常不应该影响主业务流程
            }
        }

        /// <summary>
        /// 判断是否需要跳过日志记录
        /// </summary>
        private static bool ShouldSkipLogging(DiffLogModel diffLog)
        {
            // 对于一些不需要记录的表或操作进行过滤
            // 例如：临时表、日志表自身、频繁变更的状态表等
            string[] skipTables = new[] { "TempData", "DataLog", "DataDetail" };

            if (diffLog.BeforeData?.Count > 0)
            {
                foreach (var table in diffLog.BeforeData)
                {
                    if (skipTables.Any(t => table.TableName.Contains(t)))
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 创建基础日志实体
        /// </summary>
        private static DataLogEntity CreateBasicLogEntity(SqlSugarClient db, DiffLogModel diffLog,  long userid, string userName)
        {
            DataLogEntity dataLogEntity = new DataLogEntity
            {
                Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId(),
                DiffType = diffLog.DiffType.ToString().ToUpper(),
                LogContent = diffLog.Sql,
                CreaterName = userName,
                CreaterId = userid,
                ModifierName = userName,
                ModifierId = userid,
                CreateTime = DateTime.Now,
                ModifyTime = DateTime.Now
            };

            if (db.MappingTables?.Count > 0)
            {
                dataLogEntity.TableName = db.MappingTables[0].DbTableName;
                dataLogEntity.TableDesc = db.MappingTables[0].EntityName;
            }

            return dataLogEntity;
        }

        /// <summary>
        /// 创建表日志实体
        /// </summary>
        private static DataLogEntity CreateLogEntityForTable(DiffLogTableInfo table, string sql, DiffType diffType, long userid, string userName)
        {
            return new DataLogEntity
            {
                Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId(),
                TableName = table.TableName,
                TableDesc = table.TableDescription,
                DiffType = diffType.ToString().ToUpper(),
                LogContent = sql,
                CreaterName = userName,
                CreaterId = userid,
                ModifierName = userName,
                ModifierId = userid,
                CreateTime = DateTime.Now,
                ModifyTime = DateTime.Now
            };
        }

        /// <summary>
        /// 创建详情实体
        /// </summary>
        private static DataDetailEntity CreateDetailEntity(long logId, DiffLogColumnInfo column, DiffLogColumnInfo alterColumn, long userid, string userName)
        {
            return new DataDetailEntity
            {
                DataLogID = logId,
                Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId(),
                ColumnField = column.ColumnName,
                ColumnDesc = column.ColumnDescription,
                OldValue = column.Value?.ToString() ?? "",
                NewValue = alterColumn.Value?.ToString() ?? "",
                CreaterName = userName,
                CreaterId = userid,
                ModifierName = userName,
                ModifierId = userid,
                CreateTime = DateTime.Now,
                ModifyTime = DateTime.Now
            };
        }
    }
}