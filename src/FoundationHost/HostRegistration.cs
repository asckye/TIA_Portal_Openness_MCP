using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaOpenness.Shared;

namespace TiaMcp.FoundationHost;

internal sealed class HostRegistration : IDisposable
{
    private readonly object sync = new();
    private readonly Dictionary<string, IHostSession> sessions = new(StringComparer.Ordinal);
    private readonly JsonObject host;
    private readonly Action<Exception> report;
    private bool disposed;
    internal string FilePath { get; }
    internal string PipeName { get; }
    internal HostRegistration(HostOptions options, string sid, string dataRoot, Action<Exception> report)
    {
        this.report = report;
        using var process = Process.GetCurrentProcess();
        long ticks = process.StartTime.ToUniversalTime().Ticks;
        PipeName = OperatorPipe.Name(sid, dataRoot, process.Id, ticks);
        FilePath = Path.Combine(dataRoot, "run", "hosts", process.Id + "-" + ticks + ".json");
        host = new JsonObject { ["version"] = 1, ["hostInstanceId"] = Guid.NewGuid().ToString("N"), ["releaseKey"] = options.ReleaseKey,
            ["transport"] = options.Transport, ["pipeName"] = PipeName, ["operatorOwner"] = options.OperatorOwner,
            ["workbenchStarted"] = options.OperatorOwner != null,
            ["userSid"] = sid, ["pid"] = process.Id, ["startTicks"] = ticks, ["imagePath"] = Environment.ProcessPath,
            ["bundleRoot"] = options.BundleRoot };
    }
    internal void Publish()
    {
        lock (sync)
        {
            if (disposed) return;
            var record = (JsonObject)host.DeepClone();
            record["sessions"] = new JsonArray(sessions.Values.Select(s => (JsonNode)s.Snapshot()).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { JsonSerializer.Serialize(file, record, OperatorProtocol.Json); file.Flush(true); }
                File.Move(temporary, FilePath, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
    internal IDisposable Register(IHostSession session)
    {
        lock (sync)
        {
            if (disposed) throw new ObjectDisposedException(nameof(HostRegistration));
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
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
    }
}
