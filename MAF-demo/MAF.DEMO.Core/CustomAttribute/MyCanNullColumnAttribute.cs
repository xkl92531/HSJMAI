using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MAF.DEMO.Core
{
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public class MyCanNullColumnAttribute : Attribute
    {
    }
}
