using System;
using System.Threading;
using Hangfire;
using Hangfire.States;
using HSJM.AI.Core;

namespace HSJM.AI.AppLayer.LineUp;

public interface ILineUpImportJobScheduler
{
    void Enqueue(long batchId);
}

public sealed class LineUpImportJobScheduler : ILineUpImportJobScheduler, IAutoInject, ISelfScopedAutoInject
{
    public void Enqueue(long batchId)
    {
        if (batchId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchId));
        }

        // 2026-09-07: 显式入队到本机专用队列 local_<本机机器名>（与 PLC 上传/GRAPH 注册等同模式）。
        // 原因：LineUp 导入源文件存在本机磁盘（App_Data/lineup-imports/<batchId>/source.*），
        // 裸 BackgroundJob.Enqueue 落入多机共享的 default 队列——共享 Hangfire 库时远端 worker
        // 抢走任务后在本机磁盘找不到源文件，批次秒级报 StoredSourceMissing（实测 4 个批次全中招）。
        new BackgroundJobClient().Create<LineUpImportJob>(
            job => job.ExecuteAsync(batchId, CancellationToken.None),
            new EnqueuedState(MachineQueue.BuildCurrent()));
    }
}
