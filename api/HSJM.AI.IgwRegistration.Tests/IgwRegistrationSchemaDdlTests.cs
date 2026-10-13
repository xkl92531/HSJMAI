using System;
using System.IO;
using HSJM.AI.IDbService;
using Xunit;

namespace HSJM.AI.IgwRegistration.Tests;

/// <summary>
/// task-5 fix round 2：PLC_IgwRegistrationSchemaDdl 的两个常量是运行时执行的唯一权威 DDL 来源
/// （PLC_IgwRegistrationService.EnsureLedgerSchemaAsync 直接 db.Ado.ExecuteCommandAsync 这两个常量），
/// 同时 tools/mig_20260919_igw_registration.sql 要求逐字节内嵌同一段文本供离线手工执行。
/// 本测试断言两处不会漂移；顺带做几条防回归的内容断言（不含 nvarchar 等 SqlServer 方言、表名全小写等）。
/// </summary>
public class IgwRegistrationSchemaDdlTests
{
    private const string MigrationRelativePath = "tools/mig_20260919_igw_registration.sql";

    [Fact]
    public void Migration_sql_file_contains_the_exact_create_table_statement()
    {
        var sqlFileText = ReadMigrationSqlFile();

        Assert.Contains(PLC_IgwRegistrationSchemaDdl.CreateTableSql, sqlFileText, StringComparison.Ordinal);
    }

    [Fact]
    public void Migration_sql_file_contains_the_exact_create_index_statement()
    {
        var sqlFileText = ReadMigrationSqlFile();

        Assert.Contains(PLC_IgwRegistrationSchemaDdl.CreateIndexSql, sqlFileText, StringComparison.Ordinal);
    }

    /// <summary>
    /// 0922 的补列脚本要和代码常量逐字一致：应用启动时跑常量、离线部署跑脚本，两边必须等价。
    /// fingerprint 只在这条 ALTER 里加，**不进 CreateTableSql**——那段被 0919 脚本逐字节钉住了。
    /// </summary>
    [Fact]
    public void Fingerprint_migration_sql_file_contains_the_exact_alter_statement()
    {
        var sqlFileText = RepoFiles.ReadText("tools/mig_20260922_igw_root_fingerprint.sql");

        Assert.Contains(PLC_IgwRegistrationSchemaDdl.AddFingerprintColumnSql, sqlFileText, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_table_sql_does_not_use_sqlserver_only_nvarchar_type()
    {
        // 回归守卫：本表改手写 DDL 正是为了绕开 BaseField/CodeFirst 在 PG 上把 CreaterName/ModifierName
        // 生成成 nvarchar 导致 42704 的历史 bug（同一类问题见 PLC_StationShadowEventContextEntity.cs:44-47）；
        // 这里断言常量本身永远不含 "nvarchar"，防止未来有人复制别处（SqlServer 系）的列定义粘回来。
        Assert.DoesNotContain("nvarchar", PLC_IgwRegistrationSchemaDdl.CreateTableSql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_table_sql_targets_lowercase_unquoted_table_name()
    {
        Assert.Contains("CREATE TABLE IF NOT EXISTS ai_plc_igwregistration", PLC_IgwRegistrationSchemaDdl.CreateTableSql, StringComparison.Ordinal);
        Assert.DoesNotContain("\"", PLC_IgwRegistrationSchemaDdl.CreateTableSql);
    }

    [Fact]
    public void Both_ddl_statements_are_idempotent_if_not_exists_style()
    {
        Assert.Contains("IF NOT EXISTS", PLC_IgwRegistrationSchemaDdl.CreateTableSql, StringComparison.Ordinal);
        Assert.Contains("IF NOT EXISTS", PLC_IgwRegistrationSchemaDdl.CreateIndexSql, StringComparison.Ordinal);
    }

    private static string ReadMigrationSqlFile() => RepoFiles.ReadText(MigrationRelativePath);
}
