using System;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW;
using TiaMcp.Adapters;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Plc;
using TiaMcp.Adapters.Native.Safety;
using TiaMcp.WorkerChannel;

namespace TiaMcp.PlcWorker
{
    internal sealed partial class FoundationWorkerDispatcher
    {
        // Only these three fixture operations exist. Dispatch uses the product guards, reflection and catch.
        internal FoundationWorkerDispatcher(PlcFoundationEngine engine, Action<Exception> observedFailure)
        {
            this.engine = engine;
            releaseKey = engine.ReleaseKey;
            policyAssembly = typeof(SafetyLoginFailure).Assembly;
            failureObserved = observedFailure;
            methods = WorkerOperationModule.Register(new[] { new WorkerOperationModule("", typeof(PlcFoundationEngine),
                new[] { "CompileSoftware", "CompileLegacy", "ManagePlcSafety" }) });
        }
    }
    internal static class PlcVerifiedImportBridge
    {
        internal static PlcVerifiedCandidate Validate(PlcSoftwareRequest request) => throw new NotSupportedException();
        internal static string Execute(PlcVerifiedExecution request) => throw new NotSupportedException();
        internal static PlcCompilerEvidence Compiler(object result, bool diagnose) => throw new NotSupportedException();
    }
    internal sealed class ProjectTextProbeWorker { internal ProjectTextProbeWorker(PlcFoundationEngine engine) => throw new NotSupportedException(); }
    internal static class WorkerObservations
    {
        internal static ChannelResponse Reply(Func<JsonNode?> read) => throw new NotSupportedException();
        internal static JsonNode? Read(PlcFoundationEngine engine, string operation, JsonObject arguments, Func<long>? start)
            => throw new NotSupportedException();
    }
}
namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        internal const string ProjectPath = "C:/Projects/Scoped.ap21";
        private readonly SafetySession session;
        internal readonly PlcOrganisationAdapter Legacy;
        internal PlcFoundationEngine(SafetySession session, bool priorMutation)
        {
            this.session = session;
            Legacy = new PlcOrganisationAdapter(session) { MutationStarted = priorMutation };
        }
        internal string ReleaseKey => "19";
        internal bool CanDetachOnUncertainShutdown => false;
        internal bool DisconnectAttempted => false;
        internal Action<string>? SharedFamilyMutationIdentity => null;
        internal void RequireProjectIdentity(string expected) => MutationIdentityPolicy.RequireSameProject(expected, ProjectPath);
        internal PlcRuntimeState ReadState() => new PlcRuntimeState { IsAttached = true, ProjectFile = ProjectPath };
        internal Func<PlcSoftwareRequest, PlcVerifiedCandidate>? ValidatePlcVerifiedCandidate { get; set; }
        internal Func<PlcVerifiedExecution, string>? ExecutePlcVerifiedWorkflow { get; set; }
        internal Func<object, bool, PlcCompilerEvidence>? CollectPlcCompilerEvidence { get; set; }

        public object CompileLegacy(string softwarePath, string password, bool dryRun) => Legacy.CompileSoftware(softwarePath, password);
        public HardwareAddressingReply ManagePlcSafety(string softwarePath, string password, bool dryRun) =>
            new SafetyManagementAdapter(session).ManagePlcSafety(softwarePath, "login", dryRun: dryRun, password: password);

        private (PlcSoftware Value, DeviceItem Context, string ExactPath, string Device, string Host) ReadSelection(string softwarePath)
        {
            var container = session.GetSoftwareContainer(softwarePath);
            return (container.Software, (DeviceItem)container.Parent!, softwarePath, "D", "CPU");
        }
        private static void RequireTargetOffline((PlcSoftware Value, DeviceItem Context, string ExactPath, string Device, string Host) target) { }
        private static string[] CheckProjectOfflineProviders() => Array.Empty<string>();
        private static void Depth(int depth) { }
        private sealed class FixtureProject { internal FileInfo Path => new FileInfo(ProjectPath); }
        private static FixtureProject Project() => new FixtureProject();
    }
}
