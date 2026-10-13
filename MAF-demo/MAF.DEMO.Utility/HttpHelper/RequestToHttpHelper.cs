using MAF.DEMO.Core;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http.Json;
using System.Text;

namespace MAF.DEMO.Utility
{
    /// <summary>
    /// 请求Http帮助类
    /// </summary>
    public class RequestToHttpHelper
    {
        private const int TimeOut = 30 * 1000;//毫秒
        private readonly IHttpClientFactory httpClientFactory;

        public RequestToHttpHelper(IHttpClientFactory httpClientFactory)
        {
            this.httpClientFactory = httpClientFactory;
        }

        private HttpClient CreateClient()
        {
            return httpClientFactory.CreateClient();
        }

        /// <summary>
        /// Get类型请求
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="requestModel"></param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<T>> GetAsync<T>(HttpRequestModel requestModel)
        {
            var client = CreateClient();
            client.BaseAddress = new Uri($"{requestModel.Host}");
            AddAuthorizationHeader(client, requestModel.Token, requestModel.TokenType);
            var response = await client.GetAsync(requestModel.Path).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var isSuccessStatusCode = response.IsSuccessStatusCode;
            var statusCode = response.StatusCode;
            if (isSuccessStatusCode)
            {
                return new HttpResponseResultModel<T>
                {
                    HttpStatusCode = statusCode,
                    IsSuccess = isSuccessStatusCode,
                    BackResult = JsonHelper.DeserializeObject<T>(content)
                };
            }
            else
            {
                return new HttpResponseResultModel<T>
                {
                    HttpStatusCode = statusCode,
                    IsSuccess = isSuccessStatusCode,
                    ErrorMessage = content
                };
            }
        }


        /// <summary>
        /// Post类型请求
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="requestModel"></param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<T>> PostAsync<T>(HttpRequestModel requestModel)
        {
            var client = CreateClient();
            client.BaseAddress = new Uri($"{requestModel.Host}");
            AddAuthorizationHeader(client, requestModel.Token, requestModel.TokenType);
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, requestModel.Path);
            string json = JsonHelper.SerializeObject(requestModel.Data);
            request.Content = new StringContent(json,Encoding.UTF8, requestModel.ContentType);
            HttpResponseMessage response = await client.SendAsync(request).ConfigureAwait(false);
            var statusCode = response.StatusCode;
            var isSuccessStatusCode = response.IsSuccessStatusCode;
            var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (isSuccessStatusCode)
            {
                return new HttpResponseResultModel<T>
                {
                    HttpStatusCode = statusCode,
                    IsSuccess = isSuccessStatusCode,
                    BackResult = JsonHelper.DeserializeObject<T>(content)
                };
            }
            else
            {
                return new HttpResponseResultModel<T>
                {
                    HttpStatusCode = statusCode,
                    IsSuccess = isSuccessStatusCode,
                    ErrorMessage = content
                };
            }
        }

