using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    internal static class EngineeringExport
    {
        internal static JsonObject Export(object target, string path, bool overwrite = true)
        {
            var result = new JsonObject { ["success"] = false, ["capability"] = "unsupported",
                ["targetReplaced"] = false, ["recoveryRequired"] = false, ["validation"] = "Not performed" };
            string? temporary = null;
            try
            {
                // Resolve support before touching the destination or creating a directory.
                var candidates = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name == "Export" && !m.ContainsGenericParameters)
                    .Where(m => { var p = m.GetParameters(); return p.Length >= 1 && p[0].ParameterType == typeof(FileInfo) &&
                        (p.Length == 1 || p.Length == 2 && p[1].ParameterType.IsEnum && Enum.GetNames(p[1].ParameterType).Contains("None")); }).ToList();
                var method = candidates.FirstOrDefault(m => m.GetParameters().Length == 1)
                    ?? candidates.FirstOrDefault(m => m.GetParameters()[1].ParameterType.FullName == "Siemens.Engineering.ExportOptions")
                    ?? (candidates.Count == 1 ? candidates[0] : null);
                if (method == null) { result["error"] = "No unambiguous supported native Export(FileInfo[, options=None]) API."; return result; }
                result["capability"] = "supported";
                var destination = Path.GetFullPath(path);
                result["path"] = destination;
                result["format"] = Path.GetExtension(destination).TrimStart('.');
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".tia-export-" + Guid.NewGuid().ToString("N") + Path.GetExtension(destination));
                result["temporaryPath"] = temporary;
                var args = method.GetParameters().Length == 1 ? new object[] { new FileInfo(temporary) }
                    : new object[] { new FileInfo(temporary), Enum.Parse(method.GetParameters()[1].ParameterType, "None") };
                method.Invoke(target, args);
                var file = new FileInfo(temporary);
                if (!file.Exists || file.Length == 0) throw new IOException("Native export produced no non-empty file.");
                result["length"] = file.Length;
                result["sha256"] = HashFile(temporary);
                result["validation"] = "Exists, non-empty, SHA-256 only; importability/restorability not tested";
                if (overwrite && File.Exists(destination))
                {
                    // Same-directory atomic replace; do not emulate it with Delete + Move.
                    File.Replace(temporary, destination, null);
                    result["targetReplaced"] = true;
                }
                else File.Move(temporary, destination);
                result["temporaryPath"] = null;
                result["success"] = true;
                result["commitMode"] = "Same-directory move or atomic replace";
            }
            catch (Exception ex)
            {
                result["error"] = (ex.InnerException ?? ex).Message;
                result["partialArtifactExists"] = temporary != null && File.Exists(temporary);
                // A failed artifact is retained at a unique path for inspection. Never
                // overwrite the previous export to make a failed operation look successful.
            }
            return result;
        }

        internal static string HashFile(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
    }
}
