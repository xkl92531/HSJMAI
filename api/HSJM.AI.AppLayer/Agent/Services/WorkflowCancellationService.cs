using System;
using System.Collections.Concurrent;
using System.Threading;
using HSJM.AI.Core;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.Agent.Services
{
    /// <summary>
    /// 工作流取消服务：管理工作流的 CancellationToken
    /// 用于支持前端"停止响应"功能
    /// </summary>
    public class WorkflowCancellationService : ISelfSingletonAutoInject
    {
        private readonly ConcurrentDictionary<long, CancellationTokenSource> _cancellationTokenSources = new();
        private readonly ILogger<WorkflowCancellationService> _logger;

        public WorkflowCancellationService(ILogger<WorkflowCancellationService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// 为工作流创建并注册 CancellationTokenSource
        /// </summary>
        public CancellationToken RegisterWorkflow(long workflowId)
        {
            // 如果已存在，先清理旧的
            if (_cancellationTokenSources.TryRemove(workflowId, out var oldCts))
            {
                oldCts.Dispose();
            }

            var cts = new CancellationTokenSource();
            _cancellationTokenSources[workflowId] = cts;
            
            _logger.LogDebug("[取消服务] 注册工作流: WorkflowId={WorkflowId}", workflowId);
            return cts.Token;
        }

        /// <summary>
        /// 获取工作流的 CancellationToken（如果不存在则返回 None）
        /// </summary>
        public CancellationToken GetToken(long workflowId)
        {
            if (_cancellationTokenSources.TryGetValue(workflowId, out var cts))
            {
                return cts.Token;
            }
            return CancellationToken.None;
        }

        /// <summary>
        /// 取消指定工作流的执行
        /// </summary>
        public bool CancelWorkflow(long workflowId)
        {
            if (_cancellationTokenSources.TryGetValue(workflowId, out var cts))
            {
                try
                {
                    if (!cts.IsCancellationRequested)
                    {
                        cts.Cancel();
                        _logger.LogInformation("[取消服务] 已取消工作流: WorkflowId={WorkflowId}", workflowId);
                        return true;
                    }
                    _logger.LogWarning("[取消服务] 工作流已被取消: WorkflowId={WorkflowId}", workflowId);
                    return false;
                }
                catch (ObjectDisposedException)
                {
                    _logger.LogWarning("[取消服务] CancellationTokenSource 已释放: WorkflowId={WorkflowId}", workflowId);
                    return false;
                }
            }
            
            _logger.LogWarning("[取消服务] 未找到工作流: WorkflowId={WorkflowId}", workflowId);
            return false;
        }

        /// <summary>
        /// 检查工作流是否已被取消
        /// </summary>
        public bool IsCancelled(long workflowId)
        {
            if (_cancellationTokenSources.TryGetValue(workflowId, out var cts))
            {
                return cts.IsCancellationRequested;
            }
            return false;
        }

        /// <summary>
        /// 清理工作流的 CancellationTokenSource（工作流完成后调用）
        /// </summary>
        public void UnregisterWorkflow(long workflowId)
        {
            if (_cancellationTokenSources.TryRemove(workflowId, out var cts))
            {
                cts.Dispose();
                _logger.LogDebug("[取消服务] 注销工作流: WorkflowId={WorkflowId}", workflowId);
            }
        }

        /// <summary>
        /// 获取当前活跃的工作流数量（用于监控）
        /// </summary>
        public int ActiveWorkflowCount => _cancellationTokenSources.Count;
    }
}
