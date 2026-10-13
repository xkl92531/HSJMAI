using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HSJM.AI.Core
{
    public class AsyncLock
    {

        private readonly SemaphoreSlim semaphore;

        public AsyncLock()
        {
            semaphore = new SemaphoreSlim(1);
        }

        public ValueTask<IDisposable> LockAsync()
        {
            Task wait = semaphore.WaitAsync();
            if (wait.IsCompletedSuccessfully)
            {
                return new ValueTask<IDisposable>(new LockReleaser(this));
            }
            else
            {
                return LockAsyncInternal(this, wait);

                static async ValueTask<IDisposable> LockAsyncInternal(AsyncLock self, Task waitTask)
                {
                    await waitTask.ConfigureAwait(false);
                    return new LockReleaser(self);
                }
            }
        }

        private class LockReleaser : IDisposable
        {
            private AsyncLock target;

            internal LockReleaser(AsyncLock target)
            {
                this.target = target;
            }

            public void Dispose()
            {
                if (target == null)
                    return;
                AsyncLock tmp = target;
                target = default!;
                try
                {
                    tmp.semaphore.Release();
                }
                catch (Exception) { }
            }
        }
    }
}
