using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
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
                string sid = identity.User.Value;
                return BooleanOutput(PowerShell("$g=Get-LocalGroup -Name 'Siemens TIA Openness' -ErrorAction SilentlyContinue; " +
                    "if(!$g){'false'}else{[bool](@(Get-LocalGroupMember -Group $g | Where-Object {$_.SID.Value -eq '" + sid + "'}).Count)}"));
            }
        }

        private static string[] PortOwners(int port)
        {
            var output = PowerShell("@(Get-NetTCPConnection -State Listen -ErrorAction Stop | Where-Object {$_.LocalPort -eq " + port + "}) | " +
                "ForEach-Object {$p=Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue; " +
                "'{0}:{1} PID={2} {3}' -f $_.LocalAddress,$_.LocalPort,$_.OwningProcess,$p.ProcessName}");
            return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
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
            string address = uri.Host, port = uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var output = PowerShell("$r=Get-NetFirewallRule -Name 'TIA-MCP-" + address + "-" + port + "' -ErrorAction SilentlyContinue; " +
                "if(!$r){'false'}else{$p=$r|Get-NetFirewallPortFilter; $a=$r|Get-NetFirewallAddressFilter; " +
                "[bool]($r.Enabled -eq 'True' -and $r.Direction -eq 'Inbound' -and $r.Action -eq 'Allow' " +
                "-and $p.Protocol -eq 'TCP' -and $p.LocalPort -eq '" + port + "' -and $a.LocalAddress -contains '" + address + "')}" );
            return BooleanOutput(output);
        }

        private static bool? BooleanOutput(string output)
        { return bool.TryParse(output.Trim(), out var value) ? value : (bool?)null; }

        private static string PowerShell(string command)
        {
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop'; " +
                "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); " + command));
            var result = LocalProcess.Run(Path.Combine(System.Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded }, System.Environment.SystemDirectory, null, 10).GetAwaiter().GetResult();
            if (!result.Success || !result.DataComplete) throw new InvalidOperationException("Local Windows inspection unavailable: " + result.Stderr);
            return result.Stdout;
        }
    }
}
