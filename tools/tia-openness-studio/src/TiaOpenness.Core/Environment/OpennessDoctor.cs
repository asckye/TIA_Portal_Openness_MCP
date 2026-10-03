using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using Microsoft.Win32;
using TiaOpenness.Contracts.Models;

namespace TiaOpenness.Core.Environment
{
    /// <summary>
    /// Checks every precondition Openness needs, and says how to fix each one.
    /// Openness fails with unhelpful COM errors when any of these is missing, so this
    /// runs first and turns "0x80040154" into "add yourself to the Siemens TIA Openness group".
    /// </summary>
    public static class OpennessDoctor
    {
        /// <summary>The Windows local group whose members are allowed to use Openness.</summary>
        public const string OpennessGroupName = "Siemens TIA Openness";

        public static DoctorReport Run()
        {
            var report = new DoctorReport
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                MachineName = System.Environment.MachineName,
                UserName = System.Environment.UserDomainName + "\\" + System.Environment.UserName,
                Is64BitProcess = System.Environment.Is64BitProcess,
                ClrVersion = System.Environment.Version.ToString(),
            };

            report.Installations = OpennessLocator.FindAll().ToList();

            report.Checks.Add(CheckProcessBitness());
            report.Checks.Add(CheckDotNetFramework());
            report.Checks.Add(CheckInstallation(report.Installations));
            report.Checks.Add(CheckAssemblyLayout(report.Installations));
            report.Checks.Add(CheckGroupMembership());
            report.Checks.Add(CheckTiaRunning());
            report.Checks.Add(CheckFirewallProfileHint());

            return report;
        }

        private static DoctorCheck CheckProcessBitness()
        {
            var ok = System.Environment.Is64BitProcess;
            return new DoctorCheck
            {
                Id = "ENV-BITNESS",
                Title = "Process is 64-bit",
                Status = ok ? CheckStatus.Pass : CheckStatus.Fail,
                Detail = ok ? "x64" : "x86",
                Remedy = ok ? null
                    : "Siemens.Engineering.dll is x64-only. Build the bridge with <PlatformTarget>x64</PlatformTarget>.",
            };
        }

        private static DoctorCheck CheckDotNetFramework()
        {
            var release = TiaOpenness.Shared.OpennessEnvironment.DotNetFrameworkRelease();

            // 528040 is .NET Framework 4.8; 533320 is 4.8.1.
            var ok = release >= 528040;
            return new DoctorCheck
            {
                Id = "ENV-NETFX",
                Title = ".NET Framework 4.8 or later",
                Status = ok ? CheckStatus.Pass : CheckStatus.Fail,
                Detail = release == 0 ? "not detected" : "Release " + release,
                Remedy = ok ? null : "Install the .NET Framework 4.8 runtime, then restart the bridge.",
            };
        }

        private static DoctorCheck CheckInstallation(IReadOnlyList<OpennessInstallation> installations)
        {
            if (installations.Count == 0)
            {
                return new DoctorCheck
                {
                    Id = "TIA-INSTALL",
                    Title = "TIA Portal Openness installed",
                    Status = CheckStatus.Fail,
                    Detail = "no installation found",
                    Remedy = "Install TIA Portal V14 SP1, V15.1 or V16-V21 with the Openness option enabled. " +
                             "The bridge looks in HKLM\\SOFTWARE\\Siemens\\Automation\\Openness and in " +
                             "%ProgramFiles%\\Siemens\\Automation\\Portal V**\\PublicAPI.",
                };
            }

            var summary = string.Join(", ", installations.Select(i =>
                "V" + i.Version + " " + (i.IsModular ? "modular" : "monolithic") + " (" + i.DiscoveredBy + ")"));

            return new DoctorCheck
            {
                Id = "TIA-INSTALL",
                Title = "TIA Portal Openness installed",
                Status = CheckStatus.Pass,
                Detail = summary,
            };
        }

        /// <summary>
        /// V21 split the API into several assemblies under a <c>net48</c> folder and re-signed
        /// them, so a build made against V20 does not load on V21 and vice versa. The mismatch
        /// otherwise surfaces as a bare "Could not load file or assembly".
        /// </summary>
        private static DoctorCheck CheckAssemblyLayout(IReadOnlyList<OpennessInstallation> installations)
        {
            var check = new DoctorCheck { Id = "TIA-LAYOUT", Title = "Openness assembly layout" };

            if (installations.Count == 0)
            {
                check.Status = CheckStatus.Warn;
                check.Detail = "nothing installed to inspect";
                return check;
            }

            var newest = installations[0];
            check.Status = CheckStatus.Pass;
            check.Detail = newest.IsModular
                ? "V" + newest.Version + " is modular: " + newest.Assemblies.Count +
                  " assemblies in " + newest.PublicApiDirectory
                : "V" + newest.Version + " is monolithic: " + newest.PrimaryAssembly +
                  " in " + newest.PublicApiDirectory;

            if (installations.Count > 1 && installations.Any(i => i.IsModular) && installations.Any(i => !i.IsModular))
            {
                check.Status = CheckStatus.Warn;
                check.Remedy = "Both layouts are installed. One bridge process binds one version; " +
                               "pass --openness-version to choose the matching prebuilt adapter.";
            }
            return check;
        }

