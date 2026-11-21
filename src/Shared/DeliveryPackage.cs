using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace TiaMcp.Versioning
{
    /// <summary>Names used by the release packager and by installed clients looking for delivery assets.</summary>
    public static class DeliveryPackage
    {
        private const string LegacyPrefix = "TIA_MCP_Delivery_v";
        private const string FinalPrefix = "TIA_MCP_v";
        public const string VersionPattern = @"\d+\.\d+\.\d+(?:-(?:beta|rc)\.\d+)?";
        private const string StemPattern = "(?:" + LegacyPrefix + "|" + FinalPrefix + ")" + VersionPattern + @"_(?<date>\d{8})";
        private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        private static readonly Regex Stem = new Regex(@"\A" + StemPattern + @"\z", Options);
        private static readonly Regex Zip = new Regex(@"\A" + StemPattern + @"\.zip\z", Options);
        private static readonly Regex Sidecar = new Regex(@"\A" + StemPattern + @"\.sha256\z", Options);

        /// <summary>The legacy updater recognizes only the old prefix. Final releases that require migration use the new prefix.</summary>
        public static string Name(ProductReleaseVersion version, DateTime releaseDate)
        {
            var prefix = version.Major >= 4 && !version.IsPrerelease ? FinalPrefix : LegacyPrefix;
            return prefix + version.Label + "_" + releaseDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        }

        public static bool IsPackageName(string name) => Stem.IsMatch(name);
        public static bool IsZip(string name) => Zip.IsMatch(name);
        public static bool IsSidecar(string name) => Sidecar.IsMatch(name);

        /// <summary>Publishing requires the exact name that preparation would produce for the version and a real release date.</summary>
        public static bool IsCanonicalZip(string name, ProductReleaseVersion version)
        {
            var match = Zip.Match(name);
            return match.Success && DateTime.TryParseExact(match.Groups["date"].Value, "yyyyMMdd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && name == Name(version, date) + ".zip";
        }
    }
}
