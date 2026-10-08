using System;
using System.Threading;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcp.Engine.Tests
{
    internal static class OfficialWorkflowTests
    {
        private sealed class ProjectProxy
        {
            internal string Id = "";
            internal bool Stale;
            public override bool Equals(object? obj) => Stale ? throw new InvalidOperationException("stale") : obj is ProjectProxy p && p.Id == Id;
            public override int GetHashCode() => Id.GetHashCode();
        }
        private sealed class Scope : IEngineeringTransaction
        {
            public bool CanCommit { get; set; } = true;
            public bool CommitRequested { get; private set; }
            public bool IsCancellationRequested { get; set; }
            internal bool Disposed, LoseCommit, IgnoreCommit, ThrowDispose;
            public void Commit() { CommitRequested = !IgnoreCommit; if (LoseCommit) CanCommit = false; }
            public void Dispose() { Disposed = true; if (ThrowDispose) throw new InvalidOperationException("dispose failed"); }
        }

        internal static void Run(Action<bool, string> check)
        {
            var requested = new System.Reflection.AssemblyName("Siemens.Engineering.Base, Version=21.0.0.0, Culture=neutral, PublicKeyToken=29bfe5fdf4ba5d3b");
            EngineeringAssemblyIdentity.RequireMatch(requested, new System.Reflection.AssemblyName(requested.FullName), "offline.dll");
            check(true, "resolver: matching assembly accepted");
            foreach (var wrong in new[] { requested.FullName.Replace("21.0.0.0", "20.0.0.0"), requested.FullName.Replace(".Base", ".Step7"), requested.FullName.Replace("29bfe5fdf4ba5d3b", "null") })
            {
                bool rejected = false;
                try { EngineeringAssemblyIdentity.RequireMatch(requested, new System.Reflection.AssemblyName(wrong), "offline.dll"); }
                catch (System.IO.FileLoadException) { rejected = true; }
                check(rejected, "resolver: wrong version/name/token refused before assembly load");
            }
            var priorLocation = Engineering.TiaPortalLocationOverride;
            var priorEnvironment = Environment.GetEnvironmentVariable("TiaPortalLocation");
            try
            {
                Environment.SetEnvironmentVariable("TiaPortalLocation", "C:/Siemens/Portal V21");
                Engineering.TiaPortalLocationOverride = "D:/custom/Portal V20";
                check(Engineering.DetectTiaMajorVersion() == 20, "resolver: explicit V20 wins over V21 environment without registry probing");
                Engineering.TiaPortalLocationOverride = null;
                check(Engineering.DetectTiaMajorVersion() == 21, "resolver: explicit environment version is honored");
            }
            finally { Engineering.TiaPortalLocationOverride = priorLocation; Environment.SetEnvironmentVariable("TiaPortalLocation", priorEnvironment); }
            var opened = new ProjectProxy { Id = "own" };
            var foreign = new ConnectLogic.Candidate { ProcessId = 1, Attached = true, ProjectNames = { "UserProject" } };
            check(ConnectLogic.Choose(new[] { foreign }, "TestProject") == null,
                "connect: missing requested project never binds a different open project");
            var empty = new ConnectLogic.Candidate { ProcessId = 2, Attached = true };
            check(ConnectLogic.Choose(new[] { foreign, empty }, "TestProject") == empty,
                "connect: only an empty instance is an acceptable fallback for a requested project");
            check(ProjectOwnership.Owns(opened, new ProjectProxy { Id = "own" }), "lifetime: equivalent proxy retains ownership");
            check(!ProjectOwnership.Owns(opened, new ProjectProxy { Id = "foreign" }), "lifetime: discovered project never inherits ownership");
            check(!ProjectOwnership.Owns(null, opened), "lifetime: attached project is not owned");
            opened.Stale = true;
            check(!ProjectOwnership.Owns(opened, new ProjectProxy { Id = "own" }), "lifetime: stale identity fails closed");
            int closes = 0, detaches = 0, errors = 0;
            ProjectOwnership.Release(false, () => closes++, () => detaches++, _ => errors++);
            check(closes == 0 && detaches == 1, "lifetime: shared project stays open on session cleanup");
            ProjectOwnership.Release(true, () => closes++, () => detaches++, _ => errors++);
            check(closes == 1 && detaches == 2, "lifetime: owned project closes independently of session detach");
            ProjectOwnership.Release(true, () => throw new Exception("close"), () => detaches++, _ => errors++);
            check(detaches == 3 && errors == 1, "lifetime: failed close still detaches");

            var acquired = new object(); int releases = 0;
            ApartmentState apartment = ApartmentState.Unknown;
            var attached = TimedAttachment.Run(() => { apartment = Thread.CurrentThread.GetApartmentState(); return acquired; }, _ => releases++, 5000);
            check(ReferenceEquals(acquired, attached) && releases == 0, "attach: successful result has exactly one owner");
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                check(apartment == ApartmentState.MTA, "attach: native background worker explicitly uses MTA");
            using (var resume = new ManualResetEventSlim())
            using (var released = new ManualResetEventSlim())
            {
                var late = TimedAttachment.Run(() => { resume.Wait(); return acquired; }, _ => { Interlocked.Increment(ref releases); released.Set(); }, 10);
                check(late == null, "attach: caller timeout does not claim the unfinished native result");
                resume.Set();
                check(released.Wait(5000) && releases == 1, "attach: successful late attachment is released exactly once");
            }
            bool threw = false;
            try { TimedAttachment.Run<object>(() => throw new InvalidOperationException("denied"), _ => releases++, 5000); }
            catch (InvalidOperationException ex) { threw = ex.Message == "denied"; }
            check(threw && releases == 1, "attach: refusal propagates without releasing an unacquired session");

            foreach (var name in new[] { "SaveProject", "Compile", "ConnectOnlinePlc", "Connect", "CallTool", "ApplyToolBatch", "RunPlcCompanionTool", "ManagePlcGitRepository", "UnknownFutureTool" })
            {
                bool refused = false;
                try { TransactionExecution.RequireSupported(name); } catch (ArgumentException) { refused = true; }
                check(refused, "transaction: disallows non-project or unreviewed effect " + name);
            }
            TransactionExecution.RequireSupported("createplctypegroup");
            var success = new Scope(); var meta = new JsonObject(); int calls = 0;
            check(TransactionExecution.Run(2, () => success, _ => { calls++; return true; }, meta)
                && calls == 2 && success.Disposed && meta["committed"]!.GetValue<bool>(), "transaction: commit only after all calls and successful disposal");
            var refusedCommit = new Scope { CanCommit = false }; meta = new JsonObject();
            check(!TransactionExecution.Run(1, () => refusedCommit, _ => true, meta) && !refusedCommit.CommitRequested
                && meta["rollbackCompleted"]!.GetValue<bool>(), "transaction: CanCommit=false never reports committed");
            var failed = new Scope(); calls = 0;
            check(!TransactionExecution.Run(3, () => failed, _ => ++calls < 2, new JsonObject()) && calls == 2 && !failed.CommitRequested && failed.Disposed,
                "transaction: failed or unknown inner result stops subsequent writes and rolls back");
            var cancelled = new Scope { IsCancellationRequested = true }; calls = 0;
            check(!TransactionExecution.Run(1, () => cancelled, _ => { calls++; return true; }, new JsonObject()) && calls == 0,
                "transaction: already cancelled scope runs no write");
            var cancelDuring = new Scope();
            check(!TransactionExecution.Run(1, () => cancelDuring, _ => { cancelDuring.IsCancellationRequested = true; return true; }, new JsonObject()) && !cancelDuring.CommitRequested,
                "transaction: cancellation after final write prevents commit");
            var ignored = new Scope { IgnoreCommit = true };
            check(!TransactionExecution.Run(1, () => ignored, _ => true, new JsonObject()), "transaction: missing CommitRequested never reports committed");
            var lost = new Scope { LoseCommit = true };
            check(!TransactionExecution.Run(1, () => lost, _ => true, new JsonObject()), "transaction: invalidated commit never reports committed");
            var broken = new Scope { ThrowDispose = true }; meta = new JsonObject(); threw = false;
            try { TransactionExecution.Run(1, () => broken, _ => true, meta); } catch (InvalidOperationException) { threw = true; }
            check(threw && !meta["committed"]!.GetValue<bool>() && !meta["rollbackCompleted"]!.GetValue<bool>(),
                "transaction: disposal failure leaves commit and rollback unconfirmed");
        }
    }
}
