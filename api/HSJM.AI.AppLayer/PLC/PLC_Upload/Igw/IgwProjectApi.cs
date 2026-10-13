using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.PLC.PLC_Upload.Igw
{
    /// <summary>
    /// IGW Configurator 管理面 REST（/api/v1/project/*）的共享只读/变更助手（2026-09-19 从
    /// PLC_IgwGraphPlusChannelService 抽出；该服务的「落通用再搬组」逻辑已随 task-7 删除，现供 <c>IgwBulkClient</c>（旧设备清理）使用）。
    /// 仅内部使用（同程序集内 <c>internal</c>），不对外暴露。
    /// </summary>
    internal static class IgwProjectApi
    {
        internal sealed class NodeInfo
        {
            public string Id { get; set; }
            public string Kind { get; set; }
            public string Name { get; set; }
            public string ActualPath { get; set; }
        }

        internal sealed class EntityDoc
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Endpoint { get; set; }
            public Dictionary<string, string> Settings { get; set; }
        }

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        /// <summary>GET /api/v1/project/summary → 根节点 Id（响应 {root:{id,kind,name,…}}，camelCase）。</summary>
        internal static async Task<string> GetRootIdAsync(HttpClient http, string baseUrl, CancellationToken ct)
        {
            using var response = await http.GetAsync($"{baseUrl}api/v1/project/summary", ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"IGW summary HTTP {(int)response.StatusCode}: {Truncate(body, 200)}");
            }
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("root", out var root) &&
                root.TryGetProperty("id", out var idEl) &&
                idEl.GetString() is { Length: > 0 } rid)
            {
                return rid;
            }
            throw new InvalidOperationException("IGW summary 响应缺少 root.id");
        }

        /// <summary>GET /api/v1/project/tree/{kind}/{id}/children → 子节点列表（camelCase 数组）。</summary>
        internal static async Task<List<NodeInfo>> GetChildrenAsync(
            HttpClient http, string baseUrl, string kind, string id, CancellationToken ct)
        {
            var url = $"{baseUrl}api/v1/project/tree/{kind}/{Uri.EscapeDataString(id)}/children";
            using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"IGW 树查询 HTTP {(int)response.StatusCode}: {Truncate(body, 200)}");
            }
            using var doc = JsonDocument.Parse(body);
            var list = new List<NodeInfo>();
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    list.Add(ParseNode(el));
                }
            }
            return list;
        }

        /// <summary>GET /api/v1/project/entity/{kind}/{id} → 实体文档（含 settings/endpoint）。</summary>
        internal static async Task<EntityDoc> GetEntityAsync(
            HttpClient http, string baseUrl, string kind, string id, CancellationToken ct)
        {
            using var response = await http.GetAsync($"{baseUrl}api/v1/project/entity/{kind}/{Uri.EscapeDataString(id)}", ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null; // 实体查询失败按"未知状态"处理，交由调用方 upsert 校正
            }
            try
            {
                return JsonSerializer.Deserialize<EntityDoc>(body, JsonOpts);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        internal static NodeInfo ParseNode(JsonElement el)
        {
            var node = new NodeInfo();
            if (el.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in el.EnumerateObject())
                {
                    if (string.Equals(prop.Name, "id", StringComparison.OrdinalIgnoreCase)) node.Id = prop.Value.GetString();
                    else if (string.Equals(prop.Name, "kind", StringComparison.OrdinalIgnoreCase)) node.Kind = prop.Value.GetString();
                    else if (string.Equals(prop.Name, "name", StringComparison.OrdinalIgnoreCase)) node.Name = prop.Value.GetString();
                    else if (string.Equals(prop.Name, "actualPath", StringComparison.OrdinalIgnoreCase)) node.ActualPath = prop.Value.GetString();
                }
            }
            return node;
        }

        private static string GetEtag(HttpResponseMessage response)
        {
            var tag = response.Headers.ETag?.Tag;
            return string.IsNullOrEmpty(tag) ? response.Headers.ETag?.ToString() ?? string.Empty : tag;
        }

        /// <summary>POST /api/v1/project/mutations（If-Match 乐观并发；409 冲突重取 ETag 重试，≤3 次）。
        /// kind/entityKind 用 camelCase 字符串枚举（与服务端 ProjectStore.Options 的 JsonStringEnumConverter 对齐）。</summary>
        internal static async Task<(bool Ok, string Error)> MutateWithRetryAsync(
            HttpClient http, string baseUrl, string kind, string entityKind,
            IReadOnlyList<string> entityIds, string targetId, string documentJson, CancellationToken ct)
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                // 每次（含重试）先取最新修订号：树接口响应头 ETag 即修订号
                using var probe = await http.GetAsync($"{baseUrl}api/v1/project/summary", ct).ConfigureAwait(false);
                var etag = GetEtag(probe);
                if (string.IsNullOrWhiteSpace(etag))
                {
                    return (false, "未取得工程修订号（ETag）");
                }

                var payload = new Dictionary<string, object>
                {
                    ["kind"] = kind,
                    ["entityKind"] = entityKind,
                    ["entityIds"] = entityIds,
                    ["targetId"] = targetId,
                    ["documentJson"] = documentJson,
                };
                using var content = new StringContent(
                    JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json");
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}api/v1/project/mutations")
                {
                    Content = content,
                };
                request.Headers.TryAddWithoutValidation("If-Match", etag);
                using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return (true, null);
                }
                if ((int)response.StatusCode == 409 && attempt < 3)
                {
                    continue; // 修订号冲突 → 重取再试
                }
                return (false, $"HTTP {(int)response.StatusCode}: {Truncate(body, 300)}");
            }
            return (false, "修订号冲突重试耗尽");
        }

        internal static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s.Substring(0, max) + "…");
    }
}
