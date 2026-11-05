using System;
using System.Threading;

namespace FlareAI.TestKit
{
    /// <summary>
    /// Test classes run in parallel and many of them block threads (waits, sleeps, synchronous HTTP). On a small CI runner the
    /// thread pool then adds threads slowly, and a mock server or the client call that waits on the pool can go past its
    /// 10 second timeout (#396). A higher floor makes the pool start threads at once.
    /// </summary>
    public static class PoolFloor
    {
        public static void Raise()
        {
            int worker, io;
            ThreadPool.GetMinThreads(out worker, out io);
            if (worker < 64 || io < 64) ThreadPool.SetMinThreads(Math.Max(worker, 64), Math.Max(io, 64));
        }
    }
}
