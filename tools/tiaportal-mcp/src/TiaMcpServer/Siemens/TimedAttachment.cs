using System;
using System.Threading;

namespace TiaMcpServer.Siemens
{
    // A timed-out native Attach cannot be cancelled. Transfer ownership once, or
    // release a late result on the worker that created it. Never abort that thread.
    internal static class TimedAttachment
    {
        internal static T? Run<T>(Func<T> attach, Action<T> release, int timeoutMs) where T : class
        {
            if (timeoutMs < 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
            var gate = new object();
            T? result = null;
            Exception? error = null;
            bool completed = false, abandoned = false;
            var worker = new Thread(() =>
            {
                T? acquired = null;
                Exception? failure = null;
                try { acquired = attach(); }
                catch (Exception ex) { failure = ex; }
                lock (gate)
                {
                    if (!abandoned)
                    {
                        result = acquired; error = failure; completed = true;
                        Monitor.PulseAll(gate);
                        return;
                    }
                }
                if (acquired != null) { try { release(acquired); } catch /* swallow(teardown): late attachment release cannot change the timeout already returned to the caller */ { } }
            }) { IsBackground = true, Name = "TIA Openness attach" };
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                worker.SetApartmentState(ApartmentState.MTA);
            lock (gate)
            {
                worker.Start();
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                while (!completed)
                {
                    int remaining = timeoutMs - (int)elapsed.ElapsedMilliseconds;
                    if (remaining <= 0) { abandoned = true; return null; }
                    Monitor.Wait(gate, remaining);
                }
                if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
                return result;
            }
        }
    }
}
