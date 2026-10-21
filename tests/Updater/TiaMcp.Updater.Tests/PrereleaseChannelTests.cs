using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TiaMcp.Updater;
using Xunit;

namespace TiaMcp.Updater.Tests
{
    public sealed class PrereleaseChannelTests
    {
        [Theory]
        [InlineData("4.0.0-beta.2", "4.0.0-beta.1", 1)]
        [InlineData("4.0.0", "4.0.0-rc.1", 1)]
        [InlineData("3.3.0", "4.0.0-beta.1", -1)]
        [InlineData("v4.0.0-rc.1", "4.0.0-beta.2", 1)]
        [InlineData("4.0.0-beta.1", "v4.0.0-beta.1", 0)]
        [InlineData("4.0.0", "3.3.0", 1)]
        public void Versions_follow_semantic_versioning_precedence(string left, string right, int expected)
        {
            Assert.Equal(expected, UpdaterEngine.CompareVersions(left, right));
            Assert.Equal(-expected, UpdaterEngine.CompareVersions(right, left));
        }

        [Fact]
        public void Prerelease_tags_normalize_and_other_suffixes_are_refused()
        {
            Assert.Equal("4.0.0-beta.1", UpdaterEngine.NormalizeVersion("v4.0.0-beta.1"));
            Assert.Equal("3.3.0", UpdaterEngine.NormalizeVersion("V3.3.0"));
            Assert.Throws<InvalidOperationException>(() => UpdaterEngine.NormalizeVersion("v4.0.0-alpha.1"));
            Assert.Throws<InvalidOperationException>(() => UpdaterEngine.NormalizeVersion(" v3.3.0"));
            Assert.True(UpdaterEngine.UsesPrereleaseChannel("4.0.0-beta.1"));
            Assert.False(UpdaterEngine.UsesPrereleaseChannel("3.3.0"));
        }

        [Fact]
        public void Plain_install_reads_only_the_latest_release_endpoint()
        {
            using var f = new ChannelFixture("3.3.0");
            var lines = f.Check();
            Assert.Equal(new[] { "/api/repos/owner/repo/releases/latest" }, f.Urls);
            Assert.Contains(lines, line => line.Contains("3.3.0 -> 4.0.0", StringComparison.Ordinal));
        }

        [Fact]
        public void Plain_install_refuses_a_latest_answer_that_is_a_prerelease()
        {
            using var f = new ChannelFixture("3.3.0") { Latest = Release("v4.0.0-beta.1", prerelease: true) };
            var error = Assert.Throws<InvalidOperationException>(() => f.Check());
            Assert.Contains("pre-release", error.Message, StringComparison.Ordinal);
            using var flagged = new ChannelFixture("3.3.0") { Latest = Release("v4.0.0", prerelease: true) };
            Assert.Throws<InvalidOperationException>(() => flagged.Check());
        }

