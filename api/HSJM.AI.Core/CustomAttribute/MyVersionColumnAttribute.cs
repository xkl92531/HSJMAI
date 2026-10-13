using System;
using System.Collections.Generic;
using System.Text;

namespace HSJM.AI.Core
{
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public class MyVersionColumnAttribute : MyColumnAttribute
    {
        public MyVersionColumnTypeEnum VersionColumnType { get; private set; }

        public MyVersionColumnAttribute() : this(MyVersionColumnTypeEnum.Number) { }
        public MyVersionColumnAttribute(MyVersionColumnTypeEnum versionColumnType)
        {
            VersionColumnType = versionColumnType;
        }
        public MyVersionColumnAttribute(string name, MyVersionColumnTypeEnum versionColumnType) : base(name)
        {
            VersionColumnType = versionColumnType;
        }
    }

    /// <summary>
    /// 
    /// </summary>
    public enum MyVersionColumnTypeEnum
    {
        Number,
        RowVersion
    }
}
