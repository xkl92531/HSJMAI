using System;

namespace HSJM.AI.IDbService
{
    /// <summary>
    /// IGW 注册身份（I-3，2026-09-20）：规范化 IGW 基址 + PLC 直连 IP + 项目通道名。
    /// 任一变化都意味着 IGW 上「本项目的位置」变了：旧通道/旧 IP 逻辑设备需清理、本次需全量注册。
    /// 比较一律大小写不敏感（IGW 通道名查找、主机名均不区分大小写）。
    /// </summary>
    public sealed record IgwRegistrationIdentity(string IgwBaseUrl, string PlcIp, string ChannelName)
    {
        /// <summary>任一部分为空 → null（身份不完整，不参与比较/登记）</summary>
        public static IgwRegistrationIdentity? Create(string? iotUrl, string? plcIp, string? channelName)
        {
            var url = NormalizeIgwBaseUrl(iotUrl);
            var ip = plcIp?.Trim();
            var channel = channelName?.Trim();
            if (url == null || string.IsNullOrEmpty(ip) || string.IsNullOrEmpty(channel))
            {
                return null;
            }
            return new IgwRegistrationIdentity(url, ip, channel);
        }

        /// <summary>
        /// 规范化 IGW 基址：补 http://、去尾部斜杠后以单个 / 结尾（DcsIgwConsts.NormalizeGatewayBaseUrl），scheme 与 host[:port] 小写。
        /// 空白 → null。与 IgwBulkClient 的探测缓存键同一规则。
        /// </summary>
        public static string? NormalizeIgwBaseUrl(string? iotUrl)
        {
            if (string.IsNullOrWhiteSpace(iotUrl))
            {
                return null;
            }
            var normalized = DcsIgwConsts.NormalizeGatewayBaseUrl(iotUrl);
            if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
            {
                return normalized;
            }
            return uri.Scheme.ToLowerInvariant() + "://" + uri.Authority.ToLowerInvariant() + uri.PathAndQuery;
        }

        public bool SameIgw(IgwRegistrationIdentity other) =>
            other != null && string.Equals(IgwBaseUrl, other.IgwBaseUrl, StringComparison.OrdinalIgnoreCase);

        public bool Matches(IgwRegistrationIdentity other) =>
            SameIgw(other)
            && string.Equals(PlcIp, other.PlcIp, StringComparison.OrdinalIgnoreCase)
            && string.Equals(ChannelName, other.ChannelName, StringComparison.OrdinalIgnoreCase);

        public string Describe() => $"IGW {IgwBaseUrl}，PLC {PlcIp}，通道 {ChannelName}";
    }

    /// <summary>ai_plc_igwregistrationstate 一行（原生 SQL 映射，属性名即 SELECT 别名）</summary>
    public sealed class IgwRegistrationStateRow
    {
        public long ProjectId { get; set; }
        public string? RegisteredIotBaseUrl { get; set; }
        public string? RegisteredPlcIp { get; set; }
        public string? RegisteredChannel { get; set; }
        public DateTime? RegisteredAt { get; set; }
        public DateTime? GraphFailedAt { get; set; }
        public string? GraphFailure { get; set; }
        public string? PendingArgsJson { get; set; }
        public string? PendingQueue { get; set; }
        public DateTime? PendingAt { get; set; }

        /// <summary>最近一次成功写入 IGW 的注册身份（未登记/不完整 → null）</summary>
        public IgwRegistrationIdentity? RegisteredIdentity =>
            IgwRegistrationIdentity.Create(RegisteredIotBaseUrl, RegisteredPlcIp, RegisteredChannel);
    }

    /// <summary>待补跑记录（pendingargsjson / pendingqueue）的写入规则（M-1 fix round 2，生产 SQL 事务与单测内存实现共用）</summary>
    public static class IgwPendingRecordRules
    {
        /// <summary>
        /// 由现有记录与本次写入算出最终（范围 JSON, 所属队列）：
        /// 无现有范围 / 现有记录属于本队列 → 本次快照（本机闸门里的最新完整状态）；
        /// 现有记录属于其它队列 → 并集，保留其所属队列（由其所属机器恢复，本机不覆盖、不清除）；
        /// 现有范围无所属队列（无人会恢复）→ 并集，归本队列。
        /// </summary>
        public static (string Json, string Queue) ResolveWrite(string? existingJson, string? existingQueue, string json, string queue,
            Func<string, string, string>? merge)
        {
            if (string.IsNullOrEmpty(existingJson) || string.Equals(existingQueue, queue, StringComparison.Ordinal))
            {
                return (json, queue);
            }
            var merged = merge?.Invoke(existingJson, json) ?? json;
            return (merged, string.IsNullOrEmpty(existingQueue) ? queue : existingQueue);
        }
    }

    /// <summary>其它未删除项目的网关配置与登记身份（同 IP / 同通道冲突判定、删项目时的共享通道保护）</summary>
    public sealed class IgwPeerProjectRow
    {
        public long ProjectId { get; set; }
        public string? ProjectName { get; set; }
        public string? IotUrl { get; set; }
        public string? PlcIp { get; set; }

        /// <summary>项目表 igwchannelname 列（首次注册解析通道名时写入；未注册过 / 列缺失 → null）</summary>
        public string? ConfiguredChannel { get; set; }

        /// <summary>
        /// igwchannelname 列读不到（列缺失 42703，迁移未执行）→ true：<see cref="ConfiguredChannel"/> 为 null 不代表「未注册过」，
        /// 旧位置占用判定按「可能占用」处理（宁可不删）
        /// </summary>
        public bool ConfiguredChannelUnknown { get; set; }

        public IgwRegistrationIdentity? RegisteredIdentity { get; set; }
    }
}
