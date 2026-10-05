using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using Siemens.Engineering.SW;
using TiaMcp.Adapters.Native.Plc;
using TiaMcp.Logic.V4;

namespace TiaMcp.PlcFoundation
{
    public sealed partial class PlcFoundationEngine
    {
        private readonly PlcImportSession importCandidate = new PlcImportSession();
        private readonly List<KeyValuePair<PlcSoftware, PlcImportAdapter>> importCandidateAdapters = new List<KeyValuePair<PlcSoftware, PlcImportAdapter>>();

        // A new operation admitted only in a worker selecting the P6-IMPORT candidate.
        public JsonElement ImportPlcCandidate(string tool, string softwarePath, string inputPath, string blockGroupPath = "", string typeGroupPath = "", string tagFolderPath = "",
            string technologyFolderPath = "", string regexName = "", string fileNameWithoutExtension = "", string[]? importOrder = null,
            bool overwrite = false, string versionPolicy = "exact", string onError = "stop", bool compileAfter = false, int maxItems = 128,
            string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "", long bindingEpoch = 0, string requestId = "")
        {
            Envelope result;
            try
            {
                Check();
                if (BehaviorCapabilities.Select(typeof(PlcFoundationEngine).Assembly, ReleaseKey, "P6-IMPORT") != BehaviorPolicy.SafeV4) PlcImportSession.Unsupported(ReleaseKey, "candidate-not-selected");
                if (!PlcImportContract.Entries.Contains(tool, StringComparer.Ordinal)) PlcImportSession.Invalid("tool");
                if (importCandidate.RequiresSessionReset) PlcImportSession.Refuse("The import session must be rebuilt.", new SessionResetRequiredDetails("plc-import-unknown"));
                var selected = ReadSelection(softwarePath);
                if (selected.ExactPath != softwarePath) PlcImportSession.Invalid("softwarePath");
                PlcLifecyclePolicy.RequireLocalSessionExecution(lifecycle.IsLocalSession, false);
                var project = Project();
                PlanIdentity Identity()
                {
                    Check(); if (!object.Equals(project, Project())) PlcImportSession.IdentityMismatch();
                    int pid = lifecycle.ProcessId ?? throw new InvalidOperationException("No attached process identity.");
                    using var process = Process.GetProcessById(pid); if (process.HasExited) throw new InvalidOperationException("Attached process exited.");
                    string path = project.Path.FullName; RequireProjectIdentity(path);
                    return new PlanIdentity(pid, new DateTimeOffset(process.StartTime.ToUniversalTime()), DeviceCreationSession.CanonicalProject(path), bindingEpoch, null, Array.Empty<PlanFile>());
                }
                PlcSoftware Software()
                {
                    Identity(); var fresh = ReadSelection(softwarePath);
                    if (fresh.ExactPath != selected.ExactPath || !object.Equals(fresh.Value, selected.Value) || !object.Equals(fresh.Context, selected.Context)) PlcImportSession.IdentityMismatch();
                    return fresh.Value;
                }
                PlcImportAdapter? adapter = null;
                foreach (var pair in importCandidateAdapters) if (object.Equals(pair.Key, selected.Value)) adapter = pair.Value;
                if (adapter == null)
                {
                    if (importCandidateAdapters.Count >= 128) PlcImportSession.Refuse("The session PLC budget is exhausted.", new LimitExceededDetails("plcs", 128, importCandidateAdapters.Count + 1));
                    adapter = new PlcImportAdapter(ReleaseKey, Identity, Software, () => RequireTargetOffline(ReadSelection(softwarePath)), !PlcImportContract.IsDirectory(tool));
                    importCandidateAdapters.Add(new KeyValuePair<PlcSoftware, PlcImportAdapter>(selected.Value, adapter));
                }
                adapter.Identity = Identity; adapter.Software = Software; adapter.RequireOffline = () => RequireTargetOffline(ReadSelection(softwarePath));
                adapter.OverwriteSupported = !PlcImportContract.IsDirectory(tool);
                var request = new PlcImportRequest { SoftwarePath = softwarePath, InputPath = inputPath, BlockGroupPath = blockGroupPath, TypeGroupPath = typeGroupPath,
                    TagFolderPath = tagFolderPath, TechnologyFolderPath = technologyFolderPath, RegexName = regexName, FileNameWithoutExtension = fileNameWithoutExtension,
                    ImportOrder = importOrder ?? Array.Empty<string>(), Overwrite = overwrite, VersionPolicy = versionPolicy, OnError = onError, CompileAfter = compileAfter, MaxItems = maxItems };
                result = importCandidate.Run(adapter, ReleaseKey, tool, requestId, request, mode, confirm, expectedPlanHash, expectedProjectFile);
            }
            catch (Exception ex)
            {
                var error = ex is PlcImportRejection refused ? refused.Error : new Error("The exact import target is unavailable; no import was issued.", new PreconditionFailedDetails("plc-import-target", null));
                result = PlcImportSession.Result(ReleaseKey, tool, requestId, null, error, Outcome.RejectedBeforeOperation, Execution.NotStarted);
            }
            return JsonSerializer.Deserialize<JsonElement>(V4Json.Serialize(result));
        }
    }
}
