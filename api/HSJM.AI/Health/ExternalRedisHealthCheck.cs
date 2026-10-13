using HSJM.AI.Utility;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HSJM.AI.Health;

public interface IRedisHealthProbe
{
    Task<bool> PingAsync(CancellationToken cancellationToken);
}

public sealed class RedisHealthProbe : IRedisHealthProbe
{
    public async Task<bool> PingAsync(CancellationToken cancellationToken)
    {
        var client = CommonRedisHelper.client
            ?? throw new InvalidOperationException("Redis 客户端尚未初始化。");

        return await client.PingAsync().WaitAsync(cancellationToken);
    }
}

public sealed class ExternalRedisHealthCheck : IHealthCheck
{
    private readonly string? _redisUrl;
    private readonly IRedisHealthProbe _probe;

    public ExternalRedisHealthCheck(IConfiguration configuration, IRedisHealthProbe probe)
    {
        _redisUrl = configuration["WebConfig:RedisServerUrl"];
        _probe = probe;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_redisUrl))
        {
            return HealthCheckResult.Unhealthy("未配置外部 Redis 地址。");
        }

        try
        {
            return await _probe.PingAsync(cancellationToken)
                ? HealthCheckResult.Healthy("外部 Redis PING 成功。")
                : HealthCheckResult.Unhealthy("外部 Redis PING 失败。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("外部 Redis PING 失败。", exception);
        }
    }
}
