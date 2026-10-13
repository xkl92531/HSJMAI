using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Fake = HSJM.AI.IgwRegistration.Tests.IgwReadChainRoutingTests.Fake;
using Xunit;

namespace HSJM.AI.IgwRegistration.Tests.DiagnosisRead;

public class Original353ManualModeReaderTests
{
    [Fact]
    public async Task GoodModeBitsConfirmManualButBadQualityDoesNot()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["IndependentPlcPipeline:Enabled"] = "true",
            ["IndependentPlcPipeline:ExpectedDatabase"] = "hsjm_ai_new_test",
            ["IndependentPlcPipeline:ConnectionString"] = "Host=127.0.0.1;Database=hsjm_ai_new_test;Username=test;Password=test",
            ["TestConstraintV2:ManualModeReadEnabled"] = "true"
        }).Build();
        var project = Fake.Create<IPLC_ProjectService>((method, _) =>
            method.Name == "GetById" ? new PLC_ProjectEntity {
                Id = 1, PlcIp = "10.11.38.10", IotUrl = "http://igw", IsDeleted = false
            } : throw new NotSupportedException(method.Name));
        var ledger = IgwLedgerSnapshotBuilder.Build([
            new PLC_IgwRegistrationEntity { ProjectId = 1, RootKind = "DbBlock",
                RootName = "DB_Mode", RootGroup = "DB_Mode", DeviceKind = "S7Plus",
                Status = "Registered", IsDeleted = false }
        ]);
        var registrations = Fake.Create<IPLC_IgwRegistrationService>((method, _) =>
            method.Name == nameof(IPLC_IgwRegistrationService.GetLedgerForReadAsync)
                ? Task.FromResult(ledger) : throw new NotSupportedException(method.Name));
        var qualityGood = true;
        var iot = Fake.Create<IIotService>((method, args) => {
            if (method.Name != nameof(IIotService.ReadTagsAsList)) throw new NotSupportedException(method.Name);
            var aliases = (List<string>)args[1];
            return Task.FromResult(new HttpResponseResultModel<List<PLC_ReadTagResult>> {
                IsSuccess = true,
                BackResult = aliases.Select(alias => new PLC_ReadTagResult {
                    TagName = alias, IsGood = qualityGood,
                    Value = alias.EndsWith("ManualMod", StringComparison.Ordinal) ? true : false
                }).ToList()
            });
        });
        var reader = new Original353ManualModeReader(config, new PostgresPipelineStore(config),
            project, registrations, iot, NullLogger<Original353ManualModeReader>.Instance);
        var bindings = new[] { new Original353ManualModeBinding("DB_Mode", "General.ManualMod") };

        Assert.Equal("Manual", (await reader.ReadAsync(1, bindings, CancellationToken.None)).Status);
        qualityGood = false;
        Assert.Equal("Unknown", (await reader.ReadAsync(1, bindings, CancellationToken.None)).Status);
    }
}
