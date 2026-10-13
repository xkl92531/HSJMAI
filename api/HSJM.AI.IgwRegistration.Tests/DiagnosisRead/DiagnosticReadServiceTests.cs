using System.Net;
using HSJM.AI.Core;
using HSJM.AI.DbService;
using HSJM.AI.IDbService;
using HSJM.AI.Utility;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Fake = HSJM.AI.IgwRegistration.Tests.IgwReadChainRoutingTests.Fake;

namespace HSJM.AI.IgwRegistration.Tests.DiagnosisRead;

/// <summary>D-1 Task 3：PLC_VariableAccessService.ReadDiagnosticSignalsAsync。项目 Id 与 IotUrl 端口各用例唯一（静态缓存隔离）。</summary>
public class DiagnosticReadServiceTests
{
    private const string Ip = "10.0.0.1";

    static DiagnosticReadServiceTests()
    {
        // AbstractBaseService 基类构造从 CommonServiceProvider 取 ISqlSugarClient
        CommonServiceProvider.ServiceProvider ??= new ServiceCollection().BuildServiceProvider();
    }

    private sealed class Harness
    {
        public readonly List<List<string>> QualityBatches = new();
        public readonly List<(string Device, List<string> Tags)> LegacyCalls = new();
        public Func<string, DcsTagQualityItem> QualityRow =
            alias => new DcsTagQualityItem { Key = alias, IsGood = true, Value = true, Status = "Good" };
        public Exception QualityException;
        public Func<string, List<string>, HttpResponseResultModel<List<PLC_ReadTagResult>>> Legacy =
            (_, tags) => Ok(tags.Select(_ => new PLC_ReadTagResult { IsGood = true, Value = true }).ToArray());
        public IgwLedgerSnapshot Ledger = IgwLedgerSnapshot.Empty;
        /// <summary>Fix round 1：非 null 时台账读取抛出该异常（模拟 GetLedgerForReadAsync 失败，走空台账回退）。</summary>
        public Exception LedgerException;
        /// <summary>Fix round 2：非 null 时项目全量 IO 点加载抛出该异常（模拟台账不可用时的重名兜底校验本身也失败）。</summary>
        public Exception FullIoTagLoadException;
        public readonly PLC_VariableAccessService Service;

        /// <param name="tags">诊断子集（_diagnosticTagLoader 返回值，走 PlcDataTypeList/ConstraintStatus 过滤后的口径）。</param>
        /// <param name="fullIoTags">
        /// Fix round 2：项目全量 IO 点（_fullIoTagLoader 返回值，注册规划器判定重名用的口径，不受诊断过滤影响）。
        /// 缺省时与 <paramref name="tags"/> 相同——多数用例两个集合并无差异，只有专门验证"诊断点与非诊断点重名"
        /// 的用例才需要传一个比 <paramref name="tags"/> 更大的集合。
        /// </param>
        public Harness(PLC_ProjectEntity project, IReadOnlyList<PLC_IOTagEntity> tags, IReadOnlyList<PLC_IOTagEntity> fullIoTags = null)
        {
            var projectService = Fake.Create<IPLC_ProjectService>((m, a) =>
                m.Name == nameof(IPLC_ProjectService.GetById) && project != null && a[0] is long id && id == project.Id
                    ? project
                    : null);
            var iot = Fake.Create<IIotService>((m, a) =>
            {
                switch (m.Name)
                {
                    case nameof(IIotService.ReadTagsWithQualityAsync):
                        var aliases = ((List<string>)a[1]).ToList();
                        QualityBatches.Add(aliases);
                        if (QualityException != null)
                        {
                            return Task.FromException<IgwTransportResult<List<DcsTagQualityItem>>>(QualityException);
                        }
                        return Task.FromResult(new IgwTransportResult<List<DcsTagQualityItem>>
                        {
                            HttpStatusCode = HttpStatusCode.OK,
                            IsHttpSuccess = true,
                            Envelope = new HttpResponseResultModel<List<DcsTagQualityItem>>
                            {
                                IsSuccess = true,
                                BackResult = aliases.Select(QualityRow).ToList(),
                            },
                        });
                    case nameof(IIotService.ReadTags):
                        var parame = (PLC_ReadTagParame)a[1];
                        LegacyCalls.Add((parame.DeviceName, parame.TagList.ToList()));
                        return Task.FromResult(Legacy(parame.DeviceName, parame.TagList));
                    default:
                        throw new NotSupportedException("IIotService." + m.Name);
                }
            });
            var ledger = Fake.Create<IPLC_IgwRegistrationService>((m, a) =>
                m.Name == nameof(IPLC_IgwRegistrationService.GetLedgerForReadAsync)
                    ? LedgerException != null
                        ? Task.FromException<IgwLedgerSnapshot>(LedgerException)
                        : Task.FromResult(Ledger)
                    : throw new InvalidOperationException("unexpected call " + m.Name));
            var resolvedFullIoTags = fullIoTags ?? tags;
            Service = new PLC_VariableAccessService(projectService, iot,
                (_, _, _) => Task.FromResult<IReadOnlyList<PLC_IOTagEntity>>(Array.Empty<PLC_IOTagEntity>()),
                ledger,
                (_, _) => Task.FromResult(tags),
                igwDiagnosticReader: null,
                fullIoTagLoader: (_, _) => FullIoTagLoadException != null
                    ? Task.FromException<IReadOnlyList<PLC_IOTagEntity>>(FullIoTagLoadException)
                    : Task.FromResult(resolvedFullIoTags));
        }
    }

