using System.Diagnostics;
using System.IO.Pipes;
using ModelContextProtocol;
using TiaOpenness.Shared;

namespace TiaMcp.FoundationHost;

internal sealed class OperatorEndpoint : IAsyncDisposable
{
    private readonly HostRegistration registration;
    private readonly Func<CancellationToken, Task<IHostSession>> create;
    private readonly Func<Stream, int?> verify;
    private readonly Process? owner;
    private readonly bool operatorOnly;
    private readonly CancellationTokenSource stopping = new();
    private readonly SemaphoreSlim slots = new(OperatorPipe.MaximumInstances - 1);
    private readonly object sync = new();
    private readonly HashSet<Task> connections = new();
    private readonly Task listening;
    private readonly Task? watchingOwner;
    private readonly IOperatorListener listener;
    private bool ownedSessionEnded;
    internal string PipeName => registration.PipeName;
    internal Task Completion => listening;
    internal void Stop() => stopping.Cancel();

    internal OperatorEndpoint(HostRegistration registration, Func<CancellationToken, Task<IHostSession>> create, Func<Stream, int?> verify,
        IOperatorListener listener, Process? owner, bool operatorOnly)
    {
        this.registration = registration; this.create = create; this.verify = verify; this.listener = listener; this.owner = owner;
        this.operatorOnly = operatorOnly;
        listening = Listen();
        if (owner != null) watchingOwner = WatchOwner();
    }
    internal static OperatorEndpoint? Start(HostOptions options, Func<CancellationToken, Task<IHostSession>> create)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (options.OperatorOwner != null || options.Transport == "operator") throw new PlatformNotSupportedException("Operator transport requires Windows.");
            return null;
        }
        string sid = LocalPipeSecurity.CurrentSid;
        string image = BundleLayout.WorkbenchControlImagePath(options.BundleRoot, AppContext.BaseDirectory);
        Process? owner = null;
        if (options.OperatorOwner is { } pid)
        {
            owner = Process.GetProcessById(pid);
            if (owner.HasExited || !LocalPipeSecurity.ProcessMatches((uint)pid, sid, image))
            { owner.Dispose(); throw new UnauthorizedAccessException("The operator owner must be the same-user bundle Workbench image."); }
        }
        var registration = new HostRegistration(options, sid, DataLocations.Current.WorkbenchDataDirectory, Report);
        NamedPipeServerStream? pipe = null;
        try
        {
            pipe = OperatorPipe.CreateServer(registration.PipeName, sid, true);
            registration.Publish();
            return new OperatorEndpoint(registration, create, p => OperatorPipe.VerifiedClient((NamedPipeServerStream)p, sid, image),
                new PipeListener(pipe, () => OperatorPipe.CreateServer(registration.PipeName, sid, false)), owner, options.Transport == "operator");
        }
        catch (Exception error)
        {
            pipe?.Dispose(); registration.Dispose(); owner?.Dispose();
            if (options.OperatorOwner == null && error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            { Report(error); return null; }
            throw;
        }
    }
    internal IDisposable Register(IHostSession session) => registration.Register(session);
    internal static void Report(Exception error) => Console.Error.WriteLine("Operator endpoint: " + error.GetType().Name);
    private async Task WatchOwner()
    {
        try { await owner!.WaitForExitAsync(stopping.Token).ConfigureAwait(false); stopping.Cancel(); }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) /* swallow(teardown): owner watching ends with the endpoint lifetime */ { }
    }
    private async Task Listen()
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                await slots.WaitAsync(stopping.Token).ConfigureAwait(false);
                bool handedOff = false;
                try
                {
                    var pipe = await listener.Accept(stopping.Token).ConfigureAwait(false);
                    Task task;
                    lock (sync) { task = Serve(pipe); connections.Add(task); }
                    _ = task.ContinueWith(ConnectionEnded, CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    handedOff = true;
                }
                finally { if (!handedOff) slots.Release(); }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) /* swallow(teardown): cancellation closes the listener during host shutdown */ { }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or ObjectDisposedException)
        { Report(error); stopping.Cancel(); }
        finally { listener.Dispose(); registration.Dispose(); }
    }
    private void OwnedSessionEnded()
    {
        // Closed/state notifications precede worker detach. Only OperatorConnection's completed shutdown reaches here.
        lock (sync) ownedSessionEnded = true;
    }
    private void ConnectionEnded(Task completed)
    {
        lock (sync)
        {
            connections.Remove(completed);
            if (operatorOnly && ownedSessionEnded && connections.Count == 0 && !stopping.IsCancellationRequested) stopping.Cancel();
        }
    }
    private async Task Serve(Stream pipe)
    {
        using (pipe)
        {
            try
            {
                var connection = new OperatorConnection(registration, create, pid => owner != null && !owner.HasExited && owner.Id == pid, OwnedSessionEnded);
                await connection.Serve(pipe, verify(pipe), stopping.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or McpException)
            { if (!stopping.IsCancellationRequested) Report(error); }
            finally { slots.Release(); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        stopping.Cancel(); listener.Dispose();
        await listening.ConfigureAwait(false);
        Task[] active; lock (sync) active = connections.ToArray();
        await Task.WhenAll(active).ConfigureAwait(false);
        if (watchingOwner != null) await watchingOwner.ConfigureAwait(false);
        registration.Dispose(); owner?.Dispose(); slots.Dispose(); stopping.Dispose();
    }
    private sealed class PipeListener(NamedPipeServerStream initial, Func<NamedPipeServerStream> create) : IOperatorListener
    {
        private NamedPipeServerStream listener = initial;
        public async Task<Stream> Accept(CancellationToken token)
        {
            await listener.WaitForConnectionAsync(token).ConfigureAwait(false);
            var pipe = listener;
            try { listener = create(); return pipe; }
            catch { pipe.Dispose(); throw; }
        }
        public void Dispose() => listener.Dispose();
    }
}

internal interface IOperatorListener : IDisposable
{
    Task<Stream> Accept(CancellationToken token);
}
