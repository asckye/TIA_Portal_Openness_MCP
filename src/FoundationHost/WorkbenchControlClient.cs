using System.IO.Pipes;
using System.Security.Principal;
using TiaOpenness.Shared;

namespace TiaMcp.FoundationHost;

internal interface IWorkbenchConnection : IDisposable
{
    Stream Stream { get; }
    Task Connect(CancellationToken token);
    bool MatchesServer(string sid, string image);
}

internal sealed class WorkbenchControlClient
{
    private readonly Func<IWorkbenchConnection> connection;
    private readonly Func<string> sid;
    private readonly Func<string> image;

    internal WorkbenchControlClient(Func<IWorkbenchConnection>? connection = null, Func<string>? sid = null,
        Func<string>? image = null)
    {
        this.connection = connection ?? (() => new PipeConnection());
        this.sid = sid ?? (() => LocalPipeSecurity.CurrentSid);
        this.image = image ?? (() => BundleLayout.WorkbenchControlImagePath(
            BundleLayout.RequireRoot(AppContext.BaseDirectory), AppContext.BaseDirectory));
    }

    internal async Task<WorkbenchControlResponse> Send(WorkbenchControlRequest request, CancellationToken token)
    {
        bool connected = false, verifying = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(request.DeadlineUtc - DateTimeOffset.UtcNow > TimeSpan.Zero
            ? request.DeadlineUtc - DateTimeOffset.UtcNow : TimeSpan.Zero);
        try
        {
            using var pipe = connection();
            await pipe.Connect(deadline.Token).ConfigureAwait(false);
            connected = true;
            // Identity is checked before the first byte of the request is written.
            verifying = true;
            if (!pipe.MatchesServer(sid(), image()))
                return Refused(request, WorkbenchControlError.AccessDenied, "pipe-identity", "pipe-owner");
            verifying = false;
            await WorkbenchControlFrames.Write(pipe.Stream, request, deadline.Token).ConfigureAwait(false);
            return await WorkbenchControlFrames.ReadResponse(pipe.Stream, request.RequestId, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) /* swallow(privacy): expose only the typed connect/deadline boundary */
        { return Refused(request, connected ? WorkbenchControlError.Timeout : WorkbenchControlError.ResourceUnavailable,
            connected ? "workbench-deadline" : "workbench-unavailable", "workbench-control"); }
        catch (TimeoutException) /* swallow(privacy): expose only the typed connect/deadline boundary */
        { return Refused(request, connected ? WorkbenchControlError.Timeout : WorkbenchControlError.ResourceUnavailable,
            connected ? "workbench-deadline" : "workbench-unavailable", "workbench-control"); }
        catch (UnauthorizedAccessException) /* swallow(privacy): identity refusals do not expose private security exception text */
        { return Refused(request, connected ? WorkbenchControlError.AccessDenied : WorkbenchControlError.ResourceUnavailable,
            connected ? "pipe-identity" : "workbench-unavailable", connected ? "pipe-owner" : "workbench-control"); }
        catch (BundleResourceUnavailableException) /* swallow(privacy): expose only the unavailable local Workbench resource */
        { return Refused(request, WorkbenchControlError.ResourceUnavailable, "workbench-unavailable", "workbench-control"); }
        catch (IOException) when (!connected) /* swallow(privacy): connection failures expose only the unavailable Workbench resource */
        { return Refused(request, WorkbenchControlError.ResourceUnavailable, "workbench-unavailable", "workbench-control"); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) when (verifying) /* swallow(privacy): fail closed before sending and hide private identity exception text */
        { return Refused(request, WorkbenchControlError.AccessDenied, "pipe-identity", "pipe-owner"); }
        catch (Exception) /* swallow(privacy): transport failures expose only host-generated diagnostic correlation */
        {
            var result = Refused(request, WorkbenchControlError.InternalError, "workbench-channel-failed", "workbench-control");
            result.Status = WorkbenchControlStatus.Failed;
            return result;
        }
    }

    internal static WorkbenchControlResponse Refused(WorkbenchControlRequest request, WorkbenchControlError code,
        string condition, string target) => new()
    {
        RequestId = request.RequestId, Status = WorkbenchControlStatus.Refused,
        Workbench = new() { Version = "unavailable" },
        Refusal = new() { Code = code, Condition = condition, Target = target, Resource = "workbench-control",
            Stage = request.Operation is WorkbenchControlOperation.ReadState or WorkbenchControlOperation.ReadSelection
                ? "workbench-read" : "workbench-ui", DiagnosticId = code == WorkbenchControlError.InternalError ? request.RequestId : null }
    };

    private sealed class PipeConnection : IWorkbenchConnection
    {
        private readonly NamedPipeClientStream pipe = new(".", WorkbenchControlPipe.CurrentName, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        public Stream Stream => pipe;
        public Task Connect(CancellationToken token) => pipe.ConnectAsync(WorkbenchControlPipe.ConnectTimeoutMilliseconds, token);
        public bool MatchesServer(string sid, string image) => WorkbenchControlPipe.ServerMatches(pipe, sid, image);
        public void Dispose() => pipe.Dispose();
    }
}
