using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.States;
using HSJM.AI.AppLayer.Agent.Services.Plc_diagnosis;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.PLC.PLC_Upload
{
    /// <summary>
    /// 约束重置 / 单 LU / 单块重跑（审查 P2/P3，2026-10-02）：统一用项目保存的上传包（<see cref="PlcArchiveStore"/>）走 XMLV2 发布器，
    /// 不再调用老方法 IOConstraintPreprocessService。后台 Hangfire 本机队列执行，同项目防重；
    /// 与上传的 XMLV2 发布共用同一把进程内项目锁（<see cref="AcquirePublishLockAsync"/>），不会并发改同一项目的规则。
    /// </summary>
    public sealed class PLC_XmlV2RerunJob(
        Original353TestPublisher publisher,
        PlcArchiveStore archives,
        IPLC_LogicUnitService logicUnitService,
        IPLC_BlockService blockService,
        IOConstraintProgressService legacyProgress,
        ILogger<PLC_XmlV2RerunJob> logger) : ISelfScopedAutoInject
    {
        /// <summary>排队 / 执行中的项目（入队时登记，执行结束摘除；登记超过 2 小时视为失效，防止 worker 丢任务后永远挡住）。</summary>
        private static readonly ConcurrentDictionary<long, DateTime> InFlight = new();
        private static readonly ConcurrentDictionary<long, SemaphoreSlim> PublishLocks = new();
        private static readonly TimeSpan InFlightExpiry = TimeSpan.FromHours(2);

        public const string NoSavedArchiveMessage = "该项目没有保存的 XML 包，请重新上传一次";

        /// <summary>同项目 XMLV2 发布串行（上传发布 + 保存包、重置、重跑共用）。</summary>
        public static async Task<IDisposable> AcquirePublishLockAsync(long projectId, CancellationToken ct = default)
        {
            var gate = PublishLocks.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct).ConfigureAwait(false);
            return new Releaser(gate);
        }

        /// <summary>不等待地拿同项目发布锁；正在发布（上传 / 重置 / 重跑）时返回 null。请求线程读保存包前用（审查复核 M-1）。</summary>
        public static IDisposable TryAcquirePublishLock(long projectId)
        {
            var gate = PublishLocks.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
            return gate.Wait(0) ? new Releaser(gate) : null;
        }

        private sealed class Releaser(SemaphoreSlim gate) : IDisposable
        {
            private int released;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref released, 1) == 0) gate.Release();
            }
        }

        public static bool IsQueuedOrRunning(long projectId) =>
            InFlight.TryGetValue(projectId, out var since) && DateTime.UtcNow - since < InFlightExpiry;

        /// <summary>登记项目；已有未过期登记返回 false。</summary>
        internal static bool TryClaim(long projectId)
        {
            var now = DateTime.UtcNow;
            while (true)
            {
                if (InFlight.TryAdd(projectId, now)) return true;
                if (!InFlight.TryGetValue(projectId, out var since)) continue;
                if (now - since < InFlightExpiry) return false;
                if (InFlight.TryUpdate(projectId, now, since)) return true;
            }
        }

        internal static void Release(long projectId) => InFlight.TryRemove(projectId, out _);

        public static bool EnqueueProject(long projectId) =>
            Enqueue(projectId, client => client.Create<PLC_XmlV2RerunJob>(job => job.RunProjectAsync(projectId), Queue()));

        public static bool EnqueueLogicUnit(long projectId, long luId, bool refreshAi) =>
            Enqueue(projectId, client => client.Create<PLC_XmlV2RerunJob>(job => job.RunLogicUnitAsync(projectId, luId, refreshAi), Queue()));

        public static bool EnqueueBlock(long projectId, long blockId, bool refreshAi) =>
            Enqueue(projectId, client => client.Create<PLC_XmlV2RerunJob>(job => job.RunBlockAsync(projectId, blockId, refreshAi), Queue()));

        private static EnqueuedState Queue() => new(MachineQueue.BuildCurrent());

        private static bool Enqueue(long projectId, Func<BackgroundJobClient, string> create)
        {
            if (!TryClaim(projectId)) return false;
            try
            {
                create(new BackgroundJobClient());
                return true;
            }
            catch
            {
                Release(projectId);
                throw;
            }
        }

        [AutomaticRetry(Attempts = 0)]
        [JobDisplayName("XMLV2 约束重置（整项目重新发布） · 项目{0}")]
        public Task RunProjectAsync(long projectId) => RunAsync(projectId, "整项目", async path =>
        {
            var result = await publisher.PublishArchiveAsync(projectId, path).ConfigureAwait(false);
            logger.LogInformation("[XMLV2 重置] 项目 {ProjectId} 已重新发布：写入 {Rules} 条，替换 {Deleted} 条，缺程序体块保留 {Preserved} 条（{Blocks}）",
                projectId, result.RulesWritten, result.RulesDeleted, result.PreservedRules, result.Integrity.DescribeNonFc(10));
        });

        [AutomaticRetry(Attempts = 0)]
        [JobDisplayName("XMLV2 单 LU 重跑 · 项目{0} LU{1}")]
        public Task RunLogicUnitAsync(long projectId, long luId, bool refreshAi) => RunAsync(projectId, $"LU#{luId}", async path =>
        {
            var request = await BuildLogicUnitRequestAsync(logicUnitService, blockService, luId).ConfigureAwait(false);
            var (result, scope) = await publisher.PublishArchiveScopeAsync(projectId, path, request.Request, refreshAi).ConfigureAwait(false);
            logger.LogInformation("[XMLV2 重跑] 项目 {ProjectId} LU#{LuId}（{Block} 网络 {Network}）：写入 {Rules} 条，替换 {Deleted} 条 {Notes}",
                projectId, luId, scope.BlockName, request.Request.Target?.NetworkId, result.RulesWritten, result.RulesDeleted, string.Join("；", scope.Notes));
        });

        [AutomaticRetry(Attempts = 0)]
        [JobDisplayName("XMLV2 单块重跑 · 项目{0} 块{1}")]
        public Task RunBlockAsync(long projectId, long blockId, bool refreshAi) => RunAsync(projectId, $"Block#{blockId}", async path =>
        {
            var request = await BuildBlockRequestAsync(logicUnitService, blockService, blockId).ConfigureAwait(false);
            var (result, scope) = await publisher.PublishArchiveScopeAsync(projectId, path, request.Request, refreshAi).ConfigureAwait(false);
            logger.LogInformation("[XMLV2 重跑] 项目 {ProjectId} 块 {Block}：写入 {Rules} 条，替换 {Deleted} 条 {Notes}",
                projectId, scope.BlockName, result.RulesWritten, result.RulesDeleted, string.Join("；", scope.Notes));
        });

        private async Task RunAsync(long projectId, string what, Func<string, Task> body)
        {
            try
            {
                using (await AcquirePublishLockAsync(projectId).ConfigureAwait(false))
                {
                    if (!archives.Exists(projectId))
                        throw new PipelineException("NoSavedArchive");
                    if (legacyProgress.IsRunning(projectId))
                        throw new PipelineException("LegacyConstraintJobRunning");
                    await body(archives.PathFor(projectId)).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[XMLV2 重跑] 项目 {ProjectId} {What} 失败（未做任何改动）：{Reason}",
                    projectId, what, DescribeError(ex));
                throw;
            }
            finally
            {
                Release(projectId);
            }
        }

        /// <summary>库 LU → 重跑请求（附带同块全部 LU，用来找共用同一归档程序段的兄弟 LU）。</summary>
        internal static async Task<(long ProjectId, Original353RerunRequest Request)> BuildLogicUnitRequestAsync(
            IPLC_LogicUnitService logicUnitService, IPLC_BlockService blockService, long luId)
        {
            var lu = (await logicUnitService.GetListAsync(x => x.Id == luId && !x.IsDeleted).ConfigureAwait(false)).FirstOrDefault()
                ?? throw new PipelineException($"RerunLogicUnitNotFound:{luId}");
            if (!lu.BlockId.HasValue) throw new PipelineException($"RerunLogicUnitWithoutBlock:{luId}");
            var (projectId, blockName, units) = await LoadBlockAsync(logicUnitService, blockService, lu.BlockId.Value).ConfigureAwait(false);
            var target = units.FirstOrDefault(u => u.Id == luId) ?? new DbLogicUnitRef(luId, blockName, lu.NetworkId);
            return (projectId, Original353RerunRequest.ForLogicUnit(target, units));
        }

        internal static async Task<(long ProjectId, Original353RerunRequest Request)> BuildBlockRequestAsync(
            IPLC_LogicUnitService logicUnitService, IPLC_BlockService blockService, long blockId)
        {
            var (projectId, blockName, units) = await LoadBlockAsync(logicUnitService, blockService, blockId).ConfigureAwait(false);
            return (projectId, Original353RerunRequest.ForBlock(blockName, units));
        }

        private static async Task<(long ProjectId, string BlockName, IReadOnlyList<DbLogicUnitRef> Units)> LoadBlockAsync(
            IPLC_LogicUnitService logicUnitService, IPLC_BlockService blockService, long blockId)
        {
            var block = (await blockService.GetListAsync(b => b.Id == blockId && !b.IsDeleted).ConfigureAwait(false)).FirstOrDefault();
            if (block == null || !block.ProjectId.HasValue || string.IsNullOrWhiteSpace(block.BlockName))
                throw new PipelineException($"RerunBlockNotFound:{blockId}");
            var name = block.BlockName.Trim();
            var units = (await logicUnitService.GetListAsync(x => x.BlockId == blockId && !x.IsDeleted).ConfigureAwait(false))
                .Select(l => new DbLogicUnitRef(l.Id, name, l.NetworkId)).ToArray();
            return (block.ProjectId.Value, name, units);
        }

        /// <summary>失败原因 → 中文说明（PipelineException 的 Code 原样附后）。</summary>
        public static string DescribeError(Exception ex)
        {
            var code = ex is PipelineException pe ? pe.Code : ex.GetBaseException().Message;
            var head = code.Split(':')[0];
            var text = head switch
            {
                "NoSavedArchive" => NoSavedArchiveMessage,
                "ProjectPublishBusy" => "该项目正在发布约束（上传 / 重置 / 重跑），请稍后再试",
                "LegacyConstraintJobRunning" => "老方法约束任务正在运行，请等它结束后再试",
                "RerunLogicUnitNotFound" => "LU 不存在或已删除",
                "RerunLogicUnitWithoutBlock" => "LU 缺少所属程序块",
                "RerunBlockNotFound" => "程序块不存在、已删除或缺少项目",
                "RerunLogicUnitNotInArchive" => "该 LU 在保存的 XML 包里找不到对应程序段（块名 + 网络号），请重新上传后再试",
                "RerunBlockNotInArchive" => "保存的 XML 包里没有这个程序块，请重新上传后再试",
                "RerunScopeMissingSource" => "该程序块在保存的 XML 包里没有程序体（空块 / 加密块），无法重跑",
                "RerunUnderstandingFailed" => "理解步骤（FB 契约 / 普通网络 AI）整体失败，范围内规则不完整",
                "RerunNetworkAiFailed" or "RerunNetworkAiIncomplete" => "范围内普通网络 AI 读取不完整（模型不可用或超时）",
                "RerunFbContractUnavailable" => "范围内调用的 FB 契约读取失败（模型不可用或超时）",
                "RerunFbContractIncomplete" => "范围内调用的超长 FB 契约有分段读取失败（模型不可用或超时）",
                "RerunCodeProofIncomplete" => "代码证明未完整完成（单段异常或到期），范围内规则可能不全",
                "AiDescriptionsIncomplete" => "范围内规则的 AI 说明生成不完整（模型不可用或超时）",
                "IncompleteXmlArchiveForFullRuleReplace" => "保存的 XML 包与库里的程序块不一致，请重新上传后再试",
                "IncompleteXmlRecognition" => "保存的 XML 包没有识别出任何约束",
                _ => null
            };
            return text == null ? code : $"{text}（{code}）";
        }
    }
}
