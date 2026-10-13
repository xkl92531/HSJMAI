using System.Text;
using System.Text.Json;
using HSJM.AI.AppLayer.PLC.PLC_Upload.Igw;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Xunit;

namespace HSJM.AI.IgwRegistration.Tests;

/// <summary>
/// 请求体字节预算（2026-09-20）：现场上传 IGW 注册报
/// `BadHttpRequestException: Request body too large. The max request body size is 30000000 bytes.`
/// —— 规划器此前只按点数（MaxItemsPerRequest=100000）切分，不看字节数；实测每点 231 字节（最大 296），
/// 10 万点约 22~30 MB，正好压在 Kestrel 默认 30 MB 上限边缘，导致部分请求被服务端掐连接（客户端表现为 Broken pipe）。
/// </summary>
public class IgwRegistrationPlannerRequestBytesTests
{
    private const string Ip = "192.168.1.10";

    private static PlannerInput Input(IReadOnlyList<PlannerDbRow> rows, IgwRegistrationConfig config) =>
        new("线体A_PLC1", Ip, 102, rows, new List<PlannerIoTag>(),
            new List<string>(), new List<string>(), false, true, config);

    /// <summary>构造深路径 + 中文描述的行，逼近现场最大点（296 字节）的体积</summary>
    private static List<PlannerDbRow> BulkyRows(int count)
    {
        var rows = new List<PlannerDbRow>(count);
        for (var i = 0; i < count; i++)
        {
            var path = $"V90[{i % 8}].Servo_Recive.POS_ZSW2.Direct_output_via_traversing_block_{i}";
            rows.Add(new PlannerDbRow("DB640_OP60Drive", 640, path, "Bool", null, null, "伺服位置控制字反馈位说明文字"));
        }
        return rows;
    }

    [Fact]
    public void Every_request_body_stays_within_the_configured_byte_budget()
    {
        var budget = 2 * 1024 * 1024; // 2 MB，便于用小数据量触发
        var config = new IgwRegistrationConfig { MaxRequestBytes = budget };

        var plan = IgwRegistrationPlanner.Plan(Input(BulkyRows(30_000), config));

        Assert.NotEmpty(plan.Requests);
        foreach (var request in plan.Requests)
        {
            var bytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request.Request, IgwBulkJson.Options));
            Assert.True(bytes <= budget,
                $"单个请求 {bytes:N0} 字节超出预算 {budget:N0}（点数 {request.Request.Items.Count:N0}）");
        }
        // 所有点都要被发出去，不能因为切分丢点
        Assert.Equal(30_000, plan.Requests.Sum(r => r.Request.Items.Count));
    }

    [Fact]
    public void Default_budget_keeps_requests_under_the_kestrel_default_limit()
    {
        // 默认预算必须明显低于 Kestrel 默认 30,000,000 字节，留出 replaceGroups / 通道设备头的余量
        Assert.True(new IgwRegistrationConfig().MaxRequestBytes <= 24_000_000,
            "默认请求体预算应 ≤ 24 MB，避免贴着 Kestrel 默认 30 MB 上限");
    }
}
