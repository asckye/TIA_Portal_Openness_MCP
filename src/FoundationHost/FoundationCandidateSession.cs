using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.Logic.V4;
using TiaMcp.PlcWorker;

namespace TiaMcp.LegacyHost;

internal sealed class FoundationCandidateSession(IFoundationWorker worker)
{
    private static readonly ConditionalWeakTable<IFoundationWorker, FoundationCandidateSession> Sessions = new();
    internal static FoundationCandidateSession For(IFoundationWorker worker) => Sessions.GetValue(worker, w => new(w));
    private readonly object serial = new();
    private readonly DeviceCreationSession devices = new();
    private readonly PlcImportSession imports = new();
    private bool poisoned;

    internal Envelope Device(string release, string id, string type, string name, string family, string mode, bool confirm, string hash, string project, CancellationToken token)
    {
        lock (serial)
        {
            if (poisoned) return DeviceCreationSession.Result(release, "CreateHardwareDevice", id, null,
                new Error("The device-create session must be rebuilt.", new SessionResetRequiredDetails("device-create-unknown")), Outcome.RejectedBeforeOperation, Execution.NotStarted);
            var result = devices.Run(new DeviceProxy(worker, token), release, "CreateHardwareDevice", id, type, name, family, mode, confirm, hash, project, foundation: true);
            poisoned |= result.Meta.RequiresSessionReset;
            return result;
        }
    }
    internal Envelope Import(string release, string tool, string id, PlcImportRequest request, string mode, bool confirm, string hash, string project, CancellationToken token)
    {
        lock (serial)
        {
            if (poisoned) return PlcImportSession.Result(release, tool, id, null,
                new Error("The import session must be rebuilt.", new SessionResetRequiredDetails("plc-import-unknown")), Outcome.RejectedBeforeOperation, Execution.NotStarted);
            var result = imports.Run(new ImportProxy(worker, release, tool, request, token), release, tool, id, request, mode, confirm, hash, project);
            poisoned |= result.Meta.RequiresSessionReset;
            return result;
        }
    }
    private static bool Unknown(Exception ex) => !(ex.Data["foundationRequestSent"] is bool sent && !sent)
        && !(ex is WorkerOperationException known && known.KnownNoMutation);
    private static OperationCanceledException NotSent(CancellationToken token)
    {
        var failure = new OperationCanceledException(token);
        failure.Data["foundationRequestSent"] = false;
        return failure;
    }

    private sealed class DeviceProxy(IFoundationWorker worker, CancellationToken token) : IDeviceCreationAdapter, IDeviceCandidateBoundary
    {
        public string RootId { get; private set; } = "";
        private DeviceCandidateReply Call(DeviceCandidateCall candidate, string mode = "preview")
        {
            if (token.IsCancellationRequested) throw NotSent(token);
            var args = new JsonObject { ["candidate"] = JsonSerializer.SerializeToNode(candidate), ["mode"] = mode };
            var reply = CandidateWire.Device(worker.Call(WorkerOperations.DeviceCreationCandidate, args, token).GetAwaiter().GetResult(), args);
            if (reply.Fault != null) throw new CandidateObservationException(reply.Fault);
            RootId = reply.RootId;
            return reply;
        }
        public CandidateIdentity ReadIdentity() => Call(new()).Identity!;
        public IReadOnlyList<DeviceCatalogEntry> ReadCatalog(string type) => Call(new() { Action = "catalog", TypeIdentifier = type }).Catalog!;
        public IReadOnlyList<DeviceInventoryItem> ReadInventory() => Call(new() { Action = "inventory" }).Inventory!;
        public void BeforeCreate() => throw new InvalidOperationException("Use the atomic candidate boundary.");
        public DeviceInventoryItem Create(string type, string name) => throw new InvalidOperationException("Use the atomic candidate boundary.");
        public DeviceCreateAttempt Execute(DeviceCreateCheck check)
        {
            try { return Call(new() { Action = "execute", Check = check }, "apply").Attempt!; }
            catch (Exception ex)
            {
                if (ex is CandidateObservationException observed) return new() { Fault = observed.Fault };
                bool unknown = Unknown(ex);
                return new() { Issued = unknown, RequiresSessionReset = unknown, Fault = new() { Kind = "preflight" },
                    Residue = new() { Reason = "worker-channel-failure" } };
            }
        }
    }

    private sealed class ImportProxy(IFoundationWorker worker, string release, string tool, PlcImportRequest request, CancellationToken token) : IPlcImportAdapter, IImportCandidateBoundary
    {
        private ImportCandidateReply Call(ImportCandidateCall candidate, string mode = "preview")
        {
            if (token.IsCancellationRequested) throw NotSent(token);
            candidate.Tool = tool; candidate.Request = request;
            var args = new JsonObject { ["candidate"] = JsonSerializer.SerializeToNode(candidate), ["mode"] = mode };
            var reply = CandidateWire.Import(worker.Call(WorkerOperations.PlcImportCandidate, args, token).GetAwaiter().GetResult(), args);
            if (reply.Fault != null) throw new CandidateObservationException(reply.Fault);
            return reply;
        }
        public CandidateIdentity ReadIdentity() => Call(new()).Identity!;
        public IReadOnlyList<PlcImportInput> ReadInputs(string key, string entry, PlcImportRequest input, IDictionary<string, Stream> locks)
        {
            if (key != release || entry != tool || !ReferenceEquals(input, request)) throw new InvalidOperationException("Import request binding mismatch.");
            var inputs = Call(new() { Action = "inputs" }).Inputs!;
            foreach (var path in inputs.SelectMany(i => i.Files))
                if (!locks.ContainsKey(path)) { PlcImportFiles.SafePath(new FileInfo(path)); locks.Add(path, new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)); }
            return inputs;
        }
        public IReadOnlyList<PlcImportObject> ReadInventory() => Call(new() { Action = "inventory" }).Inventory!;
        public string TargetGroupIdentity(PlcImportObject target) => Call(new() { Action = "group", Target = target }).GroupIdentity;
        public bool SupportsOverwrite(PlcImportInput input) => Call(new() { Action = "overwrite", Input = input }).OverwriteSupported;
        public void BeforeImport(PlcImportInput input) => throw new InvalidOperationException("Use the atomic candidate boundary.");
        public PlcImportObject Import(PlcImportInput input, bool overwrite) => throw new InvalidOperationException("Use the atomic candidate boundary.");
        public string ReadContent(PlcImportInput input, PlcImportObject imported) => throw new InvalidOperationException("Use the atomic candidate boundary.");
        public PlcImportAttempt Execute(PlcImportCheck check, IDictionary<string, Stream> locks)
        {
            try { return Call(new() { Action = "execute", Check = check }, "apply").Attempt!; }
            catch (Exception ex)
            {
                if (ex is CandidateObservationException observed) return new() { Fault = observed.Fault };
                bool unknown = Unknown(ex);
                return new() { Issued = unknown, RequiresSessionReset = unknown, Fault = new() { Kind = "preflight" },
                    Residue = new() { Reason = "worker-channel-failure" } };
            }
        }
    }
}
