using System;
using System.Threading;

namespace TiaMcpServer.ModelContextProtocol
{
    internal static class ResponseClock
    {
        private static readonly AsyncLocal<DateTimeOffset?> Pinned = new AsyncLocal<DateTimeOffset?>();

        internal static DateTime Now => Pinned.Value?.LocalDateTime ?? DateTime.Now;
        internal static DateTime UtcNow => Pinned.Value?.UtcDateTime ?? DateTime.UtcNow;

        // Overrides are scoped to the current execution context, including async continuations.
        internal static IDisposable Pin(DateTimeOffset instant) => new Scope(instant);

        private sealed class Scope : IDisposable
        {
            private readonly DateTimeOffset? previous;
            private bool disposed;

            internal Scope(DateTimeOffset instant)
            {
                previous = Pinned.Value;
                Pinned.Value = instant;
            }

            public void Dispose()
            {
                if (disposed) return;
                Pinned.Value = previous;
                disposed = true;
            }
        }
    }
}
