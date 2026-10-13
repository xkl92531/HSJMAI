using System;

namespace MAF.DEMO.Core
{
    public interface IDBTransaction : IDisposable
    {
        void Complete();
    }
}
