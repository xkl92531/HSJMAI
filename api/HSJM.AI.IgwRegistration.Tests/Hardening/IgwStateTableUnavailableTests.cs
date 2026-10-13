using HSJM.AI.AppLayer.PLC.PLC_Upload.Igw;
using HSJM.AI.IDbService;
using Xunit;
using static HSJM.AI.IgwRegistration.Tests.Hardening.HardeningFixture;

namespace HSJM.AI.IgwRegistration.Tests.Hardening;

/// <summary>
/// final fix round：注册状态表不可用（PG 42P01 表不存在且建不出来）时注册不被整体拖垮——
/// 身份变化检测 / 冲突检查 / GRAPH 冷却全部放行（回到加固前行为），结果详情里给出提示；其它数据库错误仍然失败。
/// </summary>
public class IgwStateTableUnavailableTests
{
    private const string Channel = "线体A_PLC1";

    /// <summary>SqlSugar 把 Npgsql 异常包一层，两层 Message 都带 SQLSTATE 前缀（同 IgwSqlErrorClassifier 注释）</summary>
    private static Exception UndefinedTable() =>
        new InvalidOperationException("SqlSugar 执行出错",
            new InvalidOperationException("42P01: relation \"ai_plc_igwregistrationstate\" does not exist"));

    private sealed class FailingStateStore : IIgwProjectStateStore
    {
        public Exception GetStateError { get; set; }
        public Exception ListPeersError { get; set; }
        public Exception SaveIdentityError { get; set; }
        public IgwRegistrationStateRow State { get; set; }
        public List<IgwRegistrationIdentity> SavedIdentities { get; } = new();

        public Task<IgwRegistrationStateRow> GetStateAsync(long projectId) =>
            GetStateError != null ? Task.FromException<IgwRegistrationStateRow>(GetStateError) : Task.FromResult(State);

        public Task SaveIdentityAsync(long projectId, IgwRegistrationIdentity identity)
        {
            if (SaveIdentityError != null)
            {
                return Task.FromException(SaveIdentityError);
            }
            SavedIdentities.Add(identity);
            return Task.CompletedTask;
        }

        public Task SaveGraphFailureAsync(long projectId, DateTime? failedAt, string error) => Task.CompletedTask;

        public Task<IReadOnlyList<IgwPeerProjectRow>> ListPeerProjectsAsync(long projectId) =>
            ListPeersError != null
                ? Task.FromException<IReadOnlyList<IgwPeerProjectRow>>(ListPeersError)
                : Task.FromResult<IReadOnlyList<IgwPeerProjectRow>>(Array.Empty<IgwPeerProjectRow>());
    }

    private static MemoryRegistrationStore PopulatedStore()
    {
        var store = new MemoryRegistrationStore
        {
            ChannelName = Channel,
            Data = new IgwRegistrationData(new[] { "DB1" }, new List<PlannerDbRow> { ClassicRow("DB1", 1, "V", 0) },
                new List<PlannerIoTag>()),
        };
        store.Seed(IgwLedgerRootKind.DbBlock, "DB1", "S7", IgwLedgerStatus.Registered);
        return store;
    }

    private static IgwRegistrationArgs ChangeDb1() => new(new[] { "DB1" }, Array.Empty<string>(), false, false, null);

    [Theory]
    [InlineData("42P01: relation \"x\" does not exist", true)]
    [InlineData("42703: column \"y\" does not exist", false)]
    [InlineData("连接被重置", false)]
    public void Undefined_table_is_classified_from_the_exception_chain(string message, bool expected)
    {
        var ex = new InvalidOperationException("外层", new InvalidOperationException(message));

        Assert.Equal(expected, IgwSqlErrorClassifier.IsUndefinedTable(ex));
        Assert.False(IgwSqlErrorClassifier.IsUndefinedTable(null));
    }

    [Fact]
    public async Task Missing_state_table_on_peer_listing_registers_anyway_with_a_note()
    {
        var igw = new FakeIgwServer();
        var state = new FailingStateStore { ListPeersError = UndefinedTable() };

        var outcome = await Runner(igw, PopulatedStore(), state: state).RunAsync(Project(UniqueUrl()), ChangeDb1(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        Assert.Contains("注册状态表不可用", outcome.Detail);
        Assert.NotEmpty(igw.BulkRequests);
        Assert.Empty(state.SavedIdentities); // 表都没有，登记身份也免了
    }

    [Fact]
    public async Task Missing_state_table_on_state_read_registers_anyway()
    {
        var igw = new FakeIgwServer();
        var state = new FailingStateStore { GetStateError = UndefinedTable() };

        var outcome = await Runner(igw, PopulatedStore(), state: state).RunAsync(Project(UniqueUrl()), ChangeDb1(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        Assert.Contains("注册状态表不可用", outcome.Detail);
        Assert.NotEmpty(igw.BulkRequests);
    }

    [Fact]
    public async Task Missing_state_table_on_identity_save_only_notes_the_failure()
    {
        var igw = new FakeIgwServer();
        var state = new FailingStateStore { SaveIdentityError = UndefinedTable() };

        var outcome = await Runner(igw, PopulatedStore(), state: state).RunAsync(Project(UniqueUrl()), ChangeDb1(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        Assert.Contains("注册状态表不可用", outcome.Detail);
        Assert.NotEmpty(igw.BulkRequests);
    }

    [Fact]
    public async Task Other_state_table_errors_still_fail_the_registration()
    {
        var igw = new FakeIgwServer();
        var state = new FailingStateStore { ListPeersError = new InvalidOperationException("连接被重置") };

        var outcome = await Runner(igw, PopulatedStore(), state: state).RunAsync(Project(UniqueUrl()), ChangeDb1(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Failed, outcome.Status);
        Assert.Contains("连接被重置", outcome.Detail);
        Assert.Empty(igw.BulkRequests);
    }
}
