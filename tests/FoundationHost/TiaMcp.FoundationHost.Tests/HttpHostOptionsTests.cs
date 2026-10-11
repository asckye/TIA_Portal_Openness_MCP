extern alias foundationhost;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Xunit;
using HttpHostOptions = foundationhost::TiaMcp.FoundationHost.HttpHostOptions;

// The HTTP hosts must not watch the working directory they were started from.
public sealed class HttpHostOptionsTests
{
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
    public void Both_http_entry_points_build_with_the_shared_options()
    {
        string root = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        foreach (var file in new[] { "Program.cs", "EngineReleaseHost.cs" })
        {
            string source = File.ReadAllText(Path.Combine(root, "src", "FoundationHost", file));
            Assert.Contains("WebApplication.CreateBuilder(", source, StringComparison.Ordinal);
            foreach (var call in source.Split("WebApplication.CreateBuilder(").Skip(1))
                Assert.StartsWith("HttpHostOptions.Create()", call.Replace("TiaMcp.FoundationHost.", "", StringComparison.Ordinal), StringComparison.Ordinal);
        }
    }
}
