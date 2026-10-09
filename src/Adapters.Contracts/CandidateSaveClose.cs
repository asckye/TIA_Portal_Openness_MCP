using System;
using System.IO;
using System.Text;

namespace TiaMcp.Adapters.Contracts.Candidates
{
    public sealed class SaveCloseRequest
    {
        public string Action { get; set; } = "save";
        public bool SaveChanges { get; set; }
        public bool DiscardChanges { get; set; }
        public string NewProjectPath { get; set; } = "";
    }
    public sealed class SaveCloseObservation
    {
        public SessionState Binding { get; set; } = new SessionState();
        public bool? Dirty { get; set; }
        public bool LocalSession { get; set; }
        public string ObjectValidity { get; set; } = "unavailable";
        public bool DisconnectSupported { get; set; }
        public string WorkerCleanup { get; set; } = "not-requested";
    }
    public sealed class SaveCloseCheck
    {
        public SaveCloseRequest Request { get; set; } = new SaveCloseRequest();
        public SaveCloseObservation Before { get; set; } = new SaveCloseObservation();
        public string Digest { get; set; } = "";
    }
    public sealed class SaveCloseAttempt
    {
        public bool Issued { get; set; }
        public bool RequiresSessionReset { get; set; }
        public SaveCloseObservation? After { get; set; }
        public CandidateFault? Fault { get; set; }
    }
    public sealed class SaveCloseCall
    {
        public string Action { get; set; } = "observe";
        public SaveCloseCheck? Check { get; set; }
    }
    public sealed class SaveCloseReply : TiaMcp.Adapters.Contracts.IWorkerOperationReply
    {
        bool TiaMcp.Adapters.Contracts.IWorkerOperationReply.MayHaveChanged => Attempt?.Issued == true;
        bool TiaMcp.Adapters.Contracts.IWorkerOperationReply.BlockReadsAfterUncertain => true;
        public SaveCloseObservation? Observation { get; set; }
        public SaveCloseAttempt? Attempt { get; set; }
        public CandidateFault? Fault { get; set; }
        public bool RequiresSessionReset { get; set; }
    }
    public interface ISaveCloseAdapter
    {
        SaveCloseObservation Observe();
        void BeforeAction();
        void Save();
        void SaveCopy(string directory);
        void Close();
        void Disconnect();
        void MarkUncertain();
    }
    public interface ISaveCloseBoundary { SaveCloseAttempt Execute(SaveCloseCheck check); }
    public static class SaveClosePrimitives
    {
        public static string Digest(SaveCloseCheck check)
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write("save-close-v1"); var r = check.Request; var o = check.Before; var s = o.Binding;
                writer.Write(r.Action); writer.Write(r.SaveChanges); writer.Write(r.DiscardChanges); writer.Write(r.NewProjectPath);
                writer.Write(s.ProcessId ?? 0); writer.Write(s.ProcessStartUtc?.UtcDateTime.Ticks ?? 0); writer.Write(s.ProjectFile == null ? "" : CandidatePrimitives.CanonicalProject(s.ProjectFile));
                writer.Write(s.Ownership); writer.Write(s.Epoch); writer.Write(s.WorkerEpoch);
                writer.Write(o.Dirty.HasValue); writer.Write(o.Dirty ?? false); writer.Write(o.LocalSession);
                writer.Write(o.ObjectValidity); writer.Write(o.DisconnectSupported); writer.Write(o.WorkerCleanup);
            }
            return CandidatePrimitives.ByteHash(stream.ToArray());
        }
        public static void Verify(SaveCloseCheck check, SaveCloseObservation fresh)
        {
            var a = check.Before.Binding; var b = fresh.Binding;
            if (a.ProcessId != b.ProcessId || a.ProcessStartUtc != b.ProcessStartUtc || (a.ProjectFile == null ? "" : CandidatePrimitives.CanonicalProject(a.ProjectFile)) != (b.ProjectFile == null ? "" : CandidatePrimitives.CanonicalProject(b.ProjectFile))
                || a.Ownership != b.Ownership || a.Epoch != b.Epoch || a.WorkerEpoch != b.WorkerEpoch)
                CandidatePrimitives.Fail("identity", "save-close-binding");
            if (Digest(check) != Digest(new SaveCloseCheck { Request = check.Request, Before = fresh }))
                CandidatePrimitives.Fail("stale", "dirty-validity-or-capability-changed");
        }
        public static void Admission(SaveCloseCheck check)
        {
            var r = check.Request; var o = check.Before; var s = o.Binding;
            if (r.SaveChanges || r.Action != "close" && r.DiscardChanges || r.Action != "save-copy" && r.NewProjectPath.Length > 0)
                CandidatePrimitives.Invalid("save-close-options");
            if (!s.ProcessId.HasValue || !s.ProcessStartUtc.HasValue || s.Ownership == "unknown") CandidatePrimitives.Fail("precondition", "known-binding");
            if (r.Action == "disconnect")
            {
                if (!o.DisconnectSupported || s.ProjectFile != null && s.Ownership == "owned") CandidatePrimitives.Fail("precondition", "explicit-close-before-detach");
            }
            else
            {
                if (s.ProjectFile == null || o.ObjectValidity != "valid" || !o.Dirty.HasValue) CandidatePrimitives.Fail("precondition", "valid-bound-project");
                if (r.Action == "close" && (s.Ownership != "owned" || o.Dirty == true && !r.DiscardChanges)) CandidatePrimitives.Fail("precondition", "owned-and-saved-or-discarded-project");
                if (r.Action == "save-copy" && o.LocalSession) CandidatePrimitives.Fail("precondition", "ordinary-project-copy");
            }
        }
    }
    public static partial class CandidateExecution
    {
        public static SaveCloseAttempt SaveClose(ISaveCloseAdapter adapter, SaveCloseCheck check)
        {
            var result = new SaveCloseAttempt();
            try
            {
                if (check.Digest != SaveClosePrimitives.Digest(check)) CandidatePrimitives.Invalid("save-close-check");
                SaveClosePrimitives.Admission(check);
                SaveClosePrimitives.Verify(check, adapter.Observe());
                adapter.BeforeAction();
                SaveClosePrimitives.Verify(check, adapter.Observe());
                if (check.Digest != SaveClosePrimitives.Digest(check)) CandidatePrimitives.Fail("stale", "save-close-request-changed");
                if (check.Request.Action == "save-copy" && (Directory.Exists(check.Request.NewProjectPath) || File.Exists(check.Request.NewProjectPath)))
                    CandidatePrimitives.Fail("stale", "copy-destination-appeared");
                if (check.Request.Action != "save" && check.Request.Action != "save-copy" && check.Request.Action != "close" && check.Request.Action != "disconnect")
                    CandidatePrimitives.Invalid("action");
                result.Issued = true;
                switch (check.Request.Action)
                {
                    case "save": adapter.Save(); break;
                    case "save-copy": adapter.SaveCopy(check.Request.NewProjectPath); break;
                    case "close": adapter.Close(); break;
                    case "disconnect": adapter.Disconnect(); break;
                }
                result.After = adapter.Observe();
                VerifySaveCloseReadback(check, result.After);
            }
            catch (Exception error)
            {
                result.Fault = error is CandidateObservationException observed ? observed.Fault : new CandidateFault { Kind = "preflight", Subject = "save-close-native-or-observation" };
                result.RequiresSessionReset = result.Issued;
                try { result.After = adapter.Observe(); }
                catch (Exception) /* swallow(privacy): failed observation is reported as unavailable; never retry a native save or close */ { }
                if (result.Issued) adapter.MarkUncertain();
            }
            return result;
        }
        public static void VerifySaveCloseReadback(SaveCloseCheck check, SaveCloseObservation after)
        {
            if (after == null) throw new InvalidDataException("Missing save/close readback.");
            var before = check.Before.Binding; var state = after.Binding; string action = check.Request.Action;
            if (state.WorkerEpoch != before.WorkerEpoch) throw new InvalidDataException("Worker epoch changed inside one native dispatch.");
            if (action == "disconnect")
            {
                if (state.ProcessId != null || state.ProjectFile != null || state.Ownership != "none" || state.Epoch != before.Epoch + 1)
                    throw new InvalidDataException("Disconnect did not verify a detached binding.");
                return;
            }
            if (state.ProcessId != before.ProcessId || state.ProcessStartUtc != before.ProcessStartUtc)
                throw new InvalidDataException("Process identity changed during save/close.");
            if (action == "close")
            {
                if (state.ProjectFile != null || state.Ownership != "none" || state.Epoch != before.Epoch + 1 || after.ObjectValidity != "invalid")
                    throw new InvalidDataException("Close did not verify an unbound, invalidated project handle.");
            }
            else
            {
                string expected = action == "save-copy" ? CandidatePrimitives.CanonicalProject(Path.Combine(check.Request.NewProjectPath, Path.GetFileName(before.ProjectFile!))) : before.ProjectFile!;
                if (state.ProjectFile == null || CandidatePrimitives.CanonicalProject(state.ProjectFile) != expected || state.Ownership != before.Ownership || state.Epoch != before.Epoch + (action == "save-copy" ? 1 : 0)
                    || after.ObjectValidity != "valid" || after.Dirty != false || after.LocalSession != check.Before.LocalSession)
                    throw new InvalidDataException("Save did not verify the planned identity and clean state.");
            }
        }
    }
}
