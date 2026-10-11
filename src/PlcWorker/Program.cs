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
              if (wanted.Name != null && wanted.Name.StartsWith("Siemens.Collaboration.", StringComparison.Ordinal))
              {
                  using var resource = typeof(Program).Assembly.GetManifestResourceStream("ProjectGroupRuntime." + wanted.Name + ".dll");
                  if (resource == null) return null;
                  using var bytes = new MemoryStream(); resource.CopyTo(bytes);
                  var embedded = Assembly.Load(bytes.ToArray());
                  if (!string.Equals(wanted.FullName, embedded.FullName, StringComparison.OrdinalIgnoreCase))
                      throw new FileLoadException("Embedded project group helper identity differs.");
                  return embedded;
              }
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
        if (args[1] != "20" && args[1] != "21") return Run(args[1], api, args[3]);
        int result = 0;
        Exception? failure = null;
        var owner = new System.Threading.Thread(() =>
        {
            try { result = Run(args[1], api, args[3]); }
            catch (Exception ex) { failure = ex; }
        });
        owner.SetApartmentState(System.Threading.ApartmentState.MTA);
        owner.Start();
        owner.Join();
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string releaseKey, string api, string nonce)
    {
        using (var engine = new PlcFoundationEngine(releaseKey, api))
        {
            engine.OrganisationEvidenceJson = evidence => JsonSerializer.Serialize(evidence);
            engine.PlcB5DocumentPreflight = text => JsonSerializer.Deserialize<object>(TiaMcpServer.Siemens.EngineeringAuditLogic.DocumentPreflight(text, int.Parse(releaseKey, System.Globalization.CultureInfo.InvariantCulture), null).ToJsonString())!;
#if !OPCUA_NO_EXTENSION
            engine.PlcB5GenerateModelled = request => JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, object?>>(new TiaMcpServer.Siemens.OpcUaModelledNative(engine.PlcB5Session).GenerateOpcUaModelledInterface(request.SoftwarePath, request.InterfaceName, request.NamespaceUri, request.OutputPath, request.UnitName, request.KeepFolderStructure, request.KeepEmptyDataBlocks, request.AccessLevelsJson, request.DryRun).ToJsonString())!;
#endif
            var dispatcher = new FoundationWorkerDispatcher(engine, typeof(Program).Assembly);
            using var session = new FoundationProcessLease(() => TiaOpenness.Shared.DataLocations.Current.LeasesDirectory,
                dispatcher.Dispatch, () => dispatcher.State.IsAttached, () => dispatcher.Detached,
                () => dispatcher.SessionOutcome.RequiresReset, engine.Dispose);
            engine.ProjectEnsureGroup = () => TiaMcpServer.Siemens.OpennessGroup.EnsureMembership().GetAwaiter().GetResult();
            engine.ProjectConnectionCreated = connection => {
                int processId = connection.GetCurrentProcess().Id;
                engine.AdoptCreatedProjectLifecycleProcess(processId, session.ReserveCreatedProcess(processId));
            };
            engine.ProjectRetrieved = engine.AdoptProjectLifecycleRetrieved;
            engine.ProjectSavedAs = engine.RefreshProjectLifecyclePath;
            engine.ProjectReleased = engine.ReleaseProjectLifecycleProject;
            engine.ProjectCachedBinding = () => {
                var state = engine.ReadState();
                return new System.Collections.Generic.Dictionary<string, object?> {
                    ["identity"] = new System.Collections.Generic.Dictionary<string, object?> { ["projectPath"] = state.ProjectFile },
                    ["instanceReserved"] = state.IsAttached, ["verifiedLive"] = false
                };
            };
            string HashFile(string path)
            {
                using(var stream=File.OpenRead(path))
                using(var sha=System.Security.Cryptography.SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
            var identity=new ChannelIdentity(releaseKey,HashFile(typeof(Program).Assembly.Location),
                HashFile(typeof(PlcFoundationEngine).Assembly.Location),System.Diagnostics.Process.GetCurrentProcess().Id,nonce);
            var input=Console.OpenStandardInput();
            var server=new ChannelServer(input,Console.OpenStandardOutput(),identity,dispatcher.Observe,session.Dispatch);
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
