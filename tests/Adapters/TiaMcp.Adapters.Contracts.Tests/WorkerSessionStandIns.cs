// Managed stand-ins only; the linked production callbacks run without Siemens assemblies.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using TiaMcp.Adapters.Contracts;
namespace Siemens.Engineering {
    public enum TiaPortalMode { WithoutUserInterface, WithUserInterface }
    public sealed class TiaPortal {
        public TiaPortalMode Mode { get; }
        public TiaPortal(TiaPortalMode mode = TiaPortalMode.WithoutUserInterface) { Mode = mode; }
        public int ExclusiveCalls;
        public IDisposable ExclusiveAccess(string text) { ExclusiveCalls++; return Process.GetCurrentProcess(); }
    }
    public sealed class Project {
        private FileInfo _path = null!;
        private string _name = "";
        public FileInfo Path { get { TiaMcpServer.ModelContextProtocol.InvocationJournal.Calls.Add("project.Path"); return _path; } set { _path = value; } }
        public string Name { get { TiaMcpServer.ModelContextProtocol.InvocationJournal.Calls.Add("project.Name"); return _name; } set { _name = value; } }
    }
}
namespace TiaMcpServer.Siemens {
    public enum PortalErrorCode { InvalidState }
    public sealed class PortalException : Exception {
        public PortalException(PortalErrorCode code, string message, Exception? inner = null) : base(message, inner) { }
    }
}
namespace TiaMcpServer.ModelContextProtocol {
    public static class InvocationJournal {
        public static readonly List<string> Calls = new();
        public static T Native<T>(string name, Func<T> action) { Calls.Add(name); return action(); }
    }
}
namespace TiaMcp.Adapters.Native.Session { public sealed class FakeSession { } }
namespace TiaMcp.Adapters.Native.Hmi {
    public sealed class HmiObject : Dictionary<string,object?> {
        public HmiObject(Dictionary<string,object?> values) : base(values) { }
    }
    public static class HmiMeta {
        public static Dictionary<string,object?> Basic(bool ok, params (string,object?)[] values) => new();
    }
}
namespace TiaMcp.Adapters {
    public sealed partial class PlcFoundationEngine {
        private Siemens.Engineering.TiaPortal? portal;
        private Siemens.Engineering.Project? project;
        private readonly PlcLifecycleState lifecycle = new();
        private bool ownsPortal;
        private object? saveCloseAdapter;
        private FakeCandidate? sessionCandidateAdapter;
        private DateTimeOffset? sessionCandidateStart;
        private sealed class FakeCandidate { public void VerifyOwnedState() { } }
        public bool OwnsPortal => ownsPortal;
        public object? SaveCloseAdapter => saveCloseAdapter;
        public string[] NativeCalls => TiaMcpServer.ModelContextProtocol.InvocationJournal.Calls.ToArray();
        public void ClearCalls() => TiaMcpServer.ModelContextProtocol.InvocationJournal.Calls.Clear();
        public string ReleaseKey { get; }
        public bool HasEngineeringTransaction { get; set; }
        public Func<bool>? EngineeringProjectMissing { get; set; }
        public Func<string>? ProjectValidationTree { get; set; }
        public Func<IDisposable>? AcquireEngineeringEditAccess { get; set; }
        public Action<string>? SharedFamilyMutationIdentity { get; set; }
        public Func<Func<string>,TiaMcp.Adapters.Contracts.Candidates.CandidateIdentity>? SharedHardwareCandidateIdentity { get; set; }
        public Func<object?>? HmiBindingIdentity { get; set; }
        public Func<SeedHmiRequest,PlcSeedHmiReply>? SeedReferenceHmiImport { get; set; }
        public readonly Siemens.Engineering.TiaPortal Connection = new();
        public long Ticks => Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
        public string ProjectFile => lifecycle.ProjectFile!;
        public int ExclusiveCalls => portal!.ExclusiveCalls;
        public bool LocalSession { set { var state = ReadState(); state.IsLocalSession = value; lifecycle.Adopt(state); } }
        public void DefaultLocalSessionGuard() => PlcLifecyclePolicy.RequireLocalSessionExecution(lifecycle.IsLocalSession, false);
        public PlcFoundationEngine(string release) {
            ReleaseKey = release;
            ProjectConnectionCreated = connection => { portal = connection; AdoptCreatedProjectLifecycleProcess(Environment.ProcessId, Ticks); };
            ProjectRetrieved = AdoptProjectLifecycleRetrieved;
            ProjectSavedAs = RefreshProjectLifecyclePath;
            ProjectReleased = ReleaseProjectLifecycleProject;
        }
        public void ConfigureFixture() {
            ConfigureWorkerSessionCallbacks(() => Ticks, () => true, (major,pid,ticks,path,name) => {
                var identity = new TiaMcpServer.Siemens.ProjectBindingIdentity(major,pid,ticks,path,name);
                return new WorkerSessionIdentity(pid,ticks,identity.ProjectPath,name,identity.Generation,
                    System.Text.Json.JsonSerializer.Deserialize<Dictionary<string,object?>>(identity.ToJson().ToJsonString())!,identity.Verify);
            }, value => System.Text.Json.JsonSerializer.Deserialize<PlcSeedHmiReply>(System.Text.Json.JsonSerializer.Serialize(value))!);
            if (ReleaseKey == "20" || ReleaseKey == "21") SeedReferenceHmiImport = ImportProjectLifecycleSeedHmi;
        }
        public Siemens.Engineering.Project NewProject(string name) => new() { Name = name, Path = new FileInfo("C:/fixture/P.ap" + PlcLifecyclePolicy.FileSuffix(ReleaseKey)) };
        public void ReplaceProjectHandle(Siemens.Engineering.Project replacement) { project = replacement; }
        public void RenameProjectOnly(string name) { project!.Name = name; }
        public string ReadProjectTree() { TiaMcpServer.ModelContextProtocol.InvocationJournal.Calls.Add("tree.Read"); return "adapter:" + project!.Name; }
        public void RenameProject(string name) { project!.Name = name; project.Path = new FileInfo("C:/fixture/" + name + ".ap" + PlcLifecyclePolicy.FileSuffix(ReleaseKey)); }
        private void Check(bool allowDisconnected = false) { }
        public PlcRuntimeState ReadState() => new() { ProcessId = lifecycle.ProcessId, ProjectFile = lifecycle.ProjectFile,
            IsAttached = lifecycle.ProcessId.HasValue, OwnsProject = lifecycle.OwnsProject, IsLocalSession = lifecycle.IsLocalSession };
        public HmiRequest? LastHmiRequest;
        public bool LastHmiDryRun;
        public PlcSoftwareFailure? HmiFailure;
        public HmiReply ExecuteHmi(HmiRequest request, bool dryRun) {
            LastHmiRequest = request; LastHmiDryRun = dryRun;
            return new HmiReply { Failure = HmiFailure, Value = new HmiImportBatch {
                Imported = new List<string> { "imported" }, Failed = new List<HmiImportFailure> { new() { Path = "failed", Error = "fixture" } } } };
        }
    }
}
