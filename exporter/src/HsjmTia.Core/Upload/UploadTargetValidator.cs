using System;
using HsjmTia.Core.Model;

namespace HsjmTia.Core.Upload
{
    public enum UploadVerdict
    {
        Match,
        IpMismatch,
        PlcIpMissing,
        ProjectIpMissing
    }

    public class UploadCheck
    {
        public UploadVerdict Verdict { get; set; }
        public bool RequiresConfirmation { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// 判断"这个 PLC 的包能不能传进这个后端项目"。
    /// 只看 IP，绝不看名字 —— PLC 名常是没改过的 TIA 默认值，跨工程大量重复。
    /// 传错项目会连锁触发 IGW 注册身份变更、全量重注册、旧位置清理。
    /// </summary>
    public static class UploadTargetValidator
    {
        public static UploadCheck Validate(PlcIdentity plc, BackendProject project)
        {
            if (plc == null) throw new ArgumentNullException(nameof(plc));
            if (project == null) throw new ArgumentNullException(nameof(project));

            var plcIp = (plc.Ip ?? string.Empty).Trim();
            var projectIp = (project.PlcIp ?? string.Empty).Trim();

            if (plcIp.Length == 0)
            {
                return new UploadCheck
                {
                    Verdict = UploadVerdict.PlcIpMissing,
                    RequiresConfirmation = true,
                    Message = $"PLC「{plc.Name}」未组态 IP，无法自动校验目标项目「{project.ProjectName}」，请人工核对。"
                };
            }

            if (projectIp.Length == 0)
            {
                return new UploadCheck
                {
                    Verdict = UploadVerdict.ProjectIpMissing,
                    RequiresConfirmation = true,
                    Message = $"目标项目「{project.ProjectName}」未填写 PlcIp，无法自动校验，请人工核对。"
                };
            }

            if (string.Equals(plcIp, projectIp, StringComparison.OrdinalIgnoreCase))
            {
                return new UploadCheck { Verdict = UploadVerdict.Match, RequiresConfirmation = false };
            }

            return new UploadCheck
            {
                Verdict = UploadVerdict.IpMismatch,
                RequiresConfirmation = true,
                Message = $"IP 不一致：PLC「{plc.Name}」是 {plcIp}，目标项目「{project.ProjectName}」是 {projectIp}。" +
                          "传错项目会触发 IGW 全量重注册与旧位置清理，请确认后再继续。"
            };
        }
    }
}
