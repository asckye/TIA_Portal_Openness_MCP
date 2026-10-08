using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Siemens.Engineering.SW;
using TiaMcp.Adapters.Native.Plc;
using TiaMcp.Adapters.Contracts.Candidates;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        private readonly List<KeyValuePair<PlcSoftware, PlcExportAdapter>> exportCandidateAdapters = new List<KeyValuePair<PlcSoftware, PlcExportAdapter>>();
        public ExportCandidateReply ExportPlcCandidate(ExportCandidateCall candidate, string mode = "preview", long bindingEpoch = 0)
        {
            var result = new ExportCandidateReply();
            try
            {
                Check();
                var selected = ReadSelection(candidate.Request.SoftwarePath);
                TiaOpenness.Shared.NativeExportPolicy.RequireSoftwarePath(candidate.Request.SoftwarePath, selected.ExactPath, candidate.Tool == "ExportPlcBlocks" || candidate.Tool == "ExportPlcTypes" || candidate.Tool == "ExportPlcBlocksDocuments");
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
                PlcExportAdapter? adapter = null;
                foreach (var pair in exportCandidateAdapters) if (object.Equals(pair.Key, selected.Value)) adapter = pair.Value;
                if (adapter == null)
                {
                    if (exportCandidateAdapters.Count >= 128) CandidatePrimitives.Refuse("The session PLC budget is exhausted.", "plcs", 128, exportCandidateAdapters.Count + 1);
                    adapter = new PlcExportAdapter(ReleaseKey, Identity, Software, () => RequireTargetOffline(ReadSelection(candidate.Request.SoftwarePath)), false);
                    exportCandidateAdapters.Add(new KeyValuePair<PlcSoftware, PlcExportAdapter>(selected.Value, adapter));
                }
                adapter.Identity = Identity; adapter.Software = Software; adapter.RequireOffline = () => RequireTargetOffline(ReadSelection(candidate.Request.SoftwarePath));
                adapter.OverwriteSupported = false;
                switch (candidate.Action)
                {
                    case "identity": result.Identity = adapter.ReadIdentity(); break;
                    case "objects": result.Objects = adapter.ReadObjects(candidate.Tool, candidate.Request).ToArray(); break;
                    case "overwrite": result.OverwriteSupported = adapter.SupportsOverwrite(candidate.Tool); break;
                    case "execute":
                        if (mode != "apply" || candidate.Check == null || candidate.Check.Release != ReleaseKey || candidate.Check.Tool != candidate.Tool
                            || candidate.Check.Request.SoftwarePath != candidate.Request.SoftwarePath) CandidatePrimitives.Invalid("candidate");
                        result.Attempt = CandidateExecution.Export(adapter, candidate.Check!);
                        result.RequiresSessionReset = result.Attempt.RequiresSessionReset;
                        break;
                    default: CandidatePrimitives.Invalid("candidate.action"); break;
                }
            }
            catch (Exception ex)
            { result.Fault = ex is TiaMcp.Adapters.Contracts.AdapterPreconditionException typed ? new CandidateFault { Kind = typed.IsArgument ? "invalid" : "precondition", Subject = typed.ParamName ?? "arguments" } : ex is CandidateObservationException observed ? observed.Fault : new CandidateFault { Kind = ex is IOException ? "io" : "preflight", Subject = "plc-import-target" }; }
            return result;
        }
    }
}
