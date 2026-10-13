using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HsjmTia.Core.Upload;
using Xunit;

namespace HsjmTia.Core.Tests.Upload
{
    internal class StubHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests = new List<HttpRequestMessage>();

        // HttpClient disposes each HttpRequestMessage (and its Content) once SendAsync's
        // returned task completes, so request.Content is unreadable by the time a test's
        // assertions run afterward. Capture the body text here, while the content is still
        // alive, so assertions can inspect it without racing that disposal.
        public List<string> RequestBodies = new List<string>();

        // Same reasoning as RequestBodies, but raw bytes: I3 switches the upload file part to
        // StreamContent, and the uploaded bytes aren't guaranteed to be valid UTF-8, so a string
        // capture alone can silently mangle them. HttpContent buffers on the first Read call
        // (LoadIntoBufferAsync), so reading both string and bytes here is safe — the second read
        // reuses the buffer rather than re-consuming the underlying FileStream.
        public List<byte[]> RequestBytes = new List<byte[]>();

        public Func<HttpRequestMessage, HttpResponseMessage> Responder;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            RequestBodies.Add(request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync().ConfigureAwait(false));
            RequestBytes.Add(request.Content == null
                ? null
                : await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
            return Responder(request);
        }

        public static HttpResponseMessage Json(string body)
            => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };

        public static HttpResponseMessage Status(HttpStatusCode code, string body)
            => new HttpResponseMessage(code)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };
    }

    public class BackendClientTests
    {
        private static BackendClient Make(StubHandler handler)
            => new BackendClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:2405/") });

        [Fact]
        public async Task LoginAsync_SendsTheAccountField_BecauseTheBackendBindsLoginInfoViewAccount()
        {
            // 后端 LoginInfoView 的字段叫 Account；发 userName 会绑成 null 而登录失败，
            // 而桩测试不会暴露这一点 —— 所以把字段名钉死在这里。
            var handler = new StubHandler
            {
                Responder = _ => StubHandler.Json("{\"isSuccess\":true,\"backResult\":{\"token\":\"abc\"}}")
            };

            await Make(handler).LoginAsync("someone", "secret");

            var body = handler.RequestBodies[0];
            Assert.Contains("\"account\"", body);
            Assert.DoesNotContain("userName", body);
            Assert.Contains("someone", body);
        }

        [Fact]
        public async Task LoginAsync_ReturnsTheTokenFromBackResult()
        {
            var handler = new StubHandler
            {
                Responder = _ => StubHandler.Json("{\"isSuccess\":true,\"backResult\":{\"token\":\"abc\"}}")
            };
            Assert.Equal("abc", await Make(handler).LoginAsync("u", "p"));
        }

        [Fact]
        public async Task GetProjectsAsync_SendsTheBearerTokenFromLogin()
        {
            // PLC_Project/GetList 返回裸数组，不是信封 —— 见下面几个测试的说明。
            var handler = new StubHandler();
            handler.Responder = req => req.RequestUri.AbsolutePath.EndsWith("Login")
                ? StubHandler.Json("{\"isSuccess\":true,\"backResult\":{\"token\":\"abc\"}}")
                : StubHandler.Json("[]");

            var client = Make(handler);
            await client.LoginAsync("u", "p");
            await client.GetProjectsAsync();

            Assert.Equal("abc", handler.Requests[1].Headers.Authorization.Parameter);
            Assert.Equal("Bearer", handler.Requests[1].Headers.Authorization.Scheme);
        }

        [Fact]
        public async Task GetProjectsAsync_IssuesAGetRequest_BecauseTheControllerIsHttpGetFromQuery()
        {
            // PLC_ProjectController.GetList 是 [HttpGet]+[FromQuery]，不是 POST
            // （api/HSJM.AI/Controllers/PLC/PLC_Project/PLC_ProjectController.cs:189-197）。
            var handler = new StubHandler
            {
                Responder = _ => StubHandler.Json("[]")
            };

            await Make(handler).GetProjectsAsync();

            Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        }

        [Fact]
        public async Task GetProjectsAsync_ParsesABareRootArray_NotAnEnvelope()
        {
            // controller 直接 return Ok(list)，没有 HttpResponseResultModel 信封，
            // 所以根节点本身就是数组，不是 { backResult: [...] }。
            var handler = new StubHandler
            {
                Responder = _ => StubHandler.Json(
                    "[{\"id\":7,\"projectName\":\"泰兴PLC1\",\"plcIp\":\"192.168.6.31\",\"plcCode\":\"PLC1\"}]")
            };

            var list = await Make(handler).GetProjectsAsync();
            Assert.Single(list);
            Assert.Equal(7, list[0].Id);
            Assert.Equal("泰兴PLC1", list[0].ProjectName);
            Assert.Equal("192.168.6.31", list[0].PlcIp);
        }

        [Fact]
        public async Task GetProjectsAsync_ParsesAStringSnowflakeId()
        {
            // LongSnowflakeSafeJsonConverter 把 long 雪花主键序列化成 JSON 字符串，不是数字
            // （api/HSJM.AI/Program.cs:118-125）。
            var handler = new StubHandler
            {
                Responder = _ => StubHandler.Json(
                    "[{\"id\":\"4541719068092272640\",\"projectName\":\"x\",\"plcIp\":\"1.1.1.1\",\"plcCode\":\"c\"}]")
            };

            var list = await Make(handler).GetProjectsAsync();
            Assert.Equal(4541719068092272640L, list[0].Id);
        }

        [Fact]
        public async Task GetProjectsAsync_ThrowsWithTheBackendMessage_WhenTheResponseIsAnIpFilterRejectionObject()
        {
            // 这个路由被 IP 过滤拒绝、或后端抛未处理异常时，仍然返回 HTTP 200 —— 但根节点是
            // 一个对象（isSuccess:false + errorMessage），不是数组。旧实现 `json as JArray ?? new JArray()`
            // 把这种情况悄悄读成"后端没有项目"，调用方完全看不出请求其实被拒绝了。
            var handler = new StubHandler
            {
                Responder = _ => StubHandler.Json("{\"isSuccess\":false,\"errorMessage\":\"请求来源非法\"}")
            };

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Make(handler).GetProjectsAsync());
            Assert.Contains("请求来源非法", ex.Message);
        }

        [Fact]
        public async Task GetProjectsAsync_ThrowsForOtherNonArrayJson()
        {
            var handler = new StubHandler
            {
                Responder = _ => StubHandler.Json("{\"unexpected\":true}")
            };

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Make(handler).GetProjectsAsync());
            Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        }

        [Fact]
        public async Task GetProjectsAsync_ThrowsWithTheResponseBody_OnNonSuccessStatus()
        {
            // 旧实现在 response.EnsureSuccessStatusCode() 之前就把 text 读出来又原样扔掉，
            // 工程师只能看到 "Response status code does not indicate success: 500"，看不到后端
            // 实际给出的原因。
            var handler = new StubHandler
            {
                Responder = _ => StubHandler.Status(HttpStatusCode.InternalServerError, "后端内部错误详情 boom")
            };

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Make(handler).GetProjectsAsync());
            Assert.Contains("后端内部错误详情 boom", ex.Message);
        }

        [Fact]
        public async Task StartUploadAsync_ThrowsWithTheResponseBody_OnNonSuccessStatus()
        {
            // 413（请求体超过后端限制）等失败响应，后端会给出有用的 body；
            // 旧实现同样把 text 读出来又扔掉，只留下状态码本身。
            var zip = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".zip");
            File.WriteAllBytes(zip, new byte[] { 1, 2, 3 });
            try
            {
                var handler = new StubHandler
                {
                    Responder = _ => StubHandler.Status(HttpStatusCode.RequestEntityTooLarge,
                        "{\"isSuccess\":false,\"errorMessage\":\"请求体过大\"}")
                };

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => Make(handler).StartUploadAsync(zip, 7, false));
                Assert.Contains("请求体过大", ex.Message);
            }
            finally
            {
                File.Delete(zip);
            }
        }

        [Fact]
        public async Task StartUploadAsync_SendsTheFilesBytesIntactInTheMultipartBody()
        {
            // I3：确保切换到 StreamContent 之后功能依旧正确——multipart 请求体里仍然带着
            // 完整、未损坏的文件字节，而不是"编译通过但内容丢了/损坏了"。
            //
            // 注意这条测试本身证明不了"流式而非整体缓冲"——桩 HttpMessageHandler 在断言前
            // 已经把整个请求体读进内存了，换成 ByteArrayContent 也会通过。它是一条有价值的
            // 完整性测试（字节没有在传输链路上被损坏），但名字不该声称验证了流式传输本身。
            var zip = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".zip");
            var payload = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0xAA, 0xBB, 0x00, 0xFF, 0x10, 0x20 };
            File.WriteAllBytes(zip, payload);
            try
            {
                var handler = new StubHandler
                {
                    Responder = _ => StubHandler.Json("{\"isSuccess\":true,\"backResult\":{\"taskId\":\"t-1\"}}")
                };

                await Make(handler).StartUploadAsync(zip, 7, false);

                var sentBytes = handler.RequestBytes[0];
                Assert.Contains(ChunkedSubsequences(sentBytes, payload.Length), c => c.SequenceEqual(payload));
            }
            finally
            {
                File.Delete(zip);
            }
        }

        private static IEnumerable<byte[]> ChunkedSubsequences(byte[] haystack, int length)
        {
            for (var i = 0; i + length <= haystack.Length; i++)
            {
                var slice = new byte[length];
                Array.Copy(haystack, i, slice, 0, length);
                yield return slice;
            }
        }

        [Fact]
        public async Task StartUploadAsync_PostsMultipartWithFileAndProjectId()
        {
            var zip = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".zip");
            File.WriteAllBytes(zip, new byte[] { 1, 2, 3 });
            try
            {
                var handler = new StubHandler
                {
                    Responder = _ => StubHandler.Json("{\"isSuccess\":true,\"backResult\":{\"taskId\":\"t-1\"}}")
                };

                var taskId = await Make(handler).StartUploadAsync(zip, 7, false);

                Assert.Equal("t-1", taskId);
                Assert.IsType<MultipartFormDataContent>(handler.Requests[0].Content);
                var body = handler.RequestBodies[0];
                Assert.Contains("projectId", body);
                Assert.Contains("fullProjectUpdate", body);
            }
            finally
            {
                File.Delete(zip);
            }
        }

        [Fact]
        public async Task GetProgressAsync_ReturnsNull_WhenTheTaskIsUnknown()
        {
            var handler = new StubHandler
            {
                Responder = _ => StubHandler.Json("{\"isSuccess\":false,\"errorMessage\":\"任务不存在或已过期\"}")
            };
            Assert.Null(await Make(handler).GetProgressAsync("nope"));
        }

        [Fact]
        public async Task GetProgressAsync_ThrowsWithTheBackendMessage_OnATransientError()
        {
            // 旧实现把每个 isSuccess:false 都读成 null（"任务不存在或已过期"），
            // 一次瞬时后端错误（数据库连不上之类）会被无声当成"任务查不到"，
            // 轮询方看不出这是可重试的故障还是任务真的已经过期/不存在。
            var handler = new StubHandler
            {
                Responder = _ => StubHandler.Json("{\"isSuccess\":false,\"errorMessage\":\"数据库连接失败\"}")
            };
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Make(handler).GetProgressAsync("t-1"));
            Assert.Contains("数据库连接失败", ex.Message);
        }

        [Fact]
        public async Task GetProgressAsync_MapsStatus3ToCompleted()
        {
            // UploadStatus.Completed = 3；后端未注册 StringEnumConverter，枚举按整数序列化
            // （api/HSJM.AI/Program.cs:118-125），旧实现读的是不存在的 isCompleted 字段。
            var handler = new StubHandler
            {
                Responder = _ => StubHandler.Json(
                    "{\"isSuccess\":true,\"backResult\":{\"taskId\":\"t-1\",\"status\":3,\"percentage\":100,\"message\":\"done\"}}")
            };

            var progress = await Make(handler).GetProgressAsync("t-1");

            Assert.Equal(UploadStatus.Completed, progress.Status);
            Assert.True(progress.IsCompleted);
        }

        [Fact]
        public async Task GetProgressAsync_MapsStatus1ToNotCompleted()
        {
            var handler = new StubHandler
            {
                Responder = _ => StubHandler.Json(
                    "{\"isSuccess\":true,\"backResult\":{\"taskId\":\"t-1\",\"status\":1,\"percentage\":40,\"message\":\"processing\"}}")
            };

            var progress = await Make(handler).GetProgressAsync("t-1");

            Assert.Equal(UploadStatus.Processing, progress.Status);
            Assert.False(progress.IsCompleted);
        }

        [Fact]
        public async Task GetProgressAsync_ReadsPercentage_NotPercent()
        {
            // UploadProgress 真实字段是 percentage，不是 percent
            // （api/HSJM.AI.AppLayer/PLC/PLC_Upload/PLC_UploadProgressService.cs）。
            var handler = new StubHandler
            {
                Responder = _ => StubHandler.Json(
                    "{\"isSuccess\":true,\"backResult\":{\"taskId\":\"t-1\",\"status\":1,\"percentage\":57,\"message\":\"x\"}}")
            };

            var progress = await Make(handler).GetProgressAsync("t-1");

            Assert.Equal(57, progress.Percentage);
        }

        [Fact]
        public async Task LoginAsync_ThrowsWithTheBackendMessage_OnFailure()
        {
            var handler = new StubHandler
            {
                Responder = _ => StubHandler.Json("{\"isSuccess\":false,\"errorMessage\":\"用户名或密码错误\"}")
            };
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Make(handler).LoginAsync("u", "p"));
            Assert.Contains("用户名或密码错误", ex.Message);
        }
    }
}
