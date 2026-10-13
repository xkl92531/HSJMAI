using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;

namespace HSJM.AI.AppLayer.PLC.PLC_Upload
{
    /// <summary>
    /// 上传约束阶段的共享辅助（2026-10-09 后台化改造）：约束生成已拆分为 Hangfire 后台任务
    /// <see cref="PLC_UploadConstraintJob"/>（上传入库成功后入队，经上传任务的后处理槽呈现进度），
    /// 原「上传任务内同步发布」的 RunAsync 路径已删除。本类保留两处仍被使用的静态方法。
    /// </summary>
    public static class PLC_UploadConstraintStage
    {
        /// <summary>
        /// 上传后台任务最外层兜底（审查复核 I-1）：只在任务尚未终态时标失败（「上传处理失败: 根因」）。
        /// 已失败的（如保存 XML 包失败）保留第一条更具体的原因；已完成的不翻成失败。返回是否写入。
        /// 注意（2026-10-09 后台化）：约束发布失败不再经过这里——它只把后处理槽标 Failed，
        /// 上传主体状态（Completed）不受影响。
        /// </summary>
        public static bool ReportUploadFailure(PLC_UploadProgressService progress, string taskId, Exception ex) =>
            progress.TryMarkFailed(taskId, $"上传处理失败: {ex.GetBaseException().Message}");

        /// <summary>发布结果里需要让用户看到的说明：缺程序体的非 FC 块（其旧规则原样保留）与 FC 缺程序体数（不计）。</summary>
        public static string PublishNote(Original353PublishResult result)
        {
            var parts = new List<string>();
            if (result.Integrity.HasNonFc)
                parts.Add($"归档里缺程序体的非 FC 块 {result.Integrity.DescribeNonFc(5)}，其旧规则 {result.PreservedRules} 条原样保留");
            if (result.Integrity.Fc.Count > 0)
                parts.Add($"缺程序体的 FC {result.Integrity.Fc.Count} 个（FC 不计）");
            return parts.Count == 0 ? null : string.Join("；", parts);
        }
    }
}
