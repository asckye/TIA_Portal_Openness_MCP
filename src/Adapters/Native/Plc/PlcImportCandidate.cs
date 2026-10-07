using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Siemens.Engineering.SW;
using TiaMcp.Adapters.Native.Plc;
using TiaMcp.Adapters.Contracts.Candidates;

namespace TiaMcp.PlcFoundation
{
    public sealed partial class PlcFoundationEngine
    {
        private readonly List<KeyValuePair<PlcSoftware, PlcImportAdapter>> importCandidateAdapters = new List<KeyValuePair<PlcSoftware, PlcImportAdapter>>();
        public ImportCandidateReply ImportPlcCandidate(ImportCandidateCall candidate, string mode = "preview", long bindingEpoch = 0)
        {
            var result = new ImportCandidateReply();
            var locks = new Dictionary<string, Stream>(StringComparer.OrdinalIgnoreCase);
            try
            {
                Check();
                var selected = ReadSelection(candidate.Request.SoftwarePath);
                TiaOpenness.Shared.NativeExportPolicy.RequireSoftwarePath(candidate.Request.SoftwarePath, selected.ExactPath, CandidateImportNames.IsDirectory(candidate.Tool));
                PlcLifecyclePolicy.RequireLocalSessionExecution(lifecycle.IsLocalSession, false);
                var project = Project();
                CandidateIdentity Identity()
                {
                    Check(); if (!object.Equals(project, Project())) CandidatePrimitives.Fail("identity", "project-binding");
                    int pid = lifecycle.ProcessId ?? throw new InvalidOperationException("No attached process identity.");
                    using var process = Process.GetProcessById(pid); if (process.HasExited) throw new InvalidOperationException("Attached process exited.");
                    string path = project.Path.FullName; RequireProjectIdentity(path);
                    return new CandidateIdentity(pid, new DateTimeOffset(process.StartTime.ToUniversalTime()), CandidatePrimitives.CanonicalProject(path), bindingEpoch);
                }
                PlcSoftware Software()
                {
                    Identity(); var fresh = ReadSelection(candidate.Request.SoftwarePath);
                    if (fresh.ExactPath != selected.ExactPath || !object.Equals(fresh.Value, selected.Value) || !object.Equals(fresh.Context, selected.Context)) CandidatePrimitives.Fail("identity", "project-binding");
                    return fresh.Value;
                }
                PlcImportAdapter? adapter = null;
                foreach (var pair in importCandidateAdapters) if (object.Equals(pair.Key, selected.Value)) adapter = pair.Value;
                if (adapter == null)
                {
                    if (importCandidateAdapters.Count >= 128) CandidatePrimitives.Refuse("The session PLC budget is exhausted.", "plcs", 128, importCandidateAdapters.Count + 1);
                    adapter = new PlcImportAdapter(ReleaseKey, Identity, Software, () => RequireTargetOffline(ReadSelection(candidate.Request.SoftwarePath)), !CandidateImportNames.IsDirectory(candidate.Tool));
                    importCandidateAdapters.Add(new KeyValuePair<PlcSoftware, PlcImportAdapter>(selected.Value, adapter));
                }
                adapter.Identity = Identity; adapter.Software = Software; adapter.RequireOffline = () => RequireTargetOffline(ReadSelection(candidate.Request.SoftwarePath));
                adapter.OverwriteSupported = !CandidateImportNames.IsDirectory(candidate.Tool);
                switch (candidate.Action)
                {
                    case "identity": result.Identity = adapter.ReadIdentity(); break;
                    case "inputs": result.Inputs = adapter.ReadInputs(ReleaseKey, candidate.Tool, candidate.Request, locks).ToArray(); break;
                    case "inventory": result.Inventory = adapter.ReadInventory().ToArray(); break;
                    case "group": result.GroupIdentity = adapter.TargetGroupIdentity(candidate.Target!); break;
                    case "overwrite": result.OverwriteSupported = adapter.SupportsOverwrite(candidate.Input!); break;
                    case "execute":
                        if (mode != "apply" || candidate.Check == null || candidate.Check.Release != ReleaseKey || candidate.Check.Tool != candidate.Tool
                            || candidate.Check.Request.SoftwarePath != candidate.Request.SoftwarePath) CandidatePrimitives.Invalid("candidate");
                        foreach (var file in candidate.Check!.Files)
                        {
                            CandidateImportFiles.SafePath(new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(file.Path)));
                            locks.Add(file.Path, TiaOpenness.Shared.NativeInputPolicy.OpenRead(file.Path, "importPath"));
                        }
                        result.Attempt = CandidateExecution.Import(adapter, candidate.Check, locks);
                        result.RequiresSessionReset = result.Attempt.RequiresSessionReset;
                        break;
                    default: CandidatePrimitives.Invalid("candidate.action"); break;
                }
            }
            catch (Exception ex)
            { result.Fault = ex is TiaMcp.Adapters.Contracts.AdapterPreconditionException typed ? new CandidateFault { Kind = typed.IsArgument ? "invalid" : "precondition", Subject = typed.ParamName ?? "arguments" } : ex is CandidateObservationException observed ? observed.Fault : new CandidateFault { Kind = ex is IOException ? "io" : "preflight", Subject = "plc-import-target" }; }
            foreach (var stream in locks.Values)
                try { stream.Dispose(); }
                catch (Exception) /* swallow(cleanup): preserve an explicit uncertain outcome when input lock cleanup fails */
                {
                    if (result.Attempt?.Issued == true)
                    {
                        result.Attempt.RequiresSessionReset = result.RequiresSessionReset = true;
                        result.Attempt.Fault = new CandidateFault { Kind = "completion", Subject = "completion-or-lock-cleanup" };
                    }
                    else result.Fault = new CandidateFault { Kind = "io", Subject = "close-input" };
                }
            return result;
        }
    }
}
