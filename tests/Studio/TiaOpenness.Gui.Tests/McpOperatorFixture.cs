extern alias PlcRendering;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaOpenness.Gui.Services;
using TiaOpenness.Shared;

namespace TiaOpenness.Gui.Tests;

internal sealed class McpOperatorFixture : IAsyncDisposable
{
    internal static string RepositoryRoot { get { var path = new DirectoryInfo(AppContext.BaseDirectory); while (path != null && !File.Exists(Path.Combine(path.FullName, "CLAUDE.md"))) path = path.Parent; return path?.FullName ?? throw new IOException("Repository root not found."); } }
    internal readonly string Root = Path.GetFullPath(Path.Combine(RepositoryRoot, "bin-build", "refactor", "P8-21d-evidence", "fixture-" + Guid.NewGuid().ToString("N")));
    internal readonly string Key = Guid.NewGuid().ToString("N");
    internal readonly List<(string Name, JsonObject Arguments)> Calls = [];
    internal readonly List<string> Actions = [];
    internal readonly TaskCompletionSource<bool> Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource<bool> Approval = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource stopping = new();
    private readonly List<Task> connections = [];
    private readonly JsonObject state;
    internal readonly OperatorHost Host;
    internal bool HoldApproval, RejectPreview, RejectApply;
    internal bool CandidatePolicies;
    private readonly Dictionary<string, JsonObject> schemas = [];
    internal Action<ApprovalRequest>? ApprovalRequested;
    internal string? HandshakeError;
    internal string? ReturnedKey;
    internal int ExecutedWrites;
    internal IMcpServer? Server;
    internal FakeOwnedHost? Owner;
    internal ProcessStartInfo? Start;
    internal McpOperatorFixture(string release = "19")
    {
        Directory.CreateDirectory(Path.Combine(Root, "run", "hosts"));
        using var current = Process.GetCurrentProcess();
        Host = new(current.Id, current.StartTime.ToUniversalTime().Ticks, release, "fixture", Path.Combine(Root, "run", "hosts", "host.json"));
        state = new() { ["sessionKey"] = Key, ["releaseKey"] = release, ["revision"] = 1L, ["owner"] = "mcp",
            ["participants"] = new JsonObject { ["mcpClient"] = "fixture AI", ["mcpClientVerified"] = false, ["workbench"] = 0 },
            ["worker"] = new JsonObject { ["pid"] = 123, ["generation"] = 1, ["state"] = "idle" },
            ["binding"] = null, ["lane"] = null, ["sessionLocked"] = false, ["approvalEnabled"] = true, ["recentCalls"] = new JsonArray() };
        Publish();
    }
    private void Publish() => File.WriteAllText(Host.RegistrationPath, new JsonObject { ["version"] = 1, ["releaseKey"] = Host.ReleaseKey,
        ["pid"] = Host.Pid, ["startTicks"] = Host.StartTicks, ["pipeName"] = Host.PipeName, ["sessions"] = new JsonArray(state.DeepClone()) }.ToJsonString());
    internal McpSessionClient Client(FakeOwnedHost? owner = null) => new(RepositoryRoot, Root, Connect, start =>
    { Start = start; return Owner = owner ?? new FakeOwnedHost(Host.Pid); });
    private Task<Stream> Connect(OperatorHost host, CancellationToken token)
    {
        var left = new Pipe(); var right = new Pipe();
        Stream server = new Duplex(left.Reader.AsStream(), right.Writer.AsStream());
        Stream client = new Duplex(right.Reader.AsStream(), left.Writer.AsStream());
        connections.Add(Serve(server));
        return Task.FromResult(client);
    }
    private async Task Serve(Stream stream)
    {
        using (stream)
        {
            try
            {
                var request = await OperatorProtocol.Read<OperatorHandshake>(stream, stopping.Token);
                Actions.Add(request.Action);
                if (request.Action != "list") { state["participants"]!["workbench"] = 1; state["revision"] = (long)state["revision"]! + 1; Publish(); }
                await OperatorProtocol.Write(stream, new OperatorHandshakeResult { Accepted = HandshakeError == null, Error = HandshakeError,
                    SessionKey = request.Action == "list" ? null : ReturnedKey ?? Key, Sessions = [(JsonObject)state.DeepClone()] }, stopping.Token);
                if (request.Action == "list" || HandshakeError != null) return;
                var tools = new McpServerPrimitiveCollection<McpServerTool>();
                var baseline = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryRoot, "manifest", "contracts", "v4", "baseline", Host.ReleaseKey + ".json")))!;
                foreach (var definition in baseline["tools"]!.AsArray().OfType<JsonObject>())
                {
                    string name = (string)definition["name"]!;
                    JsonElement schema = CandidatePolicies ? CandidateSchema(name) ?? JsonSerializer.SerializeToElement(definition["inputSchema"])
                        : JsonSerializer.SerializeToElement(definition["inputSchema"]);
                    schemas[name] = JsonNode.Parse(schema.GetRawText())!.AsObject();
                    tools.Add(new FixtureTool(new Tool { Name = name, InputSchema = schema }, this));
                }
                await using var server = McpServerFactory.Create(new StreamServerTransport(stream, stream, "fixture"), new McpServerOptions {
                    ServerInfo = new() { Name = "operator fixture", Version = "1" }, Capabilities = new() { Tools = new() { ToolCollection = tools } } });
                Server = server;
                await server.RunAsync(stopping.Token);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
            { System.Diagnostics.Trace.TraceInformation("Fixture connection ended: {0}", error.GetType().Name); }
        }
    }
    internal async Task Notify(Action<JsonObject> change)
    {
        change(state); state["revision"] = (long)state["revision"]! + 1; Publish();
        if (Server != null) await Server.SendNotificationAsync(OperatorProtocol.SessionStateNotification, state.DeepClone(), cancellationToken: stopping.Token);
    }
    internal JsonObject Snapshot() => (JsonObject)state.DeepClone();
    private static JsonElement? CandidateSchema(string tool) => tool switch
    {
        "CompilePlcSoftware" => PlcRendering::TiaMcp.Logic.V4.CompileContract.Schema(tool),
        "SaveProject" or "DisconnectPortal" => PlcRendering::TiaMcp.Logic.V4.SaveCloseContract.Schema(tool),
        "OpenProject" or "ConnectPortal" or "AttachOpenProject" => PlcRendering::TiaMcp.Logic.V4.SessionCandidateContract.Schema(tool),
        "ExportPlcBlock" => PlcRendering::TiaMcp.Logic.V4.PlcExportContract.Schema(tool, "19"),
        "ImportPlcBlock" => PlcRendering::TiaMcp.Logic.V4.PlcImportContract.Schema(tool, "19"),
        _ => null
    };
    private async ValueTask<CallToolResult> Invoke(string name, CallToolRequestParams request, CancellationToken token)
    {
        var arguments = JsonSerializer.SerializeToNode(request.Arguments)!.AsObject();
        var schema = schemas[name];
        if (arguments.Any(a => schema["properties"]?[a.Key] == null)
            || (schema["required"] as JsonArray ?? new()).Any(field => !arguments.ContainsKey((string)field!)))
            throw new InvalidDataException("Fixture request does not satisfy the advertised schema for " + name);
        lock (Calls) Calls.Add((name, arguments));
        bool preview = (bool?)arguments["dryRun"] == true || (string?)arguments["mode"] == "preview",
            apply = (bool?)arguments["dryRun"] == false || (string?)arguments["mode"] == "apply";
        if (apply && schema["properties"]?["expectedPlanHash"] != null && (string?)arguments["expectedPlanHash"] != new string('a', 64))
            throw new InvalidDataException("The apply request did not preserve the reviewed plan hash.");
        if (apply)
        {
            if (HoldApproval) ApprovalRequested?.Invoke(FeaturePageFixtures.Request("panel-call") with { Actor = "workbench",
                OperatorCallId = Guid.NewGuid().ToString("N"), Deadline = DateTimeOffset.UtcNow.AddMinutes(1), TimeoutSeconds = 60 });
            Pending.TrySetResult(true);
            if (HoldApproval && !await Approval.Task.WaitAsync(token)) return Envelope(name, new(), "ACCESS_DENIED");
            if (RejectApply) return Envelope(name, new(), "PLAN_STALE");
            ExecutedWrites++;
        }
        if (preview && RejectPreview) return Envelope(name, new(), "PRECONDITION_FAILED");
        JsonObject data = name switch
        {
            "ListPortalProcessProjects" => new() { ["processes"] = new JsonArray(new JsonObject { ["processId"] = 123, ["projectPath"] = @"D:\fixture\Line.ap19", ["osStartTimeUtc"] = "2026-10-10T12:00:00.0000000Z" }) },
            "GetProjectTree" => new() { ["value"] = "Line\n  PlcSoftware: PLC_1 [softwarePath=devices/Station/PLC_1]" },
            "ListPlcBlocks" => new() { ["items"] = new JsonArray(Block()) },
            "GetPlcBlockHierarchy" => new() { ["name"] = "Program blocks", ["blocks"] = new JsonArray(Block()), ["groups"] = new JsonArray() },
            "GetPlcBlockInfo" => Block(),
            "CompilePlcSoftware" => new() { ["state"] = "Success", ["errorCount"] = 0, ["warningCount"] = 0 },
            _ => new() { ["executed"] = apply }
        };
        if (name == "ExportPlcBlock")
        {
            string file = Path.Combine((string)arguments["exportPath"]!, "Main.xml"); data["outputFile"] = file;
            if (apply) File.WriteAllText(file, "<Document/>");
        }
        if (name is "RenderPlcBlock" or "RenderPlcProgramAtlas") File.WriteAllText((string)arguments["outputPath"]!, "<html>fixture</html>");
        if (CandidatePolicies && preview) data["plan"] = new JsonObject { ["hash"] = new string('a', 64) };
        if (CandidatePolicies && name == "ExportPlcBlock" && apply)
        {
            string file = (string)data["outputFile"]!; data.Remove("outputFile");
            data["items"] = new JsonArray(new JsonObject { ["result"] = new JsonObject { ["data"] = new JsonObject {
                ["files"] = new JsonArray(new JsonObject { ["path"] = file }) } } });
        }
        if (name == "AttachOpenProject") await Notify(s => s["binding"] = new JsonObject { ["processId"] = 123, ["projectFile"] = @"D:\fixture\Line.ap19", ["bindingEpoch"] = 1 });
        if (name == "DisconnectPortal") await Notify(s => { s["worker"]!["state"] = "disconnected"; s["endedBy"] = "workbench"; s["endReason"] = "disconnect"; });
        return Envelope(name, data);
    }
    private static JsonObject Block() => new() { ["name"] = "Main", ["typeName"] = "OB", ["programmingLanguage"] = "LAD", ["isConsistent"] = true,
        ["isKnowHowProtected"] = false, ["attributes"] = new JsonArray(new JsonObject { ["name"] = "Number", ["value"] = 1 }) };
    private CallToolResult Envelope(string name, JsonObject data, string? code = null)
    {
        var envelope = new JsonObject { ["schemaVersion"] = 4, ["ok"] = code == null, ["data"] = data,
            ["error"] = code == null ? null : new JsonObject { ["code"] = code, ["message"] = "fixture refusal", ["details"] = code == "PRECONDITION_FAILED" ? new JsonObject { ["condition"] = "fixture", ["target"] = null } : code == "PLAN_STALE" ? new JsonObject { ["planHash"] = null, ["reason"] = "fixture" } : new JsonObject { ["operation"] = name, ["target"] = null } },
            ["meta"] = new JsonObject { ["timestamp"] = DateTime.UtcNow.ToString("O"), ["releaseKey"] = Host.ReleaseKey, ["tool"] = name,
                ["requestId"] = Guid.NewGuid().ToString("N"), ["outcome"] = code == null ? "succeeded" : "rejected-before-operation",
                ["execution"] = code == null ? "read-only" : "not-started", ["requiresSessionReset"] = false, ["behaviorPolicy"] = "not-applicable",
                ["completeness"] = "complete", ["paging"] = null, ["warnings"] = new JsonArray() } };
        return new() { IsError = code != null, StructuredContent = envelope, Content = [new TextContentBlock { Text = envelope.ToJsonString() }] };
    }
    private sealed class FixtureTool(Tool tool, McpOperatorFixture fixture) : McpServerTool
    {
        public override Tool ProtocolTool => tool;
        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken token = default)
            => fixture.Invoke(tool.Name, request.Params!, token);
    }
    internal sealed class FakeOwnedHost(int pid) : IOwnedOperatorHost
    {
        public int Id => pid;
        public bool HasExited => Exited.Task.IsCompleted;
        internal bool Disposed, Forced, AutoExit = true;
        internal readonly TaskCompletionSource<bool> Waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> Exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Stopwatch elapsed = new();
        internal TimeSpan ForcedAfter;
        internal int WaitCalls;
        public Task WaitForExitAsync(CancellationToken token)
        {
            Interlocked.Increment(ref WaitCalls);
            elapsed.Start(); Waiting.TrySetResult(true);
            if (AutoExit) Exited.TrySetResult(true);
            return Exited.Task.WaitAsync(token);
        }
        public void Terminate() { Forced = true; ForcedAfter = elapsed.Elapsed; Exited.TrySetResult(true); }
        public void Dispose() => Disposed = true;
    }
    public async ValueTask DisposeAsync()
    {
        stopping.Cancel(); await Task.WhenAll(connections); stopping.Dispose();
        Directory.Delete(Root, recursive: true);
    }
    private sealed class Duplex(Stream read, Stream write) : Stream
    {
        public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => write.Flush(); public override Task FlushAsync(CancellationToken token) => write.FlushAsync(token);
        public override int Read(byte[] bytes, int offset, int count) => read.Read(bytes, offset, count);
        public override Task<int> ReadAsync(byte[] bytes, int offset, int count, CancellationToken token) => read.ReadAsync(bytes, offset, count, token);
        public override ValueTask<int> ReadAsync(Memory<byte> bytes, CancellationToken token = default) => read.ReadAsync(bytes, token);
        public override void Write(byte[] bytes, int offset, int count) => write.Write(bytes, offset, count);
        public override Task WriteAsync(byte[] bytes, int offset, int count, CancellationToken token) => write.WriteAsync(bytes, offset, count, token);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default) => write.WriteAsync(bytes, token);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { read.Dispose(); write.Dispose(); } base.Dispose(disposing); }
    }
}
