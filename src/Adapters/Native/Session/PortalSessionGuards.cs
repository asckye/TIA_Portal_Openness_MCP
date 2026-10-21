using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using Siemens.Engineering;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters
{
    // Documented Openness refusals (API audit 2026-10-19, F4): each type only on the releases whose SDK defines it.
    // MissingProducts/LicenseNotFound/Multiuser derive from EngineeringTargetInvocationException, EngineeringSecurityException
    // from EngineeringException; the exact type decides, never the message text.
    internal static class OpennessRefusalMapping
    {
        internal static Exception? Refusal(Exception error, out string? code)
        {
            code = null;
            for (var cause = error; cause != null; cause = cause.InnerException)
            {
                if (cause is AdapterPreconditionException) return null;
                if (cause is EngineeringSecurityException) code = OpennessRefusals.AccessDenied;
#if OPENNESS_MISSING_PRODUCTS
                else if (cause is MissingProductsException) code = OpennessRefusals.MissingProducts;
#endif
#if OPENNESS_LICENSE_EXCEPTION
                else if (cause is LicenseNotFoundException) code = OpennessRefusals.LicenseNotFound;
#endif
#if PROJECT_MULTIUSER
                else if (cause is Siemens.Engineering.Multiuser.MultiuserException) code = OpennessRefusals.MultiuserRefused;
#endif
                if (code != null) return cause;
            }
            return null;
        }

        internal static T Run<T>(string operation, bool credentialsGiven, Func<T> call)
        {
            try { return call(); }
            catch (Exception error) when (Refusal(error, out _) != null)
            {
                var native = Refusal(error, out var code)!;
                throw OpennessRefusals.Exception(code!, operation, credentialsGiven, native);
            }
        }

        internal static void Run(string operation, bool credentialsGiven, Action call)
            => Run(operation, credentialsGiven, () => { call(); return true; });
    }

    // UMAC credentials for one native open or retrieve (API audit 2026-10-19, F2). The password lives only in a
    // SecureString; it is never logged, journaled, echoed, stored in results or audit records, or shown on the approval card.
    internal sealed class UmacLogin : IDisposable
    {
        private readonly string userName = "";
        private readonly string userType = "Project";
        private readonly SecureString? secret;
        internal bool Given => secret != null;

        internal UmacLogin(string? userName, string? password, string? userType)
        {
            EngineeringCredentialRules.ValidateUmacCredentials(userName ?? "", password ?? "", userType ?? "");
            if (string.IsNullOrEmpty(userName)) return;
            this.userName = userName!;
            this.userType = string.IsNullOrEmpty(userType) ? "Project" : userType!;
            secret = EngineeringCredentialRules.ToSecureString(password!);
        }

#if PROJECT_UMAC
        private void Fill(UmacCredentials credentials)
        {
            credentials.Name = userName;
            credentials.Type = (UmacUserType)Enum.Parse(typeof(UmacUserType), userType);
            credentials.SetPassword(secret);
        }

        // Called by TIA only when the project is protected; null keeps the documented unprotected overload behaviour.
        internal UmacDelegate? Delegate => secret == null ? null : new UmacDelegate(Fill);

        // V17+: TiaPortal.Authentication is raised only when a protected project opens without UmacDelegate. When credentials
        // were given, answer it for this call with the same credentials; without credentials no handler is installed.
        internal IDisposable Authenticate(TiaPortal portal)
        {
#if PROJECT_AUTHENTICATION_EVENT
            if (secret == null) return Release.None;
            EventHandler<AuthenticationEventArgs> handler = (_, e) =>
            {
                var credentials = e.UmacCredentials;
                if (credentials == null) return;
                Fill(credentials);
                e.AuthenticationTypeProvider = AuthenticationTypeProvider.Credentials;
            };
            portal.Authentication += handler;
            return new Release(() => portal.Authentication -= handler);
#else
            return Release.None;
#endif
        }
#endif

        public void Dispose() => secret?.Dispose();

        internal sealed class Release : IDisposable
        {
            internal static readonly Release None = new Release(() => { });
            private Action? release;
            internal Release(Action release) { this.release = release; }
            public void Dispose() { var action = release; release = null; action?.Invoke(); }
        }
    }

    // A TiaPortal that raised Disposed (TIA closed) is lost; the next call reports it as a non-recoverable process loss.
    public sealed class PortalDisposedNonRecoverableException : InvalidOperationException
    {
        public PortalDisposedNonRecoverableException()
            : base("TIA Portal closed this connection (TiaPortal.Disposed). The session is lost; inspect TIA Portal and start a new session.") { }
    }

    public sealed partial class PlcFoundationEngine
    {
        // Set only by this worker's own TiaPortal(mode) start; an attached portal is never owned.
        private bool workerStartedPortal;
        private volatile bool portalLost;
        private TiaPortal? observedPortal;
        private readonly List<Dictionary<string, object?>> portalDialogs = new List<Dictionary<string, object?>>();
        private static readonly TimeSpan ProcessLookupTimeout = TimeSpan.FromSeconds(30);

        // TiaPortal.Confirmation / Notification / Disposed (F17). Handlers run on TIA's callback thread: they only answer the
        // event arguments and record, and never call Openness.
        private void ObservePortal(TiaPortal connection)
        {
            if (ReferenceEquals(observedPortal, connection)) return;
            ReleasePortalEvents();
            connection.Confirmation += OnPortalConfirmation;
            connection.Notification += OnPortalNotification;
            connection.Disposed += OnPortalDisposed;
            observedPortal = connection;
            portalLost = false;
        }

        private void ReleasePortalEvents()
        {
            var connection = observedPortal;
            observedPortal = null;
            if (connection == null) return;
            connection.Confirmation -= OnPortalConfirmation;
            connection.Notification -= OnPortalNotification;
            connection.Disposed -= OnPortalDisposed;
        }

        private void OnPortalConfirmation(object? sender, ConfirmationEventArgs e)
        {
            var offered = Enum.GetValues(typeof(ConfirmationChoices)).Cast<ConfirmationChoices>()
                .Where(choice => choice != ConfirmationChoices.None && (e.Choices & choice) == choice).Select(choice => choice.ToString()).ToArray();
            string? refusal = PortalDialogPolicy.RefusingChoice(offered);
            if (refusal != null) { e.Result = (ConfirmationResult)Enum.Parse(typeof(ConfirmationResult), refusal); e.IsHandled = true; }
            PortalDialogPolicy.Record(portalDialogs, new Dictionary<string, object?> {
                ["event"] = "Confirmation", ["utc"] = DateTimeOffset.UtcNow.ToString("O"), ["caption"] = e.Caption, ["text"] = e.Text,
                ["detailText"] = e.DetailText, ["choices"] = offered, ["answer"] = refusal ?? "unanswered",
                ["rule"] = refusal != null ? "refused by default; no approved plan answers TIA confirmations" : "no refusing choice offered; TIA decides" });
            Console.Error.WriteLine("TIA confirmation " + (refusal != null ? "refused (" + refusal + ")" : "left unanswered") + ": " + e.Caption + " - " + e.Text);
        }

        private void OnPortalNotification(object? sender, NotificationEventArgs e)
        {
            PortalDialogPolicy.Record(portalDialogs, new Dictionary<string, object?> {
                ["event"] = "Notification", ["utc"] = DateTimeOffset.UtcNow.ToString("O"), ["caption"] = e.Caption, ["text"] = e.Text, ["detailText"] = e.DetailText });
            Console.Error.WriteLine("TIA notification: " + e.Caption + " - " + e.Text);
        }

        private void OnPortalDisposed(object? sender, EventArgs e)
        {
            portalLost = true;
            PortalDialogPolicy.Record(portalDialogs, new Dictionary<string, object?> { ["event"] = "Disposed", ["utc"] = DateTimeOffset.UtcNow.ToString("O") });
            Console.Error.WriteLine("TIA Portal raised Disposed; the session is lost.");
        }

        internal Dictionary<string, object?>[] PortalDialogRecords() => PortalDialogPolicy.Snapshot(portalDialogs);

        private void RequirePortalAlive() { if (portalLost && portal != null) throw new PortalDisposedNonRecoverableException(); }

        // F18: the documented targeted lookup with a timeout inside the host's attach budget. Nothing is attached yet, so a
        // failed lookup is a refusal before any native change.
        private static TiaPortalProcess FindProcess(int processId)
        {
            TiaPortalProcess? process;
            try { process = TiaPortal.GetProcess(processId, ProcessLookupTimeout); }
            catch (Exception error) when (!(error is AdapterPreconditionException))
            { throw new AdapterPreconditionException("Selected TIA process was not found by this release's API (TiaPortal.GetProcess): " + error.Message, "processId", true, error); }
            return process ?? throw new AdapterPreconditionException("Selected TIA process was not found by this release's API.", "processId");
        }

        // F5: TIA keeps one primary project per instance; a second open is refused by TIA only inside the native call.
        private void RequireNoOpenProject(TiaPortal connection, string operation)
        {
            if (connection.Projects.Any()
#if PROJECT_MULTIUSER
                || connection.LocalSessions.Any()
#endif
                )
                throw new AdapterPreconditionException(operation + " needs a TIA Portal instance without an open project or local session. "
                    + "Bind the open one explicitly (AttachOpenProject), or close it in TIA Portal first; open projects are never closed or replaced automatically.", isArgument: false);
        }

        // F4: the process names a project, but the composition is empty (TIA hides projects whose products are missing).
        private void RequireVisibleProject(string full)
        {
            string? reported = null;
            try { reported = portal!.GetCurrentProcess().ProjectPath?.FullName; }
            catch (Exception) /* swallow(probe-optional): the extra hint needs the process path; the refusal stays exact without it */ { }
            if (reported != null && string.Equals(reported, full, StringComparison.OrdinalIgnoreCase) && !portal!.Projects.Any()
#if PROJECT_MULTIUSER
                && !portal.LocalSessions.Any()
#endif
                )
                throw new AdapterPreconditionException(OpennessRefusals.Message(OpennessRefusals.ProjectNotVisible, "bind", false, null),
                    OpennessRefusals.ProjectNotVisible, null);
        }

        private bool OwnedPortalHasOpenProjects()
        {
            if (lifecycle.ProjectFile != null) return true;
            if (portal == null) return false;
            return portal.Projects.Any()
#if PROJECT_MULTIUSER
                || portal.LocalSessions.Any()
#endif
                ;
        }

        // F15: the exclusive-access text TIA shows to the interactive user names the running tool.
        internal static string ExclusiveAccessText()
        {
            string? tool = TiaMcp.Adapters.Diagnostics.AdapterJournal.CurrentTool;
            return "TIA MCP: " + (string.IsNullOrEmpty(tool) ? "an engineering change" : tool) + " is changing the project";
        }

        // Worker shutdown (F1) after Disconnect refused because a project is still open in a worker-started portal: dispose it
        // once anyway. With UI, Dispose disconnects; a portal started without UI is closed by its only client
        // (Terminating-the-connection topic).
        private void ShutdownPortal()
        {
            bool started = ownsPortal == true && workerStartedPortal && !sharedPortal;
            disconnect.Execute(lifecycle.ProcessId, ownsPortal, () => { ReleasePortalEvents(); portal!.Dispose(); }, sharedPortal, workerStartedPortal, false);
            _resetWorkerFamilyState?.Invoke();
            portal = null; project = null; lifecycle.Detached();
#if PROJECT_MULTIUSER
            localSession = null;
#endif
            if (started) workerStartedPortal = false;
        }
    }
}
