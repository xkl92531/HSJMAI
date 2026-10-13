using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO.Ports;
using System.Threading;


namespace MAF.DEMO.Utility
{
    public enum StopBit
    {
        None,
        One,
        Two,
        OnePointFive
    }
    public enum SerialParity
    {
        None,
        Odd,
        Even,
        Mark,
        Space
    }

    public class SerialPortHelper
    {
        private SerialPort _serialPort;
        private Thread _readThread;
        private bool _continueReading;
        public Action<string> DataReceiveAction; 



        /// <summary>
        /// 
        /// </summary>
        /// <param name="portName">串口</param>
        /// <param name="baudRate">波特率</param>
        /// <param name="parity">校验位</param>
        /// <param name="dataBits">数据位</param>
        /// <param name="stopBits">停止位</param>
        public SerialPortHelper(string portName, int baudRate, SerialParity parity, int dataBits, StopBit stopBits)
        {
            var p = (Parity)parity.ToInt();
            var s = (StopBits)stopBits.ToInt();
            _serialPort = new SerialPort(portName, baudRate, p, dataBits,s );
            _serialPort.DataReceived += new SerialDataReceivedEventHandler(DataReceivedHandler);
        }

        /// <summary>
        /// 获取串口列表
        /// </summary>
        /// <returns></returns>
        public static List<string> GetSerialPortList() {
            string[] portNames = SerialPort.GetPortNames();
            return portNames.ToList();

        }


        public bool Connect()
        {
            if (!_serialPort.IsOpen)
            {
                try
                {
                    _serialPort.Open();
                    _continueReading = true;
                    _readThread = new Thread(new ThreadStart(ReadThread));
                    _readThread.Start();
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to connect to serial port. {ex.Message}");
                }
            }
            return false;
        }

        public void Disconnect()
        {
            if (_serialPort.IsOpen)
            {
                _continueReading = false;
                //_readThread.Join();
                _serialPort.Close();
            }
        }

        public bool IsConnected()
        {
            return _serialPort.IsOpen;
        }

        public void Send(string message)
        {
            if (_serialPort.IsOpen)
            {
                _serialPort.Write(message);
            }
        }

        private void DataReceivedHandler(object sender, SerialDataReceivedEventArgs e)
        {
            SerialPort sp = (SerialPort)sender;
            string data = sp.ReadExisting();
            DataReceiveAction?.Invoke(data);
            // do something with the received data
        }

        private void ReadThread()
        {
            while (_continueReading)
            {
                if (!_serialPort.IsOpen)
                {
                    Console.WriteLine("Serial port is not open. Reconnecting...");
                    _serialPort.Open();
                }
                else {
                    try
                    {
                        Send("1");
                    }
                    catch {
                        Disconnect();
                        Connect();
                    }
                }
                Thread.Sleep(5000);
            }
        }
    }

}
