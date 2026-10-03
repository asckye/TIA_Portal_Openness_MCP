using System;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TiaOpenness.Shared
{
    // Read-only Windows facts. Each caller keeps its installation selection policy.
    internal static class OpennessEnvironment
    {
        public static RegistryKey OpenLocalMachineKey(RegistryView view, string path)
        {
            using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                return machine.OpenSubKey(path);
        }

        public static string InstalledPath(RegistryView view, int version, string section)
        {
            using (var key = OpenLocalMachineKey(view, @"SOFTWARE\Siemens\Automation\_InstalledSW\TIAP" + version + @"\" + section))
                return key == null ? null : Convert.ToString(key.GetValue("Path"));
        }

        public static int DotNetFrameworkRelease()
        {
            try
            {
                using (var key = OpenLocalMachineKey(RegistryView.Registry64, @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                    return key == null ? 0 : (int)(key.GetValue("Release") ?? 0);
            }
            catch { return 0; }
        }

        public static int? PathVersion(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var match = Regex.Match(path, @"[Vv](\d{2})(?!\d)", RegexOptions.RightToLeft);
            int version;
            return match.Success && int.TryParse(match.Groups[1].Value, out version) ? version : (int?)null;
        }

        public static bool PathMatchesVersion(string path, int version)
        {
            var named = PathVersion(path);
            return !named.HasValue || named.Value == version;
        }
    }
}
