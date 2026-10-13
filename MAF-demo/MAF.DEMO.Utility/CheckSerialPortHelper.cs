using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;

namespace MAF.DEMO.Utility
{
    public static class CheckSerialPortHelper
    {
        /// <summary>
        /// 检查串口是否存在
        /// </summary>
        /// <param name="checkPorts"></param>
        /// <returns></returns>
        public static List<string> CheckSerialPort(List<string> checkPorts)
        {
            //获取设备目前串口
            string[] serialPortList = SerialPort.GetPortNames();
            var existsPorts = serialPortList.ToList();
            //获取所需串口与现有串口的差值部分
            var unExistsPorts = checkPorts.Where(t => !existsPorts.Exists(z => z.Equals(t))).ToList();
            return unExistsPorts;
        }
     
    }
}
