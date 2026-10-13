using System;
using HsjmTia.Core.Model;
using HsjmTia.Core.Naming;
using Xunit;

namespace HsjmTia.Core.Tests.Naming
{
    public class PlcNamerTests
    {
        private static PlcIdentity Plc(string name, string ip = null, int index = 1)
            => new PlcIdentity { Name = name, Ip = ip, DeviceIndex = index, OrderNumber = "6ES7" };

        [Fact]
        public void BuildFolderName_AppendsIp_SoDefaultNamesStayDistinguishable()
            => Assert.Equal("PLC_1_192.168.6.31", PlcNamer.BuildFolderName(Plc("PLC_1", "192.168.6.31")));

        [Fact]
        public void BuildFolderName_AppendsIp_EvenWhenNameIsMeaningful()
            => Assert.Equal("OP30_Main_192.168.6.31", PlcNamer.BuildFolderName(Plc("OP30_Main", "192.168.6.31")));

        [Fact]
        public void BuildFolderName_FallsBackToDeviceIndex_WhenIpIsMissing()
            => Assert.Equal("PLC_1_#2", PlcNamer.BuildFolderName(Plc("PLC_1", null, 2)));

        [Fact]
        public void BuildFolderName_SanitizesBothNameAndIp()
            => Assert.Equal("A_B_#1", PlcNamer.BuildFolderName(Plc("A/B", "", 1)));

        [Fact]
        public void BuildPackageFileName_CombinesProjectPlcAndTimestamp()
        {
            var stamp = new DateTime(2026, 9, 20, 10, 30, 0);
            Assert.Equal(
                "泰兴产线_PLC_1_192.168.6.31_20260920103000.zip",
                PlcNamer.BuildPackageFileName("泰兴产线", Plc("PLC_1", "192.168.6.31"), stamp));
        }

        [Fact]
        public void BuildPackageFileName_SanitizesProjectName()
        {
            var stamp = new DateTime(2026, 9, 20, 10, 30, 0);
            Assert.StartsWith("A_B_", PlcNamer.BuildPackageFileName("A:B", Plc("P", "1.1.1.1"), stamp));
        }
    }
}
