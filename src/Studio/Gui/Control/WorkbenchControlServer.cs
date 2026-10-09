using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;
using TiaOpenness.Shared;

namespace TiaOpenness.Gui.ControlChannel;

internal sealed class WorkbenchControlServer : IDisposable
{
    private sealed record ControlWork(WorkbenchControlRequest Request, TaskCompletionSource<WorkbenchControlResponse> Completion);
    private sealed class OriginState { internal bool InFlight; internal DateTimeOffset LastDisplay, LastSeen; }
    private readonly IWorkbenchControlSurface _surface;
    private readonly Dispatcher _dispatcher;
    private readonly Func<string, string> _hostImage;
    private readonly Func<DateTimeOffset> _now;
    private readonly Action<string> _open;
    private readonly string _name, _sid;
    private readonly WorkbenchControlLog _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _connections = new(WorkbenchControlPipe.MaximumInstances - 1);
    private readonly Channel<ControlWork> _queue = Channel.CreateBounded<ControlWork>(new BoundedChannelOptions(16)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly object _gate = new();
    private readonly Dictionary<string, OriginState> _origins = [];
    private NamedPipeServerStream? _listener;
    private bool _closing;
    private readonly Task _dispatch;
    internal Task Completion { get; private set; } = Task.CompletedTask;

    internal WorkbenchControlServer(IWorkbenchControlSurface surface, Dispatcher dispatcher, Func<string, string> hostImage,
        string? pipeName = null, Action<string>? open = null, string? logPath = null, Func<DateTimeOffset>? now = null)
    {
        _surface = surface; _dispatcher = dispatcher; _hostImage = hostImage;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _name = pipeName ?? WorkbenchControlPipe.CurrentName; _sid = LocalPipeSecurity.CurrentSid;
        _open = open ?? (path => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }));
        _log = new WorkbenchControlLog(logPath);
        Completion = _dispatch = Task.Run(Dispatch);
    }
    internal void Start()
    {
        _listener = WorkbenchControlPipe.CreateServer(_name, _sid, true);
        Completion = Task.WhenAll(Task.Run(Listen), _dispatch);
    }
    private async Task Listen()
    {
        var handlers = new List<Task>();
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _connections.WaitAsync(_stop.Token).ConfigureAwait(false);
                var pipe = _listener!;
                try
                {
                    await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (_closing) { pipe.Dispose(); _connections.Release(); break; }
                        _listener = WorkbenchControlPipe.CreateServer(_name, _sid, false);
                    }
                    handlers.RemoveAll(task => task.IsCompleted);
                    handlers.Add(Handle(pipe));
                }
                catch { pipe.Dispose(); _connections.Release(); throw; }
            }
        }
        catch (Exception ex) when ((ex is OperationCanceledException or ObjectDisposedException) && _stop.IsCancellationRequested)
        { Trace.TraceInformation("Workbench control listener stopped."); }
        catch (Exception ex)
        { Trace.TraceWarning("Workbench control listener failed: {0}", ex.GetType().Name); Dispose(); }
        finally { await Task.WhenAll(handlers).ConfigureAwait(false); }
    }
    private async Task Handle(NamedPipeServerStream pipe)
    {
        WorkbenchControlRequest? request = null;
        var clock = Stopwatch.StartNew();
        using (pipe)
        using (var frameLimit = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            frameLimit.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                // Read the bounded first frame before impersonation, exactly as the shared transport requires.
                request = await WorkbenchControlFrames.Read<WorkbenchControlRequest>(pipe, frameLimit.Token).ConfigureAwait(false);
                WorkbenchControlResponse response;
                if (!WorkbenchControlPipe.PeerMatches(pipe, _sid, _hostImage(request.Origin.ReleaseKey))
                    || !GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) || pid != request.Origin.HostProcessId)
                    response = ControlResponse.Refuse(request, "workbench-peer-identity", WorkbenchControlError.AccessDenied, "pipe-peer");
                else response = await Execute(request).ConfigureAwait(false);
                _log.Record(request, response, clock.ElapsedMilliseconds);
                // Closing responses still get a short chance to reach already connected clients.
                using var writeLimit = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await WorkbenchControlFrames.Write(pipe, response, writeLimit.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception or ObjectDisposedException or ArgumentException)
            {
                // Malformed requests have no trustworthy correlation ID; close without echoing their bytes.
                Trace.TraceInformation("Workbench control connection ended: {0}", ex.GetType().Name);
            }
            finally { _connections.Release(); }
        }
    }
    internal async Task<WorkbenchControlResponse> Execute(WorkbenchControlRequest request)
    {
        if (IsClosing) return ControlResponse.Refuse(request, "workbench-closing");
        if (request.Version != 1) return ControlResponse.Refuse(request, "workbench-version", WorkbenchControlError.UnsupportedCapability);
        if (request.CheckDeadline(_now()) is { } deadline)
            return ControlResponse.Refuse(request, "workbench-deadline", deadline);
        if (request.Operation is WorkbenchControlOperation.ReadState or WorkbenchControlOperation.ReadSelection)
            return ControlResponse.Done(request, _surface.Snapshot.Read(request.Arguments));
        string key = ControlResponse.OriginKey(request.Origin);
        lock (_gate)
        {
            var now = _now();
            foreach (var old in new List<string>(_origins.Keys))
                if (!_origins[old].InFlight && now - _origins[old].LastSeen > TimeSpan.FromMinutes(1)) _origins.Remove(old);
            if (!_origins.TryGetValue(key, out var origin))
            {
                if (_origins.Count >= 1024) return ControlResponse.Refuse(request, "workbench-busy");
                _origins[key] = origin = new();
            }
            origin.LastSeen = now;
            if (origin.InFlight || now - origin.LastDisplay < TimeSpan.FromMilliseconds(300))
                return ControlResponse.Refuse(request, "workbench-busy");
            origin.InFlight = true;
        }
        try
        {
            var remaining = request.DeadlineUtc - _now();
            using var limit = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
            ControlArtifact? artifact = null;
            try
            {
                var metadata = request.Arguments switch
                {
                    WorkbenchDisplayLadderArguments ladder => ladder.Artifact,
                    WorkbenchDisplayAtlasArguments atlas => atlas.Artifact, _ => null,
                };
                if (metadata != null)
                {
                    try { artifact = await ControlArtifact.Validate(metadata, limit.Token).ConfigureAwait(false); }
                    catch (FileNotFoundException) /* swallow(ui): missing render files are a typed refusal before dispatch */ { return ControlResponse.Refuse(request, "render-artifact-missing", WorkbenchControlError.NotFound, "render-artifact"); }
                    catch (DirectoryNotFoundException) /* swallow(ui): missing render directories are a typed refusal before dispatch */ { return ControlResponse.Refuse(request, "render-artifact-missing", WorkbenchControlError.NotFound, "render-artifact"); }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) /* swallow(ui): unsafe or changed artifacts are refused without echoing paths or exception text */
                    { return ControlResponse.Refuse(request, "render-artifact-changed", WorkbenchControlError.IdentityMismatch, "render-artifact"); }
                }
                var work = new ControlWork(request, new(TaskCreationOptions.RunContinuationsAsynchronously));
                if (!_queue.Writer.TryWrite(work)) return ControlResponse.Refuse(request, IsClosing ? "workbench-closing" : "workbench-busy");
                var response = await work.Completion.Task.WaitAsync(limit.Token).ConfigureAwait(false);
                if (response.Status == WorkbenchControlStatus.Done)
                {
                    lock (_gate) _origins[key].LastDisplay = _now();
                    if (artifact != null) _open(artifact.Path);
                }
                return response;
            }
            finally { artifact?.Dispose(); }
        }
        catch (OperationCanceledException) /* swallow(ui): an elapsed deadline returns TIMEOUT without locking a native session */ { return ControlResponse.Refuse(request, "workbench-deadline", WorkbenchControlError.Timeout); }
        catch (Exception ex)
        {
            string id = Guid.NewGuid().ToString("N");
            Trace.TraceWarning("Workbench control failure {0}: {1}", id, ex.GetType().Name);
            return ControlResponse.Failed(request, id);
        }
        finally { lock (_gate) _origins[key].InFlight = false; }
    }
    private async Task Dispatch()
    {
        await foreach (var work in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (IsClosing) { work.Completion.TrySetResult(ControlResponse.Refuse(work.Request, "workbench-closing")); continue; }
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            var remaining = work.Request.DeadlineUtc - _now();
            limit.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
            try
            {
                var response = await _dispatcher.InvokeAsync(() =>
                    IsClosing ? ControlResponse.Refuse(work.Request, "workbench-closing")
                    : work.Request.CheckDeadline(_now()) != null ? ControlResponse.Refuse(work.Request, "workbench-deadline", WorkbenchControlError.Timeout)
                    : _surface.Apply(work.Request), DispatcherPriority.Normal, limit.Token).Task.ConfigureAwait(false);
                work.Completion.TrySetResult(response);
            }
            catch (OperationCanceledException) /* swallow(ui): canceled dispatch never applies the UI action and completes the waiting request */ { work.Completion.TrySetResult(IsClosing
                ? ControlResponse.Refuse(work.Request, "workbench-closing")
                : ControlResponse.Refuse(work.Request, "workbench-deadline", WorkbenchControlError.Timeout)); }
            catch (Exception ex)
            {
                string id = Guid.NewGuid().ToString("N");
                Trace.TraceWarning("Workbench UI failure {0}: {1}", id, ex.GetType().Name);
                work.Completion.TrySetResult(ControlResponse.Failed(work.Request, id));
            }
        }
    }
    private bool IsClosing { get { lock (_gate) return _closing; } }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_closing) return;
            _closing = true;
            _queue.Writer.TryComplete();
            _stop.Cancel();
            _listener?.Dispose();
        }
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
}
