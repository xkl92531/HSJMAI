using System;
using System.Collections.Generic;
using System.Text;

namespace HSJM.AI.Utility
{
    /// <summary>
    /// 全局静态变量
    /// </summary>
    public class GlobalStaticVariable
    {

        /// <summary>
        /// PLC 数据类型
        /// </summary>
        public static List<string> PlcDataTypeList = new List<string> {
                "Bool", "Byte", "SInt", "USInt", "Int", "UInt", "DInt", "UDInt",
                "Word", "DWord", "LWord", "LInt", "ULInt", "Real", "LReal", "Char", "String"
            };
    }
}
