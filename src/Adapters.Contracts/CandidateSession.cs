using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TiaMcp.Adapters.Contracts.Candidates
{
    public sealed class SessionRequest
    {
        public string Action { get; set; } = "attach";
        public int ProcessId { get; set; }
        public DateTimeOffset ProcessStartUtc { get; set; }
        public string ProjectPath { get; set; } = "";
        public bool StartNew { get; set; }
        public bool ReuseOpen { get; set; }
        public string Upgrade { get; set; } = "reject";
        public string CopyPath { get; set; } = "";
        public string OpenedProjectFile { get; set; } = "";
    }
    public sealed class SessionProcess
    {
        public int ProcessId { get; set; }
        public DateTimeOffset? ProcessStartUtc { get; set; }
        public string[] ProjectFiles { get; set; } = Array.Empty<string>();
        public bool Complete { get; set; }
    }
    public sealed class SessionState
    {
        public int? ProcessId { get; set; }
        public DateTimeOffset? ProcessStartUtc { get; set; }
        public string? ProjectFile { get; set; }
        public string Ownership { get; set; } = "none";
        public long Epoch { get; set; }
        public long WorkerEpoch { get; set; }
    }
    public sealed class SessionObservation
    {
        public SessionProcess[] Processes { get; set; } = Array.Empty<SessionProcess>();
        public SessionState State { get; set; } = new SessionState();
        public bool UpgradeSupported { get; set; }
        public bool LocalSessionOpenSupported { get; set; }
    }
    public sealed class SessionCheck
    {
        public SessionRequest Request { get; set; } = new SessionRequest();
        public SessionObservation Before { get; set; } = new SessionObservation();
        public CandidateFile[] Files { get; set; } = Array.Empty<CandidateFile>();
        public string Digest { get; set; } = "";
    }
    public sealed class SessionAttempt
    {
        public bool Issued { get; set; }
        public bool RequiresSessionReset { get; set; }
        public SessionObservation? After { get; set; }
        public CandidateFault? Fault { get; set; }
        public string? Reason { get; set; }
    }
    public sealed class SessionCandidateCall
    {
        public string Action { get; set; } = "observe";
        public SessionCheck? Check { get; set; }
    }
    public sealed class SessionCandidateReply
    {
        public SessionObservation? Observation { get; set; }
        public SessionAttempt? Attempt { get; set; }
        public CandidateFault? Fault { get; set; }
        public bool RequiresSessionReset { get; set; }
    }
    public interface ISessionCandidateAdapter
    {
        SessionObservation Observe();
        void BeforeAction();
        void Attach(SessionRequest request);
        void Bind(SessionRequest request);
        void Open(SessionRequest request);
        void MarkUncertain();
    }
    public interface ISessionCandidateBoundary { SessionAttempt Execute(SessionCheck check); }

    public static class SessionPrimitives
    {
        public const string ConfirmationReason = "awaiting-openness-confirmation";
        public const string ConfirmationGuidance = "Answer the TIA Portal Openness access prompt (Yes / Yes to all), then inspect the session state and retry with a fresh session and preview.";
        public static string? TimeoutReason(bool timeout, bool matchingProcess) => timeout && matchingProcess ? ConfirmationReason : null;
        public static bool IsTimeout(Exception error)
        {
            for (Exception? current = error; current != null; current = current.InnerException)
                if (current is TimeoutException) return true;
            return false;
        }
        public static string? ExceptionReason(Exception error)
        {
            for (Exception? current = error; current != null; current = current.InnerException)
                if (current.Data["sessionReason"] is string reason && reason == ConfirmationReason) return reason;
            return null;
        }
        public static string Digest(SessionCheck check)
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write("session-observation-v1");
                var r = check.Request;
                writer.Write(r.Action); writer.Write(r.ProcessId); writer.Write(r.ProcessStartUtc.UtcDateTime.Ticks);
                writer.Write(r.ProjectPath); writer.Write(r.StartNew); writer.Write(r.ReuseOpen); writer.Write(r.Upgrade); writer.Write(r.CopyPath); writer.Write(r.OpenedProjectFile);
                var s = check.Before.State;
                writer.Write(s.ProcessId ?? 0); writer.Write(s.ProcessStartUtc?.UtcDateTime.Ticks ?? 0); writer.Write(s.ProjectFile ?? "");
                writer.Write(s.Ownership); writer.Write(s.Epoch); writer.Write(s.WorkerEpoch); writer.Write(check.Before.UpgradeSupported); writer.Write(check.Before.LocalSessionOpenSupported);
                foreach (var p in check.Before.Processes.OrderBy(p => p.ProcessId))
                { writer.Write(p.ProcessId); writer.Write(p.ProcessStartUtc?.UtcDateTime.Ticks ?? 0); writer.Write(p.Complete);
                    writer.Write(p.ProjectFiles.Length); foreach (var f in p.ProjectFiles.OrderBy(f => f, StringComparer.Ordinal)) writer.Write(CandidatePrimitives.CanonicalProject(f)); }
                writer.Write(CandidateDigest.Files(check.Files));
            }
            return CandidatePrimitives.ByteHash(stream.ToArray());
        }
        public static void Verify(SessionCheck check, SessionObservation fresh)
        {
            var selected = fresh.Processes.SingleOrDefault(p => p.ProcessId == check.Request.ProcessId);
            if (selected == null || !selected.Complete || selected.ProcessStartUtc != check.Request.ProcessStartUtc)
                CandidatePrimitives.Fail("identity", "process-id-start-time");
            var before = check.Before.State; var after = fresh.State;
            if (before.ProcessId != after.ProcessId || before.ProcessStartUtc != after.ProcessStartUtc || before.ProjectFile != after.ProjectFile
                || before.Ownership != after.Ownership || before.Epoch != after.Epoch || before.WorkerEpoch != after.WorkerEpoch)
                CandidatePrimitives.Fail("identity", "session-binding-ownership-epoch");
            var previous = check.Before.Processes.Single(p => p.ProcessId == selected!.ProcessId);
            if (!previous.ProjectFiles.OrderBy(p => p, StringComparer.Ordinal).SequenceEqual(selected!.ProjectFiles.OrderBy(p => p, StringComparer.Ordinal)))
                CandidatePrimitives.Fail("identity", "process-project-switch");
            if (check.Before.UpgradeSupported != fresh.UpgradeSupported) CandidatePrimitives.Fail("stale", "upgrade-capability");
            if (check.Before.LocalSessionOpenSupported != fresh.LocalSessionOpenSupported) CandidatePrimitives.Fail("stale", "local-session-capability");
        }
        private static string FileHash(Stream input)
        { using var sha = System.Security.Cryptography.SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant(); }
        public static CandidateFile[] Files(string projectPath, string copyPath, bool tree)
        {
            var files = new List<CandidateFile>();
            foreach (var path in new[] { projectPath, copyPath }.Where(p => p.Length > 0))
            {
                var full = Path.GetFullPath(path);
                var parent = Path.GetDirectoryName(full)!;
                for (var directory = new DirectoryInfo(parent); directory != null; directory = directory.Parent)
                    if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) CandidatePrimitives.Fail("precondition", "reparse-project-path");
                void Add(string file)
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) CandidatePrimitives.Fail("precondition", "reparse-project-file");
                    if (files.Count >= 4096) CandidatePrimitives.Fail("precondition", "project-file-budget");
                    using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                    files.Add(new CandidateFile { Path = CandidatePrimitives.CanonicalProject(file), Exists = true, ByteLength = input.Length, Sha256 = FileHash(input) });
                }
                void Walk(string directory)
                {
                    foreach (var d in Directory.GetDirectories(directory).OrderBy(d => d, StringComparer.Ordinal))
                    { if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) CandidatePrimitives.Fail("precondition", "reparse-project-directory"); Walk(d); }
                    foreach (var f in Directory.GetFiles(directory).OrderBy(f => f, StringComparer.Ordinal)) Add(f);
                }
                if (tree) Walk(parent); else Add(full);
            }
            return files.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
        }
    }
    public static partial class CandidateExecution
    {
        public static SessionAttempt Session(ISessionCandidateAdapter adapter, SessionCheck check)
        {
            var result = new SessionAttempt();
            try
            {
                if (check.Digest != SessionPrimitives.Digest(check)) CandidatePrimitives.Invalid("session-check");
                SessionPrimitives.Verify(check, adapter.Observe());
                void Files()
                { if (check.Request.Action == "open" && CandidateDigest.Files(check.Files) != CandidateDigest.Files(SessionPrimitives.Files(check.Request.ProjectPath, check.Request.CopyPath, check.Request.Upgrade == "allow"))) CandidatePrimitives.Fail("stale", "project-copy-files"); }
                Files(); adapter.BeforeAction(); SessionPrimitives.Verify(check, adapter.Observe()); Files();
                if (check.Digest != SessionPrimitives.Digest(check)) CandidatePrimitives.Fail("stale", "session-check-changed-at-boundary");
                result.Issued = true;
                switch (check.Request.Action)
                {
                    case "attach": adapter.Attach(check.Request); break;
                    case "bind": adapter.Bind(check.Request); break;
                    case "open": adapter.Open(check.Request); break;
                    default: throw new InvalidOperationException("Unrecognized session primitive.");
                }
                result.After = adapter.Observe();
                VerifySessionReadback(check, result.After);
                if (check.Request.Upgrade == "allow")
                {
                    string source = Path.GetDirectoryName(check.Request.ProjectPath)! + "\\";
                    var original = check.Files.Where(f => f.Path.StartsWith(source, StringComparison.OrdinalIgnoreCase));
                    if (CandidateDigest.Files(original) != CandidateDigest.Files(SessionPrimitives.Files(check.Request.ProjectPath, "", true)))
                        throw new InvalidDataException("Original project changed during copy-only upgrade.");
                }
            }
            catch (Exception error)
            {
                result.Fault = error is CandidateObservationException observed ? observed.Fault : new CandidateFault { Kind = "preflight", Subject = "session-native" };
                if (result.Issued)
                {
                    result.RequiresSessionReset = true; adapter.MarkUncertain();
                    try { result.After = adapter.Observe(); }
                    catch (Exception) /* swallow(privacy): unavailable session observation remains explicit after an uncertain native action */ { }
                    bool matching = result.After?.Processes.Any(p => p.ProcessId == check.Request.ProcessId && p.ProcessStartUtc == check.Request.ProcessStartUtc) == true;
                    result.Reason = SessionPrimitives.TimeoutReason(SessionPrimitives.IsTimeout(error), matching);
                }
            }
            return result;
        }
        public static void VerifySessionReadback(SessionCheck check, SessionObservation after)
        {
            var r = check.Request; var s = after.State;
            var selected = after.Processes.SingleOrDefault(p => p.ProcessId == r.ProcessId);
            if (selected == null || !selected.Complete || selected.ProcessStartUtc != r.ProcessStartUtc
                || s.ProcessId != r.ProcessId || s.ProcessStartUtc != r.ProcessStartUtc || s.Epoch != check.Before.State.Epoch + 1)
                throw new InvalidDataException("Session process/epoch readback differs from the reviewed identity.");
            if (r.Action == "attach")
            {
                if (s.ProjectFile != null || s.Ownership != "none") throw new InvalidDataException("Attachment unexpectedly bound a project.");
                if (!selected.ProjectFiles.OrderBy(p => p, StringComparer.Ordinal).SequenceEqual(check.Before.Processes.Single(p => p.ProcessId == r.ProcessId).ProjectFiles.OrderBy(p => p, StringComparer.Ordinal)))
                    throw new InvalidDataException("Project files changed during attachment.");
            }
            else
            {
                string target = r.Action == "open" ? r.OpenedProjectFile : r.ProjectPath;
                bool same = s.ProjectFile == target;
                if (!same || s.Ownership != (r.Action == "bind" ? "borrowed" : "owned") || !selected.ProjectFiles.Contains(s.ProjectFile, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidDataException("Session project/ownership readback differs from the reviewed target.");
            }
        }
    }
}
