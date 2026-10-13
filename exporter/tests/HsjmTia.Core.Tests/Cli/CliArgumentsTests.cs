using HsjmTia.Core.Cli;
using Xunit;

namespace HsjmTia.Core.Tests.Cli
{
    public class CliArgumentsTests
    {
        [Fact]
        public void Parse_ReadsProjectAndOutput()
        {
            var a = CliArguments.Parse(new[] { "export", "--project", @"D:\x.ap16", "--out", @"D:\o" });
            Assert.Equal(@"D:\x.ap16", a.ProjectPath);
            Assert.Equal(@"D:\o", a.OutputRoot);
            Assert.False(a.Upload);
        }

        [Fact]
        public void Parse_ReadsRepeatedPlcTargetMappings()
        {
            var a = CliArguments.Parse(new[]
            {
                "export", "--project", @"D:\x.ap16", "--upload",
                "--plc", "PLC_1@192.168.6.31=123",
                "--plc", "PLC_2@192.168.6.32=124"
            });

            Assert.True(a.Upload);
            Assert.Equal(123, a.PlcTargets["192.168.6.31"]);
            Assert.Equal(124, a.PlcTargets["192.168.6.32"]);
        }

        [Fact]
        public void Parse_DefaultsOutputRootToCurrentDirectoryXml()
        {
            var a = CliArguments.Parse(new[] { "export", "--project", @"D:\x.ap16" });
            Assert.EndsWith("xml", a.OutputRoot);
        }

        [Fact]
        public void Parse_RejectsAPlcMappingWithoutAnIp()
            => Assert.Throws<System.FormatException>(() =>
                CliArguments.Parse(new[] { "export", "--project", "p", "--plc", "PLC_1=123" }));

        [Fact]
        public void Parse_RejectsAnUnknownFlag()
            => Assert.Throws<System.FormatException>(() =>
                CliArguments.Parse(new[] { "export", "--nope" }));
    }
}
