using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace TiaMcpServer.Siemens
{
    // Session-lifetime OS file handle: release may occur on a different MTA thread.
    // Same Windows user, all MCP ports/profiles/versions. A killed owner leaves ACTIVE,
    // so a new worker cannot overlap an uncertain native operation after a crash.
    internal sealed class PortalProcessLease : IDisposable
    {
        private FileStream? stream;
        internal string Key { get; }
        private PortalProcessLease(FileStream handle, string key) { stream = handle; Key = key; }
        internal static PortalProcessLease Acquire(string root, int pid, long startUtcTicks)
        {
            if (!Path.IsPathRooted(root) || pid <= 0 || startUtcTicks <= 0) throw new ArgumentException("Invalid process lease identity.");
            Directory.CreateDirectory(root);
            string key = pid.ToString(CultureInfo.InvariantCulture) + "-" + startUtcTicks.ToString(CultureInfo.InvariantCulture);
            FileStream handle;
            try { handle = new FileStream(Path.Combine(root, key + ".lease"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) { throw new InvalidOperationException("TIA instance is already reserved by another MCP, or its lease is inaccessible. Use a separate TIA instance; no attachment attempted.", ex); }
            var lease = new PortalProcessLease(handle, key);
            try
            {
                string previous;
                using (var reader = new StreamReader(handle, Encoding.UTF8, false, 1024, true)) previous = reader.ReadToEnd();
                if (previous.Length != 0 && previous != "RELEASED\n")
                    throw new InvalidOperationException("The prior MCP owner did not release this TIA instance cleanly. Native outcome is unknown. Inspect diagnostics and restart that TIA instance before reconnecting; do not erase the lease to bypass this guard.");
                lease.Write("ACTIVE\n");
                return lease;
            }
            catch { lease.Dispose(); throw; }
        }
        private void Write(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            stream!.Position = 0; stream.SetLength(0); stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
        }
        internal void ReleaseCleanly()
        {
            if (stream == null) return;
            try { Write("RELEASED\n"); } finally { Dispose(); }
        }
        // Dispose alone intentionally leaves the persistent uncertainty marker.
        public void Dispose() { var handle = stream; stream = null; handle?.Dispose(); }
    }
}
