using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using TiaOpenness.Shared;

namespace TiaOpenness.Core.Rpc
{
    /// <summary>Requires the exact live process and an exclusively held MCP process lease.</summary>
    public static class VciReadOnlyBinding
    {
        public static void Require(int processId, long startUtcTicks)
            => Require(processId, startUtcTicks, DataLocations.Current.LeasesDirectory);

        internal static void Require(int processId, long startUtcTicks, string leaseDirectory)
        {
            if (processId <= 0 || startUtcTicks <= 0) throw new InvalidOperationException("No shared process lease identity.");
            using (var process = Process.GetProcessById(processId))
                if (process.StartTime.ToUniversalTime().Ticks != startUtcTicks)
                    throw new InvalidOperationException("The shared process identity changed.");
            string key = processId.ToString(CultureInfo.InvariantCulture) + "-" + startUtcTicks.ToString(CultureInfo.InvariantCulture);
            string lease = Path.Combine(leaseDirectory, key + ".lease");
            if (!File.Exists(lease)) throw new InvalidOperationException("No active shared process lease.");
            try
            {
                using (new FileStream(lease, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { }
            }
            catch (IOException error) when ((error.HResult & 0xffff) == 32)
            { return; }
            throw new InvalidOperationException("The shared process lease is no longer held.");
        }
    }
}
