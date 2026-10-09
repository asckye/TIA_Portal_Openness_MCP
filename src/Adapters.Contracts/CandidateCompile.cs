using System;
using System.IO;
using System.Linq;
using System.Text;

namespace TiaMcp.Adapters.Contracts.Candidates
{
    public sealed class CompileRequest
    {
        public string Entry { get; set; } = "CompilePlcSoftware";
        public string SoftwarePath { get; set; } = "";
        public string[] DevicePath { get; set; } = Array.Empty<string>();
        public string[] ItemPath { get; set; } = Array.Empty<string>();
        public string OfflinePolicy { get; set; } = "require";
        public bool PasswordProvided { get; set; }
    }
    public sealed class CompileObservation
    {
        public SessionState Binding { get; set; } = new SessionState();
        public string ObjectValidity { get; set; } = "unavailable";
        public bool? Dirty { get; set; }
        public string TargetId { get; set; } = "";
        public string SoftwareId { get; set; } = "";
        public string TargetKind { get; set; } = "";
        public string TargetName { get; set; } = "";
        public bool Compilable { get; set; }
        public string OfflineState { get; set; } = "unavailable";
        public string[] OfflineTargets { get; set; } = Array.Empty<string>();
        public string SafetyPermission { get; set; } = "api-unavailable";
    }
    public sealed class CompileMessage
    {
        public string State { get; set; } = "";
        public string Path { get; set; } = "";
        public string Description { get; set; } = "";
        public int ErrorCount { get; set; }
        public int WarningCount { get; set; }
        public CompileMessage[] Messages { get; set; } = Array.Empty<CompileMessage>();
    }
    public sealed class CompileDiagnostics
    {
        public string State { get; set; } = "";
        public int RootErrorCount { get; set; }
        public int RootWarningCount { get; set; }
        public CompileMessage[] Messages { get; set; } = Array.Empty<CompileMessage>();
        public int LeafErrorCount { get; set; }
        public int LeafWarningCount { get; set; }
        public bool CountsConsistent { get; set; }
    }
    public sealed class CompileCheck
    {
        public CompileRequest Request { get; set; } = new CompileRequest();
        public CompileObservation Before { get; set; } = new CompileObservation();
        public string Digest { get; set; } = "";
    }
    public sealed class CompileAttempt
    {
        public bool LoginIssued { get; set; }
        public bool LoginCreated { get; set; }
        public bool CompileIssued { get; set; }
        public bool LogoutIssued { get; set; }
        public bool DispatchUncertain { get; set; }
        public bool RequiresSessionReset { get; set; }
        public string Stage { get; set; } = "preflight";
        public string CleanupState { get; set; } = "not-needed";
        public CompileDiagnostics? Diagnostics { get; set; }
        public CompileObservation? After { get; set; }
        public CandidateFault? Fault { get; set; }
        public CandidateFault? CleanupFault { get; set; }
    }
    public sealed class CompileCall
    {
        public string Action { get; set; } = "observe";
        public CompileRequest Request { get; set; } = new CompileRequest();
        public CompileCheck? Check { get; set; }
        public string Password { get; set; } = "";
    }
    public sealed class CompileReply : TiaMcp.Adapters.Contracts.IWorkerOperationReply
    {
        bool TiaMcp.Adapters.Contracts.IWorkerOperationReply.MayHaveChanged => Attempt?.CompileIssued == true || Attempt?.LoginIssued == true || Attempt?.LogoutIssued == true;
        bool TiaMcp.Adapters.Contracts.IWorkerOperationReply.BlockReadsAfterUncertain => true;
        public CompileObservation? Observation { get; set; }
        public CompileAttempt? Attempt { get; set; }
        public CandidateFault? Fault { get; set; }
        public bool RequiresSessionReset { get; set; }
    }
    public interface ICompileAdapter
    {
        CompileObservation Observe();
        void BeforeAction();
        void Login(string password);
        CompileDiagnostics Compile();
        void Logout();
        void MarkUncertain();
    }
    public interface ICompileBoundary { CompileAttempt Execute(CompileCheck check, string password); }
    public static class CompilePrimitives
    {
        public static string Digest(CompileCheck check)
        {
            using var stream = new MemoryStream();
            using (var w = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                var r = check.Request; var o = check.Before; var b = o.Binding;
                w.Write("compile-v1"); w.Write(r.Entry); w.Write(r.SoftwarePath); w.Write(r.OfflinePolicy); w.Write(r.PasswordProvided);
                w.Write(r.DevicePath.Length); foreach (var p in r.DevicePath) w.Write(p);
                w.Write(r.ItemPath.Length); foreach (var p in r.ItemPath) w.Write(p);
                w.Write(b.ProcessId ?? 0); w.Write(b.ProcessStartUtc?.UtcDateTime.Ticks ?? 0); w.Write(b.ProjectFile ?? "");
                w.Write(b.Epoch); w.Write(b.WorkerEpoch); w.Write(b.Ownership); w.Write(o.ObjectValidity); w.Write(o.Dirty.HasValue); w.Write(o.Dirty ?? false);
                w.Write(o.TargetId); w.Write(o.SoftwareId); w.Write(o.TargetKind); w.Write(o.TargetName); w.Write(o.Compilable);
                w.Write(o.OfflineState); w.Write(o.OfflineTargets.Length); foreach (var p in o.OfflineTargets) w.Write(p);
                w.Write(o.SafetyPermission);
            }
            return CandidatePrimitives.ByteHash(stream.ToArray());
        }
        public static void Validate(CompileObservation o)
        {
            if (o == null || o.Binding == null || !o.Binding.ProcessId.HasValue || o.Binding.ProcessId <= 0 || !o.Binding.ProcessStartUtc.HasValue
                || o.Binding.ProcessStartUtc.Value.Offset != TimeSpan.Zero || o.Binding.ProjectFile == null || o.Binding.Epoch < 0 || o.Binding.WorkerEpoch < 0
                || o.TargetId.Length == 0 || o.TargetKind.Length == 0 || o.OfflineTargets == null
                || !new[] { "valid", "invalid", "unavailable" }.Contains(o.ObjectValidity)
                || !new[] { "offline", "online", "unavailable", "not-applicable" }.Contains(o.OfflineState)
                || !new[] { "api-unavailable", "not-applicable", "unprotected", "logged-on", "password-required" }.Contains(o.SafetyPermission))
                throw new InvalidDataException("Incomplete compile observation.");
        }
        public static void Admission(CompileCheck check)
        {
            Validate(check.Before); var o = check.Before; var r = check.Request;
            if (r.OfflinePolicy != "require") CandidatePrimitives.Invalid("offlinePolicy");
            if (!new[] { "CompilePlcSoftware", "CompilePlcDiagnostics", "CompileHmiDiagnostics", "CompileDevice" }.Contains(r.Entry)) CandidatePrimitives.Invalid("entry");
            if (o.ObjectValidity != "valid" || !o.Dirty.HasValue) CandidatePrimitives.Fail("precondition", "valid-bound-project");
            if (!o.Compilable) CandidatePrimitives.Fail("unsupported", "ICompilable");
            if (o.OfflineState == "online") CandidatePrimitives.Fail("offline", o.TargetName);
            if (o.OfflineState == "unavailable") CandidatePrimitives.Fail("precondition", "positive-offline-evidence");
            if (r.PasswordProvided && (o.SafetyPermission == "api-unavailable" || o.SafetyPermission == "not-applicable")) CandidatePrimitives.Fail("unsupported", "SafetyAdministration");
            if (!r.PasswordProvided && o.SafetyPermission == "password-required") CandidatePrimitives.Fail("authentication", "SafetyOfflineProgram");
        }
        public static void Verify(CompileCheck check, CompileObservation fresh, bool created = false, bool afterCompile = false)
        {
            VerifyIdentity(check, fresh); var b = check.Before;
            if (fresh.ObjectValidity != "valid" || !fresh.Dirty.HasValue || !fresh.Compilable || b.TargetName != fresh.TargetName
                || fresh.OfflineState != b.OfflineState || !fresh.OfflineTargets.SequenceEqual(b.OfflineTargets)
                || (!afterCompile && fresh.Dirty != b.Dirty) || fresh.SafetyPermission != (created ? "logged-on" : b.SafetyPermission))
                CandidatePrimitives.Fail("stale", "compile-preconditions-changed");
        }
        private static void VerifyIdentity(CompileCheck check, CompileObservation fresh)
        {
            Validate(fresh); var b = check.Before; var x = b.Binding; var y = fresh.Binding;
            if (x.ProcessId != y.ProcessId || x.ProcessStartUtc != y.ProcessStartUtc || x.ProjectFile != y.ProjectFile || x.Epoch != y.Epoch
                || x.WorkerEpoch != y.WorkerEpoch || x.Ownership != y.Ownership || b.TargetId != fresh.TargetId || b.SoftwareId != fresh.SoftwareId || b.TargetKind != fresh.TargetKind)
                CandidatePrimitives.Fail("identity", "compile-binding-and-target");
        }
        public static void VerifyPermission(CompileCheck check, CompileObservation fresh, bool created)
        {
            VerifyIdentity(check, fresh);
            if (fresh.ObjectValidity != "valid" || !fresh.Dirty.HasValue || fresh.SafetyPermission != (created ? "logged-on" : check.Before.SafetyPermission))
                CandidatePrimitives.Fail("stale", "compile-project-or-safety-state");
        }
        public static void Count(CompileDiagnostics d)
        {
            if (d == null || d.Messages == null || d.RootErrorCount < 0 || d.RootWarningCount < 0) throw new InvalidDataException("Incomplete compiler result.");
            int errors = 0, warnings = 0, nodes = 0;
            void Walk(CompileMessage[] messages, int depth)
            {
                if (depth > 128) throw new InvalidDataException("Compiler diagnostic depth exceeded.");
                foreach (var m in messages)
                {
                    if (++nodes > 100000 || m == null || m.Messages == null || m.ErrorCount < 0 || m.WarningCount < 0) throw new InvalidDataException("Incomplete diagnostic tree.");
                    if (m.Messages.Length > 0) Walk(m.Messages, depth + 1);
                    else
                    {
                        errors = checked(errors + Math.Max(m.ErrorCount, m.State == "Error" ? 1 : 0));
                        warnings = checked(warnings + Math.Max(m.WarningCount, m.State == "Warning" ? 1 : 0));
                    }
                }
            }
            Walk(d.Messages, 0); d.LeafErrorCount = errors; d.LeafWarningCount = warnings;
            d.CountsConsistent = errors == d.RootErrorCount && warnings == d.RootWarningCount;
        }
    }
    public static partial class CandidateExecution
    {
        public static CompileAttempt Compile(ICompileAdapter adapter, CompileCheck check, string password)
        {
            var result = new CompileAttempt();
            try
            {
                if (check.Digest != CompilePrimitives.Digest(check) || check.Request.PasswordProvided != (password.Length > 0)) CandidatePrimitives.Invalid("compile-check");
                CompilePrimitives.Admission(check); CompilePrimitives.Verify(check, adapter.Observe());
                adapter.BeforeAction(); CompilePrimitives.Verify(check, adapter.Observe());
                if (check.Digest != CompilePrimitives.Digest(check)) CandidatePrimitives.Fail("stale", "compile-request-changed");
                if (check.Request.PasswordProvided && check.Before.SafetyPermission != "logged-on")
                {
                    result.Stage = "login"; result.LoginIssued = true;
                    adapter.Login(password); var permission = adapter.Observe();
                    CompilePrimitives.VerifyPermission(check, permission, true); result.LoginCreated = true;
                    CompilePrimitives.Verify(check, permission, true);
                }
                result.Stage = "compile"; result.CompileIssued = true;
                result.Diagnostics = adapter.Compile(); CompilePrimitives.Count(result.Diagnostics);
                CompilePrimitives.Verify(check, adapter.Observe(), result.LoginCreated, true);
                result.Stage = "completed";
            }
            catch (Exception error)
            {
                result.Fault = Fault(error, result.Stage);
                if (result.CompileIssued) result.RequiresSessionReset = true;
                if (result.LoginIssued && !result.LoginCreated)
                {
                    try
                    {
                        var fresh = adapter.Observe();
                        // A login that threw may still have established permission. Only a proven transition is ours.
                        CompilePrimitives.VerifyPermission(check, fresh, fresh.SafetyPermission == "logged-on");
                        result.LoginCreated = fresh.SafetyPermission == "logged-on";
                    }
                    catch (Exception) /* swallow(privacy): unavailable login ownership is explicit and forbids blind logout or replay */ { result.RequiresSessionReset = true; result.CleanupState = "unavailable"; }
                }
            }
            finally
            {
                if (result.LoginCreated)
                {
                    try
                    {
                        // Never log out a changed binding or a permission state whose ownership is no longer established.
                        CompilePrimitives.VerifyPermission(check, adapter.Observe(), true);
                        result.LogoutIssued = true; adapter.Logout(); result.CleanupState = "succeeded";
                    }
                    catch (Exception error) { result.CleanupState = result.LogoutIssued ? "failed" : "unavailable"; result.CleanupFault = Fault(error, "logout"); }
                }
                try
                {
                    result.After = adapter.Observe();
                    if (result.LogoutIssued && result.CleanupState == "succeeded" && result.After.SafetyPermission == "logged-on")
                    {
                        result.CleanupState = "failed";
                        result.CleanupFault = new CandidateFault { Kind = "completion", Subject = "logout-permission-readback" };
                    }
                    CompilePrimitives.VerifyPermission(check, result.After, result.LoginCreated && result.CleanupState != "succeeded" && result.After.SafetyPermission == "logged-on");
                }
                catch (Exception error)
                {
                    if (result.LoginIssued || result.CompileIssued || result.LogoutIssued) { result.RequiresSessionReset = true; result.Fault ??= Fault(error, "readback"); }
                }
                if (result.CleanupState == "unavailable") result.RequiresSessionReset = result.LoginIssued || result.CompileIssued;
                if (result.RequiresSessionReset) adapter.MarkUncertain();
            }
            return result;
        }
        private static CandidateFault Fault(Exception error, string stage) => error is CandidateObservationException observed ? observed.Fault
            : new CandidateFault { Kind = "native", Subject = stage };
    }
}
