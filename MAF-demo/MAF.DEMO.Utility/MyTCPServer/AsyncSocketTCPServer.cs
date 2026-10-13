
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Timers;

namespace MAF.DEMO.Utility
{
    /// <summary>
    /// Socket实现的异步TCP服务器
    /// </summary>
    public class AsyncSocketTCPServer<T> : IDisposable
    {



        #region Fields
        /// <summary>
        /// 服务器程序同意的最大客户端连接数
        /// </summary>
        private int maxClient;

        /// <summary>
        /// 当前的连接的客户端数
        /// </summary>
        private int clientCount;

        /// <summary>
        /// 服务器使用的异步socket
        /// </summary>
        private Socket serverSock;

        /// <summary>
        /// 客户端会话列表
        /// </summary>
        private List<AsyncSocketState> clients;

        private bool disposed = false;

        private Func<Socket, byte[], T> func;

        #endregion

        #region Properties

        /// <summary>
        /// 服务器是否正在执行
        /// </summary>
        public bool IsRunning { get; private set; }
        /// <summary>
        /// 监听的IP地址
        /// </summary>
        public IPAddress Address { get; private set; }
        /// <summary>
        /// 监听的port
        /// </summary>
        public int Port { get; private set; }
        /// <summary>
        /// 通信使用的编码
        /// </summary>
        public Encoding Encoding { get; set; }

        public List<AsyncSocketState> Clients
        {
            get
            {
                return clients;
            }
        }
        #endregion

        #region 构造函数


