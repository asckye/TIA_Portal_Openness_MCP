using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens.Services;

internal sealed class ProjectLifecyclePortService
{
    private readonly Func<string, JsonObject, JsonNode?> call;
    private readonly Func<bool> hasProject;
    private readonly Func<string> identity;
    public ProjectLifecyclePortService() : this(HardwareAddressWorkerBridge.Call, HardwareAddressWorkerBridge.HasProject, HardwareAddressWorkerBridge.ProjectIdentity) { }
    internal ProjectLifecyclePortService(Func<string, JsonObject, JsonNode?> call, Func<bool> hasProject, Func<string> identity)
    { this.call = call; this.hasProject = hasProject; this.identity = identity; }
    private JsonObject State => call("ReadState", new JsonObject())!.AsObject();
    internal bool HasProject => hasProject();
    internal bool IsConnected => (bool?)State["IsAttached"] == true;
    internal bool IsLocalSession => (bool?)State["IsLocalSession"] == true;
    private T Invoke<T>(string operation, JsonObject arguments)
        => JsonSerializer.Deserialize<T>((call("project-lifecycle." + operation, arguments)
            ?? throw new InvalidOperationException("Missing project lifecycle worker reply.")).ToJsonString())!;
    internal ResponseConnect ConnectIsolated() => Invoke<ResponseConnect>("ConnectIsolatedPortal", new JsonObject());
    internal bool EnsureGroup() => Invoke<bool>("EnsureOpennessUserGroup", new JsonObject());
    internal JsonObject GetBindingIdentity() => Invoke<JsonObject>("ReadProjectLifecycleBinding", new JsonObject());
    internal bool SaveAsProject(string path)
    {
        bool result = Invoke<bool>("ArchiveSaveProjectCopy", new JsonObject { ["path"] = path, ["dryRun"] = false, ["confirm"] = true, ["expectedProjectFile"] = identity() });
        return result;
    }
    internal ResponseMessage ArchiveSavedProject(string archivePath, bool dryRun)
    {
        var arguments = new JsonObject { ["archivePath"] = archivePath, ["dryRun"] = dryRun };
        if (!dryRun) { arguments["confirm"] = true; arguments["expectedProjectFile"] = identity(); }
        return Invoke<ResponseMessage>("ArchiveSavedProject", arguments);
    }
    internal ResponseMessage RetrieveProjectArchive(string archivePath, string destinationDirectory, bool upgrade, bool dryRun)
        => Invoke<ResponseMessage>("RetrieveProjectArchive", new JsonObject { ["request"] = JsonSerializer.SerializeToNode(new ProjectRetrieveRequest {
            ArchivePath = archivePath, DestinationDirectory = destinationDirectory, Upgrade = upgrade, DryRun = dryRun }) });
    internal ResponseMessage ReadPortalInfo(bool includeProcesses, bool includeSessions, bool includeProducts)
        => Invoke<ResponseMessage>("ReadPortalInfo", new JsonObject { ["includeProcesses"] = includeProcesses, ["includeSessions"] = includeSessions, ["includeProducts"] = includeProducts });
    internal ResponseMessage ValidateAutomationContext(string expectedPlcSoftwarePath, string expectedHmiSoftwarePath)
        => Invoke<ResponseMessage>("ValidateAutomationContext", new JsonObject { ["expectedPlcSoftwarePath"] = expectedPlcSoftwarePath, ["expectedHmiSoftwarePath"] = expectedHmiSoftwarePath });
    internal ResponseMessage ManageMultiuserSession(string action, string serverName, string projectName, string protocol, string host, int port,
        string commitComment, int offset, int limit, bool confirmChange, bool dryRun)
    {
        var request = new MultiuserRequest { Action = action, ServerName = serverName, ProjectName = projectName, Protocol = protocol,
            Host = host, Port = port, CommitComment = commitComment, Offset = offset, Limit = limit, ConfirmChange = confirmChange, DryRun = dryRun };
        void Capture(string stage, Action validate)
        {
            try { validate(); }
            catch (Exception error) when (error is ArgumentException || error is InvalidOperationException)
            { request.Faults[stage] = new SecurityValidationFault { Kind = error is InvalidOperationException ? "state" : "argument", Message = error.Message }; }
        }
        Capture("action", () => ProjectSecurityLogic.ValidateMultiuser(action, serverName, projectName, protocol, host, port, commitComment, confirmChange, dryRun));
        Capture("page", () => ProjectSecurityLogic.ValidatePage(offset, limit));
        return Invoke<ResponseMessage>("ExecuteMultiuserSession", new JsonObject { ["request"] = JsonSerializer.SerializeToNode(request) });
    }
    // Every primitive uses the existing family service in the same worker. Only
    // the offline builder runs here, with its unchanged validation and plans.
    internal PlcBuildTools PlcBuild => new PlcBuildTools(new PlcOrganisationPortService(call,
        () => !string.IsNullOrEmpty((string?)State["ProjectFile"]), () => (string?)State["ProjectFile"] ?? ""));
    internal T Primitive<T>(string kind, params string[] text) => Primitive<T>(new ProjectScaffoldPrimitive { Kind = kind, Text = text });
    private T Primitive<T>(ProjectScaffoldPrimitive request) => Invoke<T>("ExecuteProjectScaffoldPrimitive", new JsonObject { ["request"] = JsonSerializer.SerializeToNode(request) });
    internal ResponseDeviceProbe AddDeviceWithFallback(string mlfb, string version, string name, string family) => Primitive<ResponseDeviceProbe>("addDevice", mlfb, version, name, family);
    internal void ImportFromDocuments(string plc, string folder, string directory, string name) => Primitive<ResponseMessage>("lad", plc, folder, directory, name);
    internal void ImportPlcExternalSource(string plc, string folder, string path) => Primitive<ResponseMessage>("sclImport", plc, folder, path);
    internal void GenerateBlocksFromExternalSource(string plc, string name) => Primitive<ResponseMessage>("sclGenerate", plc, name);
    internal ResponseCompileDiagnose CompileAndDiagnosePlc(string plc) => Primitive<ResponseCompileDiagnose>("compile", plc);
    internal void GetHmiProgramInfo(string path) => Primitive<ResponseMessage>("hmiInfo", path);
    internal void EnsureUnifiedHmiConnection(string path, string name, string plc) => Primitive<ResponseMessage>("hmiConnect", path, name, plc);
    internal void EnsureUnifiedHmiScreen(string path, string name, uint width, uint height) => Primitive<ResponseMessage>(new ProjectScaffoldPrimitive { Kind = "hmiScreen", Text = new[] { path, name }, Numbers = new[] { width, height } });
    internal void ApplyUnifiedHmiScreenDesignJson(string path, string name, string design, bool overwrite) => Primitive<ResponseMessage>(new ProjectScaffoldPrimitive { Kind = "hmiDesign", Text = new[] { path, name, design }, Flags = new[] { overwrite } });
    internal void EnsureUnifiedHmiTag(string path, string table, string name, string type, string plc, string tag, string connection, string address, bool overwrite)
        => Primitive<ResponseMessage>(new ProjectScaffoldPrimitive { Kind = "hmiTag", Text = new[] { path, table, name, type, plc, tag, connection, address }, Flags = new[] { overwrite } });
}
