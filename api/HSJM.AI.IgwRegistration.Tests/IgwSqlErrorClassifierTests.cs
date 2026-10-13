using System;
using HSJM.AI.IDbService;
using Xunit;

namespace HSJM.AI.IgwRegistration.Tests;

/// <summary>
/// task-5 fix round 1：IgwSqlErrorClassifier.IsUndefinedColumn 纯函数单测
/// （不依赖 Npgsql/数据库，仅构造带目标消息文本的异常链）。
/// </summary>
public class IgwSqlErrorClassifierTests
{
    [Fact]
    public void Detects_42703_in_top_level_message()
    {
        var ex = new InvalidOperationException("42703: column \"igwchannelname\" does not exist");

        Assert.True(IgwSqlErrorClassifier.IsUndefinedColumn(ex));
    }

    [Fact]
    public void Detects_42703_in_inner_exception_message()
    {
        var inner = new Exception("ERROR: 42703: column \"igwchannelname\" of relation \"ai_plc_project_new\" does not exist");
        var outer = new Exception("SqlSugar 执行失败", inner);

        Assert.True(IgwSqlErrorClassifier.IsUndefinedColumn(outer));
    }

    [Fact]
    public void Returns_false_for_unrelated_exception()
    {
        var ex = new InvalidOperationException("timeout expired");

        Assert.False(IgwSqlErrorClassifier.IsUndefinedColumn(ex));
    }

    [Fact]
    public void Returns_false_for_null_exception()
    {
        Assert.False(IgwSqlErrorClassifier.IsUndefinedColumn(null));
    }

    [Fact]
    public void Returns_false_for_other_pg_error_code()
    {
        // 42P01 relation does not exist —— 不应被误判成"列不存在"
        var ex = new Exception("42P01: relation \"ai_plc_igwregistration\" does not exist");

        Assert.False(IgwSqlErrorClassifier.IsUndefinedColumn(ex));
    }
}
