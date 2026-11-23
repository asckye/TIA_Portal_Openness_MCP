extern alias foundationhost;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Xunit;
using HttpHostOptions = foundationhost::TiaMcp.FoundationHost.HttpHostOptions;

// The HTTP hosts must not watch the working directory they were started from.
public sealed class HttpHostOptionsTests
{
    [Theory]
    [InlineData("0.0.0.0", false)]
    [InlineData("[::]", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("203.0.113.7", false)]
    [InlineData("[2001:db8::1]", false)]
    [InlineData("[::ffff:203.0.113.7]", false)]
    [InlineData("127.0.0.1", true)]
    [InlineData("172.31.0.1", true)]
    [InlineData("192.168.12.1", true)]
    [InlineData("100.64.0.0", true)]
    [InlineData("100.127.255.255", true)]
    [InlineData("[::ffff:100.64.1.2]", true)]
    [InlineData("100.63.255.255", false)]
    [InlineData("100.128.0.0", false)]
    [InlineData("169.254.1.2", true)]
    [InlineData("[::1]", true)]
    [InlineData("[fe80::1]", true)]
    [InlineData("[fd00::1]", true)]
    public void Listener_requires_a_private_literal(string address, bool allowed)
    {
        string root = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        string prefix = $"http://{address}:8765/";
        string[] options = ["--bundle-root", root, "--release-key", "21", "--public-api", root, "--transport", "http", "--http-prefix", prefix];
        if (allowed) Assert.Equal(prefix, foundationhost::TiaMcp.FoundationHost.HostOptions.Parse(options, root).HttpPrefix);
        else Assert.Contains("--http-prefix", Assert.Throws<ArgumentException>(() =>
            foundationhost::TiaMcp.FoundationHost.HostOptions.Parse(options, root)).Message);
    }

    [Fact]
    public void Removed_http_key_reports_the_migration_without_echoing_the_value()
    {
        string root = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        var error = Assert.Throws<ArgumentException>(() => foundationhost::TiaMcp.FoundationHost.HostOptions.Parse(
            ["--bundle-root", root, "--release-key", "21", "--http-api-key", "private-fixture"], root));
        Assert.Equal("--http-api-key was removed (ADR 0035); HTTP no longer uses an access key", error.Message);
    }

    [Theory]
    [InlineData("http://user:fixture-secret@127.0.0.1:8765/")]
    [InlineData("http://127.0.0.1:8765/#fixture-secret")]
    public void Http_listener_rejects_credentials_and_fragments_without_echoing_them(string prefix)
    {
        string root = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        var error = Assert.Throws<ArgumentException>(() => foundationhost::TiaMcp.FoundationHost.HostOptions.Parse(
            ["--bundle-root", root, "--release-key", "21", "--transport", "http", "--http-prefix", prefix],
            Path.Combine(root, "bin-build", "relay-option-fixture")));
        Assert.DoesNotContain("fixture-secret", error.Message);
    }

    [Fact]
    public void Http_hosts_use_the_host_folder_as_content_root_without_configuration_reload()
    {
        var options = HttpHostOptions.Create();
        Assert.Equal(AppContext.BaseDirectory, options.ContentRootPath);
        Assert.Equal(new[] { HttpHostOptions.NoConfigurationReload }, options.Args);

        var builder = WebApplication.CreateBuilder(options);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(builder.Environment.ContentRootPath)));
        var json = ((IConfigurationRoot)builder.Configuration).Providers.OfType<JsonConfigurationProvider>().ToArray();
        Assert.NotEmpty(json);
        Assert.All(json, provider => Assert.False(provider.Source.ReloadOnChange, provider.Source.Path));
    }

    [Fact]
    public void Unified_http_entry_point_builds_with_the_shared_options()
    {
        string root = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        string entry = File.ReadAllText(Path.Combine(root, "src", "FoundationHost", "Program.cs"));
        Assert.Contains("SharedReleaseHost.Run(options, fileLogger)", entry, StringComparison.Ordinal);
        Assert.DoesNotContain("WebApplication.CreateBuilder(", entry, StringComparison.Ordinal);
        foreach (var file in new[] { "EngineReleaseHost.cs" })
        {
            string source = File.ReadAllText(Path.Combine(root, "src", "FoundationHost", file));
            Assert.Contains("WebApplication.CreateBuilder(", source, StringComparison.Ordinal);
            Assert.Equal(1, source.Split("WebApplication.CreateBuilder(").Length - 1);
            foreach (var call in source.Split("WebApplication.CreateBuilder(").Skip(1))
                Assert.StartsWith("HttpHostOptions.Create()", call.Replace("TiaMcp.FoundationHost.", "", StringComparison.Ordinal), StringComparison.Ordinal);
        }
    }
}
