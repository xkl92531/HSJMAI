using MAF.DEMO.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MAF.DEMO.Utility
{
    public static class InitConfigHelper
    {
        public static string InitConfigJson(string configPath)
        {
            string json = string.Empty;
            using (FileStream fs = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                using (StreamReader sr = new StreamReader(fs))
                {
                    json = sr.ReadToEnd().ToString();
                }
            }
            //if (!string.IsNullOrEmpty(json))
            //{
            //    json = json.Replace("common.", "");
            //    json = json.Replace("servermain.", "");
            //}
            return json;
        }
    }
}
