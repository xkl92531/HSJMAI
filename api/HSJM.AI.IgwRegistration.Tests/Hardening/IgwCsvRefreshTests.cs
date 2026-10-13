using HSJM.AI.AppLayer.PLC.PLC_Upload.Igw;
using Xunit;

namespace HSJM.AI.IgwRegistration.Tests.Hardening;

/// <summary>M-2：只有 IGW 项目（非空、非 2500 老网关）在 CSV 导入后刷新 IO 注册。</summary>
public class IgwCsvRefreshTests
{
    [Theory]
    [InlineData("http://192.168.6.75:48020", true)]
    [InlineData("192.168.6.75:48020", true)]
    [InlineData("http://192.168.6.123:2500", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_igw_projects_refresh_io_registration(string iotUrl, bool expected) =>
        Assert.Equal(expected, PLC_IgwRegistrationJob.IsIgwProject(iotUrl));
}
