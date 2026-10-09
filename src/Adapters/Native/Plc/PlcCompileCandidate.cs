using System;
using System.Diagnostics;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.Adapters.Native.Plc;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        private CompileAdapter? compileCandidateAdapter;
        private object? compileCandidateProject;
        public CompileReply CompileCandidate(CompileCall candidate, string mode = "preview", long bindingEpoch = 0)
        {
            var reply = new CompileReply();
            try
            {
                Check(); var p = Project();
                if (candidate.Request.Entry != "CompilePlcSoftware" && candidate.Request.Entry != "CompilePlcDiagnostics" && candidate.Request.Entry != "CompileDevice" && candidate.Request.Entry != "CompileHmiDiagnostics") CandidatePrimitives.Invalid("unadvertised-compile-entry");
                CompileNativeTarget Target()
                {
                    Check();
                    if (candidate.Request.Entry == "CompileDevice" || candidate.Request.Entry == "CompileHmiDiagnostics")
                        return new PlcOrganisationAdapter(EngineeringPlcOrganisationSession ?? new OrganisationSession(this)).CompileTarget(candidate.Request);
                    var selected = ReadSelection(candidate.Request.SoftwarePath);
                    return new CompileNativeTarget { Owner = selected.Value, Software = selected.Value, SafetyItem = (DeviceItem)selected.Context!,
                        OfflineOwner = (DeviceItem)selected.Context!, Name = selected.Value.Name, Kind = "PlcSoftware",
                        Compiler = ((IEngineeringServiceProvider)selected.Value).GetService<ICompilable>() };
                }
                CompileObservation State()
                {
                    Check(); if (!object.Equals(p, Project())) CandidatePrimitives.Fail("identity", "project-binding");
                    int pid = lifecycle.ProcessId ?? throw new InvalidOperationException("Explicit process required.");
                    using var process = Process.GetProcessById(pid); if (process.HasExited) CandidatePrimitives.Fail("identity", "process-exited");
                    string file = p.Path.FullName; RequireProjectIdentity(file);
                    PlcLifecyclePolicy.RequireLocalSessionExecution(lifecycle.IsLocalSession, false);
                    return new CompileObservation { Binding = new SessionState { ProcessId = pid, ProcessStartUtc = new DateTimeOffset(process.StartTime.ToUniversalTime()),
                        ProjectFile = CandidatePrimitives.CanonicalProject(file), Ownership = lifecycle.OwnsProject ? "owned" : "borrowed", Epoch = bindingEpoch, WorkerEpoch = bindingEpoch },
                        ObjectValidity = portal!.Projects.Any(x => object.Equals(x, p)) ? "valid" : "invalid", Dirty = p.IsModified };
                }
                if (!object.Equals(compileCandidateProject, p)) { compileCandidateProject = p; compileCandidateAdapter = null; }
                compileCandidateAdapter ??= new CompileAdapter(State, Target, () => Check());
                // Fresh callbacks carry the worker-owned epoch and current entry selection.
                compileCandidateAdapter.Target = Target;
                compileCandidateAdapter.Project = State;
                if (candidate.Action == "observe" && mode == "preview") reply.Observation = compileCandidateAdapter.Observe();
                else if (candidate.Action == "execute" && mode == "apply" && candidate.Check != null)
                {
                    if (candidate.Request.Entry != candidate.Check.Request.Entry || candidate.Request.SoftwarePath != candidate.Check.Request.SoftwarePath || !candidate.Request.DevicePath.SequenceEqual(candidate.Check.Request.DevicePath) || !candidate.Request.ItemPath.SequenceEqual(candidate.Check.Request.ItemPath)) CandidatePrimitives.Invalid("compile-scope");
                    reply.Attempt = compileCandidateAdapter.Execute(candidate.Check, candidate.Password); reply.RequiresSessionReset = reply.Attempt.RequiresSessionReset;
                }
                else CandidatePrimitives.Invalid("compile-action");
            }
            catch (Exception error) { reply.Fault = error is CandidateObservationException observed ? observed.Fault : new CandidateFault { Kind = "preflight", Subject = "compile-target" }; }
            return reply;
        }
    }
}
