using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TiaMcp.Adapters.Contracts.Candidates
{
    public sealed class FallbackRequest
    {
        public string Entry { get; set; } = "DownloadPlc";
        public string SoftwarePath { get; set; } = "";
        public string Route { get; set; } = "";
        public string RetryPolicy { get; set; } = "never";
        public bool RefreshReadHandle { get; set; }
        public Dictionary<string, string> Parameters { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }
    public sealed class FallbackRoute
    {
        public string Id { get; set; } = "";
        public string TargetId { get; set; } = "";
        public string[] NativeCalls { get; set; } = Array.Empty<string>();
    }
    public sealed class FallbackObservation
    {
        public SessionState Binding { get; set; } = new SessionState();
        public string TargetId { get; set; } = "";
        public string OfflineState { get; set; } = "not-applicable";
        public string[] OfflineTargets { get; set; } = Array.Empty<string>();
        public FallbackRoute[] Routes { get; set; } = Array.Empty<FallbackRoute>();
        public string InventoryHash { get; set; } = "";
    }
    public sealed class FallbackNativeResult
    {
        public bool Success { get; set; }
        public string State { get; set; } = "";
        public string[] Messages { get; set; } = Array.Empty<string>();
        public Dictionary<string, string>[] Items { get; set; } = Array.Empty<Dictionary<string, string>>();
    }
    public sealed class FallbackAttempt
    {
        public string Stage { get; set; } = "preflight";
        public bool ConfigurationIssued { get; set; }
        public bool? ConfigurationResult { get; set; }
        public bool OperationIssued { get; set; }
        public bool WriteIssued { get; set; }
        public bool ReadOnlyStaleObject { get; set; }
        public bool NothingExecuted { get; set; }
        public int HandleRefreshes { get; set; }
        public bool RequiresSessionReset { get; set; }
        public FallbackNativeResult? NativeResult { get; set; }
        public FallbackObservation? After { get; set; }
        public CandidateFault? Fault { get; set; }
    }
    // Evidence belongs to the primitive that failed before issuance, never to exception text.
    public sealed class ReadHandleStaleException : Exception
    {
        public bool ReadOnlyObject { get; }
        public bool NothingExecuted { get; }
        public ReadHandleStaleException(bool readOnlyObject, bool nothingExecuted, Exception error)
            : base("Read handle is stale.", error) { ReadOnlyObject = readOnlyObject; NothingExecuted = nothingExecuted; }
    }
    public sealed class FallbackCheck
    {
        public FallbackRequest Request { get; set; } = new FallbackRequest();
        public FallbackObservation Before { get; set; } = new FallbackObservation();
        public string Digest { get; set; } = "";
        public int InitialReadRefreshes { get; set; }
    }
    public interface IFallbackAdapter
    {
        bool Writes { get; }
        bool NeedsConfiguration { get; }
        bool RequiresOffline { get; }
        FallbackObservation Observe();
        void BeforeAction();
        bool ApplyConfiguration(string route);
        FallbackNativeResult Execute(FallbackRequest request, FallbackAttempt evidence);
        void RefreshReadHandle();
        void MarkUncertain();
    }
    public interface IFallbackBoundary { FallbackAttempt Execute(FallbackCheck check); }
    // A boundary may provide this evidence only when it has not dispatched the primitive.
    public sealed class FallbackNotDispatchedException : Exception
    {
        public FallbackNotDispatchedException(Exception error) : base("Fallback primitive was not dispatched.", error) { }
    }
    public static class FallbackPrimitives
    {
        public static string Digest(FallbackCheck c)
        {
            using var stream = new MemoryStream();
            using (var w = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                var r = c.Request; var o = c.Before; var b = o.Binding;
                w.Write("fallback-v1"); w.Write(r.Entry); w.Write(r.SoftwarePath); w.Write(r.Route); w.Write(r.RetryPolicy); w.Write(r.RefreshReadHandle);
                w.Write(r.Parameters.Count); foreach (var p in r.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal)) { w.Write(p.Key); w.Write(p.Value); }
                w.Write(b.ProcessId ?? 0); w.Write(b.ProcessStartUtc?.UtcDateTime.Ticks ?? 0); w.Write(b.ProjectFile ?? ""); w.Write(b.Epoch); w.Write(b.WorkerEpoch); w.Write(b.Ownership);
                w.Write(o.TargetId); w.Write(o.OfflineState); w.Write(o.OfflineTargets.Length); foreach (var p in o.OfflineTargets) w.Write(p); w.Write(o.InventoryHash); w.Write(o.Routes.Length);
                foreach (var route in o.Routes.OrderBy(x => x.Id, StringComparer.Ordinal))
                { w.Write(route.Id); w.Write(route.TargetId); w.Write(route.NativeCalls.Length); foreach (var call in route.NativeCalls) w.Write(call); }
            }
            return CandidatePrimitives.ByteHash(stream.ToArray());
        }
        public static void Validate(FallbackObservation o)
        {
            if (o == null || o.Binding == null || o.Binding.ProcessId <= 0 || !o.Binding.ProcessId.HasValue || !o.Binding.ProcessStartUtc.HasValue
                || o.Binding.ProcessStartUtc.Value.Offset != TimeSpan.Zero || string.IsNullOrEmpty(o.Binding.ProjectFile) || o.Binding.Epoch < 0 || o.Binding.WorkerEpoch < 0
                || string.IsNullOrEmpty(o.TargetId) || o.Routes == null || o.OfflineTargets == null || string.IsNullOrEmpty(o.InventoryHash)
                || !new[] { "offline", "online", "unavailable", "not-applicable" }.Contains(o.OfflineState)
                || o.Routes.Any(r => r == null || r.Id.Length == 0 || r.TargetId.Length == 0 || r.NativeCalls == null || r.NativeCalls.Length == 0)
                || o.Routes.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != o.Routes.Length)
                throw new InvalidDataException("Incomplete fallback observation.");
        }
        public static void Admission(IFallbackAdapter adapter, FallbackCheck check)
        {
            Validate(check.Before); var r = check.Request;
            if (r.RetryPolicy != "never") CandidatePrimitives.Invalid("retryPolicy");
            if (adapter.RequiresOffline && check.Before.OfflineState == "online") CandidatePrimitives.Fail("offline", r.SoftwarePath);
            if (adapter.RequiresOffline && check.Before.OfflineState != "offline") CandidatePrimitives.Fail("precondition", "positive-offline-evidence");
            if (!check.Before.Routes.Any(x => x.Id == r.Route)) CandidatePrimitives.Fail("precondition", "discovered-exact-route");
        }
        public static void Verify(FallbackCheck check, FallbackObservation fresh, bool after = false)
        {
            Validate(fresh); var b = check.Before.Binding; var f = fresh.Binding;
            if (b.ProcessId != f.ProcessId || b.ProcessStartUtc != f.ProcessStartUtc || b.ProjectFile != f.ProjectFile || b.Epoch != f.Epoch
                || b.WorkerEpoch != f.WorkerEpoch || b.Ownership != f.Ownership || check.Before.TargetId != fresh.TargetId)
                CandidatePrimitives.Fail("identity", "fallback-binding-and-target");
            if (after && !fresh.Routes.Any(r => r.Id == check.Request.Route && r.TargetId == check.Before.Routes.Single(x => x.Id == check.Request.Route).TargetId))
                CandidatePrimitives.Fail("identity", "fallback-selected-route");
            if (!after && Digest(check) != Digest(new FallbackCheck { Request = check.Request, Before = fresh })) CandidatePrimitives.Fail("stale", "fallback-state-changed");
        }
    }
    public static partial class CandidateExecution
    {
        public static FallbackAttempt Fallback(IFallbackAdapter adapter, FallbackCheck check)
        {
            var a = new FallbackAttempt { HandleRefreshes = check.InitialReadRefreshes, ReadOnlyStaleObject = check.InitialReadRefreshes == 1, NothingExecuted = check.InitialReadRefreshes == 1 };
            try
            {
                if (check.Digest != FallbackPrimitives.Digest(check)) CandidatePrimitives.Invalid("fallback-check");
                FallbackPrimitives.Admission(adapter, check); FallbackPrimitives.Verify(check, adapter.Observe());
                adapter.BeforeAction(); FallbackPrimitives.Verify(check, adapter.Observe());
                if (check.Digest != FallbackPrimitives.Digest(check)) CandidatePrimitives.Fail("stale", "fallback-request-changed");
                if (adapter.NeedsConfiguration)
                {
                    a.Stage = "configuration"; a.ConfigurationIssued = true; a.WriteIssued = true;
                    a.ConfigurationResult = adapter.ApplyConfiguration(check.Request.Route);
                    if (a.ConfigurationResult != true) { a.Fault = new CandidateFault { Kind = "completion", Subject = "ApplyConfiguration-false" }; return a; }
                    FallbackPrimitives.Verify(check, adapter.Observe());
                }
                a.Stage = "operation";
                // The primitive records each call immediately before invoking it, including read commands.
                try { a.NativeResult = adapter.Execute(check.Request, a); }
                catch (ReadHandleStaleException stale)
                {
                    a.ReadOnlyStaleObject = stale.ReadOnlyObject; a.NothingExecuted = stale.NothingExecuted;
                    if (!check.Request.RefreshReadHandle || a.HandleRefreshes != 0 || !stale.ReadOnlyObject || !stale.NothingExecuted || a.OperationIssued || a.WriteIssued)
                        throw;
                    a.Stage = "refresh-read-handle"; a.HandleRefreshes = 1; adapter.RefreshReadHandle();
                    // A refresh only replaces the read proxy. The reviewed target, inventory and route must still match.
                    FallbackPrimitives.Verify(check, adapter.Observe());
                    a.Stage = "operation"; a.NativeResult = adapter.Execute(check.Request, a);
                }
                if (a.NativeResult == null || !a.OperationIssued) throw new InvalidDataException("Missing native issuance/result evidence.");
                a.Stage = "readback"; a.After = adapter.Observe(); FallbackPrimitives.Verify(check, a.After, true);
                a.Stage = "completed";
            }
            catch (Exception error)
            {
                a.Fault = error is CandidateObservationException observed ? observed.Fault : new CandidateFault { Kind = "native", Subject = a.Stage };
                // Configuration returning false is known. An exception after any write is uncertain.
                a.RequiresSessionReset = a.WriteIssued;
                if (a.RequiresSessionReset) adapter.MarkUncertain();
            }
            return a;
        }
    }
}
