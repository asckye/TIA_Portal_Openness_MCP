using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Contracts.Rpc;
using TiaOpenness.Core.Abstractions;
using TiaOpenness.Core.Inspection;
using TiaOpenness.Core.Mock;

namespace TiaOpenness.Client
{
    public sealed class ProgressEventArgs : EventArgs { public ProgressPayload Progress { get; set; } }
    public sealed class BridgeLogEventArgs : EventArgs { public string Line { get; set; } }

    /// <summary>Studio front-end transport. Only mock mode or an explicitly selected existing MCP service.</summary>
    public sealed partial class BridgeClient : IDisposable
    {
        private MockTiaSession mock;
        private McpConnection mcp;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private readonly string[] args;
        private string expectedProject;
        private string[] softwarePaths;
        private JObject binding;
        private bool uncertain;
        public event EventHandler<ProgressEventArgs> Progress;
        public event EventHandler<BridgeLogEventArgs> Log;
        public event EventHandler Exited;
        public bool IsRunning { get; private set; }
        public bool IsMock => mock != null;
        public BridgeClient() : this(System.Environment.GetCommandLineArgs()) { }
        public BridgeClient(string[] args) { this.args = args ?? Array.Empty<string>(); }
        public BridgeClient(McpConnection connection, string project, IEnumerable<string> paths)
        {
            args = Array.Empty<string>(); mcp = connection; expectedProject = Canonical(project);
            softwarePaths = paths.ToArray(); IsRunning = true;
        }
        private string Option(string name)
        {
            var index = Array.IndexOf(args, name);
            if (index < 0) return null;
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException(name + " needs a value.");
            return args[index + 1];
        }
        private static string Canonical(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new ArgumentException("An absolute --bound-project path is required.");
            return Path.GetFullPath(path);
        }
        public void Start(string bridgeExePath = null, bool forceMock = false)
        {
            if (IsRunning)
            {
                if (forceMock != IsMock) throw new InvalidOperationException("Restart Studio to change between mock and MCP modes.");
                return;
            }
            if (bridgeExePath != null) throw new NotSupportedException("Native bridge launch is not part of this integrated build.");
            if (forceMock) mock = new MockTiaSession();
            else
            {
                var url = Option("--mcp-url");
                if (url == null) throw new InvalidOperationException("Select Mock, or launch with --mcp-url http://127.0.0.1:PORT/mcp (optional: --bound-project ABSOLUTE_PATH --software EXACT_PLC_PATH). Connect the MCP engine to that project first.");
                expectedProject = Option("--bound-project") == null ? null : Canonical(Option("--bound-project"));
                softwarePaths = Enumerable.Range(0, args.Length).Where(i => args[i] == "--software")
                    .Select(i => i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1] : throw new ArgumentException("--software needs an exact path.")).ToArray();

                mcp = new McpConnection(new Uri(url), System.Environment.GetEnvironmentVariable("TIA_STUDIO_API_KEY"));
            }
            IsRunning = true;
            Log?.Invoke(this, new BridgeLogEventArgs { Line = IsMock ? "Synthetic in-process mock; no Siemens API." : "Existing MCP service selected. Studio will not start or attach to TIA." });
        }
        public async Task<T> CallAsync<T>(string method, object parameters = null, CancellationToken cancellation = default)
        {
            await gate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                cancellation.ThrowIfCancellationRequested();
                if (!IsRunning) throw new InvalidOperationException("Start the transport first.");
                if (uncertain) throw new InvalidOperationException("Previous write outcome is unknown. Restart Studio and inspect the engine; do not replay.");
                var p = parameters == null ? new JObject() : JObject.FromObject(parameters);
                object value = IsMock ? MockCall(method, p) : await LiveCall(method, p, cancellation).ConfigureAwait(false);
                if (value == null) return default;
                return value is T typed ? typed : JToken.FromObject(value).ToObject<T>();
            }
            finally { gate.Release(); }
        }
        private void Report(string operation, int current, int total, string message) => Progress?.Invoke(this,
            new ProgressEventArgs { Progress = new ProgressPayload { Operation = operation, Current = current, Total = total, Message = message } });
        private object MockCall(string method, JObject p)
        {
            var device = p.Value<string>("deviceId");
            var workspace = p.Value<string>("workspaceName");
            switch (method)
            {
                case RpcMethods.DoctorRun: return new DoctorReport { TimestampUtc = DateTimeOffset.UtcNow, MachineName = "Mock", UserName = "Synthetic", Is64BitProcess = System.Environment.Is64BitProcess, Checks = new List<DoctorCheck> { new DoctorCheck { Id = "mock", Title = "Offline demo", Status = CheckStatus.Pass, Detail = "No Siemens assemblies or TIA process." } } };
                case RpcMethods.SessionConnect: return mock.Connect(p.Value<bool?>("withUserInterface") ?? true, true, p.Value<string>("version"));
                case RpcMethods.SessionState: return mock.GetState();
                case RpcMethods.SessionDisconnect: mock.Disconnect(); return mock.GetState();
                case RpcMethods.ProjectOpen: return mock.OpenProject(p.Value<string>("path"));
                case RpcMethods.ProjectInfo: return mock.GetProjectInfo();
                case RpcMethods.ProjectSave: mock.SaveProject(); return null;
                case RpcMethods.ProjectClose: mock.CloseProject(); return null;
                case RpcMethods.DeviceList: return mock.ListDevices();
                case RpcMethods.BlockList: return mock.ListBlocks(device, p.Value<bool?>("includeSystemBlocks") ?? false);
                case RpcMethods.BlockExport: return mock.ExportBlocks(device, p["blocks"]?.ToObject<List<string>>() ?? new List<string>(), p.Value<string>("outputDirectory"), Enum.Parse<ExportFormat>(p.Value<string>("format") ?? "SimaticMl"), p.Value<bool?>("preserveFolders") ?? true, Report);
                case RpcMethods.BlockImport: return mock.ImportBlocks(device, p["files"]?.ToObject<List<string>>() ?? new List<string>(), p.Value<bool?>("overwrite") ?? false, Report);
                case RpcMethods.CompileDevice: return mock.CompileDevice(device, p.Value<bool?>("softwareOnly") ?? true);
                case RpcMethods.InspectProject: return mock.Inspect(device, p.ToObject<InspectionOptions>());
                case RpcMethods.TagTableList: return mock.ListTagTables(device);
                case RpcMethods.TagList: return mock.ListTags(device, p.Value<string>("tableName"));
                case RpcMethods.VcSupported: return new { Supported = mock.VersionControl != null };
                case RpcMethods.VcWorkspaceList: return mock.VersionControl.ListWorkspaces();
                case RpcMethods.VcWorkspaceCreate: return mock.VersionControl.CreateWorkspace(p.Value<string>("name"), p.Value<string>("folderPath"));
                case RpcMethods.VcMapProject: return mock.VersionControl.MapProject(workspace, device, p.Value<bool?>("dryRun") ?? true, Report);
                case RpcMethods.VcStatus: return mock.VersionControl.GetStatus(workspace, p.Value<bool?>("changedOnly") ?? true);
                case RpcMethods.VcSync: return mock.VersionControl.Sync(workspace, Enum.Parse<SyncDirection>(p.Value<string>("direction") ?? "ProjectToWorkspace"), p.Value<bool?>("dryRun") ?? true, Report);
                case RpcMethods.VcDiff:
                    var w = mock.VersionControl.ListWorkspaces().Single(x => x.Name == workspace);
                    return GitWorkspaceDiff.Read(w.Name, w.RootPath, p.Value<string>("file"));
                default: throw new NotSupportedException("Studio method is unavailable: " + method);
            }
        }
        private static JToken G(JToken token, string key) => McpConnection.Get(token, key);
        private static string S(JToken token, string key) => G(token, key)?.Value<string>();
        private async Task<JObject> Tool(string name, JObject p, CancellationToken ct) => await mcp.ToolAsync(name, p, ct).ConfigureAwait(false);
        private async Task<SessionState> State(CancellationToken ct)
        {
            var state = await Tool("GetState", new JObject(), ct).ConfigureAwait(false);
            var identity = G(G(G(state, "meta"), "binding"), "identity") as JObject;
            if (G(state, "isConnected")?.Value<bool?>() != true || identity == null ||
                (expectedProject != null && !string.Equals(Canonical(S(identity, "projectPath")), expectedProject, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("The MCP engine is not bound to the selected project. Bind it explicitly in your MCP client first.");
            var major = G(identity, "tiaMajorVersion")?.Value<int>();
            if (major != 20 && major != 21) throw new NotSupportedException("Integrated desktop routing currently supports the full V20/V21 engines only.");
            if (string.IsNullOrEmpty(S(identity, "generation"))) throw new InvalidOperationException("Engine binding generation is missing.");
            if (binding != null && !JToken.DeepEquals(binding, identity)) { uncertain = true; throw new InvalidOperationException("Engine binding changed. Restart Studio to select the new session explicitly."); }
            binding = (JObject)identity.DeepClone();
            expectedProject = S(identity, "projectPath");
            return new SessionState { Connected = true, Mode = SessionMode.Openness, OpennessVersion = major.ToString(),
                OpenProject = new ProjectInfo { Name = S(identity, "projectName"), Path = S(identity, "projectPath") } };
        }
        private async Task<object> LiveCall(string method, JObject p, CancellationToken ct)
        {
            if (method == RpcMethods.DoctorRun)
            {
                var result = await Tool("Doctor", new JObject { ["fix"] = false }, ct).ConfigureAwait(false);
                return new DoctorReport { TimestampUtc = DateTimeOffset.UtcNow, MachineName = "MCP service", Checks = (G(result, "checks") as JArray ?? new JArray()).Select(c => new DoctorCheck {
                    Id = S(c, "name"), Title = S(c, "name"), Detail = S(c, "detail"), Remedy = S(c, "fix"), Status = G(c, "ok")?.Value<bool>() == true ? CheckStatus.Pass : CheckStatus.Fail }).ToList() };
            }
            if (method == RpcMethods.OpennessBuild) throw new NotSupportedException("Use the repository's exact-version engine build. Studio does not compile or load native adapters.");
            var state = method == RpcMethods.SessionConnect || method == RpcMethods.SessionState || binding == null
                ? await State(ct).ConfigureAwait(false)
                : new SessionState { Connected = true, Mode = SessionMode.Openness, OpennessVersion = S(binding, "tiaMajorVersion"),
                    OpenProject = new ProjectInfo { Name = S(binding, "projectName"), Path = expectedProject } };
            if (method == RpcMethods.SessionConnect || method == RpcMethods.SessionState) return state;
            if (method == RpcMethods.ProjectInfo) return state.OpenProject;
            if (method == RpcMethods.ProjectOpen)
            {
                if (!string.Equals(Canonical(p.Value<string>("path")), expectedProject, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Studio cannot replace the engine's selected project. Bind the intended project explicitly and restart Studio.");
                return state.OpenProject;
            }
            if (method == RpcMethods.ProjectClose || method == RpcMethods.SessionDisconnect)
                throw new NotSupportedException("The MCP client owns this TIA session. Close or disconnect through that client.");
            if (method == RpcMethods.ProjectSave)
            {
                return await Write("SaveProject", new JObject(), ct).ConfigureAwait(false);
            }
            if (method == RpcMethods.DeviceList)
            {
                if (softwarePaths == null || softwarePaths.Length == 0)
                {
                    var inventory = await Tool("GetDevices", new JObject { ["includePlcSoftware"] = true }, ct).ConfigureAwait(false);
                    softwarePaths = G(G(inventory, "meta"), "plcSoftwareNames")?.ToObject<string[]>()
                        ?? throw new InvalidOperationException("Update the MCP engine to the integrated version, or pass exact --software paths.");
                }
                var devices = new List<DeviceInfo>();
                foreach (var path in softwarePaths)
                {
                    var info = await Tool("GetSoftwareInfo", new JObject { ["softwarePath"] = path }, ct).ConfigureAwait(false);
                    devices.Add(new DeviceInfo { Id = path, Name = S(info, "name"), DisplayName = S(info, "name"), Category = "Plc", GroupPath = "", TypeName = "MCP PLC software" });
                }
                return devices;
            }
            if (method == RpcMethods.VcSupported) return new { Supported = state.OpennessVersion == "21" };
            if (method.StartsWith("vc.", StringComparison.Ordinal)) return await VersionControl(method, p, ct).ConfigureAwait(false);
            var device = p.Value<string>("deviceId");
            if (!softwarePaths.Contains(device, StringComparer.Ordinal)) throw new ArgumentException("Select one of the exact configured PLC software paths.");
            if (method == RpcMethods.BlockList) return await Blocks(device, p.Value<bool?>("includeSystemBlocks") ?? false, ct).ConfigureAwait(false);
            if (method == RpcMethods.InspectProject)
                return InspectionEngine.Run(device, await Blocks(device, false, ct).ConfigureAwait(false), p.ToObject<InspectionOptions>(), null);
            if (method == RpcMethods.CompileDevice)
            {
                if (p.Value<bool?>("softwareOnly") == false) throw new NotSupportedException("Use CompileDevice in the MCP tools panel for hardware compilation.");
                var r = await Write("CompileAndDiagnosePlc", new JObject { ["softwarePath"] = device }, ct).ConfigureAwait(false);
                var messages = new List<CompileMessage>();
                foreach (var pair in new[] { ("errors", CompileSeverity.Error), ("warnings", CompileSeverity.Warning), ("info", CompileSeverity.Information) })
                    foreach (var line in G(r, pair.Item1) as JArray ?? new JArray()) messages.Add(new CompileMessage { Severity = pair.Item2, Description = line.ToString(), Target = device });
                if (G(r, "errorCount")?.Type != JTokenType.Integer || G(r, "warningCount")?.Type != JTokenType.Integer)
                    throw new InvalidOperationException("Compile completed, but counts are incomplete. Inspect the MCP diagnostics; do not infer success.");
                return new CompileResult { State = S(r, "state"), ErrorCount = G(r, "errorCount").Value<int>(), WarningCount = G(r, "warningCount").Value<int>(), Messages = messages };
            }
            if (method == RpcMethods.BlockExport)
            {
                var documents = p.Value<string>("format") == "Source";
                var directory = Canonical(p.Value<string>("outputDirectory"));
                // Every batch gets a fresh directory; backend filenames are never guessed or reused.
                var target = Path.Combine(directory, "studio-export-" + Guid.NewGuid().ToString("N"));
                var inventory = await Blocks(device, false, ct).ConfigureAwait(false);
                var selected = p["blocks"]?.ToObject<List<string>>() ?? new List<string>();
                if (selected.Count == 0) selected = inventory.Select(b => b.Path).ToList();
                if (selected.Distinct(StringComparer.Ordinal).Count() != selected.Count || selected.Any(path => !inventory.Any(b => b.Path == path))) throw new ArgumentException("Export selection is stale or duplicated. Refresh blocks.");
                var result = new ExportResult { OutputDirectory = target, Requested = selected.Count };
                foreach (var path in selected)
                {
                    // Separate per-item directories also prevent duplicate names in separate groups colliding.
                    var itemDirectory = Path.Combine(target, result.Items.Count.ToString("D4"));
                    var r = await Write(documents ? "ExportAsDocuments" : "ExportBlock", new JObject { ["softwarePath"] = device, ["blockPath"] = path,
                        ["exportPath"] = itemDirectory, ["preservePath"] = p.Value<bool?>("preserveFolders") ?? true }, ct).ConfigureAwait(false);
                    var file = documents ? Directory.GetFiles(itemDirectory, "*", SearchOption.AllDirectories).FirstOrDefault() : S(G(r, "meta"), "exportedFile");
                    if (string.IsNullOrEmpty(file)) throw new InvalidOperationException("Export returned no file evidence; inspect the output before repeating.");
                    result.Items.Add(new ExportedItem { BlockPath = path, FilePath = file, Succeeded = true }); result.Succeeded++;
                    Report("export", result.Succeeded, result.Requested, file);
                }
                return result;
            }
            if (method == RpcMethods.BlockImport)
            {
                if (p.Value<bool?>("overwrite") != true) throw new NotSupportedException("The engine's XML importer replaces matching blocks. Choose overwrite explicitly, or cancel and use a reviewed import plan.");
                var files = p["files"]?.ToObject<List<string>>() ?? new List<string>();
                if (files.Any(f => !Path.IsPathFullyQualified(f) || !string.Equals(Path.GetExtension(f), ".xml", StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("The desktop importer accepts absolute SimaticML .xml paths.");
                var result = new ExportResult { Requested = files.Count };
                foreach (var file in files)
                {
                    var imported = await Write("ImportBlock", new JObject { ["softwarePath"] = device, ["groupPath"] = "", ["importPath"] = file }, ct).ConfigureAwait(false);
                    if (G(G(imported, "meta"), "verified")?.Value<bool?>() != true)
                    { uncertain = true; throw new InvalidOperationException("Import returned no verified readback. Inspect the project before retrying."); }
                    result.Items.Add(new ExportedItem { FilePath = file, Succeeded = true }); result.Succeeded++;
                    Report("import", result.Succeeded, result.Requested, file);
                }
                return result;
            }
            if (method == RpcMethods.TagTableList)
            {
                var r = await Tool("GetPlcTagTables", new JObject { ["softwarePath"] = device }, ct).ConfigureAwait(false);
                return (G(r, "items") as JArray ?? new JArray()).Select(x => new TagTableInfo { Name = x.ToString(), Path = x.ToString() }).ToList();
            }
            throw new NotSupportedException("Use the MCP tools panel for this operation: " + method);
        }
        // Ordinary tool errors remain recoverable in the UI. Lost responses are handled by
        // McpConnection; successful imports still require the engine's verified readback.
        private Task<JObject> Write(string name, JObject p, CancellationToken ct) => Tool(name, p, ct);
        private async Task<List<BlockInfo>> Blocks(string device, bool system, CancellationToken ct)
        {
            if (system) throw new NotSupportedException("The Studio grid covers root user blocks. ReadPlcBlockScopes provides software/safety unit and system scopes in the MCP tools panel.");
            var r = await Tool("GetBlocksWithHierarchy", new JObject { ["softwarePath"] = device }, ct).ConfigureAwait(false);
            if (G(G(r, "meta"), "dataComplete")?.Value<bool?>() != true) throw new InvalidOperationException("Engine block metadata is incomplete; inspect its read diagnostics.");
            var blocks = new List<BlockInfo>();
            Walk(G(r, "root") as JObject ?? throw new InvalidOperationException("Block hierarchy missing."), "", blocks);
            return blocks;
        }
        private static void Walk(JObject group, string parent, List<BlockInfo> result)
        {
            var name = S(group, "name");
            if (string.IsNullOrEmpty(name) || name.Contains('/')) throw new NotSupportedException("The legacy hierarchy cannot unambiguously address this folder name.");
            var folder = parent.Length == 0 ? name : parent + "/" + name;
            foreach (var b in G(group, "blocks") as JArray ?? new JArray())
            {
                var blockName = S(b, "name");
                if (string.IsNullOrEmpty(blockName) || blockName.Contains('/')) throw new NotSupportedException("Use an exact-address MCP tool for block names containing '/'.");
                if (G(b, "isConsistent")?.Type != JTokenType.Boolean || G(b, "isKnowHowProtected")?.Type != JTokenType.Boolean) throw new InvalidOperationException("Block consistency/protection metadata is unknown.");
                var attrs = G(b, "attributes") as JArray ?? new JArray();
                Func<string, JToken> attribute = key => G(attrs.FirstOrDefault(a => S(a, "name") == key), "value");
                var kindText = S(b, "typeName");
                var kind = kindText == "GlobalDB" ? BlockKind.DB : Enum.TryParse<BlockKind>(kindText, out var k) ? k : BlockKind.Unknown;
                result.Add(new BlockInfo { Name = blockName, Path = folder + "/" + blockName, FolderPath = folder,
                    Kind = kind, Number = int.TryParse(attribute("Number")?.ToString(), out var number) ? number : (int?)null,
                    HeaderAuthor = attribute("HeaderAuthor")?.ToString(), HeaderVersion = attribute("HeaderVersion")?.ToString(), ProgrammingLanguage = S(b, "programmingLanguage"),
                    IsConsistent = G(b, "isConsistent").Value<bool>(), IsKnowHowProtected = G(b, "isKnowHowProtected").Value<bool>() });
            }
            foreach (var child in (G(group, "groups") as JArray ?? new JArray()).OfType<JObject>()) Walk(child, folder, result);
        }
        public async Task<JObject> InspectToolAsync(string name, JObject p, bool execute, CancellationToken ct = default)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!IsRunning || IsMock) throw new InvalidOperationException("Select and start the MCP service first.");
                if (uncertain) throw new InvalidOperationException("Previous outcome is unknown; restart and inspect before further work.");
                if (!execute) return await Tool("FindTools", new JObject { ["query"] = name, ["limit"] = 20 }, ct).ConfigureAwait(false);
                await State(ct).ConfigureAwait(false);
                var preflight = await Tool("PreflightToolCall", new JObject { ["name"] = name, ["argumentsJson"] = p }, ct).ConfigureAwait(false);
                McpConnection.RequireSuccess(preflight);
                return await Write(name, p, ct).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }
        public void Dispose()
        {
            IsRunning = false; mock?.Dispose(); mcp?.Dispose(); Exited?.Invoke(this, EventArgs.Empty);
        }
    }
}
