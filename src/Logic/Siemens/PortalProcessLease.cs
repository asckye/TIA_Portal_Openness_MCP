using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
        internal string? PreviousOwnerState { get; private set; }
        internal IReadOnlyList<string> PreviousOwnerRequests { get; private set; } = Array.Empty<string>();
        internal bool Uncertain => state.StartsWith("UNCERTAIN", StringComparison.Ordinal);
        private string state = "";
        private string? requestId;
        private readonly HashSet<string> requests = new HashSet<string>(StringComparer.Ordinal);
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
                bool discarded;
                using (var reader = new StreamReader(handle, Encoding.UTF8, false, 1024, true))
                {
                    previous = reader.ReadLine() ?? "";
                    discarded = reader.Peek() != -1;
                }
                var parts = previous.TrimStart().Split(new[] { ' ' }, 2);
                lease.PreviousOwnerState = parts[0].Length == 0 ? null : parts[0];
                var previousRequests = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (string id in parts.Length == 2 ? parts[1].Split(',') : Array.Empty<string>())
                {
                    if (!TiaOpenness.Shared.WorkerRequestIdentity.IsValid(id)
                        || previousRequests.Count == TiaOpenness.Shared.WorkerRequestIdentity.MaximumUnsettledRequests)
                    { discarded = true; continue; }
                    if (seen.Add(id)) previousRequests.Add(id);
                }
                lease.PreviousOwnerRequests = previousRequests;
                if (discarded) Console.Error.WriteLine("Portal lease record discarded invalid or excess request IDs or trailing data.");
                lease.requests.UnionWith(lease.PreviousOwnerRequests);
                lease.Write(Record("BUSY", lease.requests));
                return lease;
            }
            catch { lease.Dispose(); throw; }
        }
        internal void SetUnsettledRequests(IReadOnlyList<string>? ids)
        {
            if (ids == null) return;
            requests.Clear();
            requests.UnionWith(ids);
        }

        internal void BeginRequest(string? id = null)
        {
            requestId = id;
            var busy = requests.OrderBy(value => value, StringComparer.Ordinal).ToList();
            if (id != null && !requests.Contains(id)) busy.Add(id);
            Write(Record("BUSY", busy));
        }
        internal void CompleteRequest(bool uncertain)
        {
            if (uncertain && requestId != null) requests.Add(requestId);
            requestId = null;
            Write(Record(uncertain || requests.Count > 0 ? "UNCERTAIN" : "IDLE", requests));
        }
        private static string Record(string status, IEnumerable<string> ids)
        {
            string values = string.Join(",", ids.OrderBy(value => value, StringComparer.Ordinal));
            return status + (values.Length == 0 ? "" : " " + values) + "\n";
        }
        private void Write(string value)
        {
            if (stream == null || state == value) return;
            var bytes = Encoding.UTF8.GetBytes(value);
            stream!.Position = 0;
            stream.Write(bytes, 0, bytes.Length);
            stream.SetLength(bytes.Length);
            stream.Flush(true); state = value;
        }
        internal void ReleaseCleanly()
        {
            if (stream == null) return;
            try { Write(Record("RELEASED", requests)); } finally { Dispose(); }
        }
        // Dispose alone intentionally leaves the persistent uncertainty marker.
        public void Dispose() { var handle = stream; stream = null; handle?.Dispose(); }
    }
}
