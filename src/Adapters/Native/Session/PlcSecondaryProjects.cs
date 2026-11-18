using System;
using System.IO;
using System.Linq;
using Siemens.Engineering;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
#if PROJECT_UMAC
        private string? secondaryProjectFile;
        private int secondaryProcessId;
        private long secondaryProcessStartTicks;
#endif
        private string? SecondaryProjectFile
        {
            get
            {
#if PROJECT_UMAC
                return secondaryProjectFile;
#else
                return null;
#endif
            }
        }
        private void ForgetSecondaryProject()
        {
#if PROJECT_UMAC
            secondaryProjectFile = null;
            secondaryProcessId = 0;
            secondaryProcessStartTicks = 0;
#endif
        }
        private string? ValidateSecondaryRecovery(string file, int processId, long processStartTicks)
        {
            string? reference = string.IsNullOrEmpty(file) ? null : MutationIdentityPolicy.AbsoluteFile(file);
            if (reference != null && (processId <= 0 || processStartTicks <= 0))
                throw new AdapterPreconditionException("Secondary project recovery requires the holding TIA PID and process start time.",
                    "secondaryProjectFile", false);
#if !PROJECT_UMAC
            if (reference != null) throw new AdapterPreconditionException("Secondary project recovery requires V15.1 or later.",
                "secondaryProjectFile", false);
#endif
            return reference;
        }
        private void RestoreSecondaryRecord(string? file, int processId, long processStartTicks)
        {
#if PROJECT_UMAC
            if (file != null)
            {
                secondaryProjectFile = file;
                secondaryProcessId = processId;
                secondaryProcessStartTicks = processStartTicks;
            }
#endif
        }
        private sealed class SecondaryProjectCloseResult : PlcMutationResult
        {
            public bool SecondaryProjectGone { get; set; }
        }
        private void RequireSecondaryClosed(string operation)
        {
            Check(true);
#if PROJECT_UMAC
            ForgetSecondaryIfHolderGone();
#endif
            if (SecondaryProjectFile != null)
                throw new AdapterPreconditionException("CloseProject secondaryProjectFile must explicitly close this worker's secondary project before "
                        + operation + ".",
                    "secondaryProjectFile", false);
        }
        private PlcMutationResult OpenSecondaryProject(string path, bool dryRun, string umacUserName, string umacPassword, string umacUserType)
        {
            Check();
#if PROJECT_UMAC
            lifecycle.RequireBound();
            if (lifecycle.IsLocalSession || project == null || !project.IsPrimary)
                throw new AdapterPreconditionException("Secondary open requires a bound primary project, not a local session or secondary project.",
                    "openMode", false);
            RequireSecondaryClosed("OpenProject");
            if (PlcLifecyclePolicy.IsSessionFile(ReleaseKey, path))
                throw new AdapterPreconditionException("Secondary open requires this release's .ap project file; local sessions are not supported.", "path");
            var input = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(Path.GetFullPath(path)));
            if (!input.Exists) throw new AdapterPreconditionException("Project file does not exist.", "path", true, new FileNotFoundException());
            var connection = Portal();
            if (connection.Projects.Any(p => string.Equals(p.Path.FullName, input.FullName, StringComparison.OrdinalIgnoreCase)))
                throw new AdapterPreconditionException("Project is already open; secondary open never replaces an open project.", "path", false);
            using var login = UmacCredentialsFor(umacUserName, umacPassword, umacUserType, "umacUserName");
            long holderStart = SecondaryProcessStart(lifecycle.ProcessId!.Value)
                ?? throw new AdapterPreconditionException("Cannot identify the holding TIA process start time.", "openMode", false);
            if (!dryRun)
            {
                Project opened;
                using (login.Authenticate(connection))
                    opened = OpennessRefusalMapping.Run("open", login.Given,
                        () => connection.Projects.Open(input, login.Delegate, ProjectOpenMode.Secondary));
                if (opened.IsPrimary) throw new InvalidOperationException("Secondary open returned a primary project; inspect GetProjectInfo.");
                string file = opened.Path.FullName;
                secondaryProjectFile = file;
                secondaryProcessId = lifecycle.ProcessId.Value;
                secondaryProcessStartTicks = holderStart;
            }
            return new PlcMutationResult { Operation = "OpenProject", Executed = !dryRun, ProjectFile = input.FullName };
