using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters;
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
        WorkerLaunchOptions options;
        try { options = WorkerLaunchOptions.Parse(args); }
        catch (ArgumentException error) { Console.Error.WriteLine(error.Message); return 2; }
        if (options.TiaPortalLocation != null)
            Environment.SetEnvironmentVariable("TiaPortalLocation", options.TiaPortalLocation);
        AppDomain.CurrentDomain.UnhandledException += (_, error) => Console.Error.WriteLine(
            "PlcWorker background exception; terminating=" + error.IsTerminating + ": " + (error.ExceptionObject as Exception)?.Message);
        var api = options.ApiDirectory;
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
        if (options.ReleaseKey != "20" && options.ReleaseKey != "21") return Run(options);
        int result = 0;
        Exception? failure = null;
        var owner = new System.Threading.Thread(() =>
        {
            try { result = Run(options); }
            catch (Exception ex) { failure = ex; }
        });
        owner.SetApartmentState(System.Threading.ApartmentState.MTA);
        owner.Start();
        owner.Join();
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(WorkerLaunchOptions options)
    {
        string releaseKey = options.ReleaseKey, api = options.ApiDirectory, nonce = options.Nonce;
        TiaMcpServer.Siemens.Engineering.TiaMajorVersion = TiaMcp.Versioning.TiaVersionCatalog.Get(releaseKey).MajorVersion;
        TiaMcpServer.Siemens.Engineering.TiaPortalLocationOverride = options.TiaPortalLocation;
        TiaMcpServer.Runtime.OpennessReadiness.MarkReady(TiaMcpServer.Runtime.EnvironmentDoctor.CurrentUserInOpennessGroup()
            || WorkerFaultInjection.PlcSdkReady(key => typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .SingleOrDefault(attribute => attribute.Key == key)?.Value));
        if (WorkerFaultInjection.PlcSdkReady(key => typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == key)?.Value))
        {
            foreach (var reference in typeof(PlcFoundationEngine).Assembly.GetReferencedAssemblies()
                .Where(name => name.Name!.StartsWith("Siemens.Engineering", StringComparison.Ordinal)))
                Console.Error.WriteLine("SDK fixture loaded: " + Assembly.Load(reference).FullName);
        }
        using (var engine = new PlcFoundationEngine(releaseKey, api))
        {
            engine.ProjectLaunchWithUserInterface = options.WithUi;
            engine.OrganisationEvidenceJson = evidence => JsonSerializer.Serialize(evidence);
            engine.UnifiedPropertyAdmission = TiaMcp.Shared.UnifiedHmiPropertyAdmission.Validate;
#if !OPCUA_NO_EXTENSION
            engine.PlcB5GenerateModelled = request => JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, object?>>(new TiaMcpServer.Siemens.OpcUaModelledNative(engine.PlcB5Session).GenerateOpcUaModelledInterface(request.SoftwarePath, request.InterfaceName, request.NamespaceUri, request.OutputPath, request.UnitName, request.KeepFolderStructure, request.KeepEmptyDataBlocks, request.AccessLevelsJson, request.DryRun).ToJsonString())!;
#endif
            WorkerSessionRecovery? recovery = null;
            var dispatcher = new FoundationWorkerDispatcher(engine, typeof(Program).Assembly,
                error => recovery!.ObserveFailure(error)
                );
            recovery = new WorkerSessionRecovery(dispatcher.SessionOutcome, () => dispatcher.Detached,
                engine.RefreshWorkerSessionBinding, engine.ClearWorkerSessionBinding, engine.LockWorkerSessionBinding,
                engine.InvalidateSharedSession, engine.Dispose, () => engine.WorkerSessionBindingFault);
            using var session = new FoundationProcessLease(() => TiaOpenness.Shared.DataLocations.Current.LeasesDirectory,
                request => recovery.Dispatch(request, dispatcher.Dispatch),
                () => dispatcher.State.IsAttached, () => dispatcher.Detached, () => dispatcher.SessionOutcome.RequiresReset,
                recovery.DisposeSession, recovery.Synchronize
                );
            dispatcher.ProcessStartTicks = () => session.ProcessStartTicks;
            engine.ProjectEnsureGroup = () => TiaMcpServer.Siemens.WindowsOpennessGroup.RequireEffectiveMembership(
                TiaMcpServer.Siemens.WindowsOpennessGroup.EnsureMembership(new TiaMcpServer.Siemens.WindowsOpennessGroupPlatform(
                    TiaMcpServer.Runtime.EnvironmentDoctor.CurrentUserInOpennessGroup)));
            engine.ProjectConnectionCreated = connection => {
                int processId = connection.GetCurrentProcess().Id;
                engine.AdoptCreatedProjectLifecycleProcess(processId, session.ReserveCreatedProcess(processId));
            };
            engine.ProjectRetrieved = engine.AdoptProjectLifecycleRetrieved;
            engine.ProjectSavedAs = engine.RefreshProjectLifecyclePath;
            engine.ProjectReleased = engine.ReleaseProjectLifecycleProject;
            engine.ConfigureWorkerSessionCallbacks(() => session.ProcessStartTicks, () => session.Reserved,
                (major, pid, ticks, path, name) => {
                    var binding = new TiaMcpServer.Siemens.ProjectBindingIdentity(major, pid, ticks, path, name);
                    return new WorkerSessionIdentity(binding.ProcessId, binding.StartUtcTicks, binding.ProjectPath,
                        binding.ProjectName, binding.Generation,
                        JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, object?>>(binding.ToJson().ToJsonString())!,
                        binding.Verify);
                }, value => JsonSerializer.Deserialize<PlcSeedHmiReply>(JsonSerializer.Serialize(value))!);
            engine.ConfigureWorkerFamilyCallbacks(text => JsonSerializer.Deserialize<object>(
                TiaMcpServer.Siemens.EngineeringAuditLogic.DocumentPreflight(text,
                    TiaMcp.Versioning.TiaVersionCatalog.Get(releaseKey).MajorVersion, null).ToJsonString())!,
                path => JsonSerializer.Deserialize<string[]>(path) ?? throw new ArgumentException("devicePathJson must be an array."));
#if WORKER_SEED_HMI_IMPORT
            engine.SeedReferenceHmiImport = engine.ImportProjectLifecycleSeedHmi;
#endif
#if PROJECT_SCAFFOLD
            WorkerProjectScaffold.Register(engine.ReleaseKey, primitive => engine.ProjectScaffoldOperation = primitive, (operation, arguments) => {
                var response = session.Dispatch(new ChannelRequest(0, "adapter." + operation, arguments.ToJsonString(), (_, _) => { }));
                if (response.Failure != null) throw new WorkerPrimitiveFailureException(response.Failure);
                return System.Text.Json.Nodes.JsonNode.Parse(response.ResultJson);
            }, text => TiaMcpServer.Siemens.EngineeringAuditLogic.DocumentPreflight(text,
                TiaMcp.Versioning.TiaVersionCatalog.Get(releaseKey).MajorVersion, null));
#endif
            string HashFile(string path)
            {
                using(var stream=File.OpenRead(path))
                using(var sha=System.Security.Cryptography.SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
            var identity=new ChannelIdentity(releaseKey,HashFile(typeof(Program).Assembly.Location),
                HashFile(typeof(PlcFoundationEngine).Assembly.Location),System.Diagnostics.Process.GetCurrentProcess().Id,nonce);
            var input=Console.OpenStandardInput();
            string? hostRequestId = null;
            TiaMcp.Adapters.Diagnostics.AdapterJournal.ConfigureCorrelation(() => hostRequestId ?? Guid.NewGuid().ToString("N"));
            var server=new ChannelServer(input,Console.OpenStandardOutput(),identity,dispatcher.Observe,request => {
                hostRequestId = request.CorrelationId;
                using var journal = request.ToolName == null ? null : TiaMcp.Adapters.Diagnostics.AdapterJournal.BeginRequest(
                    request.CorrelationId!, request.ToolName, releaseKey);
                try
                {
                    var response = dispatcher.ReadManagedObservation(request) ?? session.Dispatch(request);
                    journal?.Complete();
                    return response.Failure != null ? response : ChannelResponse.WithSpill(response.ResultJson,
                        () => WorkerReplySpill.Write(TiaOpenness.Shared.DataLocations.Current.WorkerSpillsDirectory, response.ResultJson));
                }
                finally { hostRequestId = null; }
            });
            try { server.Run(); }
            catch(ChannelFaultException ex)
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