        /// <summary>
        /// Post类型的请求(非json)
        /// application/x-www-form-urlencoded 类型
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="requestModel"></param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<T>> PostAsFormUrlEncodedAsync<T>(HttpRequestModel requestModel)
        {
            var client = CreateClient();
            client.BaseAddress = new Uri($"{requestModel.Host}");
            AddAuthorizationHeader(client, requestModel.Token, requestModel.TokenType);
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, requestModel.Path);
            // DATA 如下request
            //List<KeyValuePair<string, string>> request = new List<KeyValuePair<string, string>>();//构建
            //request.Add(new KeyValuePair<string, string>("AutoCommit", "false"));
            //request.Add(new KeyValuePair<string, string>("ConsumerId", consumerId));
            if (requestModel.Data != null)
            {
                request.Content = new FormUrlEncodedContent(requestModel.KeyValuePairData);
            }
            HttpResponseMessage response = await client.SendAsync(request).ConfigureAwait(false);
            var statusCode = response.StatusCode;
            var isSuccessStatusCode = response.IsSuccessStatusCode;
            var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (isSuccessStatusCode)
            {
                return new HttpResponseResultModel<T>
                {
                    HttpStatusCode = statusCode,
                    IsSuccess = isSuccessStatusCode,
                    BackResult = JsonHelper.DeserializeObject<T>(content)
                };
            }
            else
            {
                return new HttpResponseResultModel<T>
                {
                    HttpStatusCode = statusCode,
                    IsSuccess = isSuccessStatusCode,
                    ErrorMessage = content
                };
            }
        }

        /// <summary>
        /// Put类型请求
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="requestModel"></param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<T>> PutAsync<T>(HttpRequestModel requestModel)
        {
            var client = CreateClient();
            client.BaseAddress = new Uri($"{requestModel.Host}");
            AddAuthorizationHeader(client, requestModel.Token, requestModel.TokenType);
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Put, requestModel.Path);
            string json = JsonHelper.SerializeObject(requestModel.Data);
            request.Content = new StringContent(json, Encoding.UTF8, requestModel.ContentType);
            HttpResponseMessage response = await client.SendAsync(request).ConfigureAwait(false);
            var statusCode = response.StatusCode;
            var isSuccessStatusCode = response.IsSuccessStatusCode;
            var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (isSuccessStatusCode)
            {
                return new HttpResponseResultModel<T>
                {
                    HttpStatusCode = statusCode,
                    IsSuccess = isSuccessStatusCode,
                    BackResult = JsonHelper.DeserializeObject<T>(content)
                };
            }
            else
            {
                return new HttpResponseResultModel<T>
                {
                    HttpStatusCode = statusCode,
                    IsSuccess = isSuccessStatusCode,
                    ErrorMessage = content
                };
            }
        }


        /// <summary>
        /// Delete类型请求
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="requestModel"></param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<T>> DeleteAsync<T>(HttpRequestModel requestModel)
        {
            var client = CreateClient();
            client.BaseAddress = new Uri($"{requestModel.Host}");
            AddAuthorizationHeader(client, requestModel.Token, requestModel.TokenType);
            var response = await client.DeleteAsync(requestModel.Path).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var statusCode = response.StatusCode;
            var isSuccessStatusCode = response.IsSuccessStatusCode;
            if (isSuccessStatusCode)
            {
                return new HttpResponseResultModel<T>
                {
                    HttpStatusCode = statusCode,
                    IsSuccess = isSuccessStatusCode,
                    BackResult = JsonHelper.DeserializeObject<T>(content)
                };
            }
            else
            {
                return new HttpResponseResultModel<T>
                {
                    HttpStatusCode = statusCode,
                    IsSuccess = isSuccessStatusCode,
                    ErrorMessage = content
                };
            }
        }


        private void AddAuthorizationHeader(HttpClient client, string token, string tokenType)
        {
            if (!string.IsNullOrEmpty(token))
            {
                //tokenType 后面空格不能少
                client.DefaultRequestHeaders.Add("Authorization", $"{tokenType} " + token);
            }

        }
        /// <summary>
        /// Post类型请求
        /// </summary>
        /// <param name="host">服务host地址，如：192.168.1.1</param>
        /// <param name="path">请求的api地址</param>
        /// <param name="data">发送的数据</param>
        /// <returns>返回json数据</returns>
        public async Task<HttpResponseResultModel<T>> PostAsync<T>(string host, string path, object data)
        {
            var client = CreateClient();
            client.BaseAddress = new Uri($"{host}/");
            AddCustomHeader(client);
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, path);
            string json = JsonHelper.SerializeObject(data);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            var cancelSource = new CancellationTokenSource(TimeOut);
            try
            {
                var responseTask = client.SendAsync(request, cancelSource.Token);

                var response = await responseTask.ConfigureAwait(false);
                // HttpResponseMessage response = await client.SendAsync(request).ConfigureAwait(false);
                var statusCode = response.StatusCode;
                var isSuccessStatusCode = response.IsSuccessStatusCode;
                var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (isSuccessStatusCode)
                {
                    return new HttpResponseResultModel<T>
                    {
                        HttpStatusCode = statusCode,
                        IsSuccess = isSuccessStatusCode,
                        BackResult = JsonHelper.DeserializeObject<T>(content)
                    };
                }
                else
                {
                    return new HttpResponseResultModel<T>
                    {
                        HttpStatusCode = statusCode,
                        IsSuccess = isSuccessStatusCode,
                        ErrorMessage = content
                    };
                }
            }
            finally
            {
                cancelSource.Dispose();
            }
        }
        private void AddCustomHeader(HttpClient client)
        {
            //var csHeader = Generator.GetCustomHeaderAsString();
            //if (csHeader != null)
            //{
            //    client.DefaultRequestHeaders.Add("Custom", csHeader);
            //}
        }
    }
}
