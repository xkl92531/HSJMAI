using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;

namespace MAF.DEMO.Utility
{
    /// <summary>
    /// 异步Socket TCP事件參数类
    /// </summary>
    public class AsyncSocketEventArgs : EventArgs
    {
        /// <summary>
        /// 提示信息
        /// </summary>
        public string _msg;

        /// <summary>
        /// client状态封装类
        /// </summary>
        public AsyncSocketState _state;

        /// <summary>
        /// 是否已经处理过了
        /// </summary>
        public bool IsHandled { get; set; }

        public AsyncSocketEventArgs(string msg)
        {
            this._msg = msg;
            IsHandled = false;
        }
        public AsyncSocketEventArgs(AsyncSocketState state)
        {
            this._state = state;
            IsHandled = false;
        }
        public AsyncSocketEventArgs(string msg, AsyncSocketState state)
        {
            this._msg = msg;
            this._state = state;
            IsHandled = false;
        }
    }
}

 

 
    /// <summary>
    /// 异步SOCKET TCP 中用来存储客户端状态信息的类
    /// </summary>
    public class AsyncSocketState
    {
        #region 字段
        /// <summary>
        /// 接收数据缓冲区
        /// </summary>
        private byte[] recvBuffer;

        /// <summary>
        /// 客户端发送到server的报文
        /// 注意:在有些情况下报文可能仅仅是报文的片断而不完整
        /// </summary>
        private string datagram;

        /// <summary>
        /// 客户端的Socket
        /// </summary>
        private Socket clientSock;

        #endregion

        #region 属性

        /// <summary>
        /// 接收数据缓冲区 
        /// </summary>
        public byte[] RecvDataBuffer
        {
            get
            {
                return recvBuffer;
            }
            set
            {
                recvBuffer = value;
            }
        }

        /// <summary>
        /// 存取会话的报文
        /// </summary>
        public string Datagram
        {
            get
            {
                return datagram;
            }
            set
            {
                datagram = value;
            }
        }

        /// <summary>
        /// 获得与客户端会话关联的Socket对象
        /// </summary>
        public Socket ClientSocket
        {
            get
            {
                return clientSock;

            }
        }


        #endregion

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="cliSock">会话使用的Socket连接</param>
        public AsyncSocketState(Socket cliSock)
        {
            clientSock = cliSock;
        }

        /// <summary>
        /// 初始化数据缓冲区
        /// </summary>
        public void InitBuffer()
        {
            if (recvBuffer == null && clientSock != null)
            {
                recvBuffer = new byte[clientSock.ReceiveBufferSize];
            }
        }

        /// <summary>
        /// 关闭会话
        /// </summary>
        public void Close()
        {

            //关闭数据的接受和发送
            clientSock.Shutdown(SocketShutdown.Both);

            //清理资源
            clientSock.Close();
        }
    }

