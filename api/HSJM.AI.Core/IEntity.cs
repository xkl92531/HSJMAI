using System;
using System.Collections.Generic;
using System.Text;

namespace HSJM.AI.Core
{
    /// <summary>
    /// 
    /// </summary>
    public interface IEntity
    {
        /// <summary>
        /// 唯一标识
        /// </summary>
        public long Id { get; }
    }
    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="TPrimaryKeyType"></typeparam>
    public interface IEntity<TPrimaryKeyType>
    {
        /// <summary>
        /// 唯一标识
        /// </summary>
        public TPrimaryKeyType Id { get; }
    }
}
