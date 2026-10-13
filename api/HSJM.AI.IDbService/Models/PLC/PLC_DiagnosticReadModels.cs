using System;
using System.Collections.Generic;

namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 约束诊断读取的逐点质量（D-1，spec docs/superpowers/specs/2026-09-19-diagnosis-igw-read-design.md §4.1）。
    /// 只有 <see cref="Good"/> 带可信值；其余一律表示"没采到"，诊断层不得把它当成通过。
    /// </summary>
    public enum PLC_SignalReadStatus
    {
        /// <summary>质量 Good，Value 可信</summary>
        Good = 0,
        /// <summary>PLC 程序里没有该符号（IGW reason=SymbolNotFound，0x8034xxxx）</summary>
        SymbolNotFound,
        /// <summary>别名在 IGW 不存在（未注册/已删除），或 HSJM 无法生成别名</summary>
        NotRegistered,
        /// <summary>与设备通讯中断 / 网关不可达 / 网关热重载</summary>
        CommFailure,
        /// <summary>读取超时</summary>
        Timeout,
        /// <summary>点已注册、设备已连上，但还没有首个值</summary>
        WaitingForData,
        /// <summary>无读取权限</summary>
        AccessDenied,
        /// <summary>设备/驱动侧其它错误（含无法从旧接口报文定位的失败）</summary>
        DeviceError,
        /// <summary>老网关行 IsGood=false</summary>
        BadQuality,
        /// <summary>网关未返回该点</summary>
        Missing,
        /// <summary>
        /// 本 PLC IO 表里已登记的点，但本次诊断读取口径没有把它纳入读取范围（如 ConstraintStatus 过滤掉）。
        /// 区别于 <see cref="Missing"/>："网关没返回"暗示网关/通讯有问题，这个点根本没被发去读，不该算到
        /// 通讯故障头上。附加在枚举末尾，避免打乱既有序数值。
        /// </summary>
        NotRead,
    }

    /// <summary>项目级读取状态</summary>
    public enum PLC_DiagnosticProjectStatus
    {
        /// <summary>读取完成（逐点质量见各信号）</summary>
        Ok = 0,
        /// <summary>项目不存在 / 配置缺失（IotUrl、老网关 StationId、IGW PlcIp）/ 点表读取失败</summary>
        ProjectUnavailable,
        /// <summary>网关整体不可用：全部点通讯故障/超时，或读取抛出异常</summary>
        CommFailure,
    }

    /// <summary>诊断读取的单个信号</summary>
    public sealed class PLC_DiagnosticSignal
    {
        /// <summary>= TableName.TagName 去掉 "IQ."（与 GetReadData 返回的 TagName 同口径）</summary>
        public string TagName { get; set; } = string.Empty;

        /// <summary>ai_plc_iotag.TagName</summary>
        public string OldTagName { get; set; } = string.Empty;

        /// <summary>仅 Status=Good 时有值</summary>
        public object? Value { get; set; }

        public PLC_SignalReadStatus Status { get; set; }

        /// <summary>非 Good 时的说明（网关报文 / 状态码 / 别名）</summary>
        public string? Detail { get; set; }

        /// <summary>IGW 最后一次真实采集时间（UTC）；老网关与旧 IGW 接口为 null</summary>
        public DateTime? TimestampUtc { get; set; }
    }

    /// <summary>一个 PLC 项目的诊断读取结果</summary>
    public sealed class PLC_DiagnosticReadResult
    {
        public long ProjectId { get; set; }

        public PLC_DiagnosticProjectStatus ProjectStatus { get; set; }

        public string? ProjectError { get; set; }

        public IReadOnlyList<PLC_DiagnosticSignal> Signals { get; set; } = Array.Empty<PLC_DiagnosticSignal>();

        /// <summary>Status=Good 的信号数</summary>
        public int GoodCount
        {
            get
            {
                var count = 0;
                foreach (var signal in Signals)
                {
                    if (signal != null && signal.Status == PLC_SignalReadStatus.Good)
                    {
                        count++;
                    }
                }
                return count;
            }
        }
    }

    /// <summary>IGW 质量/原因 → <see cref="PLC_SignalReadStatus"/>（纯函数；规则同 IGW docs/dcs-read-quality.md）。</summary>
    public static class PLC_SignalReadStatusMapper
    {
        /// <summary>ReadTagsWithQuality 的 isGood + reason → 状态；未知 reason 按 DeviceError（契约只新增不改义）。</summary>
        public static PLC_SignalReadStatus FromIgwReason(bool isGood, string? reason)
        {
            if (isGood)
            {
                return PLC_SignalReadStatus.Good;
            }
            switch ((reason ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "symbolnotfound": return PLC_SignalReadStatus.SymbolNotFound;
                case "notregistered": return PLC_SignalReadStatus.NotRegistered;
                case "commfailure": return PLC_SignalReadStatus.CommFailure;
                case "timeout": return PLC_SignalReadStatus.Timeout;
                case "waitingfordata": return PLC_SignalReadStatus.WaitingForData;
                case "accessdenied": return PLC_SignalReadStatus.AccessDenied;
                default: return PLC_SignalReadStatus.DeviceError;
            }
        }

        /// <summary>
        /// OPC UA 状态码 → 状态（旧接口报文 "{名称}(0x........)" 解析后使用），与 IGW QualityReason 同表。
        /// BadNotConnected(0x808A) 在旧接口下无法区分"设备断开"与"刚注册未采到"，按通讯故障。
        /// </summary>
        public static PLC_SignalReadStatus FromStatusCode(uint statusCode)
        {
            if ((statusCode & 0xC0000000u) == 0)
            {
                return PLC_SignalReadStatus.Good;
            }
            switch (statusCode & 0xFFFF0000u)
            {
                case 0x80340000u: return PLC_SignalReadStatus.SymbolNotFound;
                case 0x800A0000u: return PLC_SignalReadStatus.Timeout;
                case 0x80320000u: return PLC_SignalReadStatus.WaitingForData;
                case 0x80050000u: return PLC_SignalReadStatus.CommFailure;
                case 0x808A0000u: return PLC_SignalReadStatus.CommFailure;
                case 0x801F0000u: return PLC_SignalReadStatus.AccessDenied;
                default: return PLC_SignalReadStatus.DeviceError;
            }
        }

        /// <summary>状态码名称 → 状态（旧接口报文不带十六进制码时的兜底）。</summary>
        public static PLC_SignalReadStatus FromStatusName(string? name)
        {
            var text = (name ?? string.Empty).Trim();
            if (string.Equals(text, "Good", StringComparison.OrdinalIgnoreCase)) return PLC_SignalReadStatus.Good;
            if (string.Equals(text, "BadNodeIdUnknown", StringComparison.OrdinalIgnoreCase)) return PLC_SignalReadStatus.SymbolNotFound;
            if (string.Equals(text, "BadTimeout", StringComparison.OrdinalIgnoreCase)) return PLC_SignalReadStatus.Timeout;
            if (string.Equals(text, "BadWaitingForInitialData", StringComparison.OrdinalIgnoreCase)) return PLC_SignalReadStatus.WaitingForData;
            if (string.Equals(text, "BadCommFailure", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "BadNotConnected", StringComparison.OrdinalIgnoreCase)) return PLC_SignalReadStatus.CommFailure;
            if (string.Equals(text, "BadUserAccessDenied", StringComparison.OrdinalIgnoreCase)) return PLC_SignalReadStatus.AccessDenied;
            return PLC_SignalReadStatus.DeviceError;
        }
    }
}
