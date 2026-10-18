using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
#if !NETFRAMEWORK
using System.Runtime.Versioning;
#endif

namespace TiaMcpServer.Siemens;

#if !NETFRAMEWORK
[SupportedOSPlatform("windows")]
#endif
internal sealed class WindowsOpennessGroupPlatform : WindowsOpennessGroup.IPlatform
{
    private readonly Func<bool> tokenMembership;
    internal WindowsOpennessGroupPlatform(Func<bool> tokenMembership) => this.tokenMembership = tokenMembership;

    public WindowsOpennessGroup.CurrentUser ReadCurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User ?? throw new InvalidOperationException("The current Windows user has no account SID.");
        return new WindowsOpennessGroup.CurrentUser(sid.Value, sid.Translate(typeof(NTAccount)).Value);
    }

    // Keep the existing readiness token check, including its unavailable => false policy.
    public bool ReadTokenMembership() => tokenMembership();

    public bool? ReadDirectLocalMembership(string sid)
    {
        UIntPtr resume = UIntPtr.Zero;
        uint status;
        do
        {
            IntPtr buffer = IntPtr.Zero;
            try
            {
                status = NetLocalGroupGetMembers(null, WindowsOpennessGroup.GroupName, 0, out buffer,
                    65536, out uint count, out _, ref resume);
                if (status == 2220) return null; // NERR_GroupNotFound
                if (status != 0 && status != 234) throw new Win32Exception(checked((int)status));
                for (uint index = 0; index < count; index++)
                {
                    // LOCALGROUP_MEMBERS_INFO_0 contains only a PSID; compare identities, not display names.
                    var member = Marshal.ReadIntPtr(buffer, checked((int)index * IntPtr.Size));
                    if (string.Equals(new SecurityIdentifier(member).Value, sid, StringComparison.Ordinal)) return true;
                }
                if (status == 234 && count == 0)
                    throw new InvalidOperationException("Local group enumeration returned an empty continuation page.");
            }
            finally { if (buffer != IntPtr.Zero) NetApiBufferFree(buffer); }
        } while (status == 234); // ERROR_MORE_DATA
        return false;
    }

    public int AddCurrentUser(string accountName)
    {
        using var process = Process.Start(WindowsOpennessGroup.AddStartInfo(Environment.SystemDirectory, accountName))
            ?? throw new InvalidOperationException("Administrator group repair did not start.");
        process.WaitForExit();
        return process.ExitCode;
    }

    // Public Windows NetAPI, always the local computer (null server name), read-only level 0.
    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint NetLocalGroupGetMembers(string? serverName, string localGroupName, uint level,
        out IntPtr buffer, uint preferredMaximumLength, out uint entriesRead, out uint totalEntries, ref UIntPtr resumeHandle);

    [DllImport("Netapi32.dll", ExactSpelling = true)]
    private static extern uint NetApiBufferFree(IntPtr buffer);
}
