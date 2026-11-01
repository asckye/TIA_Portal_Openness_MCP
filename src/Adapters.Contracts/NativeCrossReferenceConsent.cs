using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace TiaMcp.Adapters.Contracts
{
    // Query processes only read host-signed records. The authenticated Workbench operator channel owns changes.
    public static class NativeCrossReferenceConsent
    {
        public const string SessionFileVariable = "TIA_MCP_NATIVE_CROSS_REFERENCE_SESSION_FILE";
        public const string SessionSecretVariable = "TIA_MCP_NATIVE_CROSS_REFERENCE_SESSION_SECRET";
        private static readonly AsyncLocal<string[]?> Session = new AsyncLocal<string[]?>();
        public static IDisposable EnterSession(string path, string secret)
        {
            var previous = Session.Value; Session.Value = new[] { path, secret };
            return new Scope(() => Session.Value = previous);
        }
        private sealed class Scope : IDisposable
        {
            private readonly Action restore;
            internal Scope(Action restore) => this.restore = restore;
            public void Dispose() => restore();
        }
        public static string CreateSecret()
        {
            var bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return Convert.ToBase64String(bytes);
        }
        public static string Record(string secret, bool enabled, int pid, long startTicks)
        {
            string payload = "enabled=" + (enabled ? "true" : "false") + "\n" + pid.ToString(CultureInfo.InvariantCulture)
                + "\n" + startTicks.ToString(CultureInfo.InvariantCulture) + "\n";
            using (var hmac = new HMACSHA256(Convert.FromBase64String(secret)))
                return payload + Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))) + "\n";
        }
        public static bool Enabled => Read(Session.Value?[0] ?? Environment.GetEnvironmentVariable(SessionFileVariable),
            Session.Value?[1] ?? Environment.GetEnvironmentVariable(SessionSecretVariable));
        public static bool Read(string? path, string? secret)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(secret) || !Path.IsPathRooted(path)) return false;
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > 256) return false;
                string[] lines;
                // The host keeps its write handle open without write/delete sharing for this session.
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    var chars = new char[257]; int count = reader.ReadBlock(chars, 0, chars.Length);
                    if (count > 256) return false;
                    lines = new string(chars, 0, count).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                }
                if (lines.Length != 4 || lines[0] != "enabled=true" || !int.TryParse(lines[1], NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
                    || !long.TryParse(lines[2], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks)) return false;
                string expected = Record(secret, true, pid, ticks).Split('\n')[3];
                byte[] actual = Convert.FromBase64String(lines[3]), wanted = Convert.FromBase64String(expected);
                if (actual.Length != wanted.Length) return false;
                int difference = 0; for (int i = 0; i < actual.Length; i++) difference |= actual[i] ^ wanted[i];
                if (difference != 0) return false;
                using (var owner = Process.GetProcessById(pid))
                    return !owner.HasExited && owner.StartTime.ToUniversalTime().Ticks == ticks;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is FormatException
                || error is ArgumentException || error is InvalidOperationException || error is System.ComponentModel.Win32Exception)
            { return false; }
        }
        public static bool DefaultAllowed(string? release)
            => release == "14sp1" || release == "15.1" || release == "16" || release == "17"
                || release == "18" || release == "19" || release == "20";
    }
}