        [Fact]
        public void Prerelease_install_takes_the_newest_published_release_from_the_list()
        {
            using var f = new ChannelFixture("4.0.0-beta.1")
            {
                List = new[] { Release("v3.3.0"), Release("v4.0.0-beta.1", prerelease: true), Release("v4.0.0-beta.2", prerelease: true), Release("v4.0.0-beta.3", prerelease: true, draft: true), Release("latest") }
            };
            var lines = f.Check();
            Assert.Equal(new[] { "/api/repos/owner/repo/releases?per_page=100" }, f.Urls);
            Assert.Contains(lines, line => line.Contains("4.0.0-beta.1 -> 4.0.0-beta.2", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("TIA_MCP_Delivery_v4.0.0-beta.2_20261021.zip", StringComparison.Ordinal));
        }

        [Fact]
        public void Prerelease_install_is_offered_the_plain_release_that_follows_it()
        {
            using var f = new ChannelFixture("4.0.0-rc.1") { List = new[] { Release("v4.0.0-rc.1", prerelease: true), Release("v4.0.0"), Release("v3.3.0") } };
            Assert.Contains(f.Check(), line => line.Contains("4.0.0-rc.1 -> 4.0.0", StringComparison.Ordinal) && !line.Contains("4.0.0-rc.1 -> 4.0.0-", StringComparison.Ordinal));
        }

        [Fact]
        public void Explicit_version_may_name_a_prerelease_tag()
        {
            using var f = new ChannelFixture("3.3.0") { Tagged = Release("v4.0.0-beta.2", prerelease: true) };
            var lines = f.Check("v4.0.0-beta.2");
            Assert.Equal(new[] { "/api/repos/owner/repo/releases/tags/v4.0.0-beta.2" }, f.Urls);
            Assert.Contains(lines, line => line.Contains("3.3.0 -> 4.0.0-beta.2", StringComparison.Ordinal));
            int warning = lines.FindIndex(line => line.Contains("the selected release is a pre-release", StringComparison.Ordinal) && line.EndsWith("v4.0.0-beta.2", StringComparison.Ordinal));
            Assert.True(warning >= 0 && warning < lines.FindIndex(line => line.Contains("TIA_MCP_Delivery_v4.0.0-beta.2_20261021.zip", StringComparison.Ordinal)), "the pre-release warning comes before the asset");
            Assert.Throws<InvalidOperationException>(() => f.Check("v4.0.0-alpha.1"));
        }

        [Fact]
        public void Plain_target_carries_no_prerelease_warning()
        {
            using var f = new ChannelFixture("3.3.0");
            Assert.DoesNotContain(f.Check(), line => line.Contains("pre-release", StringComparison.Ordinal));
        }

        [Fact]
        public void Rate_limited_prerelease_install_reports_that_the_release_page_shows_only_plain_releases()
        {
            using var f = new ChannelFixture("4.0.0-beta.1") { FailApi = true };
            var lines = f.Check();
            Assert.Contains(lines, line => line.Contains("the release page shows only the latest plain release", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("4.0.0-beta.1 > 3.3.0", StringComparison.Ordinal));
            using var plain = new ChannelFixture("3.3.0") { FailApi = true };
            Assert.DoesNotContain(plain.Check(), line => line.Contains("the release page shows only", StringComparison.Ordinal));
        }

        private static object Release(string tag, bool prerelease = false, bool draft = false)
        {
            string version = tag.TrimStart('v');
            string package = "TIA_MCP_Delivery_v" + version + "_20261021";
            return new
            {
                tag_name = tag, published_at = "2026-10-21T00:00:00Z", prerelease, draft,
                assets = new[] { new { name = package + ".zip", browser_download_url = "https://fixture.invalid/download/" + package + ".zip" }, new { name = package + ".sha256", browser_download_url = "https://fixture.invalid/download/" + package + ".sha256" } }
            };
        }

        private sealed class ChannelFixture : IDisposable
        {
            private readonly string root = Path.Combine(Path.GetTempPath(), "p890-channel-" + Guid.NewGuid().ToString("N"));
            internal object Latest { get; set; } = Release("v4.0.0");
            internal object Tagged { get; set; }
            internal object[] List { get; set; } = Array.Empty<object>();
            internal List<string> Urls { get; } = new List<string>();
            internal bool FailApi { get; set; }

            internal ChannelFixture(string installed)
            {
                Directory.CreateDirectory(Path.Combine(root, "manifest"));
                File.WriteAllText(Path.Combine(root, "manifest/delivery.json"), JsonSerializer.Serialize(new { release = installed, package = "TIA_MCP_Delivery_v" + installed + "_20261020", engineRelease = installed }));
            }

            internal List<string> Check(string version = null)
            {
                Urls.Clear();
                var lines = new List<string>();
                var options = new UpdaterOptions
                {
                    InstallRoot = root, Repository = "owner/repo", TimeoutSeconds = 5, Check = true, Version = version,
                    ApiBaseUrl = "https://fixture.invalid/api", ReleasePageBaseUrl = "https://fixture.invalid/github",
                    ReadTextForTest = (url, _) =>
                    {
                        Urls.Add(new Uri(url).PathAndQuery);
                        if (url.Contains("/expanded_assets/", StringComparison.Ordinal))
                            return "<a href=\"/releases/download/v3.3.0/TIA_MCP_Delivery_v3.3.0_20261003.zip\">zip</a><a href=\"/releases/download/v3.3.0/TIA_MCP_Delivery_v3.3.0_20261003.sha256\">sha</a>";
                        if (FailApi) throw new IOException("synthetic rate limit");
                        if (url.EndsWith("/releases/latest", StringComparison.Ordinal)) return JsonSerializer.Serialize(Latest);
                        if (url.EndsWith("/releases?per_page=100", StringComparison.Ordinal)) return JsonSerializer.Serialize(List);
                        if (url.Contains("/releases/tags/", StringComparison.Ordinal) && Tagged != null) return JsonSerializer.Serialize(Tagged);
                        throw new IOException("unexpected request: " + url);
                    },
                    ReadRedirectForTest = (url, _) => FailApi ? "https://fixture.invalid/github/releases/tag/v3.3.0" : throw new IOException("the release page is not used here: " + url)
                };
                UpdaterEngine.Run(options, lines.Add, _ => new List<string>());
                return lines;
            }

            public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }
        }
    }
}
