using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TiaOpenness.Shared
{
    internal sealed class ApprovalSettings
    {
        internal bool Enabled { get; }
        internal int TimeoutSeconds { get; }
        internal int OnlinePlanLifetimeSeconds { get; }
        internal ApprovalSettings WithRequiredApproval(bool required)
            => new ApprovalSettings(required, TimeoutSeconds, OnlinePlanLifetimeSeconds);
        internal static string SettingsPath => Path.Combine(DataLocations.Current.ConfigDirectory, "approval.settings");
        internal static bool IsNativeCrossReferenceTarget(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            path = Path.GetFullPath(path.Trim());
            string normalized = path.Replace('\\', '/');
            if (IsBareDevicePath(normalized)) return false;
            if (normalized.IndexOf("approval.settings.sessions", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            // A directory cannot be a settings file; the human-only session directory is protected above.
            if (Directory.Exists(path)) return false;
            string name = TargetName(normalized.Substring(normalized.LastIndexOf('/') + 1));
            if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^native-cross-references-[a-f0-9]{32}\.settings$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)
                || System.Text.RegularExpressions.Regex.IsMatch(name, @"^NATIVE~[0-9]+\.SET$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)) return true;
            if (Environment.OSVersion.Platform == PlatformID.Win32NT && Directory.Exists(SettingsPath + ".sessions"))
            {
                foreach (var consent in Directory.EnumerateFiles(SettingsPath + ".sessions", "native-cross-references-*.settings"))
                {
                    using (var expectedConsent = OpenIdentity(consent, 0, 7, IntPtr.Zero, 3, 0, IntPtr.Zero))
                    using (var targetConsent = OpenIdentity(path, 0, 7, IntPtr.Zero, 3, 0, IntPtr.Zero))
                    {
                        if (expectedConsent.IsInvalid)
                        { if (!MissingIdentity(Marshal.GetLastWin32Error())) return true; continue; }
                        if (targetConsent.IsInvalid)
                        { if (!MissingIdentity(Marshal.GetLastWin32Error())) return true; continue; }
                        if (!ReadIdentity(expectedConsent, out var a) || !ReadIdentity(targetConsent, out var b)) return true;
                        if (a.Volume == b.Volume && a.IndexHigh == b.IndexHigh && a.IndexLow == b.IndexLow) return true;
                    }
                }
            }
            return false;
        }
        internal static bool IsAdministrativeTarget(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            path = Path.GetFullPath(path.Trim());
            string normalized = path.Replace('\\', '/');
            if (IsBareDevicePath(normalized)) return false;
            if (IsNativeCrossReferenceTarget(path)) return true;
            foreach (string component in normalized.Split('/'))
            {
                string name = TargetName(component);
                if (name.Equals("approval.settings", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("online-targets.settings", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("secrets.vault", StringComparison.OrdinalIgnoreCase)
                    || System.Text.RegularExpressions.Regex.IsMatch(name, @"^APPROV~[0-9]+\.SET$",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)
                    || System.Text.RegularExpressions.Regex.IsMatch(name, @"^ONLINE~[0-9]+\.SET$|^SECRET~[0-9]+\.VAU$",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)) return true;
            }
            if (Directory.Exists(path)) return false;
            if (Environment.OSVersion.Platform != PlatformID.Win32NT || string.IsNullOrWhiteSpace(path)) return false;
            // File IDs also cover short names, junctions and existing hard links. An
            // unreadable identity is refused; there is no write probe or native TIA call.
            foreach (string administrativePath in new[] { SettingsPath, OnlineTargetSettings.SettingsPath,
                Path.Combine(DataLocations.Current.ConfigDirectory, "secrets.vault") })
            {
                using var expected = OpenIdentity(administrativePath, 0, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (expected.IsInvalid)
                { if (!MissingIdentity(Marshal.GetLastWin32Error())) return true; continue; }
                using (var target = OpenIdentity(path, 0, 7, IntPtr.Zero, 3, 0, IntPtr.Zero))
                {
                    if (target.IsInvalid) return !MissingIdentity(Marshal.GetLastWin32Error());
                    if (!ReadIdentity(expected, out var a) || !ReadIdentity(target, out var b)) return true;
                    if (a.Volume == b.Volume && a.IndexHigh == b.IndexHigh && a.IndexLow == b.IndexLow) return true;
                }
            }
            return false;
        }
        private static string TargetName(string name)
        {
            if (name.Length >= 2 && name[1] == ':') name = name.Substring(2);
            int stream = name.IndexOf(':'); if (stream >= 0) name = name.Substring(0, stream);
            return name.TrimEnd(' ', '.');
        }
        private static bool IsBareDevicePath(string normalized) => normalized.StartsWith("//./", StringComparison.Ordinal)
            && normalized.IndexOf('/', 4) < 0;
        private static bool MissingIdentity(int error) => error == 2 || error == 3 || error == 53 || error == 67 || error == 123 || error == 161;
        [StructLayout(LayoutKind.Sequential)] private struct FileIdentity
        {
            internal uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh,
                IndexLow;
        }
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle OpenIdentity(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReadIdentity(SafeFileHandle handle, out FileIdentity identity);
        internal ApprovalSettings(bool enabled = true, int timeoutSeconds = 120, int onlinePlanLifetimeSeconds = 900)
        {
            if (timeoutSeconds < 1 || timeoutSeconds > 3600) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
            if (onlinePlanLifetimeSeconds < 1 || onlinePlanLifetimeSeconds > 86400)
                throw new ArgumentOutOfRangeException(nameof(onlinePlanLifetimeSeconds));
            Enabled = enabled; TimeoutSeconds = timeoutSeconds; OnlinePlanLifetimeSeconds = onlinePlanLifetimeSeconds;
        }
        internal static ApprovalSettings Load(string path)
        {
            try
            {
                using (JournalFileLock.Acquire(path + ".lock"))
                {
                    if (!File.Exists(path)) return new ApprovalSettings();
                    return Parse(File.ReadAllLines(path));
                }
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is ArgumentException)
            { Trace.TraceWarning("Approval settings unavailable: " + ex.GetType().Name); return new ApprovalSettings(); }
        }
        private static ApprovalSettings Parse(string[] lines)
        {
            if (lines.Length is not (2 or 3) || (lines[0] != "enabled=true" && lines[0] != "enabled=false")
                || !lines[1].StartsWith("timeoutSeconds=", StringComparison.Ordinal)
                || !int.TryParse(lines[1].Substring(15), NumberStyles.None, CultureInfo.InvariantCulture, out int seconds))
                throw new InvalidDataException("Invalid approval settings.");
            int lifetime = 900;
            if (lines.Length == 3 && (!lines[2].StartsWith("onlinePlanLifetimeSeconds=", StringComparison.Ordinal)
                || !int.TryParse(lines[2].Substring(26), NumberStyles.None, CultureInfo.InvariantCulture, out lifetime)))
                throw new InvalidDataException("Invalid online plan lifetime.");
            return new ApprovalSettings(lines[0] == "enabled=true", seconds, lifetime);
        }

        internal void Save(string path, AuditLog? audit = null)
        {
            using (JournalFileLock.Acquire(path + ".lock"))
            {
                bool enabledBefore = true;
                if (File.Exists(path))
                {
                    var previous = File.ReadAllLines(path);
                    try { enabledBefore = Parse(previous).Enabled; }
                    catch (Exception ex) when (ex is InvalidDataException || ex is ArgumentOutOfRangeException)
                    { Trace.TraceWarning("Invalid approval settings default to enabled: " + ex.GetType().Name); }
                }
                if (enabledBefore != Enabled)
                {
                    try { (audit ?? AuditLog.Current).ApprovalSwitch("workbench", Enabled); }
                    catch (Exception error)
                    {
                        if (!Enabled) throw new IOException("Approval switch change refused because its audit record could not be written.", error);
                        Trace.TraceWarning("Enabling approval without its audit record: " + error.GetType().Name);
                    }
                }
                using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write("enabled=" + (Enabled ? "true" : "false") + "\ntimeoutSeconds="
                        + TimeoutSeconds.ToString(CultureInfo.InvariantCulture) + "\n");
                    if (OnlinePlanLifetimeSeconds != 900)
                        writer.Write("onlinePlanLifetimeSeconds=" + OnlinePlanLifetimeSeconds.ToString(CultureInfo.InvariantCulture) + "\n");
                    writer.Flush(); stream.Flush(true);
                }
            }
        }
    }
}