#else
            throw new AdapterPreconditionException("Secondary project open requires V15.1 or later.", "openMode", false);
#endif
        }
#if PROJECT_UMAC
        private static long? SecondaryProcessStart(int pid)
        {
            try { return TiaMcp.Adapters.Session.SessionCallbackPrimitives.ProcessStart(pid); }
            catch (Exception error) when (error is ArgumentException || error is InvalidOperationException
                || error is System.ComponentModel.Win32Exception || error is System.Security.SecurityException)
            { /* swallow(env-probe): unknown process identity cannot confirm the holder or absence. */ return null; }
        }
        private bool ForgetSecondaryIfHolderGone()
        {
            if (secondaryProjectFile == null) return false;
            bool gone;
            try
            {
                using var holder = System.Diagnostics.Process.GetProcessById(secondaryProcessId);
                gone = holder.HasExited || holder.StartTime.ToUniversalTime().Ticks != secondaryProcessStartTicks;
            }
            catch (ArgumentException) { /* swallow(env-probe): no process exists with the recorded PID. */ gone = true; }
            catch (Exception error) when (error is InvalidOperationException || error is System.ComponentModel.Win32Exception
                || error is System.Security.SecurityException)
            { /* swallow(env-probe): access failure is not evidence that the holding process exited. */ gone = false; }
            if (gone) ForgetSecondaryProject();
            return gone;
        }
#endif
        private PlcMutationResult CloseSecondaryProject(string file, bool dryRun)
        {
            Check();
#if PROJECT_UMAC
            if (secondaryProjectFile == null)
                throw new AdapterPreconditionException("This worker has no recorded secondary project to close.", "secondaryProjectFile", false);
            string target;
            try
            {
                target = MutationIdentityPolicy.AbsoluteFile(file);
                MutationIdentityPolicy.RequireSameProject(target, secondaryProjectFile);
            }
            catch (ArgumentException error) { throw new AdapterPreconditionException(error.Message, "secondaryProjectFile", true, error); }
            if (ForgetSecondaryIfHolderGone())
                return new SecondaryProjectCloseResult { Operation = "CloseProject", Executed = false, ProjectFile = target,
                    SecondaryProjectGone = true };
            long? attachedStart = lifecycle.ProcessId.HasValue ? SecondaryProcessStart(lifecycle.ProcessId.Value) : null;
            if (lifecycle.ProcessId != secondaryProcessId || attachedStart == null || attachedStart != secondaryProcessStartTicks)
                throw new AdapterPreconditionException("The secondary project is held by TIA PID " + secondaryProcessId + " (started "
                    + new DateTime(secondaryProcessStartTicks, DateTimeKind.Utc).ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                    + "). ConnectPortal to that holding process before CloseProject secondaryProjectFile.",
                    "secondaryProjectFile", false);
            var owned = Portal().Projects.SingleOrDefault(p => string.Equals(p.Path.FullName, target, StringComparison.OrdinalIgnoreCase));
            if (owned == null)
            {
                ForgetSecondaryProject();
                return new SecondaryProjectCloseResult { Operation = "CloseProject", Executed = false, ProjectFile = target,
                    SecondaryProjectGone = true };
            }
            if (owned.IsPrimary) throw new AdapterPreconditionException("Closing a primary project through secondaryProjectFile is refused.",
                "secondaryProjectFile", false);
            if (!dryRun) { owned.Close(); ForgetSecondaryProject(); }
            return new SecondaryProjectCloseResult { Operation = "CloseProject", Executed = !dryRun, ProjectFile = target };
#else
            throw new AdapterPreconditionException("Secondary project close requires V15.1 or later.", "secondaryProjectFile", false);
#endif
        }
    }
}
