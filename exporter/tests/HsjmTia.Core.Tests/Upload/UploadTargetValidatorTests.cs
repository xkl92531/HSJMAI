using HsjmTia.Core.Model;
using HsjmTia.Core.Upload;
using Xunit;

namespace HsjmTia.Core.Tests.Upload
{
    public class UploadTargetValidatorTests
    {
        private static PlcIdentity Plc(string ip) => new PlcIdentity { Name = "PLC_1", Ip = ip, DeviceIndex = 1 };
        private static BackendProject Proj(string ip)
            => new BackendProject { Id = 1, ProjectName = "泰兴PLC1", PlcIp = ip, PlcCode = "PLC1" };

        [Fact]
        public void Validate_PassesWithoutConfirmation_WhenIpsMatch()
        {
            var check = UploadTargetValidator.Validate(Plc("192.168.6.31"), Proj("192.168.6.31"));
            Assert.Equal(UploadVerdict.Match, check.Verdict);
            Assert.False(check.RequiresConfirmation);
        }

        [Fact]
        public void Validate_BlocksOnIpMismatch_BecauseWrongTargetTriggersIgwReregistration()
        {
            var check = UploadTargetValidator.Validate(Plc("192.168.6.31"), Proj("192.168.6.99"));
            Assert.Equal(UploadVerdict.IpMismatch, check.Verdict);
            Assert.True(check.RequiresConfirmation);
            Assert.Contains("192.168.6.31", check.Message);
            Assert.Contains("192.168.6.99", check.Message);
        }

        [Fact]
        public void Validate_RequiresConfirmation_WhenThePlcHasNoConfiguredIp()
        {
            var check = UploadTargetValidator.Validate(Plc(null), Proj("192.168.6.31"));
            Assert.Equal(UploadVerdict.PlcIpMissing, check.Verdict);
            Assert.True(check.RequiresConfirmation);
        }

        [Fact]
        public void Validate_RequiresConfirmation_WhenTheBackendProjectHasNoIp()
        {
            var check = UploadTargetValidator.Validate(Plc("192.168.6.31"), Proj(""));
            Assert.Equal(UploadVerdict.ProjectIpMissing, check.Verdict);
            Assert.True(check.RequiresConfirmation);
        }

        [Fact]
        public void Validate_IgnoresSurroundingWhitespaceInIps()
            => Assert.Equal(UploadVerdict.Match,
                UploadTargetValidator.Validate(Plc(" 192.168.6.31 "), Proj("192.168.6.31")).Verdict);

        [Fact]
        public void Validate_NeverComparesNames_BecauseDefaultPlcNamesCollideAcrossProjects()
        {
            // 名字完全不同但 IP 一致 —— 必须通过
            var plc = new PlcIdentity { Name = "PLC_1", Ip = "192.168.6.31" };
            var proj = new BackendProject { Id = 1, ProjectName = "完全不同的名字", PlcIp = "192.168.6.31", PlcCode = "XYZ" };
            Assert.Equal(UploadVerdict.Match, UploadTargetValidator.Validate(plc, proj).Verdict);
        }
    }
}
