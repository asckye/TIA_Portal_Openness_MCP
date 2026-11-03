using System;

namespace TiaMcp.Adapters.Contracts
{
    /// <summary>Ordered release availability for reviewed families and tools.</summary>
    public static class ReleaseRange
    {
        private static readonly string[] Keys = { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" };

        public static string[] All => (string[])Keys.Clone();

        public static string[] Since(string first) => Between(first, Keys[Keys.Length - 1]);

        public static string[] Between(string first, string last)
        {
            var start = Index(first);
            var end = Index(last);
            if (start > end) throw new ArgumentException("Release key " + first + " is after " + last + ".", nameof(first));
            var result = new string[end - start + 1];
            Array.Copy(Keys, start, result, 0, result.Length);
            return result;
        }

        private static int Index(string key)
        {
            var index = Array.IndexOf(Keys, key);
            if (index < 0) throw new ArgumentException("Unknown release key: " + key, nameof(key));
            return index;
        }
    }
}
