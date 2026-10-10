using System.Diagnostics;
using System.IO.Pipes;
using ModelContextProtocol;
using TiaOpenness.Shared;

namespace TiaMcp.FoundationHost;

internal sealed class OperatorEndpoint : IAsyncDisposable
{
    private readonly HostRegistration registration;
    private readonly Func<CancellationToken, Task<IHostSession>> create;
    private readonly Func<NamedPipeServerStream, int?> verify;
    private readonly Process? owner;
    private readonly CancellationTokenSource stopping = new();
    private readonly SemaphoreSlim slots = new(OperatorPipe.MaximumInstances - 1);
    private readonly object sync = new();
    private readonly HashSet<Task> connections = new();
    private readonly Task listening;
    private readonly Task? watchingOwner;
    private NamedPipeServerStream listener;
    internal string PipeName => registration.PipeName;
    internal Task Completion => listening;
    internal void Stop() => stopping.Cancel();

    private OperatorEndpoint(HostRegistration registration, Func<CancellationToken, Task<IHostSession>> create, Func<NamedPipeServerStream, int?> verify,
        NamedPipeServerStream listener, Process? owner)
    {
        this.registration = registration; this.create = create; this.verify = verify; this.listener = listener; this.owner = owner;
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
            return new OperatorEndpoint(registration, create, p => OperatorPipe.VerifiedClient(p, sid, image), pipe, owner);
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
                    await listener.WaitForConnectionAsync(stopping.Token).ConfigureAwait(false);
                    var pipe = listener;
                    listener = OperatorPipe.CreateServer(PipeName, LocalPipeSecurity.CurrentSid, false);
                    var task = Serve(pipe);
                    lock (sync) connections.Add(task);
                    _ = task.ContinueWith(completed => { lock (sync) connections.Remove(completed); }, CancellationToken.None,
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
    private async Task Serve(NamedPipeServerStream pipe)
    {
        using (pipe)
        {
            try
            {
                var connection = new OperatorConnection(registration, create, pid => owner != null && !owner.HasExited && owner.Id == pid);
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
}
