using System.Data;
using System.Reflection;
using HSJM.AI.DbService;
using SqlSugar;
using Xunit;

namespace HSJM.AI.IgwRegistration.Tests.Hardening;

/// <summary>SavePendingAsync 自管事务：拒绝在外层事务内调用；回滚失败不掩盖原始异常（假 ISqlSugarClient / IAdo，不连库）。</summary>
public class IgwRegistrationStateServiceTransactionTests
{
    /// <summary>DispatchProxy 假实现（允许返回 null：void 方法 / 空事务）</summary>
    public class NullableFake : DispatchProxy
    {
        public Func<MethodInfo, object[], object> Handler;

        protected override object Invoke(MethodInfo targetMethod, object[] args) => Handler(targetMethod, args);

        public static T Create<T>(Func<MethodInfo, object[], object> handler)
        {
            var proxy = DispatchProxy.Create<T, NullableFake>();
            ((NullableFake)(object)proxy).Handler = handler;
            return proxy;
        }
    }

    private sealed class FakeAdo
    {
        public IDbTransaction Transaction;
        public readonly List<string> Calls = new();
        public Exception WriteFailure;
        public Exception RollbackFailure;

        public PLC_IgwRegistrationStateService Service()
        {
            var ado = NullableFake.Create<IAdo>((m, a) =>
            {
                lock (Calls)
                {
                    Calls.Add(m.Name);
                }
                switch (m.Name)
                {
                    case "get_Transaction":
                        return Transaction;
                    case nameof(IAdo.BeginTran):
                        return null;
                    case nameof(IAdo.RollbackTran):
                        return RollbackFailure != null ? throw RollbackFailure : null;
                    case nameof(IAdo.CommitTran):
                        return null;
                    case nameof(IAdo.ExecuteCommandAsync):
                        // 建表 DDL 放行；业务写入按需失败
                        var sql = (string)a[0];
                        return sql.Contains("CREATE", StringComparison.OrdinalIgnoreCase) || WriteFailure == null
                            ? Task.FromResult(0)
                            : Task.FromException<int>(WriteFailure);
                    default:
                        throw new NotSupportedException(m.Name);
                }
            });
            var db = NullableFake.Create<ISqlSugarClient>((m, _) => m.Name == "get_Ado" ? ado : throw new NotSupportedException(m.Name));
            return new PLC_IgwRegistrationStateService(db);
        }
    }

    private sealed class DummyTransaction : IDbTransaction
    {
        public IDbConnection Connection => null;
        public IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        public void Commit() { }
        public void Rollback() { }
        public void Dispose() { }
    }

    [Fact]
    public async Task Refuses_to_run_inside_an_outer_transaction()
    {
        var ado = new FakeAdo { Transaction = new DummyTransaction() };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ado.Service().SavePendingAsync(1, "{}", "local_a", (a, _) => a));

        Assert.Contains("外层事务", ex.Message);
        Assert.DoesNotContain(nameof(IAdo.BeginTran), ado.Calls);
        Assert.DoesNotContain(nameof(IAdo.ExecuteCommandAsync), ado.Calls);
    }

    [Fact]
    public async Task Rollback_failure_does_not_mask_the_original_exception()
    {
        var original = new InvalidOperationException("写入失败");
        var ado = new FakeAdo { WriteFailure = original, RollbackFailure = new InvalidOperationException("回滚失败") };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ado.Service().SavePendingAsync(1, "{}", "local_a", (a, _) => a));

        Assert.Same(original, ex);
        Assert.Contains(nameof(IAdo.RollbackTran), ado.Calls);
        Assert.DoesNotContain(nameof(IAdo.CommitTran), ado.Calls);
    }
}
