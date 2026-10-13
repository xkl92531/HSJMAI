using System;
using System.Collections.Generic;
using System.Text;

namespace MAF.DEMO.Utility.LoggerHelper
{
    public class TPLLogOptions
    {


        /// <summary>
        /// 处理信息缓存块Size
        /// </summary>
        public int HandleDataSize { set; get; } = 1;
        /// <summary>
        /// 工作流多线程数量
        /// </summary>
        public int MaxDegreeOfParallelism { set; get; } = 1;

        /// <summary>
        /// 定时处理Block间隔 ms
        /// </summary>
        public int HandleInterval { set; get; } = 5000;

        /// <summary>
        /// 超时需处理时间间隔 ms
        /// </summary>
        public int HandleTimeOut { set; get; } = 2000;

    }
}
