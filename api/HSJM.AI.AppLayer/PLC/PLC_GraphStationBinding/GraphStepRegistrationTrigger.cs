#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.AppLayer.Agent.Services.Plc_diagnosis;
using HSJM.AI.Core;

namespace HSJM.AI.AppLayer.PLC.PLC_GraphStationBinding
{
    /// <summary>一个项目的重注册请求结果：Enqueued=false 且无错误 = 该项目正在注册，已登记结束后补跑。</summary>
    public sealed record GraphRegistrationRequestResult(long ProjectId, bool Enqueued, string? Error);

    /// <summary>
    /// 工站绑定变化（绑定 / 解绑 / 接受建议）之后，对涉及的项目各排队一次全项目 GRAPH 步位重新注册，
    /// 让 IGW 里的工站分组跟上最新绑定（2026-10-14）。
    /// 用 GraphStepTagRegistrationJob.EnqueueWholeProject：项目正在注册时登记补跑，结束后自动再跑一次，变化不会丢。
    /// 排队失败只记在结果里，不影响绑定本身。
    /// </summary>
    public class GraphStepRegistrationTrigger : ISelfScopedAutoInject
    {
        private readonly Func<long, bool> _enqueueWholeProject;

        public GraphStepRegistrationTrigger() : this(GraphStepTagRegistrationJob.EnqueueWholeProject) { }

        /// <summary>测试用：替换真实的 Hangfire 入队</summary>
        internal GraphStepRegistrationTrigger(Func<long, bool> enqueueWholeProject)
        {
            _enqueueWholeProject = enqueueWholeProject;
        }

        public IReadOnlyList<GraphRegistrationRequestResult> Request(IEnumerable<long> projectIds)
        {
            var results = new List<GraphRegistrationRequestResult>();
            foreach (var projectId in (projectIds ?? Enumerable.Empty<long>()).Where(id => id > 0).Distinct().OrderBy(id => id))
            {
                try
                {
                    results.Add(new GraphRegistrationRequestResult(projectId, _enqueueWholeProject(projectId), null));
                }
                catch (Exception ex)
                {
                    results.Add(new GraphRegistrationRequestResult(projectId, false, ex.Message));
                }
            }
            return results;
        }

        /// <summary>给前端看的一句话说明；没有请求时返回空串</summary>
        public static string Describe(IReadOnlyCollection<GraphRegistrationRequestResult> results)
        {
            if (results == null || results.Count == 0)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            var enqueued = results.Where(r => r.Enqueued).Select(r => r.ProjectId).ToList();
            var rerun = results.Where(r => !r.Enqueued && r.Error == null).Select(r => r.ProjectId).ToList();
            var failed = results.Where(r => r.Error != null).ToList();
            if (enqueued.Count > 0)
            {
                parts.Add($"已排队重新注册 GRAPH 步位（项目 {string.Join("、", enqueued)}）");
            }
            if (rerun.Count > 0)
            {
                parts.Add($"项目 {string.Join("、", rerun)} 正在注册，结束后自动补跑");
            }
            if (failed.Count > 0)
            {
                parts.Add($"项目 {string.Join("、", failed.Select(f => f.ProjectId))} 排队失败（{failed[0].Error}），请手动点「重新注册」");
            }
            return string.Join("；", parts);
        }
    }
}
