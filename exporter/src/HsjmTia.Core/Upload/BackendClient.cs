using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace HsjmTia.Core.Upload
{
    /// <summary>
    /// 与后端 UploadStatus 枚举逐字对应（api/HSJM.AI.AppLayer/PLC/PLC_Upload/PLC_UploadProgressService.cs）。
    /// 后端没有注册 StringEnumConverter，所以 JSON 里的 status 是整数，这里按数值反解析。
    /// </summary>
    public enum UploadStatus
    {
        Pending = 0,
        Processing = 1,
        Saving = 2,
        Completed = 3,
        Failed = 4
    }

    public class UploadProgressDto
    {
        public string TaskId { get; set; }
        public UploadStatus Status { get; set; }
        public int Percentage { get; set; }
        public string Message { get; set; }
        public string CurrentFile { get; set; }
        public int ProcessedFiles { get; set; }
        public int TotalFiles { get; set; }
        public string ErrorDetails { get; set; }

        public bool IsCompleted => Status == UploadStatus.Completed || Status == UploadStatus.Failed;
    }

    public class BackendClient
    {
        private readonly HttpClient _http;
        private string _token;

        public BackendClient(HttpClient http)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
        }

        /// <summary>
        /// 登录。请求字段名必须是 account —— 后端绑定的是 LoginInfoView.Account
        /// (api/HSJM.AI.AppLayer/Auth/ViewObject/LoginInfoView.cs)，写成 userName 会绑成 null 直接登录失败。
        /// </summary>
        public async Task<string> LoginAsync(string user, string password)
        {
            var body = new StringContent(
                new JObject { ["account"] = user, ["password"] = password }.ToString(),
                System.Text.Encoding.UTF8, "application/json");

            var json = await SendAsync(new HttpRequestMessage(HttpMethod.Post, "Auth/Login") { Content = body })
                .ConfigureAwait(false);

            _token = (string)json["backResult"]?["token"];
            if (string.IsNullOrEmpty(_token))
            {
                throw new InvalidOperationException("登录成功但未返回 token");
            }
            return _token;
        }

        /// <summary>
        /// PLC_Project/GetList 是 [HttpGet]+[FromQuery]，不是 POST，而且 controller 直接
        /// `return Ok(list)`（list 是 List&lt;PLC_ProjectEntity&gt;）—— 不走 HttpResponseResultModel
        /// 信封（api/HSJM.AI/Controllers/PLC/PLC_Project/PLC_ProjectController.cs:189-197）。
        /// 所以响应根节点本身就是数组，不能走 SendAsync（它假定 isSuccess/backResult 信封字段）。
        /// id 是雪花主键，LongSnowflakeSafeJsonConverter 把它序列化成 JSON 字符串而不是数字
        /// （api/HSJM.AI/Program.cs:118-125），因此按字符串/数字两种形式防御性解析。
        /// </summary>
        public async Task<IReadOnlyList<BackendProject>> GetProjectsAsync()
        {
            var json = await SendForRawJsonAsync(new HttpRequestMessage(HttpMethod.Get, "PLC_Project/GetList"))
                .ConfigureAwait(false);

            // 这个路由不走信封，正常情况下根节点就是数组；但 IP 过滤中间件拒绝请求
            // （errorMessage:"请求来源非法"）和未处理的服务器异常都会以 HTTP 200 + 一个
            // JSON *对象* 返回。旧实现 `json as JArray ?? new JArray()` 把这两种情况都
            // 悄悄读成"后端没有任何项目"，调用方完全看不出请求其实被拒绝了。
            if (!(json is JArray array))
            {
                var errorMessage = (json as JObject)?["errorMessage"]?.ToString();
                if (!string.IsNullOrEmpty(errorMessage))
                {
                    throw new InvalidOperationException(errorMessage);
                }
                throw new InvalidOperationException(
                    "PLC_Project/GetList 未返回项目数组，响应内容：" + json);
            }

            var list = new List<BackendProject>();
            foreach (var item in array)
            {
                list.Add(new BackendProject
                {
                    Id = ParseSnowflakeId(item["id"]),
                    ProjectName = (string)item["projectName"],
                    PlcIp = (string)item["plcIp"],
                    PlcCode = (string)item["plcCode"]
                });
            }
            return list;
        }

        private static long ParseSnowflakeId(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return 0;
            }
            if (token.Type == JTokenType.String)
            {
                return long.Parse((string)token);
            }
            return (long?)token ?? 0;
        }

        public async Task<string> StartUploadAsync(string zipPath, long projectId, bool fullProjectUpdate)
        {
            // 导出包可能有几十上百 MB（几千个块的整工程）。旧实现 File.ReadAllBytes 把整个
            // zip 一次性读进内存再包进 ByteArrayContent，导出/上传并发跑多个 PLC 时内存占用
            // 会成倍叠加。改用 StreamContent 顶着一个只读 FileStream，边读边发。
            //
            // using 包住 fileStream/form：MultipartFormDataContent.Dispose 会级联释放
            // StreamContent 进而关闭 FileStream。即便框架在 SendAsync 返回后已经自行释放过
            // 一次 request.Content，这里的第二次 Dispose 也是安全的空操作（.NET 的 Dispose
            // 约定保证幂等，不会因为"已经被释放过"而抛异常）——两边谁先释放都不会有问题，
            // 关键是无论如何都不能一次性把整个文件读进内存。
            using (var fileStream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var form = new MultipartFormDataContent())
            {
                var fileContent = new StreamContent(fileStream);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                form.Add(fileContent, "file", Path.GetFileName(zipPath));
                form.Add(new StringContent(projectId.ToString()), "projectId");
                form.Add(new StringContent(fullProjectUpdate ? "true" : "false"), "fullProjectUpdate");

                var json = await SendAsync(
                    new HttpRequestMessage(HttpMethod.Post, "PLC_Upload/StartBackgroundUpload") { Content = form })
                    .ConfigureAwait(false);

                return (string)json["backResult"]?["taskId"];
            }
        }

        /// <summary>
        /// backResult 是真实的 UploadProgress 形状（api/HSJM.AI.AppLayer/PLC/PLC_Upload/PLC_UploadProgressService.cs）：
        /// taskId/status/percentage/message/currentFile/processedFiles/totalFiles/errorDetails 等，
        /// 没有 stage、percent、isCompleted 这几个字段——旧实现是按计划示例代码猜的形状写的，纯属虚构。
        /// status 是 UploadStatus 枚举；后端未注册 StringEnumConverter，按整数序列化
        /// （api/HSJM.AI/Program.cs:118-125）。
        /// </summary>
        public async Task<UploadProgressDto> GetProgressAsync(string taskId)
        {
            var request = new HttpRequestMessage(
                HttpMethod.Get, "PLC_Upload/GetProgress?taskId=" + Uri.EscapeDataString(taskId));

            var json = await SendAsync(request, throwOnFailure: false).ConfigureAwait(false);

            if ((bool?)json["isSuccess"] == false)
            {
                var errorMessage = (string)json["errorMessage"];
                // 后端只在任务真的查不到/已过期时才用这两个措辞；其它 isSuccess:false
                // （例如数据库瞬时连接失败）不能被当成"任务不存在"悄悄吞掉——轮询方需要知道
                // 这是应该重试的故障，而不是该放弃轮询、把上传标记为失败了。
                if (!string.IsNullOrEmpty(errorMessage) &&
                    (errorMessage.Contains("不存在") || errorMessage.Contains("过期")))
                {
                    return null;
                }
                throw new InvalidOperationException(errorMessage ?? "后端返回失败但未给出原因");
            }

            var result = json["backResult"];
            if (result == null || result.Type == JTokenType.Null)
            {
                return null;
            }

            return new UploadProgressDto
            {
                TaskId = (string)result["taskId"],
                Status = (UploadStatus)((int?)result["status"] ?? 0),
                Percentage = (int?)result["percentage"] ?? 0,
                Message = (string)result["message"],
                CurrentFile = (string)result["currentFile"],
                ProcessedFiles = (int?)result["processedFiles"] ?? 0,
                TotalFiles = (int?)result["totalFiles"] ?? 0,
                ErrorDetails = (string)result["errorDetails"]
            };
        }

        private void ApplyAuthHeader(HttpRequestMessage request)
        {
            if (!string.IsNullOrEmpty(_token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            }
        }

        private async Task<JObject> SendAsync(HttpRequestMessage request, bool throwOnFailure = true)
        {
            ApplyAuthHeader(request);

            var response = await _http.SendAsync(request).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            EnsureSuccess(response, text);

            var json = JObject.Parse(text);
            if (throwOnFailure && (bool?)json["isSuccess"] == false)
            {
                throw new InvalidOperationException((string)json["errorMessage"] ?? "后端返回失败但未给出原因");
            }
            return json;
        }

        /// <summary>
        /// 供不走 HttpResponseResultModel 信封的端点使用（目前只有 PLC_Project/GetList）：
        /// 只做鉴权头 + 状态码校验，把响应体原样按 JToken 解析后交给调用方，不假定根节点是对象。
        /// </summary>
        private async Task<JToken> SendForRawJsonAsync(HttpRequestMessage request)
        {
            ApplyAuthHeader(request);

            var response = await _http.SendAsync(request).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            EnsureSuccess(response, text);

            return JToken.Parse(text);
        }

        /// <summary>
        /// response.EnsureSuccessStatusCode() 只给出状态码本身（"Response status code does not
        /// indicate success: 413"），把已经读到手的响应体文本原样扔掉——而后端在 400/413 等失败
        /// 响应里经常带着真正有用的原因（校验失败详情、请求体过大提示等）。这里在失败时把 text
        /// 带进异常消息，成功时行为与 EnsureSuccessStatusCode 完全一致。
        /// </summary>
        private static void EnsureSuccess(HttpResponseMessage response, string text)
        {
            if (response.IsSuccessStatusCode)
            {
                return;
            }
            throw new InvalidOperationException(
                $"后端返回失败状态码 {(int)response.StatusCode} {response.ReasonPhrase}：{text}");
        }
    }
}
