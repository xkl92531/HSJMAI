using Npgsql;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

public class Original353DiagnosticIoStatusTests
{
    [Theory]
    [InlineData(4617381855010885632L)]
    [InlineData(4617382048292802560L)]
    public async Task CurrentTestProjectHasReadableBooleanIoTags(long projectId)
    {
        var connection = Environment.GetEnvironmentVariable("HSJM_XMLV2_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) return;
        Assert.Equal("hsjm_ai_new_test", new NpgsqlConnectionStringBuilder(connection).Database);
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync();
        await using var query = new NpgsqlCommand("""
            SELECT count(*) FILTER (WHERE constraintstatus IS NULL),
                   count(*) FILTER (WHERE constraintstatus IS DISTINCT FROM 6)
            FROM ai_plc_iotag
            WHERE projectid=@project AND NOT isdeleted AND datatype='Bool'
            """, db);
        query.Parameters.AddWithValue("project", projectId);
        await using var reader = await query.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0L, reader.GetInt64(0));
        Assert.True(reader.GetInt64(1) > 100);
    }
}
