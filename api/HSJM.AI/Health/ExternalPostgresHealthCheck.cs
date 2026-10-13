using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace HSJM.AI.Health;

public sealed class ExternalPostgresHealthCheck : IHealthCheck
{
    private readonly string? _connectionString;

    public ExternalPostgresHealthCheck(IConfiguration configuration)
    {
        var database = configuration
            .GetSection("WebConfig:DBList")
            .GetChildren()
            .FirstOrDefault(database =>
                string.Equals(database["ConfigId"], "DefultDataBase", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(database["DatabaseType"], "PostgreSQL", StringComparison.OrdinalIgnoreCase));

        _connectionString = database?["ConnectionString"];
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            return HealthCheckResult.Unhealthy("未配置外部 PostgreSQL 连接字符串。");
        }

        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = new NpgsqlCommand("SELECT 1", connection);
            var result = await command.ExecuteScalarAsync(cancellationToken);

            return Convert.ToInt32(result) == 1
                ? HealthCheckResult.Healthy("外部 PostgreSQL 可用。")
                : HealthCheckResult.Unhealthy("外部 PostgreSQL 就绪检查返回了异常结果。");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("外部 PostgreSQL 连接失败。", exception);
        }
    }
}
