using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;
using TiaMcpServer.ModelContextProtocol;

namespace Siemens.Engineering.HmiUnified.UI.Controls
{
    public sealed class HmiSystemDiagnosisControl
    {
        public int Calls;
        public string ScriptDiagnosisOverviewText { get { Calls++; throw new InvalidOperationException("unsafe getter called"); } }
        public string Name => "SystemDiagnostics";
    }
}
namespace TiaMcpServer.Tests
{
    internal static class HmiSnapshotSafetyTests
    {
        public sealed class EngineeringObjectDisposedException : Exception { public EngineeringObjectDisposedException() : base("fixture stale handle") { } }
        public sealed class FaultingItem
        {
            public int LaterCalls;
            public string A => "retained evidence";
            public string B => throw new EngineeringObjectDisposedException();
            public string Z { get { LaterCalls++; return "must not be read"; } }
        }
        public sealed class Sequence : IEnumerable, IEnumerator, IDisposable
        {
            public int Moves, Disposals;
            public bool FailMove;
            public object Current { get; set; } = new FaultingItem();
            public IEnumerator GetEnumerator() => this;
            public bool MoveNext() { Moves++; if (FailMove) throw new EngineeringObjectDisposedException(); return Moves == 1; }
            public void Reset() => throw new InvalidOperationException();
            public void Dispose() { Disposals++; }
        }
        public sealed class Root
        {
            public List<object> ScreenGroups { get; } = new List<object>();
            public List<object> Screens { get; } = new List<object>();
        }
        public sealed class GoodGroup
        {
            public string Name { get; set; } = "good";
            public List<object> Groups { get; } = new List<object>();
            public List<object> Screens { get; } = new List<object>();
        }
        public sealed class BadGroup
        {
            public int Reads;
            public string Name => "unrelated";
            public object Screens { get { Reads++; throw new InvalidOperationException("unrelated subtree visited"); } }
        }
        public sealed class Screen
        {
            public string Name { get; set; } = "screen";
            public List<object> ScreenItems { get; } = new List<object>();
        }
        public sealed class PrivateGetter { public int Reads; public string Secret { private get { Reads++; return "private"; } set { } } }
        public sealed class Classic { public ClassicFolder ScreenFolder { get; } = new ClassicFolder(); }
        public sealed class ClassicFolder
        {
            public string Name { get; set; } = "";
            public List<ClassicFolder> Folders { get; } = new List<ClassicFolder>();
            public List<object> Screens { get; } = new List<object>();
        }
        internal static void Run(Action<bool,string> check)
        {
            Console.WriteLine("== HMI snapshot fail-fast and exact-scope reads ==");
            var fault = new FaultingItem(); var seq = new Sequence { Current = fault };
            var snapshot = HmiSnapshot.Capture(seq);
            check(snapshot["connectionUnavailable"]!.GetValue<bool>() && !snapshot["apiCallSuccess"]!.GetValue<bool>(), "disposed getter is an API failure, not successful snapshot");
            check(snapshot["dataComplete"]!.GetValue<bool>() == false && snapshot.ToJsonString().Contains("retained evidence"), "fatal capture preserves partial evidence and marks incomplete");
            check(fault.LaterCalls == 0 && seq.Moves == 1 && seq.Disposals == 0, "no sibling, MoveNext or remote Dispose after fatal getter");
            check(snapshot["failurePath"]!.ToString() == "$/0/B" && snapshot.ToJsonString().Contains("exceptionType"), "fatal path and exception evidence retained");
            var brokenSequence = new Sequence { FailMove = true };
            check(HmiSnapshot.Capture(brokenSequence)["connectionUnavailable"]!.GetValue<bool>() && brokenSequence.Disposals == 0, "MoveNext failure aborts without secondary Dispose");
            var boundedSequence = new Sequence();
            check(HmiSnapshot.Capture(boundedSequence, 6, 1)["truncated"]!.GetValue<bool>() && boundedSequence.Moves == 0, "node limit prevents further collection reads");
            var privateGetter = new PrivateGetter(); HmiSnapshot.Capture(privateGetter);
            check(privateGetter.Reads == 0, "public setter does not authorize invoking private getter");
            var diagnostic = new global::Siemens.Engineering.HmiUnified.UI.Controls.HmiSystemDiagnosisControl();
            var skipped = HmiSnapshot.Capture(diagnostic);
            check(diagnostic.Calls == 0 && skipped["quarantinedCount"]!.GetValue<int>() == 1 && !skipped["dataComplete"]!.GetValue<bool>(), "diagnosis getter quarantined without invocation or completeness claim");
            bool denied = false; try { MigrationRead.Get(diagnostic, "ScriptDiagnosisOverviewText"); } catch (NotSupportedException) { denied = true; }
            check(denied && diagnostic.Calls == 0, "targeted branch cannot bypass diagnosis quarantine");
            check(!HmiReadSafety.ConnectionUnavailable(new NotSupportedException("ordinary unsupported field")), "ordinary property limitation does not imply lost connection");
            var tokenA = new JsonObject { ["kind"] = "InspectionSnapshot", ["elapsedMs"] = 10, ["value"] = "unchanged" };
            var tokenB = tokenA.DeepClone(); tokenB["elapsedMs"] = 100;
            check(HmiExactAccess.Token("same", tokenA) == HmiExactAccess.Token("same", tokenB), "elapsed diagnostics do not invalidate content token");
            check(HmiReadSafety.ConnectionUnavailable(new TargetInvocationException(new EngineeringObjectDisposedException())), "wrapped lifetime errors recognized");

            var root = new Root(); var good = new GoodGroup { Name = "诊断/组" }; var bad = new BadGroup();
            var screen = new Screen { Name = "page/name" }; good.Screens.Add(screen); root.ScreenGroups.Add(good); root.ScreenGroups.Add(bad);
            var exact = "/" + Uri.EscapeDataString(good.Name) + "/" + Uri.EscapeDataString(screen.Name);
            check(ReferenceEquals(HmiExactAccess.Screen(root, exact), screen) && bad.Reads == 0, "absolute encoded path avoids unrelated subtree");
            bool invalid = false; try { HmiExactAccess.Screen(root, "/bad//name"); } catch (PortalException ex) { invalid = ex.Code == PortalErrorCode.InvalidParams; }
            check(invalid && bad.Reads == 0, "malformed absolute path fails without tree scan");
            var classic = new Classic(); var folder = new ClassicFolder { Name = "Folder" }; classic.ScreenFolder.Folders.Add(folder); folder.Screens.Add(screen);
            check(ReferenceEquals(HmiExactAccess.Screen(classic, "/Folder/page%2Fname"), screen), "classic folder exact lookup retained");

            var portal = McpServer.Portal; portal.FixtureRoot = root; portal.FixtureResetReadHealth();
            portal.FixtureResolutionError = new EngineeringObjectDisposedException();
            var failed = McpServer.ReadHmiScreenSnapshot("HMI", exact);
            int calls = portal.FixtureResolveCalls;
            check(failed.Meta!["status"]!.ToString() == "HmiConnectionUnavailable" && failed.Meta["requiresExplicitRebind"]!.GetValue<bool>(), "software-resolution failure invalidates read session");
            portal.FixtureResolutionError = null;
            var blocked = McpServer.ReadHmiScreenSnapshot("HMI", exact);
            check(blocked.Meta!["status"]!.ToString() == "HmiReadSessionBlocked" && portal.FixtureResolveCalls == calls, "next request blocked before software resolution");
            check(portal.GetHmiReadHealth()["snapshotReadsBlocked"]!.GetValue<bool>() && portal.FixtureCacheClears > 0, "health records fault and clears software cache");
            portal.FixtureResetReadHealth();
            screen.ScreenItems.Add(new FaultingItem());
            var partial = McpServer.ReadHmiScreenSnapshot("HMI", exact);
            check(!partial.Meta!["success"]!.GetValue<bool>() && partial.Meta["snapshot"] != null && partial.Meta["requiresExplicitRebind"]!.GetValue<bool>(), "nested failure propagates to tool success with partial snapshot");
            check(partial.Meta["operationId"] != null && partial.Meta["lastAttemptedPath"] != null, "operation trace returned for log correlation");
            portal.FixtureResetReadHealth(); portal.FixtureRoot = null;
        }
    }
}
