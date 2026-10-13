using HSJM.AI.IDbService;
using System;
using System.Collections.Generic;

namespace HSJM.AI.AppLayer.PLC.PLC_Upload.Igw
{
    /// <summary>批量写入结果类别（R-1，2026-09-20）</summary>
    public enum IgwBulkFailureKind
    {
        /// <summary>HTTP 200</summary>
        None,

        /// <summary>瞬态：网络异常 / 超时 / 409 / 5xx（IGW 状态问题，重发可能成功）——台账 Failed，下次自动重试</summary>
        Transient,

        /// <summary>500 runtime_apply_failed：已写库、运行态未生效——算瞬态失败，但 IGW 状态已变化</summary>
        AppliedTransient,

        /// <summary>
        /// 永久且由请求内容引起（400 invalid_items / mixed_siemens_address_mode / name_conflict / 其它 400、413）：
        /// 可按 details 下标或根组二分定位到具体根组——台账 Rejected，不自动重试
        /// </summary>
        PermanentRequest,

        /// <summary>
        /// 永久但与根组内容无关（400 channel_driver_mismatch / template_instance_not_supported / invalid_request / invalid_if_match）：
        /// 二分无意义，整台设备停止
        /// </summary>
        PermanentDevice,
    }

    /// <summary>
    /// 批量写入响应分类（纯函数）。依据 IGW 契约：400/413 在写库前（或事务整笔回滚）返回、数据库不变，重发同内容必然同结果 → 永久；
    /// 其余非 200 都是 IGW/存储/网络状态问题 → 瞬态。
    /// </summary>
    public static class IgwBulkFailureClassifier
    {
        private static readonly HashSet<string> DeviceLevelErrors = new(StringComparer.Ordinal)
        {
            "channel_driver_mismatch",
            "template_instance_not_supported",
            "invalid_request",
            "invalid_if_match",
        };

        public static IgwBulkFailureKind Classify(IgwBulkResponse response)
        {
            if (response == null)
            {
                return IgwBulkFailureKind.Transient;
            }
            if (response.HttpStatus == 200)
            {
                return IgwBulkFailureKind.None;
            }
            if (response.HttpStatus == 500 && string.Equals(response.Error, "runtime_apply_failed", StringComparison.Ordinal))
            {
                return IgwBulkFailureKind.AppliedTransient;
            }
            if (response.HttpStatus == 400)
            {
                return DeviceLevelErrors.Contains(response.Error ?? string.Empty)
                    ? IgwBulkFailureKind.PermanentDevice
                    : IgwBulkFailureKind.PermanentRequest;
            }
            if (response.HttpStatus == 413)
            {
                return IgwBulkFailureKind.PermanentRequest;
            }
            return IgwBulkFailureKind.Transient;
        }
    }
}
