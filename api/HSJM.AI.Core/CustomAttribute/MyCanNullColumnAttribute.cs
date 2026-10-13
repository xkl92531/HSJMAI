using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HSJM.AI.Core
{
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public class MyCanNullColumnAttribute : Attribute
    {
    }
}
