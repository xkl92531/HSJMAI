using System;
using System.Collections.Generic;
using System.Text;

namespace MAF.DEMO.Utility
{
    public class LogModel
    {
        /// <summary>
        ///  日志类型
        /// </summary>
        public string LogType { get; set; }
        /// <summary>
        /// 日志信息
        /// </summary>
        public string LogMessage { get; set; }



        public MyLogEventLevelEnum LogEventLevel { get; set; } = MyLogEventLevelEnum.Information;

    }

    public enum MyLogEventLevelEnum
    {
        Verbose = 0,
        Debug = 1,
        Information = 2,
        Warning = 3,
        Error = 4,
        Fatal = 5
    }
}
