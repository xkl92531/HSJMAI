using MAF.DEMO.Utility.LoggerHelper;
using Microsoft.AspNetCore.Hosting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MAF.DEMO.Utility
{
    public static class WriteLogHelper
    {



        /// <summary>
        /// 写日志
        /// </summary>
        /// <param name="logStr"></param>
        public static async Task WriteLogsAsync(string logStr, string fileDirectory = "")
        {

            MyLogger.Logger.Information("{" + MyLogger.LogFilePath + "}_" + logStr, fileDirectory);

            //try
            //{
            //    string path1 = Environment.CurrentDirectory + Path.Combine("/logs/") + fileDirectory;
            //    if (!Directory.Exists(path1))
            //    {
            //        //创建索引目录
            //        Directory.CreateDirectory(path1);
            //    }
            //    string path = path1 + "/" + DateTime.Now.ToString("yyyy-MM-dd") + ".log";
            //    FileStream stream = null;
            //    if (!File.Exists(path))
            //    {
            //        stream = new FileStream(path, FileMode.Create);
            //    }
            //    else
            //    {
            //        stream = new FileStream(path, FileMode.Append);
            //    }
            //    StreamWriter writer = new StreamWriter(stream);
            //    await writer.WriteAsync(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + ":  " + logStr + Environment.NewLine);
            //    writer.Write(Environment.NewLine);
            //    writer.Flush();
            //    writer.Close();
            //    stream.Close();
            //}
            //catch
            //{
            //}
            //finally
            //{

            //}
        }
    }
}
