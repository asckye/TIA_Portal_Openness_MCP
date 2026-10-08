using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using TiaMcp.Adapters.Contracts;
using TiaMcp.PlcFoundation;
using TiaMcp.WorkerChannel;

namespace TiaMcp.PlcWorker
{
    // Both worker products use the same allowlist, argument binding and failure boundary.
    internal sealed class FoundationWorkerDispatcher
    {
        private readonly PlcFoundationEngine engine;
        private readonly string releaseKey;
        private readonly Dictionary<string, MethodInfo> methods;
        private readonly Action<Exception>? failureObserved;
        internal readonly WorkerSessionOutcomeState SessionOutcome = new WorkerSessionOutcomeState();
        private WorkerSessionOutcomeState sessionOutcome => SessionOutcome;
        private bool disconnectAttempted, disconnected;
        private PlcRuntimeState? observed;
        private long bindingEpoch;
        internal bool Ended => disconnectAttempted;
        internal PlcRuntimeState State => disconnected ? new PlcRuntimeState() : disconnectAttempted ? observed! : engine.ReadState();
        internal FoundationWorkerDispatcher(PlcFoundationEngine engine, Assembly policyAssembly, Action<Exception>? failureObserved = null)
        {
            this.engine = engine;
            this.failureObserved = failureObserved;
            releaseKey = engine.ReleaseKey;
            // Only this application's typed facade is reflected; no arbitrary Siemens
            // type/member names or object handles are accepted on the wire.
            bool deviceCandidateEnabled = releaseKey == "19" && TiaMcp.Adapters.Contracts.Candidates.CandidatePolicy.Enabled(policyAssembly, releaseKey, "P6-DEVICE");
            bool compileCandidateEnabled = TiaMcp.Adapters.Contracts.Candidates.CandidatePolicy.Enabled(policyAssembly, releaseKey, "P6-COMPILE");
            bool sourceCandidateEnabled = TiaMcp.Adapters.Contracts.Candidates.CandidatePolicy.Enabled(policyAssembly, releaseKey, "P6-SOURCE");
            engine.SourceCandidateEnabled = sourceCandidateEnabled;
            bool saveCloseCandidateEnabled = TiaMcp.Adapters.Contracts.Candidates.CandidatePolicy.Enabled(policyAssembly, releaseKey, "P6-CLOSE");
            bool sessionCandidateEnabled = TiaMcp.Adapters.Contracts.Candidates.CandidatePolicy.Enabled(policyAssembly, releaseKey, "P6-SESSION");
            bool importCandidateEnabled = TiaMcp.Adapters.Contracts.Candidates.CandidatePolicy.Enabled(policyAssembly, releaseKey, "P6-IMPORT");
            bool exportCandidateEnabled = TiaMcp.Adapters.Contracts.Candidates.CandidatePolicy.Enabled(policyAssembly, releaseKey, "P6-EXPORT");
            methods = typeof(PlcFoundationEngine).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => WorkerOperations.Names.Contains(m.Name) || deviceCandidateEnabled && m.Name == WorkerOperations.DeviceCreationCandidate
                    || importCandidateEnabled && m.Name == WorkerOperations.PlcImportCandidate
                    || exportCandidateEnabled && m.Name == WorkerOperations.PlcExportCandidate
                    || sessionCandidateEnabled && m.Name == WorkerOperations.SessionCandidate
                    || saveCloseCandidateEnabled && m.Name == WorkerOperations.SaveCloseCandidate || sourceCandidateEnabled && m.Name == WorkerOperations.SourceCandidate || compileCandidateEnabled && m.Name == WorkerOperations.CompileCandidate).ToDictionary(m => m.Name, StringComparer.Ordinal);
            if(methods.Count!=WorkerOperations.Names.Count + (deviceCandidateEnabled ? 1 : 0) + (importCandidateEnabled ? 1 : 0) + (exportCandidateEnabled ? 1 : 0) + (sessionCandidateEnabled ? 1 : 0) + (saveCloseCandidateEnabled ? 1 : 0) + (sourceCandidateEnabled ? 1 : 0) + (compileCandidateEnabled ? 1 : 0)) throw new InvalidOperationException("Worker operation allowlist does not match the compiled facade.");
        }
        internal ChannelBinding Observe()
        {
            // ReadState only copies the adapter's managed lifecycle fields. It performs
            // no Siemens calls. A terminal Disconnect cannot be queried again.
            var state=disconnected ? new PlcRuntimeState() : disconnectAttempted ? observed! : engine.ReadState();
            if(observed!=null && (observed.ProcessId!=state.ProcessId || observed.ProjectFile!=state.ProjectFile ||
                observed.OwnsProject!=state.OwnsProject || observed.IsLocalSession!=state.IsLocalSession)) bindingEpoch++;
            observed=state;
            return new ChannelBinding(bindingEpoch,state.IsAttached || !string.IsNullOrEmpty(state.ProjectFile));
        }
        internal ChannelResponse Dispatch(ChannelRequest request)
        {
            bool enteredOperation=false;
            bool readOnly=false;
            try
            {
                var name = request.Method.Substring("adapter.".Length);
                readOnly=WorkerOperations.IsReadOnly(name);
                if (!methods.TryGetValue(name, out var method)) throw new NotSupportedException("Unknown foundation operation: " + name);
                var values = WorkerJson.ParseArguments(request.ArgumentsJson);
                if (name == WorkerOperations.DeviceCreationCandidate || name == WorkerOperations.PlcImportCandidate || name == WorkerOperations.PlcExportCandidate || name == WorkerOperations.SessionCandidate || name == WorkerOperations.SaveCloseCandidate || name == WorkerOperations.SourceCandidate || name == WorkerOperations.CompileCandidate)
                {
                    if (values.ContainsKey("bindingEpoch")) throw new ArgumentException("The worker owns the binding epoch.");
                    values["bindingEpoch"] = System.Text.Json.JsonSerializer.SerializeToElement(bindingEpoch);
                    var candidateMode = WorkerJson.Get(values, "mode");
                    string? mode = candidateMode.ValueKind == System.Text.Json.JsonValueKind.Undefined ? null : candidateMode.GetString();
                    readOnly = WorkerOperations.IsDevicePreview(name, mode) || WorkerOperations.IsImportPreview(name, mode) || WorkerOperations.IsExportPreview(name, mode) || WorkerOperations.IsSessionPreview(name, mode) || WorkerOperations.IsSaveClosePreview(name, mode) || WorkerOperations.IsSourcePreview(name, mode) || WorkerOperations.IsCompilePreview(name, mode);
                }
                if(disconnectAttempted && name!="Disconnect") throw new InvalidOperationException("Disconnect ended this worker session; new explicit session required.");
                if(name=="Disconnect" && values.Count!=0) throw new ArgumentException("Disconnect takes no arguments.");
                readOnly=readOnly || (WorkerJson.Get(values,"dryRun").ValueKind==JsonValueKind.True);
                sessionOutcome.RequireUsable(readOnly);
                var confirm=WorkerJson.Get(values,"confirm");
                var expected=WorkerJson.Get(values,"expectedProjectFile");
                if(!method.GetParameters().Any(p=>p.Name=="confirm")) values.Remove("confirm");
                if(!method.GetParameters().Any(p=>p.Name=="expectedProjectFile")) values.Remove("expectedProjectFile");
                if(WorkerJson.Get(values,"dryRun").ValueKind==JsonValueKind.False)
                {
                    if(!WorkerJson.IsBoolean(confirm)) throw new AdapterPreconditionException("Execution requires confirm=true.","confirm");
                    if(!WorkerJson.IsString(expected)) throw new AdapterPreconditionException("Execution requires an absolute expected project file.","expectedProjectFile");
                    try
                    {
                        MutationIdentityPolicy.ValidateTarget(false,confirm.GetBoolean(),expected.GetString()!,name,releaseKey,
                            WorkerJson.IsString(WorkerJson.Get(values,"path")) ? WorkerJson.Get(values,"path").GetString()! : "",
                            WorkerJson.IsString(WorkerJson.Get(values,"directoryPath")) ? WorkerJson.Get(values,"directoryPath").GetString()! : "",
                            WorkerJson.IsString(WorkerJson.Get(values,"projectName")) ? WorkerJson.Get(values,"projectName").GetString()! : "",engine.RequireProjectIdentity);
                    }
                    catch(ArgumentException ex) { throw new AdapterPreconditionException(ex.Message,"expectedProjectFile",true,ex); }
                }
                var parameters = method.GetParameters();
                var call = WorkerJson.Arguments(parameters, values);
                enteredOperation=true;
                if(name=="Disconnect") disconnectAttempted=true;
                var result = method.Invoke(engine, call);
                if (result is TiaMcp.Adapters.Contracts.Candidates.DeviceCandidateReply deviceCandidate && deviceCandidate.RequiresSessionReset
                    || result is TiaMcp.Adapters.Contracts.Candidates.ImportCandidateReply importCandidate && importCandidate.RequiresSessionReset
                    || result is TiaMcp.Adapters.Contracts.Candidates.ExportCandidateReply exportCandidate && exportCandidate.RequiresSessionReset
                    || result is TiaMcp.Adapters.Contracts.Candidates.SessionCandidateReply sessionCandidate && sessionCandidate.RequiresSessionReset
                    || result is TiaMcp.Adapters.Contracts.Candidates.SaveCloseReply saveCloseCandidate && saveCloseCandidate.RequiresSessionReset || result is TiaMcp.Adapters.Contracts.Candidates.SourceReply sourceCandidate && sourceCandidate.RequiresSessionReset || result is TiaMcp.Adapters.Contracts.Candidates.CompileReply compileCandidate && compileCandidate.RequiresSessionReset) sessionOutcome.MarkUncertain(blockReads: true);
                if(result is PlcDeviceAddResult deviceAdd && deviceAdd.RequiresSessionReset) sessionOutcome.MarkUncertain();
                if(result is PlcBatchDocumentImportResult batchDocuments && batchDocuments.RequiresSessionReset) sessionOutcome.MarkUncertain(blockReads: true);
                if(result is PlcDocumentImportResult documentImport && documentImport.RequiresSessionReset) sessionOutcome.MarkUncertain();
                if(result is PlcExternalSourceDeleteResult deleted && deleted.RequiresSessionReset) sessionOutcome.MarkUncertain();
                if(result is PlcExternalSourceWorkflowResult source && source.RequiresSessionReset) sessionOutcome.MarkUncertain();
                if(result is PlcBatchDocumentExportResult documents && documents.RequiresSessionReset) sessionOutcome.MarkUncertain();
                if(result is PlcDocumentExportResult document && document.RequiresSessionReset) sessionOutcome.MarkUncertain();
                if(result is PlcSpecialExportResult special && special.RequiresSessionReset) sessionOutcome.MarkUncertain();
                if(result is PlcBatchExportResult batch && batch.RequiresSessionReset) sessionOutcome.MarkUncertain();
                if(result is PlcBatchImportResult imported && imported.RequiresSessionReset) sessionOutcome.MarkUncertain();
                if (result is TiaMcp.Adapters.Contracts.Candidates.SaveCloseReply closeReply && closeReply.Attempt?.Issued == true
                    && (string?)values["candidate"].GetProperty("Check").GetProperty("Request").GetProperty("Action").GetString() == "disconnect")
                { disconnectAttempted = true; disconnected = closeReply.Attempt.Fault == null; }
                if(name=="Disconnect") disconnected=true;
                return ChannelResponse.Success(WorkerJson.Serialize(result));
            }
            catch (Exception ex)
            {
                var cause = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                failureObserved?.Invoke(cause);
                var classification = WorkerFailurePolicy.Classify(cause, enteredOperation, readOnly);
                return ChannelResponse.Error(new ChannelFailure(WorkerFailurePolicy.DiagnosticCause(cause).Message, classification.Code,
                    classification.Outcome, WorkerJson.Evidence(cause)));
            }
        }
    }
}
