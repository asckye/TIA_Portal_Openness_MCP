using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TiaMcp.PlcFoundation;
using TiaMcp.PlcWorker;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable("TIA_MCP_LOG_SWALLOWED") == "1")
            TiaMcp.Shared.SwallowedExceptions.Sink = message => Console.Error.WriteLine(message);
        Console.InputEncoding = new System.Text.UTF8Encoding(false);
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        // A worker is launched lazily by its exact-release host.
        if (args.Length != 3 || args[0] != "--native-session")
        {
            Console.Error.WriteLine("Usage: --native-session <exact-release-key> <verified-public-api-directory>.");
            return 2;
        }
        var api = Path.GetFullPath(args[2]);
        if (!Directory.Exists(api)) throw new DirectoryNotFoundException(api);
        AppDomain.CurrentDomain.AssemblyResolve += (sender, request) =>
        {
            var wanted = new AssemblyName(request.Name);
            if (wanted.Name == null || !wanted.Name.StartsWith("Siemens.Engineering", StringComparison.Ordinal)) return null;
            var file = Path.Combine(api, wanted.Name + ".dll");
            if (!File.Exists(file)) throw new FileNotFoundException("No private/runtime fallback: missing selected Siemens dependency.", file);
            var actual = AssemblyName.GetAssemblyName(file);
            if (!string.Equals(wanted.FullName, actual.FullName, StringComparison.OrdinalIgnoreCase))
                throw new FileLoadException("Selected SDK identity mismatch: " + wanted.FullName + " / " + actual.FullName, file);
            return Assembly.LoadFrom(file);
        };
        return Run(args[1], api);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string releaseKey, string api)
    {
        using (var engine = new PlcFoundationEngine(releaseKey, api))
        {
            // Only this application's typed facade is reflected; no arbitrary Siemens
            // type/member names or object handles are accepted on the wire.
            var methods = typeof(PlcFoundationEngine).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => WorkerOperations.Names.Contains(m.Name)).ToDictionary(m => m.Name, StringComparer.Ordinal);
            if(methods.Count!=WorkerOperations.Names.Count) throw new InvalidOperationException("Worker operation allowlist does not match the compiled facade.");
            var sessionOutcome=new WorkerSessionOutcomeState();
            bool disconnectAttempted=false;
            string? line;
            while ((line = Console.ReadLine()) != null)
            {
                JToken? id = null;
                bool enteredOperation=false;
                bool readOnly=false;
                try
                {
                    if (line.Length > 1024 * 1024) throw new ArgumentException("Worker request exceeds one MiB.");
                    var request = JObject.Parse(line, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                    id = request["id"];
                    var name = (string?)request["operation"] ?? "";
                    readOnly=name=="SearchHardwareCatalog" || name=="PlanPlcExternalSourceImport" || name=="ReadState" || name=="ReadPortalProcessProjects" || name=="ReadPortalConnectReadiness" || name=="ReadWatchTableNames" || name=="ReadTechnologyObjects" || name=="ReadSoftwareInfo" || name=="ReadSoftwareTree" || name=="ReadExternalSourceNames" || name=="ListTags" || name=="ListUserConstants" || name=="ListSystemConstants" || name=="ReadBlockInfo" || name=="ReadTypeInfo" || name=="ReadBlocks" || name=="ReadTypes" || name=="ReadTagTableNames" || name=="ReadBlockHierarchy" || name=="ReadProjectTree" || name=="ListProjects";
                    if (!methods.TryGetValue(name, out var method)) throw new NotSupportedException("Unknown foundation operation: " + name);
                    var values = request["arguments"] as JObject ?? new JObject();
                    if(disconnectAttempted && name!="Disconnect") throw new InvalidOperationException("Disconnect ended this worker session; new explicit session required.");
                    if(name=="Disconnect" && values.HasValues) throw new ArgumentException("Disconnect takes no arguments.");
                    readOnly=readOnly || (values["dryRun"]?.Type==JTokenType.Boolean && (bool)values["dryRun"]!);
                    sessionOutcome.RequireUsable(readOnly);
                    var confirm=values["confirm"];
                    var expected=values["expectedProjectFile"];
                    if(!method.GetParameters().Any(p=>p.Name=="confirm")) values.Remove("confirm");
                    if(!method.GetParameters().Any(p=>p.Name=="expectedProjectFile")) values.Remove("expectedProjectFile");
                    if(values["dryRun"]?.Type==JTokenType.Boolean && !(bool)values["dryRun"]!)
                    {
                        if(confirm?.Type!=JTokenType.Boolean || expected?.Type!=JTokenType.String)
                            throw new ArgumentException("Execution requires confirmation and an absolute expected project file.");
                        MutationIdentityPolicy.ValidateTarget(false,(bool)confirm,(string)expected!,name,releaseKey,
                            values["path"]?.Type==JTokenType.String ? (string)values["path"]! : "",
                            values["directoryPath"]?.Type==JTokenType.String ? (string)values["directoryPath"]! : "",
                            values["projectName"]?.Type==JTokenType.String ? (string)values["projectName"]! : "",engine.RequireProjectIdentity);
                    }
                    var parameters = method.GetParameters();
                    if (values.Properties().Any(p => !parameters.Any(a => a.Name == p.Name))) throw new ArgumentException("Unknown worker argument.");
                    var call = parameters.Select(p =>
                    {
                        if (!values.TryGetValue(p.Name!, StringComparison.Ordinal, out var value))
                        {
                            if (p.HasDefaultValue) return p.DefaultValue;
                            throw new ArgumentException("Missing worker argument: " + p.Name);
                        }
                        if ((p.ParameterType == typeof(string) && value.Type != JTokenType.String) ||
                            (p.ParameterType == typeof(bool) && value.Type != JTokenType.Boolean) ||
                            (p.ParameterType == typeof(int) && value.Type != JTokenType.Integer) ||
                            (p.ParameterType == typeof(string[]) && (value.Type != JTokenType.Array || value.Count()>256 || value.Any(x=>x.Type!=JTokenType.String))))
                            throw new ArgumentException("Incorrect worker argument type: " + p.Name);
                        return value.ToObject(p.ParameterType);
                    }).ToArray();
                    enteredOperation=true;
                    if(name=="Disconnect") disconnectAttempted=true;
                    var result = method.Invoke(engine, call);
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
                    Console.WriteLine(JsonConvert.SerializeObject(new { id, result }));
                }
                catch (Exception ex)
                {
                    var cause = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                    Console.WriteLine(JsonConvert.SerializeObject(new { id, error = new { type = cause.GetType().Name, message = cause.Message,
                        code=cause is ArgumentException ? -32602 : -32603,
                        evidence=cause.Data.Contains("inputSha256") || cause.Data.Contains("outputFile") || cause.Data.Contains("safetyCleanup") ? new { inputFile=cause.Data["inputFile"],inputSha256=cause.Data["inputSha256"],outputFile=cause.Data["outputFile"],stagedFile=cause.Data["stagedFile"],recoveryDirectory=cause.Data["recoveryDirectory"],exportPhase=cause.Data["exportPhase"],stagedSha256=cause.Data["stagedSha256"],safetyCleanup=cause.Data["safetyCleanup"] } : null,
                        outcome=!enteredOperation ? "rejected-before-operation" : readOnly ? "read-failed" : "unknown" } }));
                }
            }
        }
        return 0;
    }
}
