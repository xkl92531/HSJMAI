using SuperSocket.ClientEngine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HSJM.AI.Utility
{
    /// <summary>
    /// socketHelper
    /// </summary>
    public class MySuperSocketClientHelper
    {
        private AsyncTcpSession client;
        string data;
        Action<string, byte[]> handleDataAction;
        string ip = "";
        int port = 0;
        private Action<string> connectedOKAction;
        private Action<string> connectedErrorAction;
        /// <summary>
        /// 
        /// </summary>
        /// <param name="ip">服务器IP</param>
        /// <param name="port">服务器端口</param>
        public MySuperSocketClientHelper(string ip, int port, Action<string, byte[]> handleDataAction, Action<string> connectedOKAction=null, Action<string> connectedErrorAction=null)
        {
            this.ip = ip;
            this.port = port;
            this.handleDataAction = handleDataAction;
            this.connectedOKAction = connectedOKAction;
            this.connectedErrorAction = connectedErrorAction;
            client = new AsyncTcpSession();
            // 连接断开事件
            client.Closed += Client_Closed;
            
            // 收到服务器数据事件
            client.DataReceived += Client_DataReceived;
            // 连接到服务器事件
            client.Connected += Client_Connected;
            // 发生错误的处理
            client.Error += Client_Error;
        }
        void Client_Error(object sender, SuperSocket.ClientEngine.ErrorEventArgs e)
        {
            connectedErrorAction?.Invoke($"{DateTime.Now.ToString()}:连接异常，尝试重连");
            ReConnect();
            // Console.WriteLine(e.Exception.Message);
        }

        public void Close()
        {
            client.Close();
        }

        void ReConnect()
        {
            Thread.Sleep(100);
            this.Connect();
            //connectedOKAction?.Invoke("重新连接成功");
        }

        void Client_Connected(object sender, EventArgs e)
        {
            connectedOKAction?.Invoke($"{DateTime.Now.ToString()}:连接成功");
            // Console.WriteLine("连接成功");
        }

        public string GetData()
        {
            string currentData = data;

            return currentData;
        }


        void Client_DataReceived(object sender, DataEventArgs e)
        {
            var arr = e.Data.Take(e.Length).ToArray();
            string msg = Encoding.Default.GetString(arr);
            data = msg;
            handleDataAction?.Invoke(msg, arr);
            //Console.WriteLine(msg);
        }

        void Client_Closed(object sender, EventArgs e)
        {
            connectedErrorAction?.Invoke($"{DateTime.Now.ToString()}:连接断开");
            //Console.WriteLine("连接断开");
            ReConnect();
        }

        /// <summary>
        /// 连接到服务器
        /// </summary>
        public void Connect()
        {
            if (client.IsConnected == false)
                client.Connect(new IPEndPoint(IPAddress.Parse(ip), port));
        }

        public void RefreshAction(Action<string, byte[]> action)
        {
            client.DataReceived += null;
            this.handleDataAction = action;
            client.DataReceived += Client_DataReceived;

        }


        /// <summary>
        /// 向服务器发命令行协议的数据
        /// </summary>
        /// <param name="key">命令名称</param>
        /// <param name="data">数据</param>
        public void SendCommand(string data)
        {
            if (client.IsConnected)
            {
                byte[] arr = Encoding.Default.GetBytes(data);
                client.Send(arr, 0, arr.Length);
            }
            else
            {
                this.ReConnect();
                this.SendCommand(data);
                //  throw new InvalidOperationException("未建立连接");
            }
        }

    }
}
