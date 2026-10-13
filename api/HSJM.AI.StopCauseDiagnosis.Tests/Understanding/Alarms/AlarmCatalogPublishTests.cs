#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding.Alarms;

public class AlarmCatalogPublishTests
{
    [Fact]
    public async Task GuardIsGenericAndContainsFailures()
    {
        var value = await UnderstandingPublishGuard.RunContainedAsync<int>(() => throw new InvalidOperationException(), 7, _ => { }, CancellationToken.None);
        Assert.Equal(7, value);
    }

    [Fact]
    public async Task GuardWithDeadlineIsGenericAndFallsBackOnTimeout()
    {
        Exception? logged = null;
        var value = await UnderstandingPublishGuard.RunContainedAsync<string>(async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return "late";
        }, "fallback", ex => logged = ex, CancellationToken.None, TimeSpan.FromMilliseconds(50));
        Assert.Equal("fallback", value);
        Assert.IsType<TimeoutException>(logged);
    }

    [Fact]
    public void CatalogIsKeptWhenTheUnderstandingStepFailed()
    {
        Assert.False(UnderstandingPublishStep.ShouldReplaceCatalog(null));
        var empty = new UnderstandingReport(new Dictionary<string, string>(), new Dictionary<string, string>(), [],
            new Dictionary<string, int>(), 0, 0);
        Assert.True(UnderstandingPublishStep.ShouldReplaceCatalog(empty));   // 理解步骤成功但没有报警：清单应被清空
    }

    static string UniqueConnection() => $"Host=test-{Guid.NewGuid():N};Database=x";

    [Fact]
    public async Task SchemaDdlRunsOncePerConnectionString()
    {
        var connection = UniqueConnection();
        var runs = 0;
        Task Run(CancellationToken _) { runs++; return Task.CompletedTask; }
        await AlarmCatalogStore.EnsureSchemaAsync(connection, Run, CancellationToken.None);
        await AlarmCatalogStore.EnsureSchemaAsync(connection, Run, CancellationToken.None);
        Assert.Equal(1, runs);
        await AlarmCatalogStore.EnsureSchemaAsync(UniqueConnection(), Run, CancellationToken.None);
        Assert.Equal(2, runs);   // 另一个连接串（另一个库）要再建一次
    }

    [Fact]
    public async Task AFailedSchemaDdlIsNotRememberedAsDone()
    {
        var connection = UniqueConnection();
        var runs = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => AlarmCatalogStore.EnsureSchemaAsync(connection,
            _ => { runs++; throw new InvalidOperationException("lock timeout"); }, CancellationToken.None));
        await AlarmCatalogStore.EnsureSchemaAsync(connection, _ => { runs++; return Task.CompletedTask; }, CancellationToken.None);
        await AlarmCatalogStore.EnsureSchemaAsync(connection, _ => { runs++; return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(2, runs);
    }
}
