using System;
using System.Collections.Generic;
using System.Text;

namespace HSJM.AI.Core
{
    /// <summary>
    /// 
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public class MyTableNameAttribute : Attribute
    {
        public MyTableNameAttribute(string tableName)
        {
            Value = tableName;
        }
        public string Value { get; private set; }
    }
}
