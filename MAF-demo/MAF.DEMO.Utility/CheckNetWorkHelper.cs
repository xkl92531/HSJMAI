using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading.Tasks;

namespace MAF.DEMO.Utility
{
    public static class CheckNetWorkHelper
    {

        public static List<string> CheckNetWork(List<string> ips)
        {
            List<string> res = new List<string>();
            foreach (string ip in ips)
            {
                bool connected = Ping(ip);
                if (!connected)
                {
                    res.Add(ip);
                }
            }
            return res;
        }

        private static bool Ping(string ip)
        {
            Ping ping = new Ping();
            PingReply pingReply = ping.Send(ip, 500);
            if (pingReply.Status == IPStatus.Success)
            {
                return true;
            }
            return false;
        }
    }
}
