using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    internal static class ArgumentRules
    {
        internal static string RequireOneOf(string value, string[] allowed, string parameter)
        {
            if (string.IsNullOrWhiteSpace(value) || !allowed.Contains(value, StringComparer.Ordinal))
                throw new ArgumentException(parameter + " must be one of: " + string.Join("/", allowed) + " (case-sensitive).");
            return value;
        }

        internal static void RequireText(string value, string parameter, int max)
            => RequireExactText(value, max, "Exact nonempty " + parameter + " required (max " + max + " chars, no surrounding whitespace).");

        internal static string RequireExactName(string value, string parameter)
        {
            RequireExactText(value, 256, parameter + " must be an exact nonempty name without surrounding whitespace.");
            return value;
        }

        private static void RequireExactText(string value, int max, string message)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Trim() != value) throw new ArgumentException(message);
        }

        internal static void Refuse(string value, string parameter, string reason)
        {
            if (!string.IsNullOrEmpty(value)) throw new ArgumentException(parameter + " " + reason);
        }

        internal static void RequireKeys(JsonObject properties, string[] allowed, string parameter)
        {
            var unknown = properties.Select(p => p.Key).Where(k => !allowed.Contains(k, StringComparer.Ordinal)).ToArray();
            if (unknown.Length > 0) throw new ArgumentException(parameter + " accepts only " + string.Join(" / ", allowed) + "; unknown: " + string.Join(", ", unknown));
        }

        internal static void RequireAbsolutePath(string path, string message)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) throw new ArgumentException(message);
        }

        // These policies preserve the original callers' distinct empty-input, size and error rules.
        internal enum ObjectRule { Optional, SoftwareUnit, HardwareNetwork, Safety, Required }

        internal static JsonObject ParseObject(string json, string parameter, ObjectRule rule = ObjectRule.Optional)
        {
            bool empty = string.IsNullOrWhiteSpace(json);
            if (rule == ObjectRule.Safety)
            {
                if (empty || json.Length > 16384) throw new ArgumentException("propertiesJson must be a JSON object (<= 16 KB).");
            }
            else if (rule == ObjectRule.Required)
            {
                if (empty) throw new ArgumentException("Missing required JSON object: " + parameter);
            }
            else
            {
                if (empty) return new JsonObject();
                if (rule == ObjectRule.HardwareNetwork && json.Length > 32768) throw new ArgumentException(parameter + " exceeds 32 KiB.");
            }

            JsonNode? node;
            try { node = JsonNode.Parse(json); }
            catch (Exception ex) when (rule == ObjectRule.SoftwareUnit)
            {
                throw new ArgumentException(parameter + " must be a JSON object: " + ex.Message);
            }
            var obj = node as JsonObject ?? throw new ArgumentException(rule == ObjectRule.Required
                ? "Expected JSON object at " + parameter : parameter + " must be a JSON object.");
            if (rule == ObjectRule.Safety && obj.Count > 50) throw new ArgumentException("At most 50 properties per request.");
            if (rule == ObjectRule.HardwareNetwork)
            {
                if (obj.Count > 50) throw new ArgumentException(parameter + ": at most 50 entries.");
                foreach (var pair in obj)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key) || !pair.Key.All(c => char.IsLetterOrDigit(c) || c == '_')) throw new ArgumentException(parameter + ": invalid key '" + pair.Key + "'.");
                    if (pair.Value is JsonObject || pair.Value is JsonArray) throw new ArgumentException(parameter + ": only scalar values are supported (" + pair.Key + ").");
                }
            }
            return obj;
        }

        internal static string Hash(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return Hex(sha.ComputeHash(bytes));
        }

        internal static string Hash(Stream stream)
        {
            using var sha = SHA256.Create();
            return Hex(sha.ComputeHash(stream));
        }

        internal static string HashFile(string path, FileShare share)
        {
            using var sha = SHA256.Create();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, share);
            return Hex(sha.ComputeHash(stream));
        }

        private static string Hex(byte[] hash) => BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }
}
