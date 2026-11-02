using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace TiaOpenness.Shared
{
    // The same DPAPI CurrentUser format as the Workbench's existing http-vK.json/client.json.
    internal static class HttpAccessKeyStore
    {
        internal static void Validate(string key)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Any(char.IsControl))
                throw new ArgumentException("A nonempty HTTP access key without control characters is required.");
        }

        internal static string Protect(string key)
        {
            Validate(key);
            return Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(key), true));
        }

        internal static string Unprotect(string value)
        {
            byte[] bytes = Transform(Convert.FromBase64String(value), false);
            try { var key = Encoding.UTF8.GetString(bytes); Validate(key); return key; }
            finally { Array.Clear(bytes, 0, bytes.Length); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Blob { internal int Size; internal IntPtr Data; }
        [DllImport("crypt32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags,
                out Blob output);
        [DllImport("crypt32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags,
                out Blob output);
        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        // DPAPI without a WindowsDesktop dependency in the cross-platform host. No machine-wide scope or optional entropy.
        private static byte[] Transform(byte[] bytes, bool protect)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                throw new PlatformNotSupportedException("HTTP access keys require Windows DPAPI for the current user.");
            var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
            Blob output = default;
            try
            {
                Marshal.Copy(bytes, 0, input.Data, bytes.Length);
                bool ok = protect ? CryptProtectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
                if (!ok) throw new CryptographicException("The current Windows user could not access the protected HTTP key.");
                var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
            }
            finally
            {
                Array.Clear(bytes, 0, bytes.Length);
                Marshal.Copy(bytes, 0, input.Data, bytes.Length); Marshal.FreeHGlobal(input.Data);
                if (output.Data != IntPtr.Zero)
                {
                    Marshal.Copy(new byte[output.Size], 0, output.Data, output.Size);
                    LocalFree(output.Data);
                }
            }
        }

        internal static Uri Endpoint(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")
                || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new ArgumentException("An HTTP(S) endpoint without credentials, query or fragment is required.");
            return uri;
        }

        internal static string PathFor(string directory, Uri endpoint)
        {
            using (var sha = SHA256.Create())
                return Path
                    .Combine(directory, "remote-" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(endpoint.AbsoluteUri))).Replace("-", "")
                        .ToLowerInvariant() + ".json");
        }

        internal static void Save(string directory, Uri endpoint, string key)
        {
            string text = JsonSerializer.Serialize(new { Endpoint = endpoint.AbsoluteUri, ProtectedKey = Protect(key) });
            Directory.CreateDirectory(directory);
            string path = PathFor(directory, endpoint), temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, text, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        internal static string Read(string directory, Uri endpoint)
        {
            string path = PathFor(directory, endpoint);
            if (File.Exists(path))
            {
                using (var json = JsonDocument.Parse(File.ReadAllText(path)))
                {
                    if (json.RootElement.GetProperty("Endpoint").GetString() != endpoint.AbsoluteUri)
                        throw new InvalidDataException("HTTP endpoint does not match the protected key record.");
                    return Unprotect(json.RootElement.GetProperty("ProtectedKey").GetString() ?? throw new InvalidDataException("Missing protected HTTP key."));
                }
            }
            // Read existing Workbench files without changing or copying their protected values.
            var candidates = Directory.Exists(directory) ? Directory.GetFiles(directory, "http-v*.json")
                .Concat(new[] { Path.Combine(directory, "client.json") }) : Array.Empty<string>();
            foreach (string candidate in candidates.Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc))
            {
                using (var json = JsonDocument.Parse(File.ReadAllText(candidate)))
                {
                    var root = json.RootElement;
                    var saved = new UriBuilder("http", root.GetProperty("Address").GetString(), root.GetProperty("Port").GetInt32(), "mcp").Uri;
                    if (saved == endpoint)
                        return Unprotect(root.GetProperty("ProtectedKey").GetString() ?? throw new InvalidDataException("Missing protected HTTP key."));
                }
            }
            throw new InvalidOperationException("No protected HTTP access key is saved for this endpoint. Save the connection in the Workbench under the " +
                "same Windows user.");
        }

        internal static string ReadHost(string directory, string releaseKey, Uri endpoint)
        {
            string path = Path.Combine(directory, "http-v" + TiaMcp.Versioning.TiaVersionCatalog.RequireRunnable(releaseKey).Key + ".json");
            if (!File.Exists(path)) return Read(directory, endpoint);
            using (var json = JsonDocument.Parse(File.ReadAllText(path)))
            {
                var root = json.RootElement;
                var saved = new UriBuilder("http", root.GetProperty("Address").GetString(), root.GetProperty("Port").GetInt32(), "mcp").Uri;
                if (saved != endpoint) throw new InvalidDataException("HTTP listener does not match the protected Workbench server configuration.");
                return Unprotect(root.GetProperty("ProtectedKey").GetString() ?? throw new InvalidDataException("Missing protected HTTP key."));
            }
        }
    }
}
