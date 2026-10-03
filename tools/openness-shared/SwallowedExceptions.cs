#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace TiaMcp.Shared
{
    internal static class SwallowedExceptions
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, long> Counts = new Dictionary<string, long>(StringComparer.Ordinal);
        private static Action<string>? sink;

        internal static Action<string>? Sink
        {
            get => Volatile.Read(ref sink);
            set => Volatile.Write(ref sink, value);
        }

        internal static void Note(string site, Exception ex)
        {
            try
            {
                if (site == null || ex == null) return;
                lock (Gate)
                {
                    Counts.TryGetValue(site, out var count);
                    Counts[site] = count + 1;
                    if (count != 0) return;
                }

                var output = Sink;
                if (output == null) return;
                // Only managed scalar fields are read; exception messages may invoke native getters.
                output("site=" + site.Replace("\r", " ").Replace("\n", " ")
                    + " type=" + ex.GetType().FullName
                    + " hresult=" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture));
            }
            catch /* swallow(logging-failure): diagnostic bookkeeping or sink failures must not replace the original operation result */
            {
            }
        }

        internal static Dictionary<string, long> Snapshot()
        {
            lock (Gate)
            {
                return new Dictionary<string, long>(Counts, StringComparer.Ordinal);
            }
        }
    }
}
