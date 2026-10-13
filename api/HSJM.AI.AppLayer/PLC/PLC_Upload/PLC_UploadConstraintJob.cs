using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
    /// 上传链路的后台约束生成任务（2026-10-09 拆分）：上传任务入库成功后即 MarkCompleted，
    /// XMLV2 约束发布由本任务在 Hangfire 后台执行，进度经上传任务的后处理槽（PostProcess）呈现。
    ///
    /// 版本一致性（接替上传任务内全程持锁的后半段）：
    /// - 上传任务在同项目发布锁内「入库 → 计算库指纹 → 保存包+meta → 入队本任务 → MarkCompleted」；
    /// - 本任务拿同一把锁执行「校验 → 发布」：库指纹或保存包 meta 与入队时的期望指纹不一致 → 跳过
    ///   （说明有更新的上传已接管，最新上传的约束任务会接力——即使它的入队被 TryClaim 挡掉，
    ///   本任务发现「包与库一致」也会按新包发布，版本始终正确）；
    /// - 同一版本已成功发布过（meta.PublishedFingerprint == 库指纹）→ 跳过，避免背靠背上传重复发布。
    ///
    /// 失败语义：发布失败只把后处理槽标 Failed（保存包保留——库与包仍同版本，可用「重置规则」重试），
    /// 不再 Discard 旧包；上传主体状态不受影响（已完成）。
    /// 与重置 / 重跑共用 <see cref="PLC_XmlV2RerunJob"/> 的 InFlight 占位（TryClaim/Release），不并发改同一项目规则。
    /// </summary>
    public sealed class PLC_UploadConstraintJob(
        Original353TestPublisher publisher,
        PlcArchiveStore archives,
        IOConstraintProgressService legacyProgress,
        PLC_UploadProgressService uploadProgress,
        IPLC_SourceFileService sourceFileService,
        ILogger<PLC_UploadConstraintJob> logger) : ISelfScopedAutoInject
    {
        public const string PostProcessKey = "xmlv2-constraint";
        public const string PostProcessTitle = "生成约束";

        /// <summary>
        /// 入队（上传任务在发布锁内调用）。占位复用 PLC_XmlV2RerunJob.TryClaim——与重置 / 重跑互斥：
        /// 占位失败（同项目已有约束任务排队/执行）时返回 false，调用方按「接力提示」写槽即可，
        /// 正在执行的任务会在版本校验后按最新包发布（见类注释）。
        /// </summary>
        public static bool Enqueue(long projectId, string taskId, string expectedFingerprint,
            PLC_UploadProgressService progress = null, ILogger logger = null)
        {
            if (!PLC_XmlV2RerunJob.TryClaim(projectId))
            {
                progress?.SetPostProcess(taskId, PostProcessKey, PostProcessTitle, "Running",
                    "同项目已有约束任务在执行，完成后将自动按最新数据发布（如未生成可使用「重置规则」兜底）");
                logger?.LogInformation(
                    "[上传约束] 项目 {ProjectId} 已有 XMLV2 任务占用，本次约束生成交由执行中任务接力: TaskId={TaskId}",
                    projectId, taskId);
                return false;
            }
            try
            {
                new BackgroundJobClient().Create<PLC_UploadConstraintJob>(job =>
                        job.RunAsync(projectId, taskId, expectedFingerprint),
                    new EnqueuedState(MachineQueue.BuildCurrent()));
                return true;
            }
            catch
            {
                PLC_XmlV2RerunJob.Release(projectId);
                throw;
            }
        }

        [AutomaticRetry(Attempts = 0)] // 不自动重试：失败保留保存包，用「重置规则」显式重试
        [Hangfire.JobDisplayName("上传约束生成（XMLV2 后台发布） · 项目{0} 任务{1}")]
        public async Task RunAsync(long projectId, string taskId, string expectedFingerprint)
        {
            try
            {
                using (await PLC_XmlV2RerunJob.AcquirePublishLockAsync(projectId).ConfigureAwait(false))
                {
                    if (legacyProgress.IsRunning(projectId))
                        throw new PipelineException("LegacyConstraintJobRunning");

                    // 版本校验：库指纹与保存包 meta 都还必须是入队时的那一个版本
                    var currentFingerprint = await ComputeLibraryFingerprintAsync(sourceFileService, projectId)
                        .ConfigureAwait(false);
                    var meta = archives.ReadMeta(projectId);
                    var decision = Decide(meta, expectedFingerprint, currentFingerprint);
                    if (decision == ConstraintVersionDecision.SkipSuperseded)
                    {
                        // 有更新的上传已入库（保存包与库已是新版本）：本任务让位，由最新上传的约束任务接力。
                        uploadProgress.SetPostProcess(taskId, PostProcessKey, PostProcessTitle, "Skipped",
                            "已有更新的上传接管，本次约束生成已跳过（由最新上传的任务按新数据发布）");
                        static string Shorten(string value) =>
                            string.IsNullOrEmpty(value) ? "-" : value[..Math.Min(12, value.Length)];
                        logger.LogInformation(
                            "[上传约束] 项目 {ProjectId} 版本已变更（入队指纹={Expected}，当前={Current}，包指纹={Saved}），跳过: TaskId={TaskId}",
                            projectId, Shorten(expectedFingerprint), Shorten(currentFingerprint), Shorten(meta?.Fingerprint), taskId);
                        return;
                    }
                    if (decision == ConstraintVersionDecision.SkipAlreadyPublished)
                    {
                        // 同一版本已发布过（背靠背上传的第二个任务）：跳过，不重复整项目发布
                        uploadProgress.SetPostProcess(taskId, PostProcessKey, PostProcessTitle, "Skipped",
                            "该版本约束已由先前的任务生成，无需重复发布");
                        return;
                    }

                    uploadProgress.SetPostProcess(taskId, PostProcessKey, PostProcessTitle, "Running", "正在生成约束（XMLV2 整项目发布）...");
                    var published = await publisher.PublishArchiveAsync(projectId, archives.PathFor(projectId))
                        .ConfigureAwait(false);
                    archives.MarkPublished(projectId, currentFingerprint, taskId);
                    var note = PLC_UploadConstraintStage.PublishNote(published);
                    uploadProgress.SetPostProcess(taskId, PostProcessKey, PostProcessTitle, "Succeeded",
                        $"XMLV2 约束已重建 {published.RulesWritten} 条" + (string.IsNullOrWhiteSpace(note) ? "" : $"（{note}）"));
                    logger.LogInformation(
                        "[上传约束] 项目 {ProjectId} XMLV2 约束已发布: Rules={Rules}, ConditionalFeedback={Conditional}, PreservedMissingSource={Preserved}: TaskId={TaskId}",
                        projectId, published.RulesWritten, published.Generated.ConditionalFeedbackCount, published.PreservedRules, taskId);
                }
            }
            catch (Exception ex)
            {
                // 发布失败：库与保存包仍同版本（本任务不改库、成功前不改包的发布标记），
                // 保存包保留供「重置规则」重试，不 Discard。上传主体已 Completed，不受影响。
                uploadProgress.SetPostProcess(taskId, PostProcessKey, PostProcessTitle, "Failed",
                    "XMLV2 约束发布失败: " + ex.GetBaseException().Message + "（保存包已保留，可用「重置规则」重试）");
                logger.LogError(ex, "[上传约束] 项目 {ProjectId} XMLV2 约束发布失败（保存包保留，可用重置规则重试）: TaskId={TaskId}",
                    projectId, taskId);
                throw;
            }
            finally
            {
                PLC_XmlV2RerunJob.Release(projectId);
            }
        }

        internal enum ConstraintVersionDecision
        {
            /// <summary>包与库仍是入队版本且未发布过：执行发布。</summary>
            Publish,
            /// <summary>包或库已比入队版本新（更新的上传接管）：让位跳过。</summary>
            SkipSuperseded,
            /// <summary>同版本已成功发布过：跳过避免重复发布。</summary>
            SkipAlreadyPublished,
        }

        /// <summary>
        /// 版本校验判定（纯函数，2026-10-09 上传约束后台执行）：
        /// 「保存包 meta 指纹 == 入队指纹 && 当前库指纹 == 入队指纹」才发布——任何一侧不一致都说明
        /// 有更新的上传已接管（让位给最新上传的约束任务，即使它的入队被 TryClaim 挡掉，本任务的让位
        /// 与最新包的版本一致性共同保证最终发布的总是最新版本）；同版本已发布过则直接跳过。
        /// </summary>
        internal static ConstraintVersionDecision Decide(PlcArchiveMeta meta, string expectedFingerprint, string currentFingerprint)
        {
            if (meta == null
                || !string.Equals(meta.Fingerprint, expectedFingerprint, StringComparison.Ordinal)
                || !string.Equals(currentFingerprint, expectedFingerprint, StringComparison.Ordinal))
            {
                return ConstraintVersionDecision.SkipSuperseded;
            }
            if (!string.IsNullOrEmpty(meta.PublishedFingerprint)
                && string.Equals(meta.PublishedFingerprint, currentFingerprint, StringComparison.Ordinal))
            {
                return ConstraintVersionDecision.SkipAlreadyPublished;
            }
            return ConstraintVersionDecision.Publish;
        }

        /// <summary>
        /// 库内程序指纹：项目全部未删源文件的 FileHash（排序后聚合 SHA256）。
        /// 上传入库后与约束任务执行前各算一次，作为「库内程序版本」——上传清旧插新必然改变指纹
        /// （ComputeSourceFingerprint 含伴随源码），库指纹一致即程序数据一致。
        /// FileHash 为 NULL 的存量行按空串参与（不影响一致性判定：同一份库两次计算结果相同）。
        /// </summary>
        public static async Task<string> ComputeLibraryFingerprintAsync(IPLC_SourceFileService service, long projectId)
        {
            var hashes = (await service.GetListAsync(s => s.ProjectId == projectId && !s.IsDeleted)
                    .ConfigureAwait(false))
                .Select(s => s.FileHash ?? string.Empty)
                .OrderBy(h => h, StringComparer.Ordinal)
                .ToList();
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", hashes)));
            return Convert.ToHexString(bytes);
        }
    }
}
