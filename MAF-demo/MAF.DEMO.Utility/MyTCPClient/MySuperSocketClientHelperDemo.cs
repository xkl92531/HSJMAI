using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MAF.DEMO.Utility.MyTCPClient
{
    internal class MySuperSocketClientHelperDemo
    {

        MySuperSocketClientHelper mySuperSocketClient;
        public MySuperSocketClientHelperDemo()
        {
            mySuperSocketClient = new MySuperSocketClientHelper("", 1000, HandleData, ConnectedOK, ConnectedError);

        }

        public void HandleData(string message, byte[] byteData)
        {
        }

        public void ConnectedOK(string message)
        {
            // showMessage?.Invoke(message);

        }

        public void ConnectedError(string message)
        {
            //   showMessage?.Invoke(message);
        }
    }
}
