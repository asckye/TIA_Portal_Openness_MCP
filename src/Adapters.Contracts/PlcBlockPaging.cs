using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using TiaMcp.Adapters;

namespace TiaMcp.Adapters.Contracts.Paging
{
    public sealed class PlcBlockPage
    {
        public PlcBlockDetails[] Items { get; set; } = Array.Empty<PlcBlockDetails>();
        public int Offset { get; set; }
        public int Total { get; set; }
        public string? NextCursor { get; set; }
    }

    public static class PlcBlockPaging
    {
        public static void Validate(int limit, string cursor)
        {
            if (limit < 1 || limit > 100) throw new ArgumentException("limit must be 1..100.", nameof(limit));
            if (cursor == null || cursor.Length > 100) throw new ArgumentException("Invalid block cursor.", nameof(cursor));
        }

        // The fingerprint binds the continuation to the worker, project, query and exact
        // inventory. Only paths are scanned before this point; details are read after slicing.
        public static PlcBlockPage Plan(string[] orderedPaths, string scope, int limit, string cursor)
        {
            Validate(limit, cursor);
            string fingerprint;
            using (var hash = SHA256.Create())
            {
                var values = new[] { scope }.Concat(orderedPaths);
                var text = string.Concat(values.Select(s => s.Length.ToString(CultureInfo.InvariantCulture) + ":" + s));
                fingerprint = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
            }
            int offset = 0;
            if (cursor.Length != 0)
            {
                var parts = cursor.Split(':');
                if (parts.Length != 2 || parts[0] != fingerprint
                    || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out offset)
                    || offset < 1 || offset >= orderedPaths.Length)
                    throw new ArgumentException("Invalid or stale block cursor. Restart with an empty cursor.", nameof(cursor));
            }
            int next = offset + Math.Min(limit, orderedPaths.Length - offset);
            return new PlcBlockPage { Offset = offset, Total = orderedPaths.Length,
                NextCursor = next < orderedPaths.Length ? fingerprint + ":" + next.ToString(CultureInfo.InvariantCulture) : null };
        }
    }
}
