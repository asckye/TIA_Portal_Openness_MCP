using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace TiaMcpServer.Siemens;

// PlcWorker only. The retiring engine keeps its original OpennessGroup wrapper.
internal static class WindowsOpennessGroup
{
    internal const string GroupName = "Siemens TIA Openness";

    internal enum Outcome { Ready, PendingLogon, AddedPendingLogon, ElevationDeclined, MissingGroup, AddFailed }

    internal sealed class Result
    {
        internal Outcome Status { get; }
        internal bool TokenMember { get; }
        internal bool? DirectLocalMember { get; }
        internal int ExitCode { get; }

        internal Result(Outcome status, bool tokenMember, bool? directLocalMember, int exitCode = 0)
        {
            Status = status; TokenMember = tokenMember; DirectLocalMember = directLocalMember; ExitCode = exitCode;
        }

        internal string Message
        {
            get
            {
                string message = Status switch
                {
                    Outcome.Ready => "Openness user group OK",
                    Outcome.PendingLogon => "OPENNESS_GROUP_RELOGON_REQUIRED: The current user is already in the local group. Sign out and back in.",
                    Outcome.AddedPendingLogon => "OPENNESS_GROUP_ADDED_RELOGON_REQUIRED: Added the current user. Membership becomes effective after sign out and logon.",
                    Outcome.ElevationDeclined => "OPENNESS_GROUP_ELEVATION_DECLINED: The user declined administrator elevation.",
                    Outcome.MissingGroup => "OPENNESS_GROUP_MISSING: Local group 'Siemens TIA Openness' does not exist. Install TIA Portal Openness.",
                    _ => "OPENNESS_GROUP_ADD_FAILED: Administrator group repair did not establish membership. Exit code " + ExitCode + "."
                };
                return message + " Token membership=" + TokenMember + "; direct local group membership="
                    + (DirectLocalMember.HasValue ? DirectLocalMember.Value.ToString() : "group missing") + ".";
            }
        }
    }

    internal sealed class CurrentUser
    {
        internal string Sid { get; }
        internal string AccountName { get; }
        internal CurrentUser(string sid, string accountName) { Sid = sid; AccountName = accountName; }
    }

    internal interface IPlatform
    {
        public CurrentUser ReadCurrentUser();
        public bool ReadTokenMembership();
        public bool? ReadDirectLocalMembership(string sid);
        public int AddCurrentUser(string accountName);
    }

    internal static bool ReadEffectiveMembership(IPlatform platform)
    {
        bool token = platform.ReadTokenMembership();
        try
        {
            var user = platform.ReadCurrentUser();
            bool? direct = platform.ReadDirectLocalMembership(user.Sid);
            if (direct != token)
                Console.Error.WriteLine("Openness group: token membership=" + token + "; direct local group membership="
                    + (direct.HasValue ? direct.Value.ToString() : "group missing") + ". Readiness uses the logon token.");
        }
        catch (Exception error)
        {
            // The extra directory diagnostic must not alter the existing token-based read result.
            Console.Error.WriteLine("Openness group: token membership=" + token + "; local group query unavailable: " + error.Message);
        }
        return token;
    }

    // Called only by the existing explicitly approved EnsureOpennessUserGroup / group.fix path.
    internal static Result EnsureMembership(IPlatform platform)
    {
        var user = platform.ReadCurrentUser();
        bool token = platform.ReadTokenMembership();
        bool? direct = platform.ReadDirectLocalMembership(user.Sid);
        if (token) return new Result(Outcome.Ready, token, direct);
        if (!direct.HasValue) return new Result(Outcome.MissingGroup, token, direct);
        if (direct.Value) return new Result(Outcome.PendingLogon, token, direct);

        int exitCode;
        try { exitCode = platform.AddCurrentUser(user.AccountName); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new Result(Outcome.ElevationDeclined, token, direct, ex.NativeErrorCode);
        }
        // Verify even after a nonzero exit: another administrator may have added the same user.
        direct = platform.ReadDirectLocalMembership(user.Sid);
        if (!direct.HasValue) return new Result(Outcome.MissingGroup, token, direct, exitCode);
        return new Result(direct.Value ? Outcome.AddedPendingLogon : Outcome.AddFailed, token, direct, exitCode);
    }

    internal static bool RequireEffectiveMembership(Result result)
    {
        Console.Error.WriteLine(result.Message);
        if (result.Status == Outcome.Ready) return true;
        throw new InvalidOperationException(result.Message);
    }

    internal static ProcessStartInfo AddStartInfo(string systemDirectory, string currentAccountName)
    {
        // No command shell or caller-supplied account/group. Reject quoting/control characters before UAC.
        if (string.IsNullOrWhiteSpace(currentAccountName) || currentAccountName.IndexOfAny(new[] { '"', '\r', '\n', '\0' }) >= 0)
            throw new ArgumentException("The current Windows account name cannot be passed to net.exe.", nameof(currentAccountName));
        return new ProcessStartInfo
        {
            FileName = Path.Combine(systemDirectory, "net.exe"),
            Arguments = "localgroup \"" + GroupName + "\" \"" + currentAccountName + "\" /add",
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = systemDirectory
        };
    }
}
