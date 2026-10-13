using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace HSJM.AI.Controllers
{
    /// <summary>
    /// Hangfire 测试控制器
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [ApiExplorerSettings(GroupName = "System")]
    public class HangfireTestController : ControllerBase
    {
        private readonly ILogger<HangfireTestController> _logger;

        public HangfireTestController(ILogger<HangfireTestController> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// 测试立即执行任务
        /// </summary>
        /// <returns></returns>
        [HttpPost("fire-and-forget")]
        public IActionResult TestFireAndForget()
        {
            var jobId = BackgroundJob.Enqueue(() => TestJob("Fire-and-Forget 测试任务"));
            
            return Ok(new
            {
                message = "任务已提交",
                jobId = jobId,
                dashboardUrl = "/hangfire"
            });
        }

        /// <summary>
        /// 测试延迟执行任务
        /// </summary>
        /// <param name="delaySeconds">延迟秒数</param>
        /// <returns></returns>
        [HttpPost("delayed")]
        public IActionResult TestDelayed([FromQuery] int delaySeconds = 10)
        {
            var jobId = BackgroundJob.Schedule(
                () => TestJob($"延迟 {delaySeconds} 秒执行的任务"),
                TimeSpan.FromSeconds(delaySeconds)
            );
            
            return Ok(new
            {
                message = $"任务将在 {delaySeconds} 秒后执行",
                jobId = jobId,
                dashboardUrl = "/hangfire"
            });
        }

        /// <summary>
        /// 测试定时任务（每分钟执行一次）
        /// </summary>
        /// <returns></returns>
        [HttpPost("recurring")]
        public IActionResult TestRecurring()
        {
            RecurringJob.AddOrUpdate(
                "test-recurring-job",
                () => TestJob("定时任务（每分钟）"),
                Cron.Minutely()
            );
            
            return Ok(new
            {
                message = "定时任务已创建（每分钟执行一次）",
                recurringJobId = "test-recurring-job",
                dashboardUrl = "/hangfire"
            });
        }

        /// <summary>
        /// 移除定时任务
        /// </summary>
        /// <returns></returns>
        [HttpDelete("recurring")]
        public IActionResult RemoveRecurring()
        {
            RecurringJob.RemoveIfExists("test-recurring-job");
            
            return Ok(new
            {
                message = "定时任务已移除"
            });
        }

        /// <summary>
        /// 测试任务链（任务依赖）
        /// </summary>
        /// <returns></returns>
        [HttpPost("continuation")]
        public IActionResult TestContinuation()
        {
            var parentJobId = BackgroundJob.Enqueue(() => TestJob("父任务"));
            var childJobId = BackgroundJob.ContinueJobWith(
                parentJobId,
                () => TestJob("子任务（父任务完成后执行）")
            );
            
            return Ok(new
            {
                message = "任务链已创建",
                parentJobId = parentJobId,
                childJobId = childJobId,
                dashboardUrl = "/hangfire"
            });
        }

        /// <summary>
        /// 测试不同优先级队列
        /// </summary>
        /// <returns></returns>
        [HttpPost("queues")]
        public IActionResult TestQueues()
        {
            var criticalJobId = BackgroundJob.Enqueue("critical", () => TestJob("高优先级任务（critical队列）"));
            var defaultJobId = BackgroundJob.Enqueue("default", () => TestJob("普通任务（default队列）"));
            var lowJobId = BackgroundJob.Enqueue("low", () => TestJob("低优先级任务（low队列）"));
            
            return Ok(new
            {
                message = "3个不同优先级的任务已提交",
                jobs = new[]
                {
                    new { queue = "critical", jobId = criticalJobId },
                    new { queue = "default", jobId = defaultJobId },
                    new { queue = "low", jobId = lowJobId }
                },
                dashboardUrl = "/hangfire"
            });
        }

        /// <summary>
        /// 实际执行的测试任务（供 Hangfire 调用，非 API 端点）
        /// </summary>
        /// <param name="message"></param>
        [NonAction]
        [Hangfire.JobDisplayName("Hangfire测试任务 · {0}")]
        public void TestJob(string message)
        {
            _logger.LogInformation("========================================");
            _logger.LogInformation("[Hangfire 测试任务] 开始执行");
            _logger.LogInformation("消息: {Message}", message);
            _logger.LogInformation("执行时间: {Time}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            _logger.LogInformation("线程ID: {ThreadId}", Thread.CurrentThread.ManagedThreadId);
            
            // 模拟耗时操作
            Thread.Sleep(2000);
            
            _logger.LogInformation("[Hangfire 测试任务] 执行完成");
            _logger.LogInformation("========================================");
        }
    }
}