    private static HttpResponseResultModel<List<PLC_ReadTagResult>> Ok(params PLC_ReadTagResult[] rows) =>
        new() { IsSuccess = true, BackResult = rows.ToList() };

    /// <summary>
    /// Fix round 2：每次调用分配唯一自增 Id（真实 DB 行的 Id 必然互不相同）。生产代码
    /// （FindCollidingIoTagGroups 的调用方）按 Id 判断"冲突分组里除自己外的其它点"，测试数据若都
    /// 用默认 Id=0 会让这条排除逻辑失真——所有点互相"看起来是同一个点"。
    /// </summary>
    private static long _nextIoTagId = 1;

    private static PLC_IOTagEntity IoTag(long pid, string table, string tag) =>
        new() { Id = _nextIoTagId++, ProjectId = pid, TableName = table, TagName = tag, DataType = "Bool", IsDeleted = false };

    private static PLC_ProjectEntity IgwProject(long pid, int port, string plcIp = Ip) =>
        new() { Id = pid, StationId = "PLC1", IotUrl = "http://127.0.0.1:" + port, PlcIp = plcIp, IsDeleted = false };

    private static PLC_ProjectEntity LegacyProject(long pid) =>
        new() { Id = pid, StationId = "PLC1", IotUrl = "http://192.168.6.75:2500", PlcIp = Ip, IsDeleted = false };

    private static PLC_ProjectEntity LegacyProjectNoStation(long pid) =>
        new() { Id = pid, StationId = null, IotUrl = "http://192.168.6.75:2500", PlcIp = Ip, IsDeleted = false };

