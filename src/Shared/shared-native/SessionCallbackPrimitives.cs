#if WORKER_SESSION_LIFECYCLE || TIA_ENGINE_LOCAL_PRIMITIVES
using System;
using System.Collections.Generic;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens;

#if TIA_ENGINE_LOCAL_PRIMITIVES
namespace TiaMcp.Shared.Native
#else
namespace TiaMcp.Adapters.Session
#endif
{
    // The engine and the standalone worker use the same cached identity and native
    // guard sequence. Delegates keep ownership, token invalidation and handles local.
    public static partial class SessionCallbackPrimitives
    {
#if TIA_ENGINE_LOCAL_PRIMITIVES
        public static long ProcessStart(int pid) => Portal.ProcessStart(pid);
#endif
        public static T Capture<T>(int? pid, long ticks, bool reserved, Func<T> capture,
            Func<int, long>? processStart = null)
        {
            if (pid == null || !reserved) throw new InvalidOperationException("No reserved TIA process.");
            if ((processStart ?? ProcessStart)(pid.Value) != ticks)
                throw new InvalidOperationException("TIA process changed before project binding.");
            return InvocationJournal.Native("Binding.Capture", capture);
        }

        public static void Verify(bool captured, int major, int? pid, bool reserved, Func<string> path,
            Func<string> name, Action<int, int, long, string, string> verify, Func<int, long>? processStart = null)
        {
            if (!captured || pid == null || !reserved)
                throw new InvalidOperationException("Exact project binding is unavailable.");
            verify(major, pid.Value, (processStart ?? ProcessStart)(pid.Value),
                InvocationJournal.Native("Binding.ProjectPath", path),
                InvocationJournal.Native("Binding.ProjectName", name));
        }

        public static Dictionary<string, object?> Snapshot(object? identity, string? fault, bool reserved)
            => new Dictionary<string, object?>
            {
                ["identity"] = identity, ["fault"] = fault,
                ["instanceReserved"] = reserved, ["verifiedLive"] = false,
                ["note"] = "Cached binding only. Project access verifies PID/start time/full path/name; no implicit rebind."
            };

        public static CandidateIdentity HardwareIdentity(int pid, DateTimeOffset start, string current, Func<string> path,
            ref string? generation, ref long epoch)
        {
            if (current != generation) { generation = current; epoch++; }
            return new CandidateIdentity(pid, start,
                CandidatePrimitives.CanonicalProject(path()), epoch);
        }

        private sealed class AmbientAccess : IDisposable
        {
            public void Dispose() { }
        }

        public static IDisposable? BeginEditAccess(bool connected, Action verify, Func<bool> transactionActive)
        {
            if (!connected) throw new PortalException(PortalErrorCode.InvalidState, "TIA session unavailable.");
            verify();
            return transactionActive() ? new AmbientAccess() : null;
        }

        public static IDisposable EditAccess(bool connected, Action verify, Func<bool> transactionActive,
            Func<IDisposable> exclusive) => BeginEditAccess(connected, verify, transactionActive) ?? exclusive();

        public static PlcSeedHmiReply SeedImport(SeedHmiRequest request, Func<HmiRequest, bool, HmiReply> execute,
            Func<object?, PlcSeedHmiReply> render)
        {
            var reply = execute(new HmiRequest
            {
                Operation = request.Kind == "tagTables" ? "ImportHmiTagTablesFromDirectory"
                    : request.Kind == "screens" ? "ImportHmiScreensFromDirectory"
                    : throw new ArgumentException("Unknown seed HMI import kind."),
                SoftwarePath = request.SoftwarePath, FolderPath = request.FolderPath, Dir = request.Directory,
                Overwrite = true, DryRun = false
            }, false);
            if (reply.Failure != null) throw new PlcSoftwareException(reply.Failure.Code, reply.Failure.Message);
            return render(reply.Value);
        }
    }
}
#endif
