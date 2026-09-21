using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TiaMcpServer.ModelContextProtocol
{
    // 2.7.57: the pure part of CheckForUpdate - version parsing / comparison and the GitHub "latest release" JSON.
    // The engine only REPORTS; replacing the files is scripts/operations/Update-Engine.ps1, which refuses while any
    // TiaMcpServer.exe runs (the maintainer chose "stop, then update" over a self-replacing hot update).
    public static class UpdateLogic
    {
        public const string DefaultRepository = "asckye/TIA_Portal_Openness_MCP";
        public const string UpdaterRelativePath = @"scripts\operations\Update-Engine.ps1";

        public sealed class Asset
        {
            public string Name { get; set; } = "";
            public string Url { get; set; } = "";
            public long Size { get; set; }
            /// <summary>GitHub's "sha256:&lt;hex&gt;" when the API reports one; empty otherwise (the .sha256 sidecar is authoritative).</summary>
            public string Digest { get; set; } = "";
        }

        public sealed class Release
        {
            public string Tag { get; set; } = "";
            public string Name { get; set; } = "";
            public string Url { get; set; } = "";
            public string PublishedAt { get; set; } = "";
            public bool Prerelease { get; set; }
            public bool Draft { get; set; }
            public List<Asset> Assets { get; } = new List<Asset>();
            public Asset? Zip => Assets.FirstOrDefault(a => Regex.IsMatch(a.Name, @"^TIA_MCP_Delivery_v\d+\.\d+\.\d+_\d{8}\.zip$", RegexOptions.IgnoreCase));
            public Asset? Sha256 => Assets.FirstOrDefault(a => Regex.IsMatch(a.Name, @"^TIA_MCP_Delivery_v\d+\.\d+\.\d+_\d{8}\.sha256$", RegexOptions.IgnoreCase));
            /// <summary>X.Y.Z of the tag, or null when the tag is not a release tag.</summary>
            public string? Version => ParseVersion(Tag) is int[] v ? string.Join(".", v) : null;
        }

        private static readonly Regex VersionPattern = new Regex(@"^v?(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?$", RegexOptions.Compiled);

        /// <summary>"v2.7.57", "2.7.57" or "2.7.57.0" -> [2,7,57]; null otherwise.</summary>
        public static int[]? ParseVersion(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var m = VersionPattern.Match(text!.Trim());
            if (!m.Success) return null;
            return new[] { int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value) };
        }

        /// <summary>Negative when a &lt; b, zero when equal, positive when a &gt; b; null when either does not parse.</summary>
        public static int? Compare(string? a, string? b)
        {
            var va = ParseVersion(a); var vb = ParseVersion(b);
            if (va == null || vb == null) return null;
            for (int i = 0; i < 3; i++) if (va[i] != vb[i]) return va[i].CompareTo(vb[i]);
            return 0;
        }

        /// <summary>owner/name only - the value is interpolated into an api.github.com path.</summary>
        public static bool IsValidRepository(string? repository)
            => !string.IsNullOrWhiteSpace(repository) && Regex.IsMatch(repository!.Trim(), @"^[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*$") && !repository!.Contains("..");

        public static string LatestReleaseUrl(string repository) => "https://api.github.com/repos/" + repository.Trim() + "/releases/latest";
        public static string ReleasePageUrl(string repository) => "https://github.com/" + repository.Trim() + "/releases/latest";

        /// <summary>Parses the GitHub REST "release" object (releases/latest or releases/tags/x).</summary>
        public static Release ParseRelease(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("GitHub release JSON must be an object.");
            var r = new Release
            {
                Tag = Text(root, "tag_name"),
                Name = Text(root, "name"),
                Url = Text(root, "html_url"),
                PublishedAt = Text(root, "published_at"),
                Prerelease = Flag(root, "prerelease"),
                Draft = Flag(root, "draft"),
            };
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                    r.Assets.Add(new Asset
                    {
                        Name = Text(a, "name"),
                        Url = Text(a, "browser_download_url"),
                        Size = a.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number ? size.GetInt64() : 0,
                        Digest = Text(a, "digest"),
                    });
            if (r.Tag.Length == 0) throw new ArgumentException("GitHub release JSON has no tag_name (rate-limited or wrong URL?).");
            return r;
        }

        public static string LatestPageUrl(string repository) => "https://github.com/" + repository.Trim() + "/releases/latest";
        public static string ExpandedAssetsUrl(string repository, string tag) => "https://github.com/" + repository.Trim() + "/releases/expanded_assets/" + tag;

        /// <summary>The tag a /releases/latest redirect landed on ("https://github.com/o/r/releases/tag/v2.7.57" -> "v2.7.57"), or null.</summary>
        public static string? TagFromReleaseUrl(string? finalUrl)
        {
            if (string.IsNullOrEmpty(finalUrl)) return null;
            var m = Regex.Match(finalUrl!, @"/releases/tag/([^/?#]+)$");
            return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
        }

        /// <summary>
        /// Fallback when the REST API is rate-limited (60 unauthenticated calls per hour per address): the release page's
        /// expanded-assets fragment lists the download links. Sizes come from the fragment's "15.2 MB" text (approximate).
        /// </summary>
        public static Release ParseReleasePage(string repository, string tag, string html)
        {
            var r = new Release { Tag = tag, Name = tag, Url = "https://github.com/" + repository.Trim() + "/releases/tag/" + tag };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Regex.Matches(html ?? "", @"/releases/download/" + Regex.Escape(tag) + @"/(?<name>[^""'<>\s]+)"))
            {
                var name = Uri.UnescapeDataString(m.Groups["name"].Value);
                if (!seen.Add(name)) continue;
                long size = 0;
                // "<span ...>15.2 MB</span>" follows the link within the same row.
                var after = html!.Substring(m.Index, Math.Min(1500, html.Length - m.Index));
                var sm = Regex.Match(after, @"(?<n>\d+(?:\.\d+)?)\s*(?<u>Bytes|KB|MB|GB)\b");
                if (sm.Success && double.TryParse(sm.Groups["n"].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n))
                    size = (long)Math.Round(n * (sm.Groups["u"].Value == "GB" ? 1073741824.0 : sm.Groups["u"].Value == "MB" ? 1048576.0 : sm.Groups["u"].Value == "KB" ? 1024.0 : 1.0));
                r.Assets.Add(new Asset { Name = name, Url = "https://github.com/" + repository.Trim() + "/releases/download/" + tag + "/" + m.Groups["name"].Value, Size = size });
            }
            return r;
        }

        private static string Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";
        private static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        /// <summary>The steps a maintainer follows; the engine never replaces its own files.</summary>
        public static IReadOnlyList<string> HowToUpdate(string? updaterPath, bool updaterPresent)
        {
            var updater = updaterPresent && updaterPath != null ? updaterPath : UpdaterRelativePath;
            return new[]
            {
                "1. Stop every TiaMcpServer.exe (and TiaMcpConfigurator.exe) on the TIA machine - the updater refuses while one runs; it never kills them.",
                "2. With internet on that machine: powershell -NoProfile -ExecutionPolicy Bypass -File \"" + updater + "\" (downloads the ZIP + .sha256, verifies, backs up the current install to .previous, replaces runtime/manifest and overlays the rest).",
                "3. Without internet: download the ZIP and its .sha256 elsewhere, copy both next to each other, then run the same script with -ZipPath <zip>.",
                "4. Start the engine again and call Bootstrap - serverVersion must show the new version. -Rollback restores the previous install.",
            };
        }

        public static string Summary(string current, Release? latest, int? comparison)
        {
            if (latest == null) return "Engine " + current + "; the latest release could not be determined.";
            var v = latest.Version ?? latest.Tag;
            if (comparison == null) return "Engine " + current + "; latest release tag '" + latest.Tag + "' is not a version tag.";
            if (comparison < 0) return "Update available: engine " + current + " -> " + v + " (" + latest.Tag + ", published " + latest.PublishedAt + "). Stop the engine, then run Update-Engine.ps1.";
            if (comparison == 0) return "Engine " + current + " is the latest release (" + latest.Tag + ").";
            return "Engine " + current + " is newer than the latest published release " + v + " (local build).";
        }
    }
}
