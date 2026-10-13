using System;

namespace HSJM.AI.IDbService
{
    /// <summary>
    /// PG 错误码分类的纯函数集合（无副作用，供 catch 分支做条件判断）。
    /// 不直接依赖 Npgsql 程序集（IDbService/DbService 均未引用 Npgsql，避免为一个错误码判断新增包依赖）——
    /// SqlSugar 把底层 Npgsql.PostgresException 包裹在 SqlSugarException 里，两层的 Message 都会带
    /// 形如 "42703: column ... does not exist" 的 SQLSTATE 前缀（CLAUDE.md §六多处记录过同类错误码），
    /// 因此改为沿异常链扫描 Message 文本，足够可靠且零额外依赖。
    /// </summary>
    public static class IgwSqlErrorClassifier
    {
        /// <summary>PG 42703（undefined_column，列不存在）的 SQLSTATE 码。</summary>
        public const string UndefinedColumnSqlState = "42703";

        /// <summary>PG 42P01（undefined_table，表不存在）的 SQLSTATE 码。</summary>
        public const string UndefinedTableSqlState = "42P01";

        /// <summary>
        /// 是否是"列不存在"错误（沿 <paramref name="ex"/> 的 InnerException 链扫描 Message 文本）。
        /// 用于 IGW 台账服务的 IgwChannelName 读写：迁移脚本/启动补列尚未执行时列缺失，调用方据此优雅降级
        /// 而不是把整条请求炸掉。
        /// </summary>
        public static bool IsUndefinedColumn(Exception? ex) => ContainsSqlState(ex, UndefinedColumnSqlState);

        /// <summary>
        /// 是否是"表不存在"错误（同样沿异常链扫描 Message 文本）。
        /// 用于 IGW 注册状态表（ai_plc_igwregistrationstate）的读写：迁移脚本未执行且启动建表也失败时整张表不可用，
        /// 调用方据此放行（回到加固前行为：不做身份变化检测 / 冲突检查 / GRAPH 冷却），而不是让整个注册失败。
        /// </summary>
        public static bool IsUndefinedTable(Exception? ex) => ContainsSqlState(ex, UndefinedTableSqlState);

        private static bool ContainsSqlState(Exception? ex, string sqlState)
        {
            for (var current = ex; current != null; current = current.InnerException)
            {
                if (!string.IsNullOrEmpty(current.Message)
                    && current.Message.Contains(sqlState, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
