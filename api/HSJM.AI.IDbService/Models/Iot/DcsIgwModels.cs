using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using HSJM.AI.Core;

namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 网关类型（按项目 IotUrl 探测结果判定）
    /// </summary>
    public enum GatewayFlavor
    {
        /// <summary>老网关：/DriverInfo/* 契约（ManualAddTagItemsAsync / ReadTag），如 http://192.168.6.75:2500/</summary>
        Legacy = 0,
        /// <summary>新 IGW：/Tag/* 契约（AddTags / ReadTagsAsList），如 http://127.0.0.1:48020</summary>
        NewIgw = 1,
    }

    /// <summary>
    /// 新 IGW POST /Tag/AddTags 的点位模型。
    /// 序列化（JsonHelper 为 camelCase）后即 IGW 契约字段：ip/port/communicationType/dbAddress/dataType/tagName。
    /// 枚举数值对照 IGW.Northbound.RestApi/DcsTagContracts.cs（t1 调研源码直读）：
    /// DcsCommunicationType{ OpcUa=0, S1200=1, S300=2, S400=3, S1500=4, S200Smart=5, S200=6, FX5U=7, Inovance_H5U=8(不支持), Omr_NJ=9, Omr_Fins=10, MemoryCache=11 }
    /// DcsTagDataType{ String=0, Boolean=1, Char=2, Byte=3, Short=4, Word=5, Long=6, DWord=7, Float=8, Double=9, Date=12, ...数组 20+ }
    /// </summary>
    public class DcsTagUpdateModel
    {
        /// <summary>PLC 直连 IP</summary>
        public string Ip { get; set; }
        /// <summary>PLC 端口（0 = 网关按协议默认，S7=102）</summary>
        public int Port { get; set; }
        /// <summary>通讯类型（见 DcsIgwConsts）</summary>
        public int CommunicationType { get; set; }
        /// <summary>PLC 地址：绝对式（DB1.DBX0.0）或符号式带点路径（DB161_HomeGraph.S_NO，驱动自动走 S7+）</summary>
        public string DbAddress { get; set; }
        /// <summary>数据类型（见 DcsIgwConsts）</summary>
        public int DataType { get; set; }
        /// <summary>点位名（可含点，如 DB161_HomeGraph.S_NO）</summary>
        public string TagName { get; set; }
        /// <summary>数组维度（数组注册必填；IGW 8/24+ unified array 契约，如 EXEC_BITS=[71]）</summary>
        public int[] Dimensions { get; set; }
        /// <summary>数组下界（缺省 0 基；长度须与 Dimensions 一致）</summary>
        public int[] LowerBounds { get; set; }
        /// <summary>元素字节步长（绝对地址数组用；符号式 S7+ 可不填）</summary>
        public int? ElementStrideBytes { get; set; }
        /// <summary>元素位步长（Bool 数组=1）</summary>
        public int? ElementStrideBits { get; set; }
        /// <summary>标记组路径（工站分组，如 OP010；支持 组.子组 嵌套；空=设备根层。IGW 8/26+ 契约，旧版 IGW 忽略该字段）</summary>
        public string Group { get; set; }
    }

    /// <summary>
    /// 新 IGW POST /Tag/ReadTagsAsList 返回 backResult 中的 {key,value} 项。
    /// key 回显请求的点位名；value 为网关线值（bool/数值/字符串）。
    /// </summary>
    public class DcsTagReadItem
    {
        public string Key { get; set; }
        public object Value { get; set; }
    }

    /// <summary>
    /// 新 IGW POST /Tag/ReadTagsWithQuality（IGW 1.6.78+）backResult 的逐点行。线上 JSON 为 camelCase
    /// （key/value/isGood/statusCode/status/timestampUtc/reason），经 Newtonsoft 默认的大小写不敏感匹配反序列化。
    /// value 仅 isGood=true 时有值；reason 取值见 IGW docs/dcs-read-quality.md。
    /// </summary>
    public class DcsTagQualityItem
    {
        public string? Key { get; set; }
        public object? Value { get; set; }
        public bool IsGood { get; set; }
        public uint StatusCode { get; set; }
        public string? Status { get; set; }
        public DateTime? TimestampUtc { get; set; }
        public string? Reason { get; set; }
    }

    /// <summary>
    /// IGW 单次 HTTP 调用的原始结果（不分批、不重试）：2xx 时 IsHttpSuccess=true、Envelope=IGW 外层
    /// {isSuccess,backResult,errorMessage}；非 2xx 时 Envelope=null、RawBody=响应体。
    /// </summary>
    public sealed class IgwTransportResult<T>
    {
        public HttpStatusCode HttpStatusCode { get; set; }
        public bool IsHttpSuccess { get; set; }
        public HttpResponseResultModel<T>? Envelope { get; set; }
        public string? RawBody { get; set; }
    }

    /// <summary>
    /// 新 IGW（/Tag/* 契约）常量与纯映射函数。
    /// 数值均源自 IGW 源码 DcsTagContracts.cs / DcsTagProjectUpdater.cs（t1 调研，置信度 100%）。
    /// </summary>
    public static class DcsIgwConsts
    {
        // ---- 通讯类型（DcsCommunicationType）----
        /// <summary>OPC UA（默认端口 4840，endpoint=opc.tcp://ip:port）</summary>
        public const int CommunicationTypeOpcUa = 0;
        /// <summary>西门子 S7-1500（Siemens.S7 驱动，默认端口 102，model=S7-1500）——本项目 PLC 类型</summary>
        public const int CommunicationTypeS71500 = 4;
        /// <summary>内存仿真（Simulator 驱动，不连真机）——安全测试路径</summary>
        public const int CommunicationTypeMemoryCache = 11;

        // ---- 数据类型（DcsTagDataType）----
        /// <summary>Boolean（Bool）</summary>
        public const int DataTypeBoolean = 1;
        /// <summary>Short（Int16）——GRAPH 步号 S_NO 用</summary>
        public const int DataTypeShort = 4;
        /// <summary>Long（Int32）</summary>
        public const int DataTypeLong = 6;
        /// <summary>String</summary>
        public const int DataTypeString = 0;
        /// <summary>BooleanArray（Bool 数组，需配 Dimensions）——EXEC_BITS 整组注册用（IGW 8/24+ 契约）</summary>
        public const int DataTypeBooleanArray = 21;

        /// <summary>GRAPH 步位注册默认通讯类型：S7-1500</summary>
        public const int DefaultGraphStepCommunicationType = CommunicationTypeS71500;

        /// <summary>S7 协议默认端口（IGW 侧 port=0 时也按协议默认，此处仅用于日志/回填提示）</summary>
        public const int DefaultS7Port = 102;

        /// <summary>新 IGW 读接口单批上限（超出返回 400 "单次最多处理 1000 个点位"，见 DcsTagOperations.MaxBatchSize）</summary>
        public const int ReadBatchLimit = 1000;

        /// <summary>逻辑别名通道前缀（IGW DcsTagAliasResolver 以 "通用.{logicalDevice}." 前缀解析）</summary>
        public const string LogicalChannelPrefix = "通用";

        /// <summary>GRAPH 工步（S7 Plus 符号式容器）专属通道名（2026-09-17 架构分离）。
        /// IGW 的 Siemens.S7 驱动是混合模式：Classic 绝对地址与 S7 Plus 符号地址按设备二选一，
        /// 标量全量注册（符号式大批量）与 GRAPH 容器若同设备/同通道会话，Plus 枚举风暴会拖垮连接。
        /// 历史约定（已废弃，sub-project B task-7 起 GRAPH 直接批量写入项目通道的 GRAPH 设备，本通道仅作为旧设备清理识别依据）：
        /// GRAPH 工位标记组注册到 通用 通道后，由 PLC_IgwGraphPlusChannelService（已删除）把工位组整体
        /// 搬到本通道的 S1500_{ip} 设备下（设备 Settings 带 dcsLogicalDevice，别名 通用.S1500_{ip}.* 经
        /// DcsTagAliasResolver 的 _groupBare 兼容字典解析，读链零改动）。标量注册留在 通用 通道不动。</summary>
        public const string GraphPlusChannelName = "通用_S7Plus";

        /// <summary>IGW 西门子通道驱动 ID（IoTSiemensDriverFactory.Id；1200/1500 混合模式：Classic+Plus）</summary>
        public const string SiemensS7DriverId = "Siemens.S7";

        /// <summary>IGW DCS 设备元数据键（DcsTagContracts.DcsTagMetadata；设备 Settings 字典的 key）：
        /// 标记该设备由 DCS AddTags 托管 + 逻辑设备名（别名解析依据）</summary>
        public const string DcsLogicalDeviceSettingKey = "dcsLogicalDevice";
        public const string DcsCommunicationTypeSettingKey = "dcsCommunicationType";

        /// <summary>GRAPH Plus 设备逻辑名（2026-09-18 修正）：S7Plus_{ip}。
        /// 不能沿用 S1500_{ip}——标量设备（通用通道）与 Plus 设备（通用_S7Plus通道）若同名，
        /// DcsTagAliasResolver 的逻辑前缀 通用.S1500_{ip}. 命中两台设备 → "DCS 点位别名不唯一"整前缀拒读
        /// （2026-09-18 实测：搬迁成功后 S_NO 读回报别名不唯一）。独立逻辑名后 GRAPH 别名形如
        /// 通用.S7Plus_{ip}.{db}.RT_DATA.EXEC_BITS，经 _aliases 唯一映射到 Plus 物理设备。</summary>
        public static string GraphPlusDeviceLogicalName(string ip) => $"S7Plus_{(ip ?? string.Empty).Trim()}";

        /// <summary>GRAPH 容器（Plus 设备）读别名：通用.S7Plus_{ip}.{tagName}</summary>
        public static string BuildGraphPlusReadAlias(string ip, string tagName) =>
            $"{LogicalChannelPrefix}.{GraphPlusDeviceLogicalName(ip)}.{tagName}";

        /// <summary>是否 GRAPH 容器读路径——不按 DB 后缀猜（RobotGraph/CacheGraph/SyncStartGraph 等
        /// 任意 Graph 味都要覆盖，2026-09-18 白名单漏判教训），按精确容器模式判：
        /// 形如 {db}.S_NO（工步号）或 {db}.RT_DATA.EXEC_BITS（执行位数组）。这两类只在 Plus 设备上注册，
        /// 其余深变量路径仍走标量设备别名。</summary>
        public static bool IsGraphContainerPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }
            var firstDot = path.IndexOf('.');
            if (firstDot <= 0 || firstDot == path.Length - 1)
            {
                return false; // 没有第二段，不构成 {db}.{member} 形态
            }
            var head = path.Substring(0, firstDot);
            var rest = path.Substring(firstDot + 1);
            return string.Equals(rest, "S_NO", StringComparison.OrdinalIgnoreCase)
                || string.Equals(rest, "RT_DATA.EXEC_BITS", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>老 IoT 网关端口（/DriverInfo/* + deviceName 模型，如 http://192.168.6.75:2500）。
        /// GRAPH 工步变量的「_Plus 优先 + 新 IGW 兜底」规则仅对配置了该端口网关的项目生效。</summary>
        public const int LegacyIotGatewayPort = 2500;

        /// <summary>老 IoT 网关判定（2026-09-18 全 IGW 迁移共享版）：URL 端口为 2500 即老网关；
        /// URL 解析失败时按 ":2500" 子串兜底。DbService 的 PLC_VariableAccessService 与
        /// AppLayer 的工位影子快照服务（StopCauseSignalSnapshotService）用同一判定分派读链。</summary>
        public static bool IsLegacyIotGatewayUrl(string iotUrl)
        {
            if (string.IsNullOrWhiteSpace(iotUrl))
            {
                return false;
            }
            if (Uri.TryCreate(iotUrl.Trim(), UriKind.Absolute, out var uri))
            {
                return uri.Port == LegacyIotGatewayPort;
            }
            return iotUrl.Contains(":" + LegacyIotGatewayPort, StringComparison.Ordinal);
        }

        /// <summary>新 IGW 兜底读基址：老网关 _Plus 设备也读不到工步变量时经此整组读回
        /// （与 6.75 部署的 igw 容器 48020 一致；异机部署时改这里）。2026-09-12 规则。</summary>
        public const string FallbackIgwBaseUrl = "http://192.168.6.75:48020";

        /// <summary>通讯类型数值 → IGW 逻辑设备名片段（与 DcsTagProjectUpdater 的 {CommType}_{ip} 命名一致）</summary>
        public static string CommunicationTypeName(int communicationType)
        {
            switch (communicationType)
            {
                case CommunicationTypeOpcUa: return "OpcUa";
                case 1: return "S1200";
                case 2: return "S300";
                case 3: return "S400";
                case CommunicationTypeS71500: return "S1500";
                case 5: return "S200Smart";
                case 6: return "S200";
                case 7: return "FX5U";
                case 9: return "Omr_NJ";
                case 10: return "Omr_Fins";
                case CommunicationTypeMemoryCache: return "MemoryCache";
                default: return communicationType.ToString();
            }
        }

        /// <summary>
        /// 构造新 IGW 读点位名（逻辑别名形态）：
        /// 通用.{CommTypeName}_{ip}[.{group}].{tagName}，如 通用.S1500_192.168.6.10.DB161_HomeGraph.S_NO（无组）/
        /// 通用.S1500_192.168.6.10.OP010.DB160_MainGraph.RT_DATA.EXEC_BITS（工站组 OP010）。
        /// 点位在标记组内时物理路径含组段（通道.设备.组.点），别名需带组；IGW 8/26+ 解析器另含无组兼容索引。
        /// DcsTagAliasResolver 支持物理路径与逻辑别名两种形态；逻辑别名与通道名解耦（通道可能叫 通用 或 通用_S1500），更稳定。
        /// </summary>
        public static string BuildTagReadAlias(string communicationTypeName, string ip, string tagName, string group = null)
        {
            var trimmedIp = (ip ?? string.Empty).Trim();
            var normalizedGroup = DcsIgwMapper.NormalizeGroupName(group);
            var groupSegment = normalizedGroup.Length == 0 ? string.Empty : normalizedGroup + ".";
            return $"{LogicalChannelPrefix}.{communicationTypeName}_{trimmedIp}.{groupSegment}{tagName}";
        }

        /// <summary>
        /// 规范化网关 baseUrl：缺 scheme 补 http://，去尾部斜杠后统一以单个 / 结尾。
        /// 所有对网关的 URL 拼接都必须先过此方法，杜绝 //Tag/... 双斜杠。
        /// </summary>
        public static string NormalizeGatewayBaseUrl(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                return host;
            }

            var trimmed = host.Trim();
            if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = "http://" + trimmed;
            }
            return trimmed.TrimEnd('/') + "/";
        }

        /// <summary>
        /// 拼接网关完整 URL：规范化 baseUrl + 相对路径（去首部斜杠），保证 base 与 path 间恰一个 /。
        /// 例：CombineGatewayUrl("http://a:1//", "/Tag/AddTags") → "http://a:1/Tag/AddTags"。
        /// </summary>
        public static string CombineGatewayUrl(string host, string path)
        {
            var baseUrl = NormalizeGatewayBaseUrl(host);
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return path;
            }
            if (string.IsNullOrWhiteSpace(path))
            {
                return baseUrl;
            }
            var relative = path.Trim().TrimStart('/');
            return baseUrl + relative;
        }
    }

    /// <summary>
    /// IQTagItem（现行台账/注册模型）→ DcsTagUpdateModel（新 IGW 契约）的纯映射，
    /// 以及新 IGW 线值的宽松解析。无副作用，供 IotService 适配层与单元测试共用。
    /// </summary>
    public static class DcsIgwMapper
    {
        /// <summary>
        /// IQTagItem → DcsTagUpdateModel。
        /// dbAddress 与 tagName 均取 Address（GRAPH 步位为符号式带点路径，IGW 驱动按地址形态自动走 S7+）。
        /// </summary>
        public static DcsTagUpdateModel ToDcsTagUpdateModel(IQTagItem item, string ip, int port, int communicationType, string group = null)
        {
            if (item == null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            return new DcsTagUpdateModel
            {
                Ip = (ip ?? string.Empty).Trim(),
                Port = port,
                CommunicationType = communicationType,
                DbAddress = item.Address,
                DataType = MapDataType(item.DataType),
                TagName = item.Address,
                Group = NormalizeGroupName(group),
            };
        }

        /// <summary>
        /// 构造数组整体注册点位（GRAPH EXEC_BITS 每 DB 一条）：
        /// dbAddress/tagName = 数组容器路径（如 DB160_MainGraph.RT_DATA.EXEC_BITS，不带末段数组名；仅取元素时才是 ....EXEC_BITS[n]），
        /// dataType=21 BooleanArray，dimensions=[elementCount]（必须覆盖最大步号），elementStrideBits=1。
        /// 契约依据：IGW DcsTagProjectUpdater.TryApplyArrayDefinition（2026-08-24 unified array）。
        /// </summary>
        public static DcsTagUpdateModel ToDcsArrayTagUpdateModel(string arraySymbolPath, int elementCount, string ip, int port, int communicationType, string group = null)
        {
            if (string.IsNullOrWhiteSpace(arraySymbolPath))
            {
                throw new ArgumentException("数组符号路径不能为空", nameof(arraySymbolPath));
            }
            if (elementCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(elementCount), "数组维度必须大于零");
            }

            return new DcsTagUpdateModel
            {
                Ip = (ip ?? string.Empty).Trim(),
                Port = port,
                CommunicationType = communicationType,
                DbAddress = arraySymbolPath,
                DataType = DcsIgwConsts.DataTypeBooleanArray,
                TagName = arraySymbolPath,
                Dimensions = new[] { elementCount },
                ElementStrideBits = 1,
                Group = NormalizeGroupName(group),
            };
        }

        /// <summary>标记组路径规范化：空白/仅分隔点返回空串（=设备根层），否则去首尾空白与多余点。</summary>
        public static string NormalizeGroupName(string group)
        {
            var trimmed = (group ?? string.Empty).Trim().Trim('.');
            return string.IsNullOrWhiteSpace(trimmed) ? string.Empty : trimmed;
        }

        /// <summary>
        /// 现行 DataType 字符串 → 新 IGW DcsTagDataType 数值；未知类型返回 -1（None，IGW 会拒绝并给出明确错误）。
        /// </summary>
        public static int MapDataType(string dataType)
        {
            if (string.IsNullOrWhiteSpace(dataType))
            {
                return -1;
            }

            switch (dataType.Trim())
            {
                case "Bool":
                case "Boolean":
                case "Bit":
                    return DcsIgwConsts.DataTypeBoolean;
                case "Short":
                case "Int":
                case "Int16":
                case "SInt":
                case "UInt":
                case "UInt16":
                case "Word":
                    return DcsIgwConsts.DataTypeShort;
                case "Int32":
                case "DInt":
                case "Long":
                case "UInt32":
                case "DWord":
                    return DcsIgwConsts.DataTypeLong;
                case "Float":
                case "Real":
                    return 8;
                case "Double":
                case "LReal":
                    return 9;
                case "String":
                    return DcsIgwConsts.DataTypeString;
                default:
                    return -1;
            }
        }

        /// <summary>
        /// 新 IGW 线值宽松解析：字符串 "true"/"false"→bool、整数字符串→long、浮点字符串→double；其余原样返回。
        /// 保证现有 L2（EXEC_BIT 值是 bool）/L3（S_NO 可转 int）验证逻辑对新网关读值同样成立。
        /// </summary>
        public static object NormalizeDcsValue(object value)
        {
            // System.Text.Json 路径兜底：object 目标反序列化得到 JsonElement（而非 CLR 原生类型），
            // 现行链路是 Newtonsoft（产出 bool/long/string），这里防御将来切换序列化器时 L2 检查全挂。
            if (value is System.Text.Json.JsonElement element)
            {
                switch (element.ValueKind)
                {
                    case System.Text.Json.JsonValueKind.Array:
                    case System.Text.Json.JsonValueKind.Object:
                        // 数组/对象不能拍平成字符串：原样返回，交由 NormalizeDcsArray 按元素处理
                        return element;
                    case System.Text.Json.JsonValueKind.True:
                        return true;
                    case System.Text.Json.JsonValueKind.False:
                        return false;
                    case System.Text.Json.JsonValueKind.Number:
                        return element.TryGetInt32(out var i32) ? (object)i32
                            : element.TryGetInt64(out var i64) ? (object)i64
                            : (object)element.GetDouble();
                    default:
                        value = element.ToString();
                        break;
                }
            }

            if (value is string text)
            {
                var trimmed = text.Trim();
                if (bool.TryParse(trimmed, out var boolValue))
                {
                    return boolValue;
                }
                if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
                {
                    return longValue;
                }
                if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
                {
                    return doubleValue;
                }
            }
            return value;
        }

        /// <summary>
        /// 新 IGW 数组线值归一化：把整组读回的数组值（Newtonsoft JArray / System.Text.Json JsonElement/JsonArray / object[]）
        /// 展开为逐元素归一化列表；非数组形态返回 null。
        /// GRAPH EXEC_BITS 整组注册后，读值验证按 BitIndex 从该列表取逐位 bool 值。
        /// </summary>
        public static List<object> NormalizeDcsArray(object value)
        {
            if (value == null)
            {
                return null;
            }

            if (value is System.Text.Json.JsonElement jsonElement)
            {
                if (jsonElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                {
                    return null;
                }
                var elementList = new List<object>();
                foreach (var item in jsonElement.EnumerateArray())
                {
                    elementList.Add(NormalizeArrayElement(item));
                }
                return elementList;
            }

            if (value is System.Text.Json.Nodes.JsonArray jsonArray)
            {
                var nodeList = new List<object>();
                foreach (var item in jsonArray)
                {
                    nodeList.Add(NormalizeArrayElement(item));
                }
                return nodeList;
            }

            if (value is string)
            {
                return null;
            }

            if (value is System.Collections.IEnumerable sequence)
            {
                var list = new List<object>();
                foreach (var item in sequence)
                {
                    list.Add(NormalizeArrayElement(item));
                }
                return list;
            }

            return null;
        }

        /// <summary>数组元素归一化：CLR 原生直接用；JsonElement 递归 NormalizeDcsValue；
        /// Newtonsoft JValue（IConvertible）按 TypeCode 转原生，避免 L2 检查拿到 JToken。</summary>
        private static object NormalizeArrayElement(object item)
        {
            if (item == null)
            {
                return null;
            }

            if (item is System.Text.Json.JsonElement element)
            {
                return NormalizeDcsValue(element);
            }

            if (item is IConvertible convertible)
            {
                try
                {
                    var code = convertible.GetTypeCode();
                    if (code == TypeCode.Boolean)
                    {
                        return convertible.ToBoolean(null);
                    }
                    if (code == TypeCode.Int32)
                    {
                        return convertible.ToInt32(null);
                    }
                    if (code == TypeCode.Int64)
                    {
                        return convertible.ToInt64(null);
                    }
                    if (code == TypeCode.Double)
                    {
                        return convertible.ToDouble(null);
                    }
                    if (code == TypeCode.String)
                    {
                        return convertible.ToString(null);
                    }
                }
                catch
                {
                    // 转换失败走兜底
                }
            }

            return NormalizeDcsValue(item);
        }
    }
}
