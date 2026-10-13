using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.PLC.PLC_Upload.Igw
{
    /// <summary>已删除项目的 IGW 相关信息（项目行已软删；RegisteredIdentity 来自注册状态表）</summary>
    public sealed record IgwRemovedProjectInfo(long ProjectId, string ProjectName, string IotUrl, string PlcIp, string ChannelName,
        IgwRegistrationIdentity RegisteredIdentity);

    /// <summary>删项目清理对持久化层的依赖（生产走 SqlSugar 服务，单测用内存实现）</summary>
    public interface IIgwProjectRemovalStore
    {
        Task<IgwRemovedProjectInfo> GetProjectAsync(long projectId);

        Task<IReadOnlyList<IgwPeerProjectRow>> ListPeerProjectsAsync(long projectId);

        /// <summary>硬删该项目的注册台账行与注册状态行</summary>
        Task DeleteRegistrationRecordsAsync(long projectId);
    }

    /// <summary>
    /// 删除项目后清理其 IGW 通道（M-4，2026-09-20）：
    /// 目标位置 = 注册状态表登记的身份（设备实际所在处）；未登记则按项目 IotUrl + PLC IP + 通道名列。
    /// 只删该通道里逻辑名属于本项目 PLC IP（S7_/S1500_/S7Plus_{ip}）且带 dcsManagedBy=bulk 的设备，通道删空了才删通道；
    /// 另一未删除项目在同一 IGW 登记了同名通道，或未登记但当前配置占用该位置（<see cref="IgwIdentityConflicts.FindOccupantOfPreviousLocation"/>）
    /// → 不动 IGW。老网关 / 从未注册 → 跳过。
    /// 成功或跳过 → 硬删本项目注册记录；IGW 清理失败 → Failed、保留记录（Hangfire 面板重试）。
    /// </summary>
    public sealed class IgwProjectRemovalCleaner
    {
        private readonly IgwBulkClient _client;
        private readonly IIgwProjectRemovalStore _store;
        private readonly Func<string, Task> _writeLog;
        private readonly ILogger _logger;

        public IgwProjectRemovalCleaner(IgwBulkClient client, IIgwProjectRemovalStore store, Func<string, Task> writeLog, ILogger logger)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _writeLog = writeLog ?? (_ => Task.CompletedTask);
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<IgwRegistrationOutcome> RunAsync(long projectId, CancellationToken ct)
        {
            var info = await _store.GetProjectAsync(projectId).ConfigureAwait(false);
            if (info == null)
            {
                return await DoneAsync(projectId, null, IgwRegistrationStatus.Skipped, "项目不存在，无需清理 IGW", deleteRecords: false)
                    .ConfigureAwait(false);
            }

            var target = info.RegisteredIdentity ?? IgwRegistrationIdentity.Create(info.IotUrl, info.PlcIp, info.ChannelName);
            if (target == null || DcsIgwConsts.IsLegacyIotGatewayUrl(target.IgwBaseUrl))
            {
                return await DoneAsync(projectId, info, IgwRegistrationStatus.Skipped,
                    "项目从未在 IGW 注册（无通道名 / PLC IP，或为老网关），无需清理 IGW", deleteRecords: true).ConfigureAwait(false);
            }

            var peers = await _store.ListPeerProjectsAsync(projectId).ConfigureAwait(false);
            var sharing = peers
                .Where(p => p.RegisteredIdentity != null && p.RegisteredIdentity.SameIgw(target)
                            && string.Equals(p.RegisteredIdentity.ChannelName, target.ChannelName, StringComparison.OrdinalIgnoreCase))
                .Select(p => $"{p.ProjectName}({p.ProjectId})")
                .ToList();
            if (sharing.Count > 0)
            {
                return await DoneAsync(projectId, info, IgwRegistrationStatus.Skipped,
                    $"通道 {target.ChannelName} 同时登记在项目 {string.Join("、", sharing)} 名下，为免误删不清理 IGW（只删除本项目注册记录）",
                    deleteRecords: true).ConfigureAwait(false);
            }

            // 未登记身份的存活项目（加固前注册、或身份保存失败）也可能正用着该位置：当前配置同 IGW + PLC IP + 通道
            // （通道列读不到时同 IGW + PLC IP 即算）→ 设备可能属于它，不动 IGW。同 IGW + 同通道但不同 IP 的未登记项目
            // 不算占用：清理只删本项目 IP 的设备，对方设备留在通道里，通道因此不会被删
            var occupant = IgwIdentityConflicts.FindOccupantOfPreviousLocation(target, peers);
            if (occupant != null)
            {
                return await DoneAsync(projectId, info, IgwRegistrationStatus.Skipped,
                    $"项目 {occupant.ProjectName}({occupant.ProjectId}) 当前配置指向同一位置（{target.Describe()}），设备可能属于它，"
                    + "为免误删不清理 IGW（只删除本项目注册记录）",
                    deleteRecords: true).ConfigureAwait(false);
            }

            IgwProjectDeviceCleanup cleanup;
            try
            {
                cleanup = await _client.RemoveProjectDevicesAsync(target.IgwBaseUrl, target.ChannelName, target.PlcIp,
                    deleteChannelIfEmpty: true, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return await DoneAsync(projectId, info, IgwRegistrationStatus.Failed,
                    $"清理 IGW 失败（{target.Describe()}）：{ex.GetBaseException().Message}；注册记录已保留，可在 Hangfire 面板重试",
                    deleteRecords: false).ConfigureAwait(false);
            }

            var detail = !cleanup.ChannelFound
                ? $"{target.Describe()}：IGW 上已无该通道"
                : $"{target.Describe()}：删除设备 {cleanup.Removed.Count} 个"
                  + (cleanup.ChannelDeleted ? "，通道已删除" : "，通道仍有其它设备，保留")
                  + (cleanup.SkippedUnmanaged.Count > 0
                      ? $"，跳过无批量标记设备 {cleanup.SkippedUnmanaged.Count} 个（{string.Join("、", cleanup.SkippedUnmanaged)}，请人工核对）"
                      : string.Empty);
            return await DoneAsync(projectId, info, IgwRegistrationStatus.Succeeded, detail, deleteRecords: true).ConfigureAwait(false);
        }

        private async Task<IgwRegistrationOutcome> DoneAsync(long projectId, IgwRemovedProjectInfo info, IgwRegistrationStatus status,
            string detail, bool deleteRecords)
        {
            if (deleteRecords)
            {
                try
                {
                    await _store.DeleteRegistrationRecordsAsync(projectId).ConfigureAwait(false);
                    IgwAliasCacheInvalidation.Invalidate(projectId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[IGW清理] 项目 {ProjectId} 删除注册记录失败", projectId);
                    detail += $"；删除注册记录失败：{ex.GetBaseException().Message}";
                    if (status == IgwRegistrationStatus.Succeeded)
                    {
                        status = IgwRegistrationStatus.Failed;
                    }
                }
            }
            var text = $"[IGW清理] 已删除项目 {info?.ProjectName}({projectId}) {status}：{detail}";
            _logger.LogInformation("{Text}", text);
            try
            {
                await _writeLog(text).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[IGW清理] 写日志失败");
            }
            return new IgwRegistrationOutcome(status, detail);
        }
    }
}