    private static IgwLedgerSnapshot LedgerWithIoRename() =>
        new(
            new Dictionary<string, IgwDeviceKind>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<(string, string), string>(),
            new Dictionary<(string, string), string> { [("默认变量表", "I1.0_X")] = "I1_0_X_2" },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>Fix round 1：模拟注册规划器对同表重名点（Flag/flag）的去重结果——第二个出现者被加后缀改名。</summary>
    private static IgwLedgerSnapshot LedgerWithFlagRename() =>
        new(
            new Dictionary<string, IgwDeviceKind>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<(string, string), string>(),
            new Dictionary<(string, string), string> { [("T", "flag")] = "flag_2" },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    [Fact]
    public async Task Igw_project_reads_io_points_by_ledger_alias_with_per_point_quality()
    {
        const long pid = 882001;
        var tags = new List<PLC_IOTagEntity>
        {
            IoTag(pid, "默认变量表", "I1.0_X"), IoTag(pid, "默认变量表", "OP010_B"), IoTag(pid, "IQ", "I106.2_SG"),
        };
        var badAlias = IgwReadAliasResolver.ResolveIo(Ip, IgwLedgerSnapshot.Empty, "默认变量表", "OP010_B");
        var stamp = new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);
        var h = new Harness(IgwProject(pid, 48201), tags)
        {
            Ledger = LedgerWithIoRename(),
            QualityRow = alias => alias == badAlias
                ? new DcsTagQualityItem { Key = alias, IsGood = false, StatusCode = 0x80340000u, Status = "BadNodeIdUnknown", Reason = "SymbolNotFound" }
                : new DcsTagQualityItem { Key = alias, IsGood = true, Value = true, Status = "Good", TimestampUtc = stamp },
        };

        var result = await h.Service.ReadDiagnosticSignalsAsync(pid, CancellationToken.None);

        Assert.Equal(PLC_DiagnosticProjectStatus.Ok, result.ProjectStatus);
        var aliases = Assert.Single(h.QualityBatches);
        Assert.Contains("通用.S7_10.0.0.1.IO.默认变量表.I1_0_X_2", aliases);
        Assert.Contains(badAlias, aliases);
        Assert.Contains(IgwReadAliasResolver.ResolveIo(Ip, IgwLedgerSnapshot.Empty, "IQ", "I106.2_SG"), aliases);
        Assert.Empty(h.LegacyCalls);

        var byKey = result.Signals.ToDictionary(s => s.TagName);
        var good = byKey["默认变量表.I1.0_X"];
        Assert.Equal(PLC_SignalReadStatus.Good, good.Status);
        Assert.Equal(true, good.Value);
        Assert.Equal("I1.0_X", good.OldTagName);
        Assert.Equal(stamp, good.TimestampUtc);
        var bad = byKey["默认变量表.OP010_B"];
        Assert.Equal(PLC_SignalReadStatus.SymbolNotFound, bad.Status);
        Assert.Null(bad.Value);
        Assert.Contains("BadNodeIdUnknown", bad.Detail);
        Assert.Contains(badAlias, bad.Detail);
        Assert.Equal(PLC_SignalReadStatus.Good, byKey["I106.2_SG"].Status); // IQ 表：键去掉 "IQ."，与 GetReadData 同口径
        Assert.Equal(2, result.GoodCount);
    }

    [Fact]
    public async Task Ledger_unavailable_marks_colliding_io_points_as_device_error_and_still_reads_others()
    {
        const long pid = 882004;
        var tags = new List<PLC_IOTagEntity>
        {
            IoTag(pid, "T", "Flag"), IoTag(pid, "T", "flag"), IoTag(pid, "T", "C"),
        };
        var h = new Harness(IgwProject(pid, 48204), tags)
        {
            LedgerException = new InvalidOperationException("ledger db down"),
        };

        var result = await h.Service.ReadDiagnosticSignalsAsync(pid, CancellationToken.None);

        Assert.Equal(PLC_DiagnosticProjectStatus.Ok, result.ProjectStatus);
        var aliasFlag = IgwReadAliasResolver.ResolveIo(Ip, IgwLedgerSnapshot.Empty, "T", "Flag");
        var aliasflag = IgwReadAliasResolver.ResolveIo(Ip, IgwLedgerSnapshot.Empty, "T", "flag");
        var aliasC = IgwReadAliasResolver.ResolveIo(Ip, IgwLedgerSnapshot.Empty, "T", "C");
        var sentAliases = Assert.Single(h.QualityBatches);
        Assert.DoesNotContain(aliasFlag, sentAliases); // 冲突点直接拒读，不发给 IGW
        Assert.DoesNotContain(aliasflag, sentAliases);
        Assert.Contains(aliasC, sentAliases);

        var byKey = result.Signals.ToDictionary(s => s.TagName);
        Assert.Equal(PLC_SignalReadStatus.DeviceError, byKey["T.Flag"].Status);
        Assert.Null(byKey["T.Flag"].Value);
        Assert.Contains("注册台账暂不可用，重名点位无法确定读取名", byKey["T.Flag"].Detail);
        Assert.Contains("T.flag", byKey["T.Flag"].Detail); // 冲突详情带上对方点名，方便运维核对
        Assert.Equal(PLC_SignalReadStatus.DeviceError, byKey["T.flag"].Status);
        Assert.Null(byKey["T.flag"].Value);
        Assert.Contains("注册台账暂不可用，重名点位无法确定读取名", byKey["T.flag"].Detail);
        Assert.Contains("T.Flag", byKey["T.flag"].Detail);
        Assert.Equal(PLC_SignalReadStatus.Good, byKey["T.C"].Status); // 不冲突的点照常读
    }

    /// <summary>Fix round 2：清洗规则把 "." 替换为 "_"，同表 "I1.0_X" 与 "I1_0_X" 清洗后同名，判定为重名。</summary>
    [Fact]
    public async Task Ledger_unavailable_dot_underscore_sanitized_collision_is_detected()
    {
        const long pid = 882006;
        var tags = new List<PLC_IOTagEntity> { IoTag(pid, "T", "I1.0_X"), IoTag(pid, "T", "I1_0_X") };
        var h = new Harness(IgwProject(pid, 48206), tags)
        {
            LedgerException = new InvalidOperationException("ledger db down"),
        };

        var result = await h.Service.ReadDiagnosticSignalsAsync(pid, CancellationToken.None);

        Assert.Empty(h.QualityBatches); // 两点互相冲突，待读别名集合为空，IGW 请求都不会发出
        var byKey = result.Signals.ToDictionary(s => s.TagName);
        Assert.Equal(PLC_SignalReadStatus.DeviceError, byKey["T.I1.0_X"].Status);
        Assert.Contains("T.I1_0_X", byKey["T.I1.0_X"].Detail);
        Assert.Equal(PLC_SignalReadStatus.DeviceError, byKey["T.I1_0_X"].Status);
        Assert.Contains("T.I1.0_X", byKey["T.I1_0_X"].Detail);
    }

    /// <summary>
    /// Fix round 2（复现审阅意见）：注册规划器在项目全量 IO 点上判定重名，不受诊断口径
    /// （PlcDataTypeList/ConstraintStatus 过滤）影响。诊断子集只含 I1.0_X（如 Word 类型点），
    /// 但项目全量集合里还有一个未参与诊断的同表点 I1_0_X（如非 PlcDataTypeList 类型或
    /// ConstraintStatus=6 被诊断过滤掉的 Bool 点）——两者清洗后同名。旧实现只在诊断子集里找重名，
    /// 找不到"隐形"的 I1_0_X，会把 I1.0_X 当成不冲突，读到的值可能其实是 I1_0_X 的（张冠李戴）。
    /// </summary>
    [Fact]
    public async Task Ledger_unavailable_diagnostic_point_colliding_with_non_diagnostic_sibling_is_device_error()
    {
        const long pid = 882007;
        // 同一行在两次查询里必然是同一个 Id；用同一个对象代表它，避免测试数据人为制造出第三个"幽灵"点。
        var i1_0X = IoTag(pid, "T", "I1.0_X");
        var i1_0XUnderscore = IoTag(pid, "T", "I1_0_X");
        var diagnosticTags = new List<PLC_IOTagEntity> { i1_0X };
        var fullIoTags = new List<PLC_IOTagEntity> { i1_0X, i1_0XUnderscore };
        var h = new Harness(IgwProject(pid, 48207), diagnosticTags, fullIoTags)
        {
            LedgerException = new InvalidOperationException("ledger db down"),
        };

        var result = await h.Service.ReadDiagnosticSignalsAsync(pid, CancellationToken.None);

        Assert.Empty(h.QualityBatches); // 唯一的诊断点因与全量集合里的隐形同名点冲突而拒读，待读集合为空
        var signal = Assert.Single(result.Signals);
        Assert.Equal("T.I1.0_X", signal.TagName);
        Assert.Equal(PLC_SignalReadStatus.DeviceError, signal.Status);
        Assert.Null(signal.Value);
        Assert.Contains("注册台账暂不可用，重名点位无法确定读取名", signal.Detail);
        Assert.Contains("T.I1_0_X", signal.Detail); // 冲突方是诊断子集之外的点，也要点名
    }

    /// <summary>Fix round 2：台账不可用时的重名兜底校验（加载项目全量 IO 点）本身失败，不猜——全部拒读。</summary>
    [Fact]
    public async Task Ledger_unavailable_and_full_io_tag_load_fails_marks_all_points_device_error()
    {
        const long pid = 882008;
        var tags = new List<PLC_IOTagEntity> { IoTag(pid, "T", "A"), IoTag(pid, "T", "B") };
        var h = new Harness(IgwProject(pid, 48208), tags)
        {
            LedgerException = new InvalidOperationException("ledger db down"),
            FullIoTagLoadException = new InvalidOperationException("io tag db down"),
        };

        var result = await h.Service.ReadDiagnosticSignalsAsync(pid, CancellationToken.None);

        Assert.Empty(h.QualityBatches); // 一个 IGW 请求都没发——无法确认是否重名，不猜
        Assert.Equal(PLC_DiagnosticProjectStatus.Ok, result.ProjectStatus); // 至少有点位报出来，不是"整体不可用"
        Assert.All(result.Signals, s =>
        {
            Assert.Equal(PLC_SignalReadStatus.DeviceError, s.Status);
            Assert.Null(s.Value);
            Assert.Contains("io tag db down", s.Detail);
        });
    }

    [Fact]
    public async Task Ledger_available_resolves_collisions_via_renames_as_before()
    {
        const long pid = 882005;
        var tags = new List<PLC_IOTagEntity> { IoTag(pid, "T", "Flag"), IoTag(pid, "T", "flag") };
        var h = new Harness(IgwProject(pid, 48205), tags)
        {
            Ledger = LedgerWithFlagRename(),
        };

        var result = await h.Service.ReadDiagnosticSignalsAsync(pid, CancellationToken.None);

        Assert.Equal(PLC_DiagnosticProjectStatus.Ok, result.ProjectStatus);
        var aliasFlag = IgwReadAliasResolver.ResolveIo(Ip, IgwLedgerSnapshot.Empty, "T", "Flag");
        var aliasFlagRenamed = IgwReadAliasResolver.ResolveIo(Ip, LedgerWithFlagRename(), "T", "flag");
        var sentAliases = Assert.Single(h.QualityBatches);
        Assert.Contains(aliasFlag, sentAliases);
        Assert.Contains(aliasFlagRenamed, sentAliases);
        Assert.NotEqual(aliasFlag, aliasFlagRenamed); // 台账改名映射生效后两点别名不再相同

        var byKey = result.Signals.ToDictionary(s => s.TagName);
        Assert.Equal(PLC_SignalReadStatus.Good, byKey["T.Flag"].Status);
        Assert.Equal(PLC_SignalReadStatus.Good, byKey["T.flag"].Status);
    }

    /// <summary>
    /// D-1 最终评审 I-2：台账可用但已过期——新增的 FLAG 点没有改名映射，与 Flag 解析到同一 IGW 别名
    /// （忽略大小写）。两点都不读，标 DeviceError 提示重新注册；有改名映射的 flag 照常读。
    /// </summary>
    [Fact]
    public async Task Stale_ledger_io_points_resolving_to_same_alias_are_device_error_not_read()
    {
        const long pid = 882009;
        var tags = new List<PLC_IOTagEntity> { IoTag(pid, "T", "Flag"), IoTag(pid, "T", "flag"), IoTag(pid, "T", "FLAG") };
        var h = new Harness(IgwProject(pid, 48209), tags)
        {
            Ledger = LedgerWithFlagRename(),
        };

        var result = await h.Service.ReadDiagnosticSignalsAsync(pid, CancellationToken.None);

        var aliasFlag = IgwReadAliasResolver.ResolveIo(Ip, LedgerWithFlagRename(), "T", "Flag");
        var aliasFLAG = IgwReadAliasResolver.ResolveIo(Ip, LedgerWithFlagRename(), "T", "FLAG");
        var aliasFlagRenamed = IgwReadAliasResolver.ResolveIo(Ip, LedgerWithFlagRename(), "T", "flag");
        Assert.Equal(aliasFlag, aliasFLAG, StringComparer.OrdinalIgnoreCase);
        var sentAliases = Assert.Single(h.QualityBatches);
        Assert.DoesNotContain(sentAliases, a => string.Equals(a, aliasFlag, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(aliasFlagRenamed, sentAliases);

        var byKey = result.Signals.ToDictionary(s => s.TagName);
        foreach (var key in new[] { "T.Flag", "T.FLAG" })
        {
            Assert.Equal(PLC_SignalReadStatus.DeviceError, byKey[key].Status);
            Assert.Null(byKey[key].Value);
            Assert.Equal("多个 IO 点解析到同一 IGW 别名（台账可能过期），请重新注册PLC标签", byKey[key].Detail);
        }
        Assert.Equal(PLC_SignalReadStatus.Good, byKey["T.flag"].Status);
    }

    [Fact]
    public async Task Igw_project_without_plc_ip_is_unavailable_and_points_not_registered()
    {
        const long pid = 882002;
        var h = new Harness(IgwProject(pid, 48202, plcIp: " "), new List<PLC_IOTagEntity> { IoTag(pid, "T", "A") });

        var result = await h.Service.ReadDiagnosticSignalsAsync(pid, CancellationToken.None);

        Assert.Equal(PLC_DiagnosticProjectStatus.ProjectUnavailable, result.ProjectStatus);
        Assert.Contains("PLC IP", result.ProjectError);
        Assert.Equal(PLC_SignalReadStatus.NotRegistered, Assert.Single(result.Signals).Status);
        Assert.Empty(h.QualityBatches);
    }

    [Fact]
    public async Task Igw_network_failure_marks_project_comm_failure()
    {
        const long pid = 882003;
        var h = new Harness(IgwProject(pid, 48203), new List<PLC_IOTagEntity> { IoTag(pid, "T", "A"), IoTag(pid, "T", "B") })
        {
            QualityException = new HttpRequestException("connection refused"),
        };

        var result = await h.Service.ReadDiagnosticSignalsAsync(pid, CancellationToken.None);

        Assert.Equal(PLC_DiagnosticProjectStatus.CommFailure, result.ProjectStatus);
        Assert.Contains("connection refused", result.ProjectError);
        Assert.All(result.Signals, s =>
        {
            Assert.Equal(PLC_SignalReadStatus.CommFailure, s.Status);
            Assert.Null(s.Value);
        });
    }

    [Fact]
    public async Task Legacy_gateway_reads_s7_then_plus_and_bad_rows_carry_no_value()
    {
        const long pid = 882011;
        var tags = new List<PLC_IOTagEntity> { IoTag(pid, "T", "A"), IoTag(pid, "T", "B"), IoTag(pid, "T", "C") };
        var h = new Harness(LegacyProject(pid), tags)
        {
            Legacy = (device, _) => device == "PLC1_S7"
                // 老网关不回显 tagName，按索引对位；C 未返回
                ? Ok(new PLC_ReadTagResult { IsGood = true, Value = true },
                    new PLC_ReadTagResult { IsGood = false, Value = false, ErrorMessage = "BadQuality" })
                : Ok(new PLC_ReadTagResult { IsGood = false, Value = false, ErrorMessage = "PlusBad" },
                    new PLC_ReadTagResult { IsGood = true, Value = false }),
        };

        var result = await h.Service.ReadDiagnosticSignalsAsync(pid, CancellationToken.None);

        Assert.Equal(new[] { "PLC1_S7", "PLC1_Plus" }, h.LegacyCalls.Select(c => c.Device));
        Assert.Equal(new[] { "T.A", "T.B", "T.C" }, h.LegacyCalls[0].Tags);
        Assert.Equal(new[] { "T.B", "T.C" }, h.LegacyCalls[1].Tags);
        Assert.Empty(h.QualityBatches);
        var byKey = result.Signals.ToDictionary(s => s.TagName);
        Assert.Equal(PLC_SignalReadStatus.Good, byKey["T.A"].Status);
        Assert.Equal(true, byKey["T.A"].Value);
        Assert.Equal(PLC_SignalReadStatus.BadQuality, byKey["T.B"].Status);
        Assert.Null(byKey["T.B"].Value); // 修复前坏行 false 会被当成真值
        Assert.Contains("PLC1_S7", byKey["T.B"].Detail);
        Assert.Equal(PLC_SignalReadStatus.Good, byKey["T.C"].Status);
        Assert.Equal(false, byKey["T.C"].Value);
        Assert.Equal(PLC_DiagnosticProjectStatus.Ok, result.ProjectStatus);
    }

    [Fact]
    public async Task Legacy_http_failure_marks_project_comm_failure()
    {
        const long pid = 882012;
        var h = new Harness(LegacyProject(pid), new List<PLC_IOTagEntity> { IoTag(pid, "T", "A") })
        {
            Legacy = (_, _) => new HttpResponseResultModel<List<PLC_ReadTagResult>> { IsSuccess = false, ErrorMessage = "SocketException 10054" },
        };

        var result = await h.Service.ReadDiagnosticSignalsAsync(pid, CancellationToken.None);

        Assert.Equal(PLC_DiagnosticProjectStatus.CommFailure, result.ProjectStatus);
        Assert.Contains("10054", result.ProjectError);
        Assert.Equal(PLC_SignalReadStatus.CommFailure, Assert.Single(result.Signals).Status);
        Assert.Single(h.LegacyCalls); // HTTP 层失败不再尝试 _Plus
    }

    [Fact]
    public async Task Legacy_plus_http_failure_appends_detail_to_retried_points_only()
    {
        const long pid = 882016;
        var tags = new List<PLC_IOTagEntity> { IoTag(pid, "T", "A"), IoTag(pid, "T", "B") };
        var h = new Harness(LegacyProject(pid), tags)
        {
            Legacy = (device, _) => device == "PLC1_S7"
                ? Ok(new PLC_ReadTagResult { IsGood = false, Value = false, ErrorMessage = "BadQuality" },
                    new PLC_ReadTagResult { IsGood = true, Value = true })
                : new HttpResponseResultModel<List<PLC_ReadTagResult>> { IsSuccess = false, ErrorMessage = "SocketException 10054" },
        };

        var result = await h.Service.ReadDiagnosticSignalsAsync(pid, CancellationToken.None);

        Assert.Equal(new[] { "PLC1_S7", "PLC1_Plus" }, h.LegacyCalls.Select(c => c.Device));
        var byKey = result.Signals.ToDictionary(s => s.TagName);
        var retried = byKey["T.A"];
        Assert.Equal(PLC_SignalReadStatus.BadQuality, retried.Status); // 状态保持 _S7 的坏质量，不因 _Plus 失败而改变
        Assert.Contains("PLC1_S7", retried.Detail);
        Assert.Contains("PLC1_Plus 读取失败", retried.Detail);
        Assert.Contains("10054", retried.Detail);
        var notRetried = byKey["T.B"];
        Assert.Equal(PLC_SignalReadStatus.Good, notRetried.Status);
        Assert.DoesNotContain("Plus", notRetried.Detail ?? string.Empty); // 未被重试的点不受 _Plus 失败影响
        Assert.Equal(PLC_DiagnosticProjectStatus.Ok, result.ProjectStatus);
    }

    [Fact]
    public async Task Io_point_with_empty_table_name_is_not_registered()
    {
        const long pid = 882013;
        var h = new Harness(IgwProject(pid, 48213), new List<PLC_IOTagEntity> { IoTag(pid, "", "A") });

        var result = await h.Service.ReadDiagnosticSignalsAsync(pid, CancellationToken.None);

        Assert.Equal(PLC_DiagnosticProjectStatus.Ok, result.ProjectStatus);
        var signal = Assert.Single(result.Signals);
        Assert.Equal(PLC_SignalReadStatus.NotRegistered, signal.Status);
        Assert.Empty(h.QualityBatches);
    }

    [Fact]
    public async Task Legacy_project_without_station_id_is_unavailable()
    {
        const long pid = 882014;
        var h = new Harness(LegacyProjectNoStation(pid), new List<PLC_IOTagEntity> { IoTag(pid, "T", "A") });

        var result = await h.Service.ReadDiagnosticSignalsAsync(pid, CancellationToken.None);

        Assert.Equal(PLC_DiagnosticProjectStatus.ProjectUnavailable, result.ProjectStatus);
        Assert.Contains("StationId", result.ProjectError);
        Assert.Equal(PLC_SignalReadStatus.CommFailure, Assert.Single(result.Signals).Status);
        Assert.Empty(h.LegacyCalls);
    }

    [Fact]
    public async Task Empty_tag_set_is_ok_with_no_signals()
    {
        const long pid = 882022;
        var h = new Harness(IgwProject(pid, 48222), new List<PLC_IOTagEntity>());

        var result = await h.Service.ReadDiagnosticSignalsAsync(pid, CancellationToken.None);

        Assert.Equal(PLC_DiagnosticProjectStatus.Ok, result.ProjectStatus);
        Assert.Empty(result.Signals);
        Assert.Empty(h.QualityBatches);
    }

    [Fact]
    public async Task Missing_project_is_unavailable()
    {
        var h = new Harness(null, new List<PLC_IOTagEntity>());

        var result = await h.Service.ReadDiagnosticSignalsAsync(882021, CancellationToken.None);

        Assert.Equal(PLC_DiagnosticProjectStatus.ProjectUnavailable, result.ProjectStatus);
        Assert.Empty(result.Signals);
    }
}
