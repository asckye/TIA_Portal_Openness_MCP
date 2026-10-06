using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.Logic.V4
{
    public class FallbackContract : IBehaviorCandidateContract
    {
        public const string Description = "[EXECUTE] Native fallback candidate. Empty-route preview discovers exact routes; select one verbatim for a reviewed plan. retryPolicy=never and refreshReadHandle=false. Apply requires confirmation, expectedPlanHash and expectedProjectFile. Failed configuration stops before download. No automatic offline transition, route switching or replay. A read handle can be refreshed once only with explicit permission and evidence of a read-only stale object before any execution. Unknown writes require session reset. Test/accepted behaviorPolicy=safe-v4.";
        public static readonly string[] Entries = { "DownloadPlc", "DownloadPlcToFolder", "CompilePlcSoftware", "ExportPlcBlockDocuments", "ImportPlcBlockDocuments",
            "ListVersionControlWorkspaces", "GetVersionControlStatus", "CreateVersionControlWorkspace", "ConnectProjectToWorkspace", "SynchronizeVersionControlWorkspace" };
        protected virtual string Entry => "DownloadPlc";
        public JsonElement InputSchema => Schema(Entry);
        public static JsonElement Schema(string entry)
        {
            var p = new JsonObject(); var required = new JsonArray();
            void Text(string key, string value = "", bool need = false) { p[key] = new JsonObject { ["type"] = "string", ["default"] = value }; if (need) required.Add(key); }
            void Flag(string key, bool value) => p[key] = new JsonObject { ["type"] = "boolean", ["default"] = value };
            if (entry == "DownloadPlc" || entry == "DownloadPlcToFolder" || entry == "CompilePlcSoftware" || entry == "ExportPlcBlockDocuments" || entry == "ImportPlcBlockDocuments") Text("softwarePath");
            if (entry == "DownloadPlc")
            {
                Flag("consistentBlocksOnly", true); Flag("keepActualValues", true); Flag("startAfterDownload", true); Flag("stopBeforeDownload", true);
                Text("password"); Text("rhTarget"); Flag("trustDeviceCertificate", false);
            }
            if (entry == "DownloadPlcToFolder") { Text("destinationDirectory", need: true); Text("targetForSoftware", "CPU"); }
            if (entry == "CompilePlcSoftware") Text("password");
            if (entry == "ExportPlcBlockDocuments") { Text("blockPath", need: true); Text("exportPath", need: true); Flag("preservePath", false); }
            if (entry == "ImportPlcBlockDocuments") { Text("groupPath"); Text("importPath", need: true); Text("fileNameWithoutExtension", need: true); }
            if (entry == "GetVersionControlStatus") Flag("changedOnly", true);
            if (entry == "CreateVersionControlWorkspace") { Text("workspaceName", need: true); Text("folderPath", need: true); }
            if (entry == "ConnectProjectToWorkspace") { Text("deviceFilter"); p["maxObjects"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 3000, ["default"] = 3000 }; }
            if (entry == "SynchronizeVersionControlWorkspace") p["direction"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("ProjectToWorkspace", "WorkspaceToProject"), ["default"] = "ProjectToWorkspace" };
            Text("route"); p["retryPolicy"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("never"), ["default"] = "never" }; Flag("refreshReadHandle", false);
            p["mode"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("preview", "apply"), ["default"] = "preview" }; Flag("confirm", false); Text("expectedPlanHash"); Text("expectedProjectFile");
            return JsonSerializer.SerializeToElement(new JsonObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = p, ["required"] = required });
        }
    }
    public sealed class FolderFallbackContract : FallbackContract { protected override string Entry => "DownloadPlcToFolder"; }
    public sealed class CompileFallbackContract : FallbackContract { protected override string Entry => "CompilePlcSoftware"; }
    public sealed class ExportDocumentsFallbackContract : FallbackContract { protected override string Entry => "ExportPlcBlockDocuments"; }
    public sealed class ImportDocumentsFallbackContract : FallbackContract { protected override string Entry => "ImportPlcBlockDocuments"; }
    public sealed class ListVciFallbackContract : FallbackContract { protected override string Entry => "ListVersionControlWorkspaces"; }
    public sealed class StatusVciFallbackContract : FallbackContract { protected override string Entry => "GetVersionControlStatus"; }
    public sealed class CreateVciFallbackContract : FallbackContract { protected override string Entry => "CreateVersionControlWorkspace"; }
    public sealed class MapVciFallbackContract : FallbackContract { protected override string Entry => "ConnectProjectToWorkspace"; }
    public sealed class SyncVciFallbackContract : FallbackContract { protected override string Entry => "SynchronizeVersionControlWorkspace"; }
}