        /// <summary>
        /// Checks the *current access token*, not the group's member list. That is the
        /// distinction that matters: adding a user to the group has no effect until they
        /// log off and back on, and only the token reflects that.
        /// </summary>
        private static DoctorCheck CheckGroupMembership()
        {
            var check = new DoctorCheck { Id = "TIA-GROUP", Title = "User is in the '" + OpennessGroupName + "' group" };

            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    if (identity.Groups == null)
                    {
                        check.Status = CheckStatus.Warn;
                        check.Detail = "token carries no group list";
                        return check;
                    }

                    foreach (var sid in identity.Groups)
                    {
                        string name;
                        try { name = sid.Translate(typeof(NTAccount)).Value; }
                        catch (Exception) /* swallow(env-probe): an unresolvable token SID is skipped while the remaining group names are checked */ { continue; }

                        if (name != null && name.EndsWith(OpennessGroupName, StringComparison.OrdinalIgnoreCase))
                        {
                            check.Status = CheckStatus.Pass;
                            check.Detail = name;
                            return check;
                        }
                    }
                }

                check.Status = CheckStatus.Fail;
                check.Detail = "not present in the current access token";
                check.Remedy = "Run as Administrator:  net localgroup \"" + OpennessGroupName + "\" \"" +
                               System.Environment.UserDomainName + "\\" + System.Environment.UserName + "\" /add" +
                               "  -- then LOG OFF AND BACK ON. The group only takes effect in a new logon token.";
                return check;
            }
            catch (Exception ex)
            {
                check.Status = CheckStatus.Warn;
                check.Detail = "could not be determined: " + ex.Message;
                return check;
            }
        }

        private static DoctorCheck CheckTiaRunning()
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName("Siemens.Automation.Portal"); }
            catch (Exception) /* swallow(env-probe): the advisory running-process check uses an empty list when local process enumeration is unavailable */ { processes = new Process[0]; }

            try
            {
                var running = processes.Length > 0;
                return new DoctorCheck
                {
                    Id = "TIA-RUNNING",
                    Title = "TIA Portal process",
                    Status = CheckStatus.Pass,
                    Detail = running
                        ? "running, " + processes.Length + " instance(s) - the bridge can attach"
                        : "not running - the bridge will start one on connect",
                };
            }
            finally
            {
                foreach (var p in processes) p.Dispose();
            }
        }

        /// <summary>
        /// Openness talks to TIA over local RPC. Some hardened images block that; there is
        /// no reliable way to detect it, so this is informational rather than a gate.
        /// </summary>
        private static DoctorCheck CheckFirewallProfileHint()
        {
            return new DoctorCheck
            {
                Id = "ENV-SECURITY",
                Title = "First-connect confirmation dialog",
                Status = CheckStatus.Warn,
                Detail = "TIA Portal asks for confirmation the first time an unknown application connects.",
                Remedy = "On the target machine, accept the dialog once with 'Yes to all' so unattended runs do not block. " +
                         "Headless (WithUserInterface=false) sessions cannot show it, so connect once with the UI visible first.",
            };
        }

        /// <summary>Human-readable console rendering used by the CLI and the bridge banner.</summary>
        public static string Format(DoctorReport report)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("TIA Openness environment report");
            sb.AppendLine("  machine : " + report.MachineName);
            sb.AppendLine("  user    : " + report.UserName);
            sb.AppendLine("  process : " + (report.Is64BitProcess ? "x64" : "x86") + ", CLR " + report.ClrVersion);
            sb.AppendLine();

            foreach (var check in report.Checks)
            {
                var mark = check.Status == CheckStatus.Pass ? "[ OK ]"
                         : check.Status == CheckStatus.Warn ? "[WARN]"
                         : "[FAIL]";
                sb.AppendLine(mark + " " + check.Title);
                if (!string.IsNullOrWhiteSpace(check.Detail)) sb.AppendLine("       " + check.Detail);
                if (!string.IsNullOrWhiteSpace(check.Remedy)) sb.AppendLine("       -> " + check.Remedy);
            }

            sb.AppendLine();
            if (report.Installations.Count > 0)
            {
                sb.AppendLine("Installations:");
                foreach (var i in report.Installations)
                {
                    sb.AppendLine("  V" + i.Version + "  " + i.EngineeringDllPath);
                }
            }

            sb.AppendLine();
            sb.AppendLine(report.CanRunOpenness
                ? "Verdict: this machine can run Openness."
                : "Verdict: Openness cannot run here yet - fix the [FAIL] items above.");
            return sb.ToString();
        }

    }
}
