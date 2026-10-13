using HSJM.AI.IDbService;
using System;
using System.Collections.Generic;

namespace HSJM.AI.AppLayer.PLC.PLC_Upload.Igw
{
    /// <summary>注册身份冲突：Blocking 非空 → 不发送任何请求（fail safe）；Warnings 只写进结果详情</summary>
    public sealed record IgwIdentityConflictResult(IReadOnlyList<string> Blocking, IReadOnlyList<string> Warnings);

    /// <summary>
    /// 同 IP / 同通道冲突判定（I-3，纯函数）：IGW 的 DCS 逻辑设备名（S7_/S1500_/S7Plus_{ip}）在整个 IGW 内全局解析，
    /// 同一 IGW 上两个项目登记相同 PLC IP → 别名歧义；登记相同通道名 → 设备名 S7/S7Plus/GRAPH 互相覆盖。
    /// 只有对方**已登记**（真的注册进了同一 IGW）才阻断；对方只是配置相同但从未注册 → 告警。
    /// </summary>
    public static class IgwIdentityConflicts
    {
        public static IgwIdentityConflictResult Evaluate(IgwRegistrationIdentity current, IEnumerable<IgwPeerProjectRow> peers)
        {
            var blocking = new List<string>();
            var warnings = new List<string>();
            if (current == null)
            {
                return new IgwIdentityConflictResult(blocking, warnings);
            }
            foreach (var peer in peers ?? Array.Empty<IgwPeerProjectRow>())
            {
                var name = $"{peer.ProjectName}({peer.ProjectId})";
                var registered = peer.RegisteredIdentity;
                var blocked = false;
                if (registered != null && registered.SameIgw(current))
                {
                    if (string.Equals(registered.PlcIp, current.PlcIp, StringComparison.OrdinalIgnoreCase))
                    {
                        blocking.Add($"项目 {name} 已在同一 IGW 以 PLC IP {current.PlcIp} 注册（逻辑设备名会重名）");
                        blocked = true;
                    }
                    else if (string.Equals(registered.ChannelName, current.ChannelName, StringComparison.OrdinalIgnoreCase))
                    {
                        blocking.Add($"项目 {name} 已在同一 IGW 使用通道 {current.ChannelName}");
                        blocked = true;
                    }
                }
                // 已阻断的项目不再重复告警；登记在别处（或别的 IP/通道）的项目也要看它**当前配置**是否已改成与本项目相同
                if (!blocked && ConfiguredOn(peer, current.IgwBaseUrl, current.PlcIp))
                {
                    warnings.Add($"项目 {name} 配置了相同的 IGW 与 PLC IP（尚未以该配置注册；若它也注册，逻辑设备名会冲突）");
                }
            }
            return new IgwIdentityConflictResult(blocking, warnings);
        }

        /// <summary>
        /// 找出占用本项目**旧注册位置**（身份变化前）的其它项目：其登记身份与旧位置完全相同，或没有登记身份但当前配置
        /// （项目表 IotUrl / PlcIp / igwchannelname）与旧位置的 IGW + PLC IP + 通道完全相同——旧通道里 S7_/S1500_/S7Plus_{旧IP}
        /// 设备可能属于它，清理旧位置会误删对方设备。对方通道列读不到（<see cref="IgwPeerProjectRow.ConfiguredChannelUnknown"/>）时，
        /// 只要配置在旧 IGW + 旧 IP 上就按占用处理（宁可不删）。无 → null。
        /// </summary>
        public static IgwPeerProjectRow FindOccupantOfPreviousLocation(IgwRegistrationIdentity previous, IEnumerable<IgwPeerProjectRow> peers)
        {
            if (previous == null)
            {
                return null;
            }
            foreach (var peer in peers ?? Array.Empty<IgwPeerProjectRow>())
            {
                if (peer.RegisteredIdentity != null)
                {
                    if (peer.RegisteredIdentity.Matches(previous))
                    {
                        return peer;
                    }
                    continue;
                }
                if (ConfiguredOn(peer, previous.IgwBaseUrl, previous.PlcIp)
                    && (peer.ConfiguredChannelUnknown
                        || string.Equals((peer.ConfiguredChannel ?? string.Empty).Trim(), previous.ChannelName, StringComparison.OrdinalIgnoreCase)))
                {
                    return peer;
                }
            }
            return null;
        }

        /// <summary>项目当前配置（项目表 IotUrl / PlcIp）是否指向该 IGW + PLC IP</summary>
        private static bool ConfiguredOn(IgwPeerProjectRow peer, string igwBaseUrl, string plcIp)
        {
            var peerUrl = IgwRegistrationIdentity.NormalizeIgwBaseUrl(peer.IotUrl);
            return peerUrl != null
                && string.Equals(peerUrl, igwBaseUrl, StringComparison.OrdinalIgnoreCase)
                && string.Equals((peer.PlcIp ?? string.Empty).Trim(), plcIp, StringComparison.OrdinalIgnoreCase);
        }
    }
}
