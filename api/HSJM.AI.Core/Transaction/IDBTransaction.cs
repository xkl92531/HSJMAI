using System;

namespace HSJM.AI.Core
{
    public interface IDBTransaction : IDisposable
    {
        void Complete();
    }
}
