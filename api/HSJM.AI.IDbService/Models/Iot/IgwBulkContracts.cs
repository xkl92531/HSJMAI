using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HSJM.AI.IDbService
{
    // IGW 批量点位注册契约 DTO（POST /api/v1/project/bulk/tags，一次请求写一台设备）。
    // 字段与 IGW ProjectBulkTagRequest 同构；序列化时使用 camelCase 属性名，
    // DataType / Access 直接以 IGW 枚举的 camelCase 字符串传递（如 "bool"、"uInt16"、"wString"、"readWrite"）。

    /// <summary>
    /// IGW 批量契约 JSON 序列化选项：camelCase 属性名、忽略 null、读取时属性名大小写不敏感
    /// </summary>
    public static class IgwBulkJson
    {
        /// <summary>只读共享实例（MakeReadOnly 后任何修改都会抛异常，防止调用方意外改动全局选项）</summary>
        public static readonly JsonSerializerOptions Options = CreateOptions();

        private static JsonSerializerOptions CreateOptions()
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                PropertyNameCaseInsensitive = true,
            };
            options.MakeReadOnly(populateMissingResolver: true);
            return options;
        }
    }

    /// <summary>目标通道：IGW 中不存在时按此定义创建；已存在时 settings 不覆盖</summary>
    public sealed class IgwBulkChannel
    {
        public string Name { get; set; }
        public string DriverId { get; set; } = "Siemens.S7";
        public Dictionary<string, string> Settings { get; set; }
    }

    /// <summary>目标设备：不存在时创建；已存在时只更新 endpoint 并补齐缺失 settings 键</summary>
    public sealed class IgwBulkDevice
    {
        public string Name { get; set; }
        public string Endpoint { get; set; }
        public Dictionary<string, string> Settings { get; set; }
    }

    /// <summary>数组点定义（dataType 必须等于 elementType）</summary>
    public sealed class IgwBulkArray
    {
        public string ElementType { get; set; }
        public int[] Dimensions { get; set; }
        public int[] LowerBounds { get; set; }
    }

    /// <summary>一条待写入点位；GroupPath 每段一个标记组（相对设备根）</summary>
    public sealed class IgwBulkItem
    {
        public List<string> GroupPath { get; set; } = new();
        public string Name { get; set; }
        public string Address { get; set; }
        public string DataType { get; set; }
        /// <summary>IGW 契约不允许 write，一律 readWrite</summary>
        public string Access { get; set; } = "readWrite";
        public int ScanRateMs { get; set; }
        public string Description { get; set; }
        public IgwBulkArray Array { get; set; }
        public int? StringLength { get; set; }
    }

    /// <summary>批量注册请求体</summary>
    public sealed class IgwBulkRequest
    {
        public IgwBulkChannel Channel { get; set; }
        public IgwBulkDevice Device { get; set; }
        /// <summary>写入前整棵删除的根组路径（相对设备根）</summary>
        public List<List<string>> ReplaceGroups { get; set; } = new();
        public List<IgwBulkItem> Items { get; set; } = new();
    }

    /// <summary>批量注册响应（成功体 + 错误体合并；HttpStatus 由客户端填）</summary>
    public sealed class IgwBulkResponse
    {
        public bool Applied { get; set; }
        public string Revision { get; set; }
        public int Created { get; set; }
        public int Updated { get; set; }
        public int Deleted { get; set; }
        public bool DeviceRestarted { get; set; }
        public string Timing { get; set; }
        /// <summary>错误码：invalid_items / name_conflict / revision_conflict / too_many_items / persistence_failed / runtime_apply_failed 等</summary>
        public string Error { get; set; }
        public string Detail { get; set; }
        public bool? Retryable { get; set; }
        public int HttpStatus { get; set; }

        /// <summary>
        /// 400 invalid_items 逐项错误（details）中 index ≥ 0 的条目下标（对应请求 Items 下标，去重、保持出现顺序；IGW 最多返回 50 条）。
        /// R-1（2026-09-20）：注册任务据此定位被拒绝的根组；index = -1（通道/设备/replaceGroups 层面）不在此列。
        /// </summary>
        public List<int> ItemErrorIndexes { get; set; } = new();
    }
}
