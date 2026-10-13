using System.Net;

namespace MAF.DEMO.Core
{

    /// <summary>
    ///  http请求结果类
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class HttpResponseResultModel<T>
    {
        /// <summary>
        /// http码
        /// </summary>
        public HttpStatusCode HttpStatusCode { get; set; } = HttpStatusCode.OK;

        /// <summary>
        /// 是否成功
        /// </summary>
        public bool IsSuccess { get; set; }

        /// <summary>
        /// 返回结果
        /// </summary>
        public T BackResult { get; set; }

        /// <summary>
        /// 错误信息
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// 异常信息
        /// </summary>
        public string ExceptionMessage { get; set; }
    }
}
