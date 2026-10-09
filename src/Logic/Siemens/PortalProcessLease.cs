using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace TiaMcpServer.Siemens
{
    // Session-lifetime OS file handle: release may occur on a different MTA thread.
    // Same Windows user, all MCP ports/profiles/versions. Durable request states
    // distinguish an idle owner exit from an interrupted or uncertain native call.
    internal sealed class PortalProcessLease : IDisposable
    {
        private FileStream? stream;
        internal string Key { get; }
        internal bool PreviousOwnerEndedIdle { get; private set; }
        internal bool Uncertain => state == "UNCERTAIN\n";
        private string state = "";
        private PortalProcessLease(FileStream handle, string key) { stream = handle; Key = key; }
        internal static PortalProcessLease Acquire(string root, int pid, long startUtcTicks)
        {
            if (!Path.IsPathRooted(root) || pid <= 0 || startUtcTicks <= 0) throw new ArgumentException("Invalid process lease identity.");
            Directory.CreateDirectory(root);
            string key = pid.ToString(CultureInfo.InvariantCulture) + "-" + startUtcTicks.ToString(CultureInfo.InvariantCulture);
            FileStream handle;
            try { handle = new FileStream(Path.Combine(root, key + ".lease"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) { throw new InvalidOperationException(TiaOpenness.Shared.SessionBehavior.LeaseReserved, ex); }
            var lease = new PortalProcessLease(handle, key);
            try
            {
                string previous;
                using (var reader = new StreamReader(handle, Encoding.UTF8, false, 1024, true)) previous = reader.ReadToEnd();
                if (previous.Length != 0 && previous != "RELEASED\n" && previous != "IDLE\n")
                    throw new InvalidOperationException(TiaOpenness.Shared.SessionBehavior.LeaseNotReleased);
                lease.PreviousOwnerEndedIdle = previous == "IDLE\n";
                lease.Write("BUSY\n");
                return lease;
            }
            catch { lease.Dispose(); throw; }
        }
        internal void BeginRequest() { if (state != "UNCERTAIN\n") Write("BUSY\n"); }
        internal void CompleteRequest(bool uncertain) => Write(uncertain || state == "UNCERTAIN\n" ? "UNCERTAIN\n" : "IDLE\n");
        private void Write(string value)
        {
            if (stream == null || state == value) return;
            var bytes = Encoding.UTF8.GetBytes(value);
            stream!.Position = 0; stream.Write(bytes, 0, bytes.Length); stream.SetLength(bytes.Length); stream.Flush(true); state = value;
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
