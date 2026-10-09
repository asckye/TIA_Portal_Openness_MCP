using System;
using System.Linq;
using System.Threading;
using System.Text.Json;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.Adapters.Native.Plc;
using TiaMcp.Logic.V4;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        private readonly CompileSession compileCandidates = new CompileSession();
        private CompileAdapter? compileAdapter;
        private object? compileProject;
        private string? compileGeneration;
        private long compileEpoch;
        private void CompileThread()
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.MTA) throw new InvalidOperationException("Compile candidates require the owning MTA thread.");
            VerifyBinding("compile-candidate");
        }
        internal Envelope RunCompileCandidate(CompileRequest request, string password, string mode, bool confirm, string hash, string file)
        {
            string release = ModelContextProtocol.McpServer.ReleaseKey, id = Meta.Correlate(ModelContextProtocol.InvocationJournal.CorrelationId);
            try
            {
                if (_project == null || _portal == null) return CompileSession.Result(release, request.Entry, id, null, new Error("No project is bound.", new ProjectNotBoundDetails()), Outcome.RejectedBeforeOperation, Execution.NotStarted);
                if (compileCandidates.RequiresSessionReset) return CompileSession.Result(release, request.Entry, id, null, new Error("The compile session must be rebuilt.", new SessionResetRequiredDetails("compile-unknown")), Outcome.RejectedBeforeOperation, Execution.NotStarted);
                CompileThread();
                if (!object.Equals(compileProject, _project)) { compileProject = _project; compileAdapter = null; }
                CompileObservation ProjectState()
                {
                    CompileThread(); var binding = GetBindingIdentity()["identity"]!.AsObject(); string generation = binding["generation"]!.GetValue<string>();
                    if (generation != compileGeneration) { compileGeneration = generation; compileEpoch++; }
                    bool valid = _session != null ? _portal!.LocalSessions.Any(s => object.Equals(s, _session) && object.Equals(s.Project, _project))
                        : _portal!.Projects.Any(p => object.Equals(p, _project));
                    return new CompileObservation { Binding = new SessionState { ProcessId = _boundProcessId, ProcessStartUtc = new DateTimeOffset(new DateTime(_processStartTicks, DateTimeKind.Utc)),
                        ProjectFile = CandidatePrimitives.CanonicalProject(_project!.Path.FullName), Ownership = _projectOpenedByUs ? "owned" : "borrowed", Epoch = compileEpoch },
                        ObjectValidity = valid ? "valid" : "invalid", Dirty = _project.IsModified };
                }
                compileAdapter ??= new CompileAdapter(ProjectState, () => CompileTarget(request), CompileThread);
                compileAdapter.Target = () => CompileTarget(request);
                var result = compileCandidates.Run(compileAdapter, release, request.Entry, id, request, password, mode, confirm, hash, file);
                if (result.Meta.RequiresSessionReset) _bindingFault = "Compile outcome is unknown; reset the session before further native access.";
                return result;
            }
            catch (Exception error)
            {
                var mapped = error is CandidateObservationException observed ? CompileSession.Map(observed.Fault, release, hash)
                    : new Error("Compile target or binding is unavailable.", new PreconditionFailedDetails("compile-binding-and-target", null));
                return CompileSession.Result(release, request.Entry, id, null, mapped, Outcome.RejectedBeforeOperation, Execution.NotStarted);
            }
        }
        private CompileNativeTarget CompileTarget(CompileRequest request)
        {
            CompileThread();
            return new PlcOrganisationAdapter(new TiaMcpServer.Worker.EnginePlcOrganisationSession(this)).CompileTarget(request);
        }
    }
}
