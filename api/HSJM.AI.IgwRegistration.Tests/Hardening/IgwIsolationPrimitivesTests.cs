using System.Net;
using System.Net.Http;
using System.Text;
using HSJM.AI.AppLayer.PLC.PLC_Upload.Igw;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HSJM.AI.IgwRegistration.Tests.Hardening;

/// <summary>R-1 基础件：错误下标解析、失败分类、根组单元拆分、Rejected 快照、块根组映射。</summary>
public class IgwIsolationPrimitivesTests
{
    private sealed class OneShot : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public OneShot(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") });
    }

    private static IgwBulkItem Item(string root, string name) =>
        new() { GroupPath = new List<string> { root }, Name = name, Address = root + "." + name, DataType = "bool", ScanRateMs = 100 };

    [Fact]
    public async Task Bulk_client_exposes_item_error_indexes_from_details()
    {
        var client = new IgwBulkClient(NullLogger<IgwBulkClient>.Instance, new OneShot(HttpStatusCode.BadRequest,
            """{"error":"invalid_items","details":[{"index":-1,"reason":"通道"},{"index":2,"reason":"a"},{"index":2,"reason":"b"},{"index":0,"reason":"c"}]}"""),
            HardeningFixture.FastRetryDelays);

        var response = await client.BulkUpsertAsync("http://igw-idx.local:48020", new IgwBulkRequest { Items = { Item("DB1", "A") } }, CancellationToken.None);

        Assert.Equal(new[] { 2, 0 }, response.ItemErrorIndexes);
        Assert.Contains("[2] a", response.Detail);
    }

    [Theory]
    [InlineData(200, null, IgwBulkFailureKind.None)]
    [InlineData(400, "invalid_items", IgwBulkFailureKind.PermanentRequest)]
    [InlineData(400, "mixed_siemens_address_mode", IgwBulkFailureKind.PermanentRequest)]
    [InlineData(400, "name_conflict", IgwBulkFailureKind.PermanentRequest)]
    [InlineData(413, "too_many_items", IgwBulkFailureKind.PermanentRequest)]
    [InlineData(400, "channel_driver_mismatch", IgwBulkFailureKind.PermanentDevice)]
    [InlineData(400, "template_instance_not_supported", IgwBulkFailureKind.PermanentDevice)]
    [InlineData(400, "invalid_request", IgwBulkFailureKind.PermanentDevice)]
    [InlineData(400, "invalid_if_match", IgwBulkFailureKind.PermanentDevice)]
    [InlineData(500, "runtime_apply_failed", IgwBulkFailureKind.AppliedTransient)]
    [InlineData(500, "persistence_failed", IgwBulkFailureKind.Transient)]
    [InlineData(503, "persistence_failed", IgwBulkFailureKind.Transient)]
    [InlineData(409, "revision_conflict", IgwBulkFailureKind.Transient)]
    public void Classifies_bulk_responses(int status, string error, IgwBulkFailureKind expected) =>
        Assert.Equal(expected, IgwBulkFailureClassifier.Classify(new IgwBulkResponse { HttpStatus = status, Error = error }));

    [Fact]
    public void Units_come_from_replace_groups_then_items_case_insensitively()
    {
        var request = new IgwBulkRequest
        {
            ReplaceGroups = { new List<string> { "IO" }, new List<string> { "DB9" } },
            Items = { Item("IO", "A"), Item("db1", "B"), Item("DB1", "C"), new IgwBulkItem { GroupPath = new List<string>(), Name = "Root" } },
        };

        Assert.Equal(new[] { "IO", "DB9", "db1", IgwRequestUnits.DeviceRoot }, IgwRequestUnits.Units(request));
    }

    [Fact]
    public void Subset_keeps_only_requested_units_and_their_replace_groups()
    {
        var channel = new IgwBulkChannel { Name = "通道" };
        var request = new IgwBulkRequest
        {
            Channel = channel,
            Device = new IgwBulkDevice { Name = "S7" },
            ReplaceGroups = { new List<string> { "DB1" }, new List<string> { "DB2" } },
            Items = { Item("DB1", "A"), Item("DB2", "B"), Item("DB1", "C") },
        };

        var subset = IgwRequestUnits.Subset(request, new[] { "db1" });

        Assert.Same(channel, subset.Channel);
        Assert.Equal("S7", subset.Device.Name);
        Assert.Equal(new[] { "DB1" }, Assert.Single(subset.ReplaceGroups));
        Assert.Equal(new[] { "A", "C" }, subset.Items.Select(i => i.Name));
        Assert.Equal(2, request.ReplaceGroups.Count); // 原请求不变
    }

    [Fact]
    public void Units_at_maps_item_indexes_and_rejects_out_of_range()
    {
        var request = new IgwBulkRequest { Items = { Item("DB1", "A"), Item("DB2", "B"), Item("DB1", "C") } };

        Assert.Equal(new[] { "DB1" }, IgwRequestUnits.UnitsAt(request, new[] { 2, 0 }));
        Assert.Equal(new[] { "DB2", "DB1" }, IgwRequestUnits.UnitsAt(request, new[] { 1, 2 }));
        Assert.Null(IgwRequestUnits.UnitsAt(request, new[] { 3 }));
        Assert.Null(IgwRequestUnits.UnitsAt(request, Array.Empty<int>()));
    }

    [Fact]
    public void Snapshot_separates_rejected_from_transiently_failed_roots()
    {
        var snapshot = IgwLedgerSnapshotBuilder.Build(new[]
        {
            new PLC_IgwRegistrationEntity { RootKind = IgwLedgerRootKind.DbBlock, RootName = "DB1", DeviceKind = "S7", Status = IgwLedgerStatus.Failed },
            new PLC_IgwRegistrationEntity { RootKind = IgwLedgerRootKind.DbBlock, RootName = "DB2", DeviceKind = "S7Plus", Status = IgwLedgerStatus.Rejected },
            new PLC_IgwRegistrationEntity { RootKind = IgwLedgerRootKind.Io, RootName = "IO", DeviceKind = "S7", Status = IgwLedgerStatus.Rejected },
        });

        Assert.Equal(new[] { "DB1" }, snapshot.FailedDbBlocks);
        Assert.Equal(new[] { "DB2" }, snapshot.RejectedDbBlocks);
        Assert.False(snapshot.HasFailedIo);
        Assert.True(snapshot.HasRejectedIo);
        Assert.Empty(snapshot.RegisteredRootKinds);
        Assert.Equal(IgwDeviceKind.S7Plus, snapshot.DeviceKindByBlock["DB2"]);
    }

    [Fact]
    public void Plan_exposes_root_group_of_every_block_including_renamed_and_replace_only()
    {
        var plan = IgwRegistrationPlanner.Plan(new PlannerInput("通道", "10.0.0.1", 102,
            new List<PlannerDbRow> { new("DB.A", 1, "V", "Int", 0m, 0m, null), new("DB_A", 2, "V", "Int", 0m, 0m, null) },
            new List<PlannerIoTag>(), Array.Empty<string>(), new[] { "DB_OLD" }, false, true, new IgwRegistrationConfig()));

        Assert.Equal("DB_A", plan.BlockRoots["DB.A"]);
        Assert.Equal("DB_A_2", plan.BlockRoots["db_a"]);
        Assert.Equal("DB_OLD", plan.BlockRoots["DB_OLD"]);
    }

    [Fact]
    public void Isolation_budget_defaults_to_64() => Assert.Equal(64, new IgwRegistrationConfig().MaxIsolationRequests);
}
