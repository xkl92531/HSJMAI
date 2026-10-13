using System;
using System.Text;

namespace HSJM.AI.Core;

/// <summary>
/// 本机专用 Hangfire 队列名计算（2026-09-07 从 GraphStepTagRegistrationJob/IOConstraintPreprocessService/
/// AlarmVariableRecognitionService/PLC_UploadApp 的 inline 拷贝提炼到 Core，作为唯一权威实现；
/// 与 HSJM.AI 顶层 HangfireQueueNames.BuildCurrentServiceQueue 保持等价语义）。
/// 语义：凡任务携带本机资源（本地文件路径、本地内存状态）或本机 dll 版本强相关时，
/// 必须入 local_&lt;机器名&gt; 队列，避免多机共享 Hangfire 库时被其它机器的 worker 抢走
/// （实测症状：任务秒级假 Succeeded / StoredSourceMissing 等本机资源缺失错误）。
/// </summary>
public static class MachineQueue
{
    /// <summary>计算本机专用队列名：local_&lt;归一化机器名&gt;。环境变量 HSJM_HANGFIRE_LOCAL_QUEUE 可覆盖机器名。</summary>
    public static string BuildCurrent()
    {
        var raw = Environment.GetEnvironmentVariable("HSJM_HANGFIRE_LOCAL_QUEUE");
        if (string.IsNullOrWhiteSpace(raw)) raw = Environment.MachineName;
        var builder = new StringBuilder();
        foreach (var ch in raw.Trim().ToLowerInvariant())
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
                builder.Append(ch);
            else if (builder.Length == 0 || builder[builder.Length - 1] != '_')
                builder.Append('_');
        }
        var normalized = builder.ToString().Trim('_');
        if (string.IsNullOrWhiteSpace(normalized)) normalized = "current";
        return $"local_{normalized}";
    }
}
