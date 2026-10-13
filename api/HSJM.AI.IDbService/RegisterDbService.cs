using HSJM.AI.Core;
using HSJM.AI.Utility;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HSJM.AI.IDbService
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

                // PostgreSQL 保留大小写表（Basic_*、AI_LineUp_* 等以带引号表名建表）必须用引号标识符访问；
                // SqlSugar 默认生成无引号标识符会被 PG 折叠为小写，导致 42P01（relation 不存在）/42703（列不存在）。
                // 判定必须以库里实际表名为准（2026-08-27 修正）：不能按"实体表名含大写"一刀切——
                // SYS_Menu 这类旧表实体名大写、库里实际是小写 sys_menu（无引号建表被 PG 折叠），
                // 加引号后变成大小写敏感精确匹配 → 42P01 relation "SYS_Menu" does not exist。
                // 规则：进程内首次使用前查 information_schema 拿"名字 ≠ 全小写"的保留大小写表集合（Ordinal 精确比对），
                //       实体表名命中该集合才加引号（该实体的列名一并加引号）；未命中一律不加引号，
                //       由 PG 折叠为小写访问（sys_menu、ai_plc_* 等历史行为完全不变）。原生 SQL 不经过实体映射，不受影响。
                // jsonb 列名集合（PostgreSQL 兼容层实体），供参数占位符追加 ::jsonb 显式转换，避免 Npgsql 以 text 发送报 42804
                var jsonbColumnNames = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
                System.Collections.Generic.HashSet<string> jsonbColumnsFromDb = new(StringComparer.Ordinal);
                if (connectionConfig.DbType == DbType.PostgreSQL)
                {
                    // SqlSugar PG 默认 PgSqlIsAutoToLower=true：生成 SQL 时把标识符强制小写，
                    // 会把 EntityService 嵌入 DbColumnName 的引号（"Id"）抹掉成无引号 id → 42703 列不存在。
                    // 关闭后：带引号标识符原样保留（大小写保留表可用），无引号标识符仍由 PG 端折叠小写（sys_menu/ai_plc_* 行为不变）。
                    connectionConfig.MoreSettings = new ConnMoreSettings
                    {
                        IsNoReadXmlDescription = true,
                        PgSqlIsAutoToLower = false,
                    };

                    var casePreservedTables = CasePreservedTableCache.Load(connectionConfig);
                    jsonbColumnsFromDb = JsonbColumnCache.Load(connectionConfig);

                    static bool NeedsQuoting(string? name, HashSet<string> preservedTables) =>
                        !string.IsNullOrEmpty(name)
                        && name.IndexOf('"') < 0
                        && preservedTables.Contains(name);

                    static bool HasUpperNoQuote(string? name) =>
                        !string.IsNullOrEmpty(name) && name.IndexOf('"') < 0 && name.Any(char.IsUpper);

                    connectionConfig.ConfigureExternalServices = new ConfigureExternalServices
                    {
                        EntityNameService = (type, entity) =>
                        {
                            if (string.IsNullOrEmpty(entity.DbTableName) || entity.DbTableName.IndexOf('"') >= 0)
                            {
                                return;
                            }
                            if (casePreservedTables.Contains(entity.DbTableName))
                            {
                                entity.DbTableName = '"' + entity.DbTableName + '"';
                            }
                            else
                            {
                                // PgSqlIsAutoToLower=false 下 SqlSugar 给标识符强加引号保留大小写，
                                // 非保留表在库里实际全小写（无引号建表被 PG 折叠），必须显式小写化，
                                // 否则 "SYS_Menu" 带引号精确匹配大写 → 42P01
                                entity.DbTableName = entity.DbTableName.ToLowerInvariant();
                            }
                        },
                        EntityService = (propertyInfo, entityInfo) =>
                        {
                            var tableAttribute = propertyInfo.ReflectedType?
                                .GetCustomAttributes(false)
                                .OfType<SugarTable>()
                                .FirstOrDefault();
                            var tableName = tableAttribute?.TableName;
                            if (string.IsNullOrEmpty(tableName) || tableName.IndexOf('"') >= 0)
                            {
                                return;
                            }
                            if (string.IsNullOrEmpty(entityInfo.DbColumnName)
                                || entityInfo.DbColumnName.IndexOf('"') >= 0
                                || !entityInfo.DbColumnName.Any(char.IsUpper))
                            {
                                return;
                            }
                            if (casePreservedTables.Contains(tableName))
                            {
                                var columnAttribute = propertyInfo.GetCustomAttributes(false)
                                    .OfType<SugarColumn>()
                                    .FirstOrDefault();
                                if (string.Equals(columnAttribute?.ColumnDataType, "jsonb", StringComparison.OrdinalIgnoreCase))
                                {
                                    jsonbColumnNames.TryAdd(entityInfo.DbColumnName, 0);
                                }

                                entityInfo.DbColumnName = '"' + entityInfo.DbColumnName + '"';
                            }
                            else
                            {
                                // 同表名小写化理由：非保留表的列在库里实际全小写
                                entityInfo.DbColumnName = entityInfo.DbColumnName.ToLowerInvariant();
                            }
                        },
                    };
                }

                static bool IsDigitSuffix(string value, int startIndex)
                {
                    for (var i = startIndex; i < value.Length; i++)
                    {
                        if (!char.IsAsciiDigit(value[i]))
                        {
                            return false;
                        }
                    }

                    return true;
                }

                static bool IsJsonbParamName(
                    string name,
                    System.Collections.Generic.HashSet<string> fromDb,
                    System.Collections.Concurrent.ConcurrentDictionary<string, byte> fromCallback)
                {
                    if (fromDb.Contains(name) || fromCallback.ContainsKey(name))
                    {
                        return true;
                    }

                    foreach (var key in fromDb)
                    {
                        if (name.Length > key.Length &&
                            name.StartsWith(key, System.StringComparison.Ordinal) &&
                            IsDigitSuffix(name, key.Length))
                        {
                            return true;
                        }
                    }

                    foreach (var key in fromCallback.Keys)
                    {
                        if (name.Length > key.Length &&
                            name.StartsWith(key, System.StringComparison.Ordinal) &&
                            IsDigitSuffix(name, key.Length))
                        {
                            return true;
                        }
                    }

                    return false;
                }

                SqlSugarScope sqlSugar = new SqlSugarScope(connectionConfig,
                    db =>
                    {
                        // db.CodeFirst.InitTables(typeof(DataDetailEntity));
                        // db.CodeFirst.InitTables(typeof(DataLogEntity));
                        // PostgreSQL 兼容层实体的 DbColumnName 带引号（见上方 ConfigureExternalServices），
                        // SqlSugar 生成参数占位 "@列名" 时会把引号一并带进占位符（@"Id"），PG 会把它当列引用报 42703。
                        // 这里在执行前把参数占位与参数名里的引号剥掉（列清单里的引号不受影响，仍正确转义表/列名）。
                        db.Aop.OnExecutingChangeSql = (sql, pars) =>
                        {
                            var quote = (char)34;
                            if (pars is { Length: > 0 })
                            {
                                foreach (var p in pars)
                                {
                                    if (p.ParameterName != null && p.ParameterName.IndexOf(quote) >= 0)
                                    {
                                        p.ParameterName = p.ParameterName.Replace(quote.ToString(), string.Empty);
                                    }
                                }
                            }

                            var fixedSql = sql;
                            if (!string.IsNullOrEmpty(fixedSql))
                            {
                                var marker = "@" + quote;
                                if (fixedSql.Contains(marker))
                                {
                                    var pattern = "@" + quote + "([A-Za-z0-9_]+)" + quote;
                                    fixedSql = System.Text.RegularExpressions.Regex.Replace(fixedSql, pattern, "@$1");
                                }

                                // jsonb 列的占位符追加显式转换，避免 Npgsql 以 text 发送报 42804。
                                // 匹配采用「列名 或 列名+数字后缀」：批量 Insertable 的参数名带编号
                                //（@ResolutionJson1/@ResolutionJson2…），精确匹配会漏掉导致 42804。
                                // 数据源 = information_schema 查询集 ∪ EntityService 回调收集：
                                // 仅靠回调收集会被「无配置 client 先解析实体→静态缓存命中→回调不触发」打断。
                                var jsonbDbSet = jsonbColumnsFromDb;
                                if (jsonbDbSet.Count > 0 || !jsonbColumnNames.IsEmpty)
                                {
                                    fixedSql = System.Text.RegularExpressions.Regex.Replace(
                                        fixedSql,
                                        "@([A-Za-z0-9_]+)",
                                        match =>
                                        {
                                            var name = match.Groups[1].Value;
                                            var isJsonb = IsJsonbParamName(name, jsonbDbSet, jsonbColumnNames);
                                            return isJsonb ? "@" + name + "::jsonb" : match.Value;
                                        });
                                }
                            }

                            return new KeyValuePair<string, SugarParameter[]>(fixedSql, pars);
                        };
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

                            //DiffLog(db, it, CurrentUserInfo.Instance.UserID, CurrentUserInfo.Instance.UserName);
                        };
                    });
                return sqlSugar;
            });
        }

        /// <summary>
        /// 库里"保留大小写"表名集合（information_schema 中 table_name ≠ lower(table_name) 的表），
        /// 进程内首次使用时加载一次，用于决定 PostgreSQL 实体表名是否加引号访问（见 RegisterService 内 PG 分支）。
        /// 比对基准 = 传入 connectionConfig 指向的库（当前业务库与默认库同库 hsjm_ai_new_test）。
        /// </summary>
        private static class CasePreservedTableCache
        {
            private static HashSet<string>? _tables;
            private static readonly object LockObj = new();

            public static HashSet<string> Load(ConnectionConfig config)
            {
                if (_tables != null) return _tables;
                lock (LockObj)
                {
                    if (_tables != null) return _tables;
                    try
                    {
                        using var probe = new SqlSugarClient(new ConnectionConfig
                        {
                            DbType = DbType.PostgreSQL,
                            ConnectionString = config.ConnectionString,
                            IsAutoCloseConnection = true,
                        });
                        var names = probe.Ado.SqlQuery<string>(
                            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_name <> lower(table_name)");
                        _tables = new HashSet<string>(names ?? new List<string>(), StringComparer.Ordinal);
                    }
                    catch (Exception ex)
                    {
                        // 拿不到清单时按"无保留大小写表"处理：等价于全表不加引号的历史行为，
                        // 系统表（sys_menu 等）不受影响；仅 Basic_*/AI_LineUp_* 等兼容层表暂不可用，重启后重试。
                        Console.WriteLine("[RegisterDbService] 读取保留大小写表清单失败，按全小写（不加引号）处理：" + ex.Message);
                        _tables = new HashSet<string>(StringComparer.Ordinal);
                    }
                }

                return _tables;
            }
        }

        /// <summary>
        /// jsonb 列名清单（information_schema 一次查询，与实体解析时机解耦）。
        /// 背景：靠 EntityService 回调收集会被「无配置 client 先解析实体 → 进程级静态缓存命中 →
        /// 回调不再触发」打断（jsonb 参数补 cast 失效 → Npgsql 42804 text→jsonb）。
        /// </summary>
        private static class JsonbColumnCache
        {
            private static HashSet<string>? _columns;
            private static readonly object LockObj = new();

            public static HashSet<string> Load(ConnectionConfig config)
            {
                if (_columns != null) return _columns;
                lock (LockObj)
                {
                    if (_columns != null) return _columns;
                    try
                    {
                        using var probe = new SqlSugarClient(new ConnectionConfig
                        {
                            DbType = DbType.PostgreSQL,
                            ConnectionString = config.ConnectionString,
                            IsAutoCloseConnection = true,
                        });
                        var names = probe.Ado.SqlQuery<string>(
                            "SELECT DISTINCT column_name FROM information_schema.columns WHERE table_schema = 'public' AND data_type = 'jsonb'");
                        _columns = new HashSet<string>(names ?? new List<string>(), StringComparer.Ordinal);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[RegisterDbService] 读取 jsonb 列清单失败：" + ex.Message);
                        _columns = new HashSet<string>(StringComparer.Ordinal);
                    }
                }

                return _columns;
            }
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

            System.Reflection.PropertyInfo property = t.GetProperties().FirstOrDefault(p => p.Name.ToLower() == field.ToLower());
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