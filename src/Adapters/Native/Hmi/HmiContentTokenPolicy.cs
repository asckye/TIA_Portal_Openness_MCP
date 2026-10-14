using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace TiaMcp.Adapters.Native.Hmi
{
    public static class HmiContentTokenPolicy
    {
        public static string Compute(string generation, string scope, Dictionary<string, object?> content,
            Func<Dictionary<string, object?>, string> serialize)
        {
            var guarded = new Dictionary<string, object?>(content, content.Comparer);
            if (guarded.TryGetValue("kind", out var kind) && kind?.ToString() == "InspectionSnapshot")
                guarded.Remove("elapsedMs");
            return Hash(generation, scope, serialize(guarded));
        }

        public static string Hash(string generation, string scope, string content)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(generation + "\n" + scope + "\n" + content)))
                .Replace("-", "").ToLowerInvariant();
        }
    }
}
