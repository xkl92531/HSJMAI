using System.Collections.Generic;

namespace HSJM.AI.Core
{
    public class DbConnectionModel
    {
        public string ConnectionString { set; get; }
        public string DatabaseType { set; get; }
        public string ConfigId { set; get; }
    }
    /// <summary>
    /// 服务器配置类
    /// </summary>
    public class AppsettingsConfig
    {


        /// <summary>
        /// Redis地址
        /// </summary>
        public static string RedisServerUrl { get; set; }

        /// <summary>
        /// 允许访问的IP
        /// </summary>
        public static List<string> AllowedIpAddresses { set; get; }
        public static List<DbConnectionModel> DBList { set; get; }
        /// <summary>
        /// 默认数据库连接
        /// </summary>
        public static string DefaultConnectionString { get; set; } = "";

        /// <summary>
        /// 数据库类型SQLSERVER，MYSQL,ORACLE
        /// </summary>
        public static string DatabaseType { get; set; } = "SQLSERVER";


        /// <summary>
        /// 生产主键机器码
        /// </summary>
        public static int? MachineId { get; set; } = 1;

        /// <summary>
        /// 生产主键数据中心id
        /// </summary>
        public static int? DataCenterId { get; set; } = 1;


        /// <summary>
        /// 默认密码
        /// </summary>
        public static string DefaultPwd { get; set; }


        /// <summary>
        /// ApiService 配置
        /// </summary>
        public static List<ServiceApiHostModel> ServiceApiHosts { get; set; }


        /// <summary>
        /// JWT配置
        /// </summary>
        public static JWTConfig JWTConfig { get; set; }

        /// <summary>
        /// AI Agent 配置
        /// </summary>
        public static AIAgentConfig AIAgent { get; set; }

        /// <summary>
        /// 获取 DiagnosticAgentRoleId，如果未配置则返回默认值 2013
        /// </summary>
        public static int GetDiagnosticAgentRoleId() => AIAgent?.DiagnosticAgentRoleId ?? 2013;

        /// <summary>
        /// IGW 自动注册配置（WebConfig:IgwRegistration，未配置时使用默认值）
        /// </summary>
        public static IgwRegistrationConfig IgwRegistration { get; set; } = new();

    }

    /// <summary>
    /// IGW 自动注册配置：扫描周期（ms）/ 每请求最大点数 / PLC 型号
    /// </summary>
    public class IgwRegistrationConfig
    {
        /// <summary>IO 变量表点位扫描周期</summary>
        public int IoScanRateMs { get; set; } = 100;

        /// <summary>DB 变量扫描周期</summary>
        public int DbScanRateMs { get; set; } = 1000;

        /// <summary>GRAPH 工步容器扫描周期</summary>
        public int GraphScanRateMs { get; set; } = 100;

        /// <summary>
        /// 工步相关 DB 块（GRAPH FB 的实例 DB）的点位扫描率，其余 DB 点仍用 <see cref="DbScanRateMs"/>。
        ///
        /// 2026-09-22：工步状态（转移条件、动作输出、步时长 T/T_MAX）住在这些块里，1000ms 太慢，
        /// 现场表现为"步已经跳过去了、条件还是旧的"。IGW 的 ScanRateMs 是按点位存的，
        /// 同一台设备上两种频率并存是支持的，所以只给工步块提速、不动其余点。
        ///
        /// ⚠️ 不要把 <see cref="DbScanRateMs"/> 整体调到这个值：现场实测同一台 S7-1500 上
        /// S7 Plus 一个请求 93 点必断连、46~56 点才稳；几百个符号点要在 100ms 周期内跑完
        /// 会频繁触发自适应窗口收窄。
        ///
        /// ⚠️ 改这个值要**重新注册**才生效——扫描率是写在点位上的。
        /// </summary>
        public int GraphDbScanRateMs { get; set; } = 100;

        /// <summary>每设备每请求最多点数（IGW 硬上限 200,000），按块边界切分</summary>
        public int MaxItemsPerRequest { get; set; } = 100_000;

        /// <summary>
        /// 每请求序列化后的字节预算（2026-09-20 新增）。点数上限挡不住体积：实测每点 231 字节、最大 296，
        /// 10 万点约 22~30 MB，正好压在 Kestrel 默认请求体上限 30,000,000 字节边缘，超出时服务端在读 body 阶段
        /// 直接掐连接（客户端表现为 Broken pipe，见 §六 2026-09-20）。默认 16 MB 留足余量。
        /// </summary>
        public int MaxRequestBytes { get; set; } = 16 * 1024 * 1024;

        /// <summary>设备 settings.model</summary>
        public string PlcModel { get; set; } = "S7-1500";

        /// <summary>
        /// 单台设备一次注册中，因 IGW 按内容拒绝（400/413）而做的隔离重发（剔除出错根组后重发 / 按根组二分）次数上限。
        /// 400/413 在写库前返回，重发不重启设备；超限未定位的根组记 Failed（瞬态），下次注册继续定位。R-1（2026-09-20）
        /// </summary>
        public int MaxIsolationRequests { get; set; } = 64;

        /// <summary>
        /// GRAPH 注册在写台账前失败（探测失败 / 版本过低 / 缺 plcip / 旧设备清理失败等）后，DB/IO 注册因「台账无 GRAPH 记录」
        /// 自动请求 GRAPH 重注册的冷却时间（分钟）。「注册PLC标签」全量与「删除了旧 GRAPH 设备」不受限制。N-6（2026-09-20）
        /// </summary>
        public int GraphRetryCooldownMinutes { get; set; } = 30;
    }

    /// <summary>
    /// AI Agent 配置
    /// </summary>
    public class AIAgentConfig
    {
        /// <summary>
        /// 最大迭代次数
        /// </summary>
        public int MaxIterations { get; set; } = 20;

        /// <summary>
        /// 请求超时时间（秒）
        /// </summary>
        public int TimeoutSeconds { get; set; } = 120;

        /// <summary>
        /// 是否启用回退逻辑
        /// </summary>
        public bool EnableFallback { get; set; } = true;

        /// <summary>
        /// 记忆提取使用的历史消息条数
        /// </summary>
        public int MemoryExtractHistoryMessages { get; set; } = 8;

        /// <summary>
        /// 诊断 Agent Role ID（诊断、验证、优化共用）
        /// </summary>
        public int DiagnosticAgentRoleId { get; set; } = 2013;
    }



}
