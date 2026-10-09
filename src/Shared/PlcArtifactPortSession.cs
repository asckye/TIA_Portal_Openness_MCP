using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens.Services
{
    internal sealed class PlcArtifactBlock { public string Name { get; set; } = ""; public string ProgrammingLanguage { get; set; } = ""; }
    internal sealed class PlcArtifactPortSession
    {
        private readonly PlcOrganisationPortService service;
        private readonly string tool;
        private string buildFormat = "";
        internal PlcArtifactPortSession(PlcOrganisationPortService service, string tool) { this.service = service; this.tool = tool; }
        internal void RequireBuildFormat(string kind)
        { var refusal = PlcSoftwareCapabilities.Unsupported(HardwareContract.ReleaseKey, tool, family: kind); if (refusal != null) throw new NotSupportedException(refusal); buildFormat = kind; }
        private JsonObject Invoke(string action, string path, string group = "", string file = "")
            => service.Artifact(new PlcSoftwareRequest { Operation = tool, Family = buildFormat, Action = action, SoftwarePath = path, GroupPath = group, FilePath = file, DryRun = action == "softwarePath" });
        public bool ImportBlock(string path, string group, string file) => (bool?)Invoke("importBlock", path, group, file)["Found"] == true;
        public bool ImportType(string path, string group, string file) => (bool?)Invoke("importType", path, group, file)["Found"] == true;
        public void ImportPlcTagTable(string path, string group, string file) => Invoke("importTagTable", path, group, file);
        public string SoftwarePath(string path) => (string?)Invoke("softwarePath", path)["Message"] ?? "";
        public PlcArtifactBlock? ExportBlock(string path, string block, string folder)
        {
            var reply = service.Artifact(new PlcSoftwareRequest { Operation = tool, SoftwarePath = path, Path = block, FilePath = folder });
            return reply["Data"] == null ? null : JsonSerializer.Deserialize<PlcArtifactBlock>(reply["Data"]!.ToJsonString());
        }
        public PlcCompilerEvidence CompileSoftware(string path)
            => JsonSerializer.Deserialize<PlcCompilerEvidence>(Invoke("compile", path)["Compiler"]!.ToJsonString())!;
        internal static ResponseCompile BuildCompileResponse(string path, PlcCompilerEvidence result) => new ResponseCompile {
            Message = "Software '" + path + "' compile state=" + result.State + "; see Meta for root counts and diagnostic scopes.", State = result.State,
            ErrorCount = result.ErrorCount, WarningCount = result.WarningCount, Messages = result.RawMessages, Meta = JsonSerializer.SerializeToNode(result.Meta)!.AsObject()
        };
        public ResponseCompileDiagnose CompileAndDiagnose(string path)
        {
            try { var result = CompileSoftware(path); return new ResponseCompileDiagnose {
                Message = "Software '" + path + "' compile state=" + result.State + "; root counts and diagnostics scopes are in Meta.", State = result.State,
                ErrorCount = result.ErrorCount, WarningCount = result.WarningCount, Errors = result.Errors, Warnings = result.Warnings, Info = result.Info, RawMessages = result.RawMessages,
                Meta = JsonSerializer.SerializeToNode(result.Meta)!.AsObject() }; }
            catch (PortalException error) { throw new McpException("Failed compiling software '" + path + "' [" + error.Code + "]: " + error.Message, error, McpErrorCode.InternalError); }
            catch (Exception error) when (error is not McpException) { throw new McpException("Unexpected error compiling software '" + path + "': " + error.Message + McpHints.Recovery(error), error, McpErrorCode.InternalError); }
        }
        public ResponseImportBatch ImportBlocksFromDirectory(string path, string group, string directory, string regexName = "", bool overwrite = true)
        {
            var reply = service.Artifact(new PlcSoftwareRequest { Operation = tool, Action = "importBlockDirectory", SoftwarePath = path, GroupPath = group, FilePath = directory, RegexName = regexName, Overwrite = overwrite, DryRun = false });
            return JsonSerializer.Deserialize<ResponseImportBatch>(reply["Batch"]!.ToJsonString())!;
        }
    }
    internal sealed partial class PlcOrganisationPortService
    {
        internal JsonObject Artifact(PlcSoftwareRequest request)
        {
            if (!hasProject()) throw new PortalException(PortalErrorCode.InvalidState, request.Action == "compile" ? "Project is null" : "No project is open. If a project is already open in the TIA Portal UI, call AttachOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (ConnectPortal is attempted automatically.)");
            return Invoke(request);
        }
        private ResponseImportBatch SeedHmiImport(string kind, string path, string folder, string directory)
        {
            if (HardwareContract.ReleaseKey != "20" && HardwareContract.ReleaseKey != "21") throw new NotSupportedException("Seed HMI imports depend on the B7 engine worker port in this release.");
            var reply = call("plc-software.SeedReferenceHmi", new JsonObject { ["request"] = JsonSerializer.SerializeToNode(new SeedHmiRequest { Kind = kind, SoftwarePath = path, FolderPath = folder, Directory = directory }), ["dryRun"] = false, ["confirm"] = true, ["expectedProjectFile"] = projectIdentity() });
            return JsonSerializer.Deserialize<ResponseImportBatch>(reply!.ToJsonString())!;
        }
    }
}
