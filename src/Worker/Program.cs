using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using TiaMcp.Adapters.Contracts;
using TiaMcp.PlcFoundation;
using TiaMcp.PlcWorker;
using TiaMcp.WorkerChannel;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable("TIA_MCP_LOG_SWALLOWED") == "1")
            TiaMcp.Shared.SwallowedExceptions.Sink = message => Console.Error.WriteLine(message);
        Console.InputEncoding = new System.Text.UTF8Encoding(false);
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        Console.SetError(new System.IO.StreamWriter(Console.OpenStandardError(), new System.Text.UTF8Encoding(false)) { AutoFlush = true });
        // A worker is launched lazily by its exact-release host.
        if (args.Length != 4 || args[0] != "--native-session")
        {
            Console.Error.WriteLine("Usage: --native-session <exact-release-key> <verified-public-api-directory> <launch-nonce>.");
            return 2;
        }
        var api = Path.GetFullPath(args[2]);
        if (!Directory.Exists(api)) throw new DirectoryNotFoundException(api);
        AppDomain.CurrentDomain.AssemblyResolve += (sender, request) =>
        {
            var wanted = new AssemblyName(request.Name);
            if (wanted.Name == null || !wanted.Name.StartsWith("Siemens.Engineering", StringComparison.Ordinal)) return null;
            var file = Path.Combine(api, wanted.Name + ".dll");
            // Siemens.Engineering.Contract and Siemens.Engineering.ClientAdapter.Interfaces are not in the
            // PublicAPI folder; TIA's own resolution loads them. Throwing here blocked it on every release.
            if (!File.Exists(file)) return null;
            var actual = AssemblyName.GetAssemblyName(file);
            if (!string.Equals(wanted.FullName, actual.FullName, StringComparison.OrdinalIgnoreCase))
                throw new FileLoadException("Selected SDK identity mismatch: " + wanted.FullName + " / " + actual.FullName, file);
            return Assembly.LoadFrom(file);
        };
        return Run(args[1], api, args[3]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string releaseKey, string api, string nonce)
    {
        using (var engine = new PlcFoundationEngine(releaseKey, api))
        {
            // Only this application's typed facade is reflected; no arbitrary Siemens
            // type/member names or object handles are accepted on the wire.
            bool deviceCandidateEnabled = releaseKey == "19" && TiaMcp.Adapters.Contracts.Candidates.CandidatePolicy.Enabled(typeof(Program).Assembly, releaseKey, "P6-DEVICE");
            bool compileCandidateEnabled = TiaMcp.Adapters.Contracts.Candidates.CandidatePolicy.Enabled(typeof(Program).Assembly, releaseKey, "P6-COMPILE");
            bool sourceCandidateEnabled = TiaMcp.Adapters.Contracts.Candidates.CandidatePolicy.Enabled(typeof(Program).Assembly, releaseKey, "P6-SOURCE");
            engine.SourceCandidateEnabled = sourceCandidateEnabled;
            bool saveCloseCandidateEnabled = TiaMcp.Adapters.Contracts.Candidates.CandidatePolicy.Enabled(typeof(Program).Assembly, releaseKey, "P6-CLOSE");
            bool sessionCandidateEnabled = TiaMcp.Adapters.Contracts.Candidates.CandidatePolicy.Enabled(typeof(Program).Assembly, releaseKey, "P6-SESSION");
            bool importCandidateEnabled = TiaMcp.Adapters.Contracts.Candidates.CandidatePolicy.Enabled(typeof(Program).Assembly, releaseKey, "P6-IMPORT");
            bool exportCandidateEnabled = TiaMcp.Adapters.Contracts.Candidates.CandidatePolicy.Enabled(typeof(Program).Assembly, releaseKey, "P6-EXPORT");
            var methods = typeof(PlcFoundationEngine).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => WorkerOperations.Names.Contains(m.Name) || deviceCandidateEnabled && m.Name == WorkerOperations.DeviceCreationCandidate
                    || importCandidateEnabled && m.Name == WorkerOperations.PlcImportCandidate
                    || exportCandidateEnabled && m.Name == WorkerOperations.PlcExportCandidate
                    || sessionCandidateEnabled && m.Name == WorkerOperations.SessionCandidate
                    || saveCloseCandidateEnabled && m.Name == WorkerOperations.SaveCloseCandidate || sourceCandidateEnabled && m.Name == WorkerOperations.SourceCandidate || compileCandidateEnabled && m.Name == WorkerOperations.CompileCandidate).ToDictionary(m => m.Name, StringComparer.Ordinal);
            if(methods.Count!=WorkerOperations.Names.Count + (deviceCandidateEnabled ? 1 : 0) + (importCandidateEnabled ? 1 : 0) + (exportCandidateEnabled ? 1 : 0) + (sessionCandidateEnabled ? 1 : 0) + (saveCloseCandidateEnabled ? 1 : 0) + (sourceCandidateEnabled ? 1 : 0) + (compileCandidateEnabled ? 1 : 0)) throw new InvalidOperationException("Worker operation allowlist does not match the compiled facade.");
            var sessionOutcome=new WorkerSessionOutcomeState();
            bool disconnectAttempted=false;
            bool disconnected=false;
            PlcRuntimeState? observed=null;
            long bindingEpoch=0;
            ChannelBinding Observe()
            {
                // ReadState only copies the adapter's managed lifecycle fields. It performs
                // no Siemens calls. A terminal Disconnect cannot be queried again.
                var state=disconnected ? new PlcRuntimeState() : disconnectAttempted ? observed! : engine.ReadState();
                if(observed!=null && (observed.ProcessId!=state.ProcessId || observed.ProjectFile!=state.ProjectFile ||
                    observed.OwnsProject!=state.OwnsProject || observed.IsLocalSession!=state.IsLocalSession)) bindingEpoch++;
                observed=state;
                return new ChannelBinding(bindingEpoch,state.IsAttached || !string.IsNullOrEmpty(state.ProjectFile));
            }
            string HashFile(string path)
            {
                using(var stream=File.OpenRead(path))
                using(var sha=System.Security.Cryptography.SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
            var identity=new ChannelIdentity(releaseKey,HashFile(typeof(Program).Assembly.Location),
                HashFile(typeof(PlcFoundationEngine).Assembly.Location),System.Diagnostics.Process.GetCurrentProcess().Id,nonce);
            var input=Console.OpenStandardInput();
            var server=new ChannelServer(input,Console.OpenStandardOutput(),identity,Observe,request =>
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
                    var classification = WorkerFailurePolicy.Classify(cause, enteredOperation, readOnly);
                    return ChannelResponse.Error(new ChannelFailure(WorkerFailurePolicy.DiagnosticCause(cause).Message, classification.Code,
                        classification.Outcome, WorkerJson.Evidence(cause)));
                }
            });
            try { server.Run(); }
            catch(ChannelFault ex)
            {
                Console.Error.WriteLine(ex.Message);
                // A poisoned session never dispatches again. Preserve the existing
                // native teardown boundary: dispose the engine only when the host
                // closes stdin, not immediately after an uncertain native outcome.
                input.CopyTo(Stream.Null);
                return 1;
            }
        }
        return 0;
    }
}