        /// <summary>
        /// 构造函数
        /// </summary>
        public AsyncSocketTCPServer(string serverIP, int serverPort, Func<Socket, byte[], T> func = null)
        {

            this.func = func;
            this.Address = IPAddress.Parse(serverIP);
            this.Port = serverPort;
            this.Encoding = Encoding.UTF8;
            maxClient = 1024;
            clients = new List<AsyncSocketState>();
            serverSock = new Socket(this.Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        }

        #endregion


        #region Method

        /// <summary>
        /// 启动服务器
        /// </summary>
        public void Start()
        {
            if (!IsRunning)
            {
                IsRunning = true;
                serverSock.Bind(new IPEndPoint(this.Address, this.Port));
                serverSock.Listen(1024);
                serverSock.BeginAccept(new AsyncCallback(HandleAcceptConnected), serverSock);
            }
        }

        /// <summary>
        /// 启动服务器
        /// </summary>
        /// <param name="backlog">
        /// 服务器所同意的挂起连接序列的最大长度
        /// </param>
        public void Start(int backlog)
        {
            if (!IsRunning)
            {
                IsRunning = true;
                serverSock.Bind(new IPEndPoint(this.Address, this.Port));
                serverSock.Listen(backlog);
                serverSock.BeginAccept(new AsyncCallback(HandleAcceptConnected), serverSock);
            }
        }

        /// <summary>
        /// 停止服务器
        /// </summary>
        public void Stop()
        {
            if (IsRunning)
            {
                IsRunning = false;
                serverSock.Close();
                //TODO 关闭对全部客户端的连接
            }
        }

        /// <summary>
        /// 处理客户端连接
        /// </summary>
        /// <param name="ar"></param>
        private void HandleAcceptConnected(IAsyncResult ar)
        {
            if (IsRunning)
            {
                Socket server = (Socket)ar.AsyncState;
                Socket client = server.EndAccept(ar);

                //检查是否达到最大的同意的客户端数目
                if (clientCount >= maxClient)
                {
                    //C-TODO 触发事件
                    RaiseOtherException(null);
                }
                else
                {
                    AsyncSocketState state = new AsyncSocketState(client);
                    lock (clients)
                    {
                        clients.Add(state);
                        clientCount++;
                        RaiseClientConnected(state); //触发客户端连接事件
                    }
                    state.RecvDataBuffer = new byte[client.ReceiveBufferSize];
                    //開始接受来自该客户端的数据
                    client.BeginReceive(state.RecvDataBuffer, 0, state.RecvDataBuffer.Length, SocketFlags.None,
                     new AsyncCallback(HandleDataReceived), state);
                }
                //接受下一个请求
                server.BeginAccept(new AsyncCallback(HandleAcceptConnected), ar.AsyncState);
            }
        }
        /// <summary>
        /// 处理客户端数据
        /// </summary>
        /// <param name="ar"></param>
        private void HandleDataReceived(IAsyncResult ar)
        {
            if (IsRunning)
            {
                AsyncSocketState state = (AsyncSocketState)ar.AsyncState;
                Socket client = state.ClientSocket;
                try
                {
                    //假设两次開始了异步的接收,所以当客户端退出的时候
                    //会两次执行EndReceive
                    int recv = client.EndReceive(ar);
                    if (recv == 0)
                    {
                        RaiseClientDisconnected(state.ClientSocket);
                        //C- TODO 触发事件 (关闭客户端)
                        Close(state);

                        RaiseNetError(state);
                        return;
                    }
                    //TODO 处理已经读取的数据 ps:数据在state的RecvDataBuffer中
                    if (func != null)
                    {
                        func(client, state.RecvDataBuffer.Skip(0).Take(recv).ToArray());
                    }
                    // tplHelper.SendToTPL(new UdpDataModel() { Bytes = state.RecvDataBuffer.Skip(0).Take(recv).ToArray() });
                    //C- TODO 触发数据接收事件
                    RaiseDataReceived(state);
                    //继续接收来自来客户端的数据
                    client.BeginReceive(state.RecvDataBuffer, 0, state.RecvDataBuffer.Length, SocketFlags.None,
                    new AsyncCallback(HandleDataReceived), state);
                }
                catch (SocketException)
                {
                    //C- TODO 异常处理
                    RaiseNetError(state);
                }
                finally
                {
                    ////继续接收来自来客户端的数据
                    //client.BeginReceive(state.RecvDataBuffer, 0, state.RecvDataBuffer.Length, SocketFlags.None,
                    //new AsyncCallback(HandleDataReceived), state);
                }
            }
        }

        /// <summary>
        /// 发送数据
        /// </summary>
        /// <param name="state">接收数据的客户端会话</param>
        /// <param name="data">数据报文</param>
        public void Send(AsyncSocketState state, byte[] data)
        {
            RaisePrepareSend(state);
            Send(state.ClientSocket, data);
        }

        /// <summary>
        /// 异步发送数据至指定的客户端
        /// </summary>
        /// <param name="client">客户端</param>
        /// <param name="data">报文</param>
        public void Send(Socket client, byte[] data)
        {
            if (!IsRunning)
                throw new InvalidProgramException("This TCP Scoket server has not been started.");

            if (client == null)
                throw new ArgumentNullException("client");

            if (data == null)
                throw new ArgumentNullException("data");
            client.BeginSend(data, 0, data.Length, SocketFlags.None,
             new AsyncCallback(SendDataEnd), client);
        }

        /// <summary>
        /// 发送数据完成处理函数
        /// </summary>
        /// <param name="ar">目标客户端Socket</param>
        private void SendDataEnd(IAsyncResult ar)
        {
            ((Socket)ar.AsyncState).EndSend(ar);
            RaiseCompletedSend(null);
        }
        #endregion

        #region 事件

        /// <summary>
        /// 与客户端的连接已建立事件
        /// </summary>
        public event EventHandler<AsyncSocketEventArgs> ClientConnectedEvent;
        /// <summary>
        /// 与客户端的连接已断开事件
        /// </summary>
        public event EventHandler<AsyncSocketEventArgs> ClientDisconnectedEvent;

        /// <summary>
        /// 触发客户端连接事件
        /// </summary>
        /// <param name="state"></param>
        private void RaiseClientConnected(AsyncSocketState state)
        {
            if (ClientConnectedEvent != null)
            {
                ClientConnectedEvent(this, new AsyncSocketEventArgs(state));
            }
        }
        /// <summary>
        /// 触发客户端连接断开事件
        /// </summary>
        /// <param name="client"></param>
        private void RaiseClientDisconnected(Socket client)
        {
            if (ClientDisconnectedEvent != null)
            {
                ClientDisconnectedEvent(this, new AsyncSocketEventArgs("连接断开"));
            }
        }

        /// <summary>
        /// 接收到数据事件
        /// </summary>
        public event EventHandler<AsyncSocketEventArgs> DataReceivedEvent;

        private void RaiseDataReceived(AsyncSocketState state)
        {
            if (DataReceivedEvent != null)
            {
                DataReceivedEvent(this, new AsyncSocketEventArgs(state));
            }
        }

        /// <summary>
        /// 发送数据前的事件
        /// </summary>
        public event EventHandler<AsyncSocketEventArgs> PrepareSendEvent;

        /// <summary>
        /// 触发发送数据前的事件
        /// </summary>
        /// <param name="state"></param>
        private void RaisePrepareSend(AsyncSocketState state)
        {
            if (PrepareSendEvent != null)
            {
                PrepareSendEvent(this, new AsyncSocketEventArgs(state));
            }
        }

        /// <summary>
        /// 数据发送完成事件
        /// </summary>
        public event EventHandler<AsyncSocketEventArgs> CompletedSendEvent;

        /// <summary>
        /// 触发数据发送完成的事件
        /// </summary>
        /// <param name="state"></param>
        private void RaiseCompletedSend(AsyncSocketState state)
        {
            if (CompletedSendEvent != null)
            {
                CompletedSendEvent(this, new AsyncSocketEventArgs(state));
            }
        }

        /// <summary>
        /// 网络错误事件
        /// </summary>
        public event EventHandler<AsyncSocketEventArgs> NetErrorEvent;
        /// <summary>
        /// 触发网络错误事件
        /// </summary>
        /// <param name="state"></param>
        private void RaiseNetError(AsyncSocketState state)
        {
            if (NetErrorEvent != null)
            {
                NetErrorEvent(this, new AsyncSocketEventArgs(state));
            }
        }

        /// <summary>
        /// 异常事件
        /// </summary>
        public event EventHandler<AsyncSocketEventArgs> OtherExceptionEvent;
        /// <summary>
        /// 触发异常事件
        /// </summary>
        /// <param name="state"></param>
        private void RaiseOtherException(AsyncSocketState state, string descrip)
        {
            if (OtherExceptionEvent != null)
            {
                OtherExceptionEvent(this, new AsyncSocketEventArgs(descrip, state));
            }
        }
        private void RaiseOtherException(AsyncSocketState state)
        {
            RaiseOtherException(state, "");
        }
        #endregion

        #region Close
        /// <summary>
        /// 关闭一个与客户端之间的会话
        /// </summary>
        /// <param name="state">须要关闭的客户端会话对象</param>
        public void Close(AsyncSocketState state)
        {
            if (state != null)
            {
                state.Datagram = null;
                state.RecvDataBuffer = null;

                clients.Remove(state);
                clientCount--;
                //TODO 触发关闭事件
                state.Close();
            }
        }
        /// <summary>
        /// 关闭全部的客户端会话,与全部的客户端连接会断开
        /// </summary>
        public void CloseAllClient()
        {
            foreach (AsyncSocketState client in clients)
            {
                Close(client);
            }
            clientCount = 0;
            clients.Clear();
        }
        #endregion

        #region 释放
        /// <summary>
        /// Performs application-defined tasks associated with freeing, 
        /// releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources
        /// </summary>
        /// <param name="disposing"><c>true</c> to release 
        /// both managed and unmanaged resources; <c>false</c> 
        /// to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!this.disposed)
            {
                if (disposing)
                {
                    try
                    {
                        Stop();
                        if (serverSock != null)
                        {
                            serverSock = null;
                        }
                    }
                    catch (SocketException)
                    {
                        //TODO
                        RaiseOtherException(null);
                    }
                }
                disposed = true;
            }
        }
        #endregion
    }
}
