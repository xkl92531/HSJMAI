using System;
using System.Globalization;

namespace HSJM.AI.AppLayer.LineUp.Runtime;

/// <summary>
/// 设备监控（PLC 工步监控）的轮询间隔取值（2026-09-22）。
///
/// 这个值决定的是「IGW 侧值已经变了，我们多久才去取」，**不是** PLC 多久被读一次——
/// 后者由注册时写在点位上的 ScanRateMs 决定。工步执行位
/// （<c>{db}.RT_DATA.EXEC_BITS.*</c>、<c>{db}.S_NO</c>）注册在 GRAPH 设备上、
/// <c>GraphScanRateMs=100</c>，所以 IGW 每 100ms 就已经刷新好了；原来这里是 500ms，
/// 意味着延迟的大头反而在我们自己这边。对齐到 100ms 后端到端延迟从「最差 600ms」
/// 降到「最差 200ms」。
///
/// **不要盲目往 50ms 调**：IGW 侧 100ms 才刷新一次，50ms 去取等于一半请求拿到
/// 完全相同的缓存值，只翻倍 HTTP 压力、不降延迟。要 50ms 有意义，得先把
/// <c>WebConfig:IgwRegistration:GraphScanRateMs</c> 也降到 50 并重新注册
/// （扫描率写在点位上，改配置后必须重注册才生效）。下限留 50 就是为了那一天。
///
/// 另外注意工步位之外的 DB 变量（转移条件、动作输出、步时长 T/T_MAX 等）走的是
/// <c>DbScanRateMs=1000</c>，本函数管不到它们——那条要调注册配置，代价是 IGW 对
/// DB 块的轮询压力涨 10 倍，需先评估现场 PLC 是否扛得住。
/// </summary>
public static class LineUpMonitorPollInterval
{
    /// <summary>与 GraphScanRateMs 对齐：再快也等不到新值。</summary>
    public const int DefaultMs = 100;

    /// <summary>留给「GraphScanRateMs 也降下去」的将来，当前配置下调到这里没有收益。</summary>
    public const int MinMs = 50;

    public const int MaxMs = 5_000;

    /// <summary>读 <c>WebConfig:LineUpMonitor:PollIntervalMs</c>；缺失或不是整数时回落默认值。</summary>
    public static int Resolve(string configured)
    {
        if (!int.TryParse((configured ?? string.Empty).Trim(),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return DefaultMs;
        }
        return Math.Clamp(parsed, MinMs, MaxMs);
    }
}
