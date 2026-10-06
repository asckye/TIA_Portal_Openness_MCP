using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using TiaOpenness.Shared;

namespace TiaOpenness.Core.Environment
{
    public static class WindowsEnvironmentSources
    {
        public static EnvironmentProbeSources Create() => new EnvironmentProbeSources
        {
            Doctor = OpennessDoctor.Run,
            ListedGroupMember = ListedGroupMember,
            FileExists = File.Exists,
            Directories = directory => Directory.Exists(directory) ? Directory.GetDirectories(directory) : Array.Empty<string>(),
            Writable = Writable,
            PortOwners = PortOwners,
            UrlReserved = UrlReserved,
            FirewallAllowed = FirewallAllowed,
        };

        public static bool Writable(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                using (var stream = new FileStream(Path.Combine(directory, ".doctor-" + Guid.NewGuid().ToString("N")),
                    FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) stream.WriteByte(0);
                return true;
            }
            catch (UnauthorizedAccessException) /* swallow(env-probe): access denial is the failed writable-directory check */ { return false; }
            catch (IOException) /* swallow(env-probe): a failed write probe is reported as an unavailable data directory */ { return false; }
        }

        private static bool? ListedGroupMember()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                IntPtr resume = IntPtr.Zero;
                while (true)
                {
                    IntPtr buffer = IntPtr.Zero;
                    int read, total;
                    int status = NetLocalGroupGetMembers(null, "Siemens TIA Openness", 0, out buffer, -1,
                        out read, out total, ref resume);
                    try
                    {
                        if (status == 1376 || status == 2220) return false;
                        if (status != 0 && status != 234) throw new Win32Exception(status);
                        for (int index = 0; index < read; index++)
                        {
                            var row = (LocalGroupMember0)Marshal.PtrToStructure(IntPtr.Add(buffer, index * IntPtr.Size), typeof(LocalGroupMember0));
                            if (row.Sid != IntPtr.Zero && new SecurityIdentifier(row.Sid).Equals(identity.User)) return true;
                        }
                        if (status == 0) return false;
                    }
                    finally { if (buffer != IntPtr.Zero) NetApiBufferFree(buffer); }
                }
            }
        }

        private static string[] PortOwners(int port)
        {
            var owners = new System.Collections.Generic.List<string>();
            ReadTcpTable(2, port, owners);
            ReadTcpTable(23, port, owners);
            return owners.Distinct().ToArray();
        }

        private static void ReadTcpTable(int addressFamily, int requestedPort, System.Collections.Generic.List<string> owners)
        {
            int size = 0;
            int status = GetExtendedTcpTable(IntPtr.Zero, ref size, true, addressFamily, 3, 0);
            if (status != 122 && status != 0) throw new Win32Exception(status);
            if (size <= sizeof(uint)) return;
            IntPtr table = Marshal.AllocHGlobal(size);
            try
            {
                status = GetExtendedTcpTable(table, ref size, true, addressFamily, 3, 0);
                if (status != 0) throw new Win32Exception(status);
                int rows = Marshal.ReadInt32(table);
                int rowSize = addressFamily == 2 ? 24 : 56;
                for (int index = 0; index < rows; index++)
                {
                    IntPtr row = IntPtr.Add(table, sizeof(uint) + checked(index * rowSize));
                    uint rawPort = unchecked((uint)Marshal.ReadInt32(row, addressFamily == 2 ? 8 : 20));
                    int localPort = (int)(((rawPort & 0xff) << 8) | ((rawPort >> 8) & 0xff));
                    if (localPort != requestedPort) continue;
                    int pid = Marshal.ReadInt32(row, addressFamily == 2 ? 20 : 52);
                    IPAddress address;
                    if (addressFamily == 2) address = new IPAddress(BitConverter.GetBytes(Marshal.ReadInt32(row, 4)));
                    else
                    {
                        byte[] bytes = new byte[16];
                        Marshal.Copy(row, bytes, 0, bytes.Length);
                        long scope = unchecked((uint)Marshal.ReadInt32(row, 16));
                        address = new IPAddress(bytes, scope);
                    }
                    string processName = "";
                    try { using (var process = Process.GetProcessById(pid)) processName = process.ProcessName; }
                    catch (ArgumentException) /* swallow(probe-optional): process exit or PID reuse leaves this listener name unavailable */ { }
                    catch (InvalidOperationException) /* swallow(probe-optional): a changing process has no stable listener name to report */ { }
                    owners.Add(address + ":" + localPort.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                        " PID=" + pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + processName);
                }
            }
            finally { Marshal.FreeHGlobal(table); }
        }

        private static bool? UrlReserved(string prefix)
        {
            // Exact URL, no listener is opened and no reservation is modified.
            var result = LocalProcess.Run(Path.Combine(System.Environment.SystemDirectory, "netsh.exe"),
                new[] { "http", "show", "urlacl", "url=" + prefix }, System.Environment.SystemDirectory, null, 10).GetAwaiter().GetResult();
            if (!result.Success || !result.DataComplete) return null;
            var match = Regex.Match(result.Stdout, @"D:(?:\([^\r\n]*?\))+", RegexOptions.CultureInvariant);
            if (!match.Success) return false;
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var principal = new WindowsPrincipal(identity);
                return UrlAclAllows(match.Value, sid => sid.Equals(identity.User) || principal.IsInRole(sid));
            }
        }

        internal static bool UrlAclAllows(string sddl, Func<SecurityIdentifier, bool> applies)
        {
            var descriptor = new RawSecurityDescriptor(sddl);
            bool allowed = false;
            if (descriptor.DiscretionaryAcl == null) return false;
            foreach (GenericAce ace in descriptor.DiscretionaryAcl)
            {
                // HTTP URL reservations require generic execute; generic all includes that right.
                if (!(ace is CommonAce common) || (common.AccessMask & 0x30000000) == 0 || !applies(common.SecurityIdentifier)) continue;
                if (common.AceQualifier == AceQualifier.AccessDenied) return false;
                if (common.AceQualifier == AceQualifier.AccessAllowed) allowed = true;
            }
            return allowed;
        }

        private static bool? FirewallAllowed(string prefix)
        {
            var uri = new Uri(prefix);
            // Canonical IPv4 is validated by the configuration service before this method.
            string address = uri.Host;
            return WindowsFirewallRule.IsAllowed("TIA-MCP-" + address + "-" + uri.Port, address, uri.Port);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LocalGroupMember0 { internal IntPtr Sid; }

        [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int NetLocalGroupGetMembers(string serverName, string groupName, int level, out IntPtr buffer,
            int preferredMaximumLength, out int entriesRead, out int totalEntries, ref IntPtr resumeHandle);

        [DllImport("Netapi32.dll")]
        private static extern int NetApiBufferFree(IntPtr buffer);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern int GetExtendedTcpTable(IntPtr table, ref int size, bool order, int addressFamily, int tableClass, int reserved);
    }
}
