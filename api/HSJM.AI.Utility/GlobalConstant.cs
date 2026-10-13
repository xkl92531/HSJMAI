namespace HSJM.AI.Utility
{
    /// <summary>
    /// 全局常量设置
    /// </summary>
    public static class GlobalConstant
    {
        /// <summary>
        /// 一周天数
        /// </summary>
        public const int DayOfOneWeek = 7;

        /// <summary>
        /// 客户端名称
        /// </summary>
        public const string HttpClientName = "Api";

        /// <summary>
        /// http 请求错误 重试次数
        /// </summary>
        public const int HttpRequestRetryTimes = 2;

        /// <summary>
        /// Polly HTTP 请求错误 重试间隔
        /// </summary>
        public const int PollyRetryIntervalMilliseconds = 500;
    }
}
