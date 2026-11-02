using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaOpenness.Shared;

namespace TiaMcp.FoundationHost;

internal sealed class HostRegistration : IDisposable
{
    internal static readonly TimeSpan PublishRetryTimeout = TimeSpan.FromSeconds(1);
    private readonly object sync = new();
    private readonly Dictionary<string, IHostSession> sessions = new(StringComparer.Ordinal);
    private readonly JsonObject host;
    private readonly Action<Exception> report;
    private readonly Action? sharingRetry;
    private bool disposed;
    private bool stopping;
    internal string FilePath { get; }
    internal string PipeName { get; }
    internal HostRegistration(HostOptions options, string sid, string dataRoot, Action<Exception> report, Action? sharingRetry = null)
    {
        this.report = report;
        this.sharingRetry = sharingRetry;
        using var process = Process.GetCurrentProcess();
        long ticks = process.StartTime.ToUniversalTime().Ticks;
        PipeName = OperatorPipe.Name(sid, dataRoot, process.Id, ticks);
        FilePath = Path.Combine(dataRoot, "run", "hosts", process.Id + "-" + ticks + ".json");
        host = new JsonObject { ["version"] = 1, ["hostInstanceId"] = Guid.NewGuid().ToString("N"), ["releaseKey"] = options.ReleaseKey,
            ["transport"] = options.Transport, ["pipeName"] = PipeName, ["operatorOwner"] = options.OperatorOwner,
            ["workbenchStarted"] = options.OperatorOwner != null || Environment.GetEnvironmentVariable("TIA_MCP_WORKBENCH_STARTED") == "1",
            ["userSid"] = sid, ["pid"] = process.Id, ["startTicks"] = ticks, ["imagePath"] = Environment.ProcessPath,
            ["hostVersion"] = typeof(HostOptions).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
                .Cast<System.Reflection.AssemblyMetadataAttribute>().Single(attribute => attribute.Key == "TiaMcpRelease").Value,
            ["httpPrefix"] = options.Transport == "http" ? options.HttpPrefix : null,
            ["bundleRoot"] = options.BundleRoot };
    }
    internal void OperatorUnavailable()
    {
        lock (sync) host["operatorAvailable"] = false;
        Publish();
    }
    internal void Publish()
    {
        lock (sync)
        {
            if (disposed) return;
            var record = (JsonObject)host.DeepClone();
            record["sessions"] = new JsonArray(sessions.Values.Select(s => (JsonNode)s.Snapshot()).ToArray());
            string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { JsonSerializer.Serialize(file, record, OperatorProtocol.Json); file.Flush(true); }
                RetrySharingViolation(() => File.Move(temporary, FilePath, overwrite: true), sharingRetry);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { report(error); }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { report(error); }
            }
        }
    }
    private static void RetrySharingViolation(Action action, Action? retrying = null)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            try { action(); return; }
            // MoveFileEx can report access denied for an open destination as well as a sharing/lock violation.
            catch (Exception error) when ((error is IOException && (error.HResult & 0xffff) is 32 or 33
                || error is UnauthorizedAccessException && (error.HResult & 0xffff) == 5) && timer.Elapsed < PublishRetryTimeout)
            {
                retrying?.Invoke();
                Thread.Sleep(TimeSpan.FromMilliseconds(Math.Clamp((PublishRetryTimeout - timer.Elapsed).TotalMilliseconds, 0, 10)));
            }
        }
    }
    internal IDisposable Register(IHostSession session)
    {
        lock (sync)
        {
            if (disposed || stopping) throw new ObjectDisposedException(nameof(HostRegistration));
            sessions.Add(session.SessionKey, session);
            session.StateChanged += Refresh;
            Refresh();
        }
        return new Membership(this, session);
    }
    private void Refresh()
    {
        try { Publish(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { report(error); }
    }
    internal JsonObject[] List()
    { lock (sync) return sessions.Values.Select(s => s.Snapshot()).ToArray(); }
    internal bool TryBeginShutdown()
    {
        lock (sync)
        {
            if (disposed || stopping) return false;
            var admitted = new List<IHostSession>();
            foreach (var session in sessions.Values)
            {
                if (!session.TryBeginHostShutdown())
                {
                    foreach (var prior in admitted) prior.CancelHostShutdown();
                    return false;
                }
                admitted.Add(session);
            }
            stopping = true;
            return true;
        }
    }
    internal void CancelShutdown()
    {
        lock (sync) { stopping = false; foreach (var session in sessions.Values) session.CancelHostShutdown(); }
    }
    internal IHostSession? Find(string key)
    { lock (sync) return sessions.GetValueOrDefault(key); }
    private sealed class Membership(HostRegistration owner, IHostSession session) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lock (owner.sync) { session.StateChanged -= owner.Refresh; owner.sessions.Remove(session.SessionKey); owner.Refresh(); }
        }
    }
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            foreach (var session in sessions.Values) session.StateChanged -= Refresh;
            sessions.Clear();
            try { if (File.Exists(FilePath)) RetrySharingViolation(() => File.Delete(FilePath)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { report(error); }
        }
    }
}
