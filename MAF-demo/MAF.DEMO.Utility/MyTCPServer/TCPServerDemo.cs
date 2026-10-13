using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace MAF.DEMO.Utility.MyTCPServer
{
    internal class TCPServerDemo
    {
        AsyncSocketTCPServer<string> listen;
        public TCPServerDemo()
        {
            listen = new AsyncSocketTCPServer<string>("", 1000, HandleReceiveData);

        }

        /// <summary>
        /// 启动socket监听，异步的
        /// </summary>
        public async Task StartAsync()
        {
            listen.Start();
            listen.ClientConnectedEvent += new EventHandler<AsyncSocketEventArgs>(ClientConnected);
            listen.ClientDisconnectedEvent += new EventHandler<AsyncSocketEventArgs>(ClientDisconnected);
           // WatcherAsync();
        }


        /// <summary>
        /// 触发链接事件
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        static void ClientConnected(object sender, AsyncSocketEventArgs e)
        {
            Console.WriteLine("连接成功");
        }
        /// <summary>
        /// 断开链接事件
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        static void ClientDisconnected(object sender, AsyncSocketEventArgs e)
        {
            Console.WriteLine("连接断开");
        }

        /// <summary>
        /// 处理接收到的数据
        /// </summary>
        /// <param name="client"></param>
        /// <param name="byteArray"></param>
        /// <returns></returns>
        string HandleReceiveData(Socket client, byte[] byteArray)
        {
            string result = ASCIIEncoding.UTF8.GetString(byteArray);
            return "";
        }
    }
}
