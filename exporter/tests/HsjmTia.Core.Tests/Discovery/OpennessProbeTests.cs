using System.Collections.Generic;
using HsjmTia.Core.Discovery;
using Xunit;

namespace HsjmTia.Core.Tests.Discovery
{
    internal class FakeRegistry : IRegistryReader
    {
        public Dictionary<string, List<string>> SubKeys = new Dictionary<string, List<string>>();
        public Dictionary<string, Dictionary<string, string>> Values =
            new Dictionary<string, Dictionary<string, string>>();

        public IReadOnlyList<string> GetSubKeyNames(string path)
            => SubKeys.TryGetValue(path, out var v) ? v : new List<string>();

        public IReadOnlyList<string> GetValueNames(string path)
            => Values.TryGetValue(path, out var v) ? new List<string>(v.Keys) : new List<string>();

        public string GetValue(string path, string name)
            => Values.TryGetValue(path, out var v) && v.TryGetValue(name, out var s) ? s : null;

        public static FakeRegistry With(params (string tia, string api, string dll)[] installs)
        {
            var r = new FakeRegistry();
            var root = OpennessProbe.RegistryRoot;
            r.SubKeys[root] = new List<string>();
            foreach (var (tia, api, dll) in installs)
            {
                r.SubKeys[root].Add(tia);
                var apiRoot = root + "\\" + tia + "\\PublicAPI";
                r.SubKeys[apiRoot] = new List<string> { api };
                r.Values[apiRoot + "\\" + api] =
                    new Dictionary<string, string> { { "Siemens.Engineering", dll } };
            }
            return r;
        }
    }

    public class OpennessProbeTests
    {
        [Fact]
        public void FindInstallations_ReadsEveryInstalledVersion()
        {
            var reg = FakeRegistry.With(("16.0", "V16", @"C:\v16.dll"), ("19.0", "V19", @"C:\v19.dll"));
            var found = OpennessProbe.FindInstallations(reg);
            Assert.Equal(2, found.Count);
            Assert.Contains(found, i => i.ApiVersion == "V16" && i.AssemblyPath == @"C:\v16.dll");
        }

        [Fact]
        public void FindInstallations_ReturnsEmpty_WhenOpennessIsNotInstalled()
            => Assert.Empty(OpennessProbe.FindInstallations(new FakeRegistry()));

        [Theory]
        [InlineData(@"D:\p\x.ap15", "V15.1")]
        [InlineData(@"D:\p\x.ap16", "V16")]
        [InlineData(@"D:\p\x.AP17", "V17")]
        [InlineData(@"D:\p\x.ap19", "V19")]
        public void RequiredVersionForProject_ComesFromTheFileExtension(string path, string expected)
            => Assert.Equal(expected, OpennessProbe.RequiredVersionForProject(path));

        [Fact]
        public void RequiredVersionForProject_ReturnsNull_ForUnknownExtension()
            => Assert.Null(OpennessProbe.RequiredVersionForProject(@"D:\p\x.zip"));

        [Fact]
        public void Select_PicksTheVersionMatchingTheProjectFile_NotTheNewestInstall()
        {
            var reg = FakeRegistry.With(("16.0", "V16", @"C:\v16.dll"), ("19.0", "V19", @"C:\v19.dll"));
            var chosen = OpennessProbe.Select(OpennessProbe.FindInstallations(reg), @"D:\p\x.ap16", out var err);
            Assert.Null(err);
            Assert.Equal("V16", chosen.ApiVersion);
        }

        [Fact]
        public void Select_RefusesToGuess_WhenTheMatchingVersionIsNotInstalled()
        {
            var reg = FakeRegistry.With(("19.0", "V19", @"C:\v19.dll"));
            var chosen = OpennessProbe.Select(OpennessProbe.FindInstallations(reg), @"D:\p\x.ap16", out var err);
            Assert.Null(chosen);
            Assert.Contains("V16", err);
            Assert.Contains("V19", err);
        }

        [Fact]
        public void Select_ReportsMissingOpenness_WhenNothingIsInstalled()
        {
            var chosen = OpennessProbe.Select(OpennessProbe.FindInstallations(new FakeRegistry()), @"D:\p\x.ap16", out var err);
            Assert.Null(chosen);
            Assert.Contains("未检测到", err);
        }
    }
}
