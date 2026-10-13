using HSJM.AI.IDbService;
using Xunit;

namespace HSJM.AI.IgwRegistration.Tests.Hardening;

/// <summary>状态表 DDL 与迁移脚本一致性、注册身份规范化与比较。</summary>
public class IgwRegistrationStateTests
{
    private const string MigrationRelativePath = "tools/mig_20260920_igw_registration_hardening.sql";

    [Fact]
    public void Migration_contains_exact_state_table_ddl_and_ledger_purge()
    {
        var sql = ReadMigration();

        Assert.Contains(PLC_IgwRegistrationStateSchemaDdl.CreateTableSql, sql, StringComparison.Ordinal);
        Assert.Contains(PLC_IgwRegistrationStateSchemaDdl.CreatePendingIndexSql, sql, StringComparison.Ordinal);
        Assert.Contains(PLC_IgwRegistrationSchemaDdl.PurgeSoftDeletedSql, sql, StringComparison.Ordinal);
    }

    [Fact]
    public void State_ddl_is_idempotent_lowercase_and_pg_only()
    {
        Assert.Contains("CREATE TABLE IF NOT EXISTS ai_plc_igwregistrationstate", PLC_IgwRegistrationStateSchemaDdl.CreateTableSql, StringComparison.Ordinal);
        Assert.Contains("IF NOT EXISTS", PLC_IgwRegistrationStateSchemaDdl.CreatePendingIndexSql, StringComparison.Ordinal);
        Assert.DoesNotContain("nvarchar", PLC_IgwRegistrationStateSchemaDdl.CreateTableSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"", PLC_IgwRegistrationStateSchemaDdl.CreateTableSql);
    }

    [Theory]
    [InlineData("HTTP://IGW.Local:48020", "http://igw.local:48020/")]
    [InlineData("igw.local:48020/", "http://igw.local:48020/")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void Normalizes_igw_base_url(string raw, string expected) =>
        Assert.Equal(expected, IgwRegistrationIdentity.NormalizeIgwBaseUrl(raw));

    [Fact]
    public void Identity_requires_all_parts_and_compares_case_insensitively()
    {
        Assert.Null(IgwRegistrationIdentity.Create("http://a:1", "10.0.0.1", " "));
        Assert.Null(IgwRegistrationIdentity.Create("http://a:1", null, "通道"));

        var a = IgwRegistrationIdentity.Create("HTTP://A:1", " 10.0.0.1 ", "线体A_PLC1");
        var b = IgwRegistrationIdentity.Create("http://a:1/", "10.0.0.1", "线体a_plc1");
        var c = IgwRegistrationIdentity.Create("http://a:1/", "10.0.0.2", "线体A_PLC1");

        Assert.Equal("10.0.0.1", a.PlcIp);
        Assert.True(a.Matches(b));
        Assert.False(a.Matches(c));
        Assert.True(a.SameIgw(c));
        Assert.Contains("10.0.0.1", a.Describe());
    }

    [Fact]
    public void State_row_exposes_registered_identity_only_when_complete()
    {
        var row = new IgwRegistrationStateRow { ProjectId = 1, RegisteredIotBaseUrl = "http://a:1/", RegisteredPlcIp = "10.0.0.1" };
        Assert.Null(row.RegisteredIdentity);

        row.RegisteredChannel = "通道";
        Assert.Equal("通道", row.RegisteredIdentity.ChannelName);
    }

    private static string ReadMigration() => RepoFiles.ReadText(MigrationRelativePath);
}
