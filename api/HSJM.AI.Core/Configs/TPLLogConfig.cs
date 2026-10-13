using System;
using System.Collections.Generic;
using System.Text;

namespace HSJM.AI.Core
{

    /// <summary>
    /// 并行任务库记录日志配置
    /// </summary>
    public static class TPLLogConfig
    {


        /// <summary>
        /// 处理信息缓存块Size
        /// </summary>
        public static int HandleDataSize { set; get; } = 1;
        /// <summary>
        /// 工作流多线程数量
        /// </summary>
        public static int MaxDegreeOfParallelism { set; get; } = 1;

        /// <summary>
        /// 定时处理Block间隔 ms
        /// </summary>
        public static int HandleInterval { set; get; } = 5000;

        /// <summary>
        /// 超时需处理时间间隔 ms
        /// </summary>
        public static int HandleTimeOut { set; get; } = 2000;

    }
}
