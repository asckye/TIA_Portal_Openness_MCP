extern alias plcsim;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters;
using TiaMcp.WorkerChannel;

namespace TiaMcp.PlcWorker
{
    internal sealed class WorkerPrimitiveFailureException : Exception
    {
        internal ChannelFailureException Failure { get; }
        internal WorkerPrimitiveFailureException(ChannelFailureException failure) : base(failure.Message, failure)
        { Failure = failure; }
    }

    // Both worker products use the same allowlist, argument binding and failure boundary.
    internal sealed class FoundationWorkerDispatcher
    {
        private readonly PlcFoundationEngine engine;
        private readonly string releaseKey;
        private readonly Dictionary<string, MethodInfo> methods;
        private readonly Action<Exception>? failureObserved;
        private readonly Assembly policyAssembly;
        internal Func<long>? ProcessStartTicks;
        internal readonly WorkerSessionOutcomeState SessionOutcome = new WorkerSessionOutcomeState();
        private WorkerSessionOutcomeState sessionOutcome => SessionOutcome;
        private bool disconnectAttempted, disconnected, sharedInvalidated;
        private PlcRuntimeState? observed;
        private long bindingEpoch;
        internal bool Ended => disconnectAttempted;
        internal bool Detached => disconnected;
        internal void EndSharedSession() { disconnectAttempted=true; disconnected=true; }
        internal void InvalidateSharedSession() { SessionOutcome.MarkUncertain(); sharedInvalidated=true; }
        internal PlcRuntimeState State => disconnected || sharedInvalidated ? new PlcRuntimeState() : disconnectAttempted ? observed! : engine.ReadState();
        internal FoundationWorkerDispatcher(PlcFoundationEngine engine, Assembly policyAssembly, Action<Exception>? failureObserved = null)
        {
            this.engine = engine;
            this.failureObserved = failureObserved;
            this.policyAssembly = policyAssembly;
            releaseKey = engine.ReleaseKey;
            engine.ValidatePlcVerifiedCandidate = PlcVerifiedImportBridge.Validate;
            engine.ExecutePlcVerifiedWorkflow = PlcVerifiedImportBridge.Execute;
            engine.CollectPlcCompilerEvidence = PlcVerifiedImportBridge.Compiler;
#if HMI_UNIFIED
            TiaMcpServer.Siemens.Services.UnifiedWorkerCodecs.Configure(engine);
#endif
            // Only this application's typed facade is reflected; no arbitrary Siemens
            // type/member names or object handles are accepted on the wire.
            var modules = new List<WorkerOperationModule> { new WorkerOperationModule("", typeof(PlcFoundationEngine), WorkerOperations.Names) };
            modules.Add(new WorkerOperationModule("plc-paging", typeof(PlcFoundationEngine), new[] { "ReadBlockPage" }));
            modules.Add(new WorkerOperationModule("plc-analysis", typeof(PlcFoundationEngine), new[] { "ExportBlockDocument" }));
            foreach (var family in PortedFamilies.All.Where(f => f.Available(releaseKey)))
                modules
                    .Add(new WorkerOperationModule(family.OperationPrefix, family.Name == "F04"
                            ? typeof(plcsim::TiaMcp.PlcWorker.Runtime.PlcSimAdvancedAdapter) : typeof(PlcFoundationEngine), WorkerOperations
                            .FamilyNames(family.Name)));
            methods = WorkerOperationModule.Register(modules);
        }
        internal ChannelBinding Observe()
        {
            // ReadState only copies the adapter's managed lifecycle fields. It performs
            // no Siemens calls. A terminal Disconnect cannot be queried again.
            var state=State;
            if(observed!=null && (observed.ProcessId!=state.ProcessId || observed.ProjectFile!=state.ProjectFile ||
                observed.OwnsProject!=state.OwnsProject || observed.IsLocalSession!=state.IsLocalSession)) bindingEpoch++;
            observed=state;
            return new ChannelBinding(bindingEpoch,state.IsAttached || !string.IsNullOrEmpty(state.ProjectFile));
        }
        internal ChannelResponse? ReadManagedObservation(ChannelRequest request)
        {
            if (request.Method != "adapter.HostObserve") return null;
            using var document = JsonDocument.Parse(request.ArgumentsJson);
            string? operation = document.RootElement.GetProperty("operation").GetString();
            if (operation is not ("status" or "environment" or "group" or "assemblies" or "reflection" or "reflection-deny" or "session.GetState" or "session.ConnectPortal"))
                return null;
            return WorkerObservations.Reply(() => {
                var value = System.Text.Json.Nodes.JsonNode.Parse(request.ArgumentsJson)!.AsObject();
                return WorkerObservations.Read(engine, operation!, value["arguments"]!.AsObject(), ProcessStartTicks);
            });
        }
        internal ChannelResponse Dispatch(ChannelRequest request)
        {
            // The engine retains its own fixture boundary. Only explicitly marked
            // standalone PlcWorker artifacts can enter this path.
            var injected = WorkerFaultInjection.PlcBeforeDispatch(policyAssembly, request, lost => {
                SessionOutcome.MarkUncertain();
                failureObserved?.Invoke(lost);
            });
            if (injected != null) return injected;
            if (request.Method == PlcSimAdvancedWorkerModule.Operation) return PlcSimAdvancedWorkerModule.Dispatch(request);
            if (request.Method == "adapter.HostObserve") return WorkerObservations.Reply(() => {
                var observation = System.Text.Json.Nodes.JsonNode.Parse(request.ArgumentsJson)!.AsObject();
                var operation = (string)observation["operation"]!;
                if (operation == "group.fix") throw new ArgumentException("HostObserve only accepts read-only observations.");
                return WorkerObservations.Read(engine, operation, observation["arguments"]!.AsObject(), ProcessStartTicks);
            });
            bool enteredOperation=false;
            bool readOnly=false;
            try
            {
                var name = request.Method.Substring("adapter.".Length);
                readOnly=WorkerOperations.IsReadOnly(name);
                if (!methods.TryGetValue(name, out var method)) throw new NotSupportedException("Unknown foundation operation: " + name);
                var values = WorkerJson.ParseArguments(request.ArgumentsJson);
                if (name == "plc-online.ExecuteOnline" && WorkerJson.Get(values, "request") is var online
                    && online.ValueKind == JsonValueKind.Object)
                    readOnly |= OnlineCapabilities.IsReadOnly(JsonSerializer.Deserialize<OnlineRequest>(online.GetRawText())!);
                string actionName = WorkerJson.Get(values, "action").ValueKind == JsonValueKind.String ? WorkerJson.Get(values, "action").GetString()! : "";
                string scope = WorkerJson.Get(values, "family").ValueKind == JsonValueKind.String ? WorkerJson.Get(values, "family").GetString()! : WorkerJson
                    .Get(values, "kind").ValueKind == JsonValueKind.String ? WorkerJson.Get(values, "kind").GetString()! : "";
                if (HardwareCapabilities.Unsupported(releaseKey, name, actionName, scope,
                    WorkerJson.Get(values, "positionNumber").ValueKind == JsonValueKind.Number ? WorkerJson.Get(values, "positionNumber").GetInt32() : -1,
                    WorkerJson.Get(values, "extendedPositionNumber").ValueKind == JsonValueKind.Number ? WorkerJson.Get(values, "extendedPositionNumber")
                        .GetInt32() : -1,
                    WorkerJson.Get(values, "password").ValueKind == JsonValueKind.String && !string
                        .IsNullOrEmpty(WorkerJson.Get(values, "password").GetString())) is string unavailable)
                    throw new NotSupportedException(unavailable);
                if(disconnectAttempted && name!="Disconnect")
                    throw new InvalidOperationException("Disconnect ended this worker session; new explicit session required.");
                if(name=="Disconnect" && values.Count!=0) throw new ArgumentException("Disconnect takes no arguments.");
                readOnly=readOnly || (WorkerJson.Get(values,"dryRun").ValueKind==JsonValueKind.True);
                if (WorkerOperations.RequiresPortal(name) && !State.IsAttached)
                    throw new AdapterPreconditionException(TiaOpenness.Shared.SessionBehavior.PortalRequired, "session", false);
                var confirm=WorkerJson.Get(values,"confirm");
                var expected=WorkerJson.Get(values,"expectedProjectFile");
                if(!method.GetParameters().Any(p=>p.Name=="confirm")) values.Remove("confirm");
                if(!method.GetParameters().Any(p=>p.Name=="expectedProjectFile")) values.Remove("expectedProjectFile");
                if(WorkerJson.Get(values,"dryRun").ValueKind==JsonValueKind.False)
                {
                    if(!WorkerJson.IsBoolean(confirm)) throw new AdapterPreconditionException("Execution requires confirm=true.","confirm");
                    if(!WorkerJson.IsString(expected))
                        throw new AdapterPreconditionException("Execution requires an absolute expected project file.","expectedProjectFile");
                    try
                    {
                        var optionalRequest = WorkerJson.Get(values, "request");
                        string operation = optionalRequest.ValueKind == JsonValueKind.Object && optionalRequest
                            .TryGetProperty("Operation", out var operationValue) ? operationValue.GetString()! : "";
                        string action = optionalRequest.ValueKind == JsonValueKind.Object && optionalRequest.TryGetProperty("Action", out var actionValue)
                            ? actionValue.GetString()! : "";
                        OptionalEngineeringMutations.ValidateWrite(name, operation, action, confirm.GetBoolean(), expected.GetString()!,
                            () => { var state = State; return (state.IsAttached, state.ProjectFile); },
                            parameter => throw new AdapterPreconditionException(parameter == "confirm" ? "Execution requires confirm=true."
                                : "Portal settings require the same attached session and project binding.", parameter),
                            () => MutationIdentityPolicy.ValidateTarget(false,confirm.GetBoolean(),expected.GetString()!,name,releaseKey,
                                WorkerJson.IsString(WorkerJson.Get(values,"path")) ? WorkerJson.Get(values,"path").GetString()! : "",
                                WorkerJson.IsString(WorkerJson.Get(values,"directoryPath")) ? WorkerJson.Get(values,"directoryPath").GetString()! : "",
                                WorkerJson.IsString(WorkerJson.Get(values,"projectName")) ? WorkerJson.Get(values,"projectName").GetString()! : "",
                                WorkerOperations.MutationIdentity(name,engine.RequireProjectIdentity,engine.SharedFamilyMutationIdentity)));
                    }
                    catch(ArgumentException ex) { throw new AdapterPreconditionException(ex.Message,"expectedProjectFile",true,ex); }
                }
                var parameters = method.GetParameters();
                // Host mutation guards are transport metadata for primitive methods without a preview parameter.
                // Validate them above, then bind only the native facade's declared arguments.
                var call = WorkerJson.FacadeArguments(parameters, values);
                enteredOperation=true;
                if(name=="Disconnect") disconnectAttempted=true;
                var result = method.Invoke(engine, call);
                if (result is IWorkerOperationReply reply && reply.RequiresSessionReset)
                    sessionOutcome.MarkUncertain(blockReads: reply.BlockReadsAfterUncertain, mutation: !readOnly);
                if(name=="Disconnect") disconnected=true;
                return ChannelResponse.Success(WorkerJson.Serialize(result));
            }
            catch (Exception ex)
            {
                var cause = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                // A refused disconnect (owned portal with an open project, P8-81c F1) made no native call: the session stays usable.
                if (disconnectAttempted && !disconnected && cause is AdapterPreconditionException && !engine.DisconnectAttempted) disconnectAttempted = false;
                failureObserved?.Invoke(cause);
                if (cause is WorkerPrimitiveFailureException primitiveFailure)
                    return ChannelResponse.Error(primitiveFailure.Failure);
                var classification = WorkerFailurePolicy.Classify(cause, enteredOperation, readOnly);
                return ChannelResponse.Error(new ChannelFailureException(WorkerFailurePolicy.DiagnosticCause(cause).Message, classification.Code,
                    classification.Outcome, WorkerJson.Evidence(cause)));
            }
        }
    }
}
