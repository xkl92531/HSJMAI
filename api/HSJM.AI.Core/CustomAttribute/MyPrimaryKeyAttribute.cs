using System;
using System.Collections.Generic;
using System.Text;

namespace HSJM.AI.Core
{
    /// <summary>
    /// 
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public class MyPrimaryKeyAttribute : Attribute
    {
        public MyPrimaryKeyAttribute(string primaryKey)
        {
            Value = primaryKey;
            this.autoIncrement = true;
        }

        public MyPrimaryKeyAttribute(string[] primaryKey) : this(string.Join(",", primaryKey))
        {
        }

        public string Value { get; private set; }
        public string SequenceName { get; set; }
        private bool autoIncrement;
        public bool AutoIncrement
        {
            get { return this.autoIncrement; }
            set
            {
                this.autoIncrement = value;
                if (value && Value.Contains(","))
                {
                    throw new InvalidOperationException("Cannot set AutoIncrement to true when the primary key is a Composite Key");
                }
            }
        }
        public bool UseOutputClause { get; set; }
    }
}
