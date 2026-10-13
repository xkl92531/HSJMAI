using System;
using System.Collections.Generic;
using System.Text;

namespace MAF.DEMO.Core
{
    public class MyId<TPrimaryKeyType>
    {
        /// <summary>
        /// 唯一标识
        /// </summary>
        public TPrimaryKeyType Id { get; set; }
    }
}
