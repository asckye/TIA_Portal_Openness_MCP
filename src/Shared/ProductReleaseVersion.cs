#nullable enable
using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace TiaMcp.Versioning
{
    /// <summary>
    /// A product release: MAJOR.MINOR.PATCH, optionally followed by a pre-release label "beta.N" or "rc.N" (Semantic Versioning).
    /// Assembly and file versions carry only the numeric core; the label appears in the informational version, the tag,
    /// the delivery package name and the release records.
    /// </summary>
    public sealed class ProductReleaseVersion : IComparable<ProductReleaseVersion>, IEquatable<ProductReleaseVersion>
    {
        private static readonly Regex Pattern = new Regex(@"^(?<core>(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+))(?:-(?<pre>(?:beta|rc)\.(?:0|[1-9]\d*)))?$", RegexOptions.CultureInvariant);

        private ProductReleaseVersion(int major, int minor, int patch, string core, string prerelease)
        {
            Major = major; Minor = minor; Patch = patch; Core = core; Prerelease = prerelease;
        }

        public int Major { get; }
        public int Minor { get; }
        public int Patch { get; }
        /// <summary>The numeric MAJOR.MINOR.PATCH text exactly as written.</summary>
        public string Core { get; }
        /// <summary>"beta.N" or "rc.N"; empty for a plain release.</summary>
        public string Prerelease { get; }
        public bool IsPrerelease => Prerelease.Length != 0;
        /// <summary>The full version: Core, or Core-Prerelease.</summary>
        public string Label => IsPrerelease ? Core + "-" + Prerelease : Core;
        /// <summary>The four-part Windows file version: Core plus ".0".</summary>
        public string FileVersion => Core + ".0";
        /// <summary>The Git tag and GitHub release name: "v" plus Label.</summary>
        public string Tag => "v" + Label;

        /// <summary>Parses a version without a "v" prefix; plain X.Y.Z or X.Y.Z-beta.N / X.Y.Z-rc.N.</summary>
        public static bool TryParse(string? text, out ProductReleaseVersion? version)
        {
            version = null;
            if (text == null) return false;
            var match = Pattern.Match(text);
            if (!match.Success) return false;
            int major, minor, patch;
            if (!int.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out major)
                || !int.TryParse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out minor)
                || !int.TryParse(match.Groups["patch"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out patch)) return false;
            version = new ProductReleaseVersion(major, minor, patch, match.Groups["core"].Value, match.Groups["pre"].Value);
            return true;
        }

        /// <summary>Parses a release tag or version: an optional leading "v" or "V", then the version. Whitespace is not accepted.</summary>
        public static bool TryParseTag(string? text, out ProductReleaseVersion? version)
        {
            version = null;
            if (text == null) return false;
            var value = text.Length > 0 && (text[0] == 'v' || text[0] == 'V') ? text.Substring(1) : text;
            return TryParse(value, out version);
        }

        public static ProductReleaseVersion Parse(string text)
        {
            ProductReleaseVersion? version;
            if (!TryParse(text, out version) || version == null) throw new FormatException("Release version must be X.Y.Z or X.Y.Z-beta.N / X.Y.Z-rc.N: " + text);
            return version;
        }

        /// <summary>Composes the label from the Version.props pair TiaMcpRelease and TiaMcpPrerelease.</summary>
        public static string Compose(string core, string? prerelease)
            => string.IsNullOrEmpty(prerelease) ? core : core + "-" + prerelease;

        /// <summary>True when the text parses as a tag or version with a pre-release label.</summary>
        public static bool IsPrereleaseText(string? text)
        {
            ProductReleaseVersion? version;
            return TryParseTag(text, out version) && version != null && version.IsPrerelease;
        }

        /// <summary>Semantic Versioning precedence: numeric core first; a pre-release sorts below its plain release; identifiers compare
        /// numerically when both are numeric and ordinally otherwise, numeric below alphanumeric, a shorter list below a longer one.</summary>
        public int CompareTo(ProductReleaseVersion? other)
        {
            if (other == null) return 1;
            var core = Major != other.Major ? Major.CompareTo(other.Major) : Minor != other.Minor ? Minor.CompareTo(other.Minor) : Patch.CompareTo(other.Patch);
            if (core != 0) return core;
            if (!IsPrerelease || !other.IsPrerelease) return IsPrerelease == other.IsPrerelease ? 0 : IsPrerelease ? -1 : 1;
            var left = Prerelease.Split('.');
            var right = other.Prerelease.Split('.');
            for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
            {
                long a, b;
                var leftNumeric = long.TryParse(left[index], NumberStyles.None, CultureInfo.InvariantCulture, out a);
                var rightNumeric = long.TryParse(right[index], NumberStyles.None, CultureInfo.InvariantCulture, out b);
                int result;
                if (leftNumeric && rightNumeric) result = a.CompareTo(b);
                else if (leftNumeric) result = -1;
                else if (rightNumeric) result = 1;
                else result = string.CompareOrdinal(left[index], right[index]);
                if (result != 0) return Math.Sign(result);
            }
            return left.Length.CompareTo(right.Length);
        }

        /// <summary>Compares two tags or versions; null when either does not parse.</summary>
        public static int? Compare(string? left, string? right)
        {
            ProductReleaseVersion? a, b;
            if (!TryParseTag(left, out a) || !TryParseTag(right, out b) || a == null || b == null) return null;
            return a.CompareTo(b);
        }

        public bool Equals(ProductReleaseVersion? other) => other != null && CompareTo(other) == 0;
        public override bool Equals(object? obj) => Equals(obj as ProductReleaseVersion);
        public override int GetHashCode() => (Major * 397 ^ Minor) * 397 ^ Patch ^ StringComparer.Ordinal.GetHashCode(Prerelease);
        public override string ToString() => Label;
    }
}
