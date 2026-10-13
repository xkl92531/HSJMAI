using HSJM.AI.AppLayer.SYS.ViewObject;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.SYS.OU.ViewObject
{
    public class OUTreeView : OUView, ITreeNode
    {

        public long GetId()
        {
            return ID;
        }

        public long GetPid()
        {
            return ParentID.Value;
        }

        public void SetChildren(IList children)
        {
            Children = (List<OUTreeView>)children;
        }

        public List<OUTreeView> Children { get; set; }
    }
}
