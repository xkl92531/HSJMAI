using System;
using System.Collections.Generic;
using System.Linq;
using Hangfire;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.Controllers
{
    /// <summary>
    /// 后台任务运维接口（Hangfire 可视化第一步，2026-09-14）。
    /// 只读聚合：指标卡 + 服务器 + 队列深度 + 死信队列检测 + 定时任务清单。
    /// 操控类接口（requeue/delete/trigger）在第二步扩展，需配套审计日志。
    /// 数据源：JobStorage.Current.GetMonitoringApi()（Hangfire 1.8.17），不直接查 hangfire 库表。
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [ApiExplorerSettings(GroupName = "System")]
    [Authorize]
    public class BackgroundOpsController : ControllerBase
    {
        private readonly ILogger<BackgroundOpsController> _logger;

        public BackgroundOpsController(ILogger<BackgroundOpsController> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// 后台任务总览。
        /// 死信队列（deadLetterQueues）判定语义：队列当前有积压任务（Length>0），
        /// 但没有任何活跃 Hangfire server 声明监听该队列 —— 典型场景：
        /// 任务入到 local_&lt;机器名&gt; 队列后该机器下线/容器重建改名，
        /// 任务永久积压无人消费（LineUp 批次卡 Uploaded 的根因模式）。
        /// </summary>
        [HttpGet("Overview")]
        public IActionResult Overview()
        {
            try
            {
                var api = JobStorage.Current.GetMonitoringApi();

                // 1. 顶部指标卡
                var stats = api.GetStatistics();

                // 2. 活跃 server 列表 + 各自监听队列（并集用于死信判定）
                var servers = api.Servers() ?? new List<ServerDto>();
                var monitoredQueues = new HashSet<string>(
                    servers.Where(s => s.Queues != null).SelectMany(s => s.Queues),
                    StringComparer.OrdinalIgnoreCase);

                var utcNow = DateTime.UtcNow;
                var serverItems = servers
                    .OrderBy(s => s.Name)
                    .Select(s => new
                    {
                        name = s.Name,
                        workersCount = s.WorkersCount,
                        queues = (s.Queues ?? Array.Empty<string>()).ToList(),
                        startedAt = s.StartedAt,
                        heartbeat = s.Heartbeat,
                        // 心跳时差（秒）。>60 视为疑似下线（Hangfire 默认 30s 级心跳）
                        heartbeatAgeSeconds = s.Heartbeat.HasValue
                            ? (long)(utcNow - s.Heartbeat.Value).TotalSeconds
                            : (long?)null,
                    })
                    .ToList();

                // 3. 队列深度 + 死信检测
                var queueItems = (api.Queues() ?? new List<QueueWithTopEnqueuedJobsDto>())
                    .Select(q => new
                    {
                        name = q.Name,
                        length = q.Length,
                        monitored = monitoredQueues.Contains(q.Name),
                        // 有积压但无任何 server 监听 → 死信，红色告警
                        deadLetter = q.Length > 0 && !monitoredQueues.Contains(q.Name),
                    })
                    .OrderByDescending(q => q.deadLetter)
                    .ThenByDescending(q => q.length)
                    .ThenBy(q => q.name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var deadLetterQueues = queueItems.Where(q => q.deadLetter).Select(q => q.name).ToList();

                // 4. 定时任务清单（station-shadow 系列 / dw-etl-daily 等）。
                //    IMonitoringApi 无此方法，走 IStorageConnection.GetRecurringJobs()（Dashboard 同源数据）。
                List<RecurringJobDto> recurringJobsRaw;
                using (var connection = JobStorage.Current.GetConnection())
                {
                    recurringJobsRaw = connection.GetRecurringJobs() ?? new List<RecurringJobDto>();
                }

                var recurringItems = recurringJobsRaw
                    .OrderBy(j => j.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(j => new
                    {
                        id = j.Id,
                        // RecurringJobDto 无 Name 字段（display 名即 Id）
                        cron = j.Cron,
                        queue = j.Queue,
                        nextExecution = j.NextExecution,
                        lastExecution = j.LastExecution,
                        lastJobId = j.LastJobId,
                        lastJobState = j.LastJobState,
                    })
                    .ToList();

                if (deadLetterQueues.Count > 0)
                {
                    _logger.LogWarning(
                        "[BackgroundOps] 检测到死信队列（有积压但无 server 监听）：{Queues}",
                        string.Join(", ", deadLetterQueues));
                }

                return Ok(new
                {
                    statistics = new
                    {
                        enqueued = stats.Enqueued,
                        processing = stats.Processing,
                        scheduled = stats.Scheduled,
                        failed = stats.Failed,
                        succeeded = stats.Succeeded,
                        deleted = stats.Deleted,
                        awaiting = stats.Awaiting,
                        servers = stats.Servers,
                        recurring = stats.Recurring,
                    },
                    servers = serverItems,
                    queues = queueItems,
                    deadLetterQueues,
                    recurringJobs = recurringItems,
                    generatedAt = DateTime.Now,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BackgroundOps] 读取 Hangfire 监控数据失败");
                return StatusCode(500, new { message = $"读取 Hangfire 监控数据失败: {ex.Message}" });
            }
        }
    }
}
