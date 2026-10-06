using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using Siemens.Engineering.SW;
using TiaMcp.Adapters.Native.Plc;
using TiaMcp.Adapters.Contracts.Candidates;

namespace TiaMcp.PlcFoundation
{
    public sealed partial class PlcFoundationEngine
    {
        private PlcSourceAdapter? sourceCandidateAdapter;
        private object? sourceCandidateProject;
        private PlcSoftware? sourceCandidateSoftware;
        public SourceReply SourceCandidate(SourceCall candidate, string mode = "preview", long bindingEpoch = 0)
        {
            var reply = new SourceReply(); var locks = new Dictionary<string, Stream>(StringComparer.OrdinalIgnoreCase);
            try
            {
                Check(); var project = Project(); var selected = ReadSelection(candidate.Request.SoftwarePath);
                PlcLifecyclePolicy.RequireLocalSessionExecution(lifecycle.IsLocalSession, false);
                CandidateIdentity Identity()
                {
                    Check(); if (!object.Equals(project, Project())) CandidatePrimitives.Fail("identity", "project-binding");
                    int pid = lifecycle.ProcessId ?? throw new InvalidOperationException("Explicit process identity required.");
                    using var process = Process.GetProcessById(pid); if (process.HasExited) CandidatePrimitives.Fail("identity", "process-exited");
                    string file = project.Path.FullName; RequireProjectIdentity(file);
                    return new CandidateIdentity(pid, new DateTimeOffset(process.StartTime.ToUniversalTime()), CandidatePrimitives.CanonicalProject(file), bindingEpoch);
                }
                PlcSoftware Software()
                {
                    Identity(); var fresh = ReadSelection(candidate.Request.SoftwarePath);
                    if (fresh.ExactPath != selected.ExactPath || !object.Equals(fresh.Value, selected.Value) || !object.Equals(fresh.Context, selected.Context)) CandidatePrimitives.Fail("identity", "plc-binding");
                    return fresh.Value;
                }
                if (!object.Equals(sourceCandidateProject, project) || !object.Equals(sourceCandidateSoftware, selected.Value))
                { sourceCandidateAdapter = null; sourceCandidateProject = project; sourceCandidateSoftware = selected.Value; }
                sourceCandidateAdapter ??= new PlcSourceAdapter(Identity, Software, () => selected.ExactPath, () => RequireTargetOffline(ReadSelection(candidate.Request.SoftwarePath)), true);
                var adapter = sourceCandidateAdapter;
                adapter.Identity = Identity; adapter.Software = Software; adapter.SoftwarePath = () => selected.ExactPath; adapter.RequireOffline = () => RequireTargetOffline(ReadSelection(candidate.Request.SoftwarePath));
                if (candidate.Action == "observe") { if (mode != "preview") CandidatePrimitives.Invalid("mode"); reply.Observation = adapter.Observe(); }
                else if (candidate.Action == "execute")
                {
                    var check = candidate.Check;
                    if (mode != "apply" || check == null || check.Release != ReleaseKey || check.Request.SoftwarePath != candidate.Request.SoftwarePath || check.Request.Items != null && check.Request.Items.Length == 0) CandidatePrimitives.Invalid("source-candidate");
                    foreach (var f in check!.Files) { CandidateImportFiles.SafePath(new FileInfo(f.Path)); locks.Add(f.Path, new FileStream(f.Path, FileMode.Open, FileAccess.Read, FileShare.Read)); }
                    reply.Attempt = CandidateExecution.Source(adapter, check, locks); reply.RequiresSessionReset = reply.Attempt.RequiresSessionReset;
                }
                else CandidatePrimitives.Invalid("source-action");
            }
            catch (PlcPathException ex) { reply.Fault = new CandidateFault { Kind = ex.Ambiguous ? "ambiguous" : "not-found", Subject = ex.Path }; reply.PathCandidates = ex.Candidates; }
            catch (Exception ex) { reply.Fault = ex is CandidateObservationException observed ? observed.Fault : new CandidateFault { Kind = "preflight", Subject = "source-target" }; }
            foreach (var stream in locks.Values)
                try { stream.Dispose(); }
                catch (Exception) /* swallow(cleanup): failed lock cleanup is explicitly returned and poisons issued writes */
                {
                    if (reply.Attempt?.Issued == true) { reply.Attempt.RequiresSessionReset = reply.RequiresSessionReset = true; reply.Attempt.Fault = new CandidateFault { Kind = "completion", Subject = "source-lock-cleanup" }; }
                    else reply.Fault = new CandidateFault { Kind = "io", Subject = "close-input" };
                }
            return reply;
        }
    }
}
