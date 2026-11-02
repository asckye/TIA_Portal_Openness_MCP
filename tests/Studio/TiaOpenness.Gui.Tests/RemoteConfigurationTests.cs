using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TiaMcpConfigurator;
using TiaOpenness.Core.Environment;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;
using TiaOpenness.Shared;
using Xunit;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class RemoteConfigurationTests(WpfContext wpf) : IDisposable
{
    private readonly string root = Path.Combine(FindRoot(), "bin-build", "relay-config-tests", Guid.NewGuid().ToString("N"));
    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Version.props"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("worktree");
    }
    private const string Secret = "synthetic-http-access-key";
    private string Engine
    {
        get
        {
            foreach (string relative in new[] { "manifest/package-manifest.json", "manifest/delivery.json", "runtime/v21/TiaMcp.FoundationHost.exe" })
            {
                string path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "{}");
            }
            return ConfigCore.Engine(root, "21");
        }
    }

    [Fact]
    public void Every_client_schema_uses_stdio_relay_without_credentials() => wpf.Run(() =>
    {
        string engine = Engine;
        foreach (var profile in ClientProfiles.All())
        {
            var entry = ClientProfiles.Entry(profile, true, "192.0.2.10", 8765, Secret, engine, "21", null!);
            string serialized = ConfigCore.Json().Serialize(entry);
            Assert.DoesNotContain(Secret, serialized); Assert.DoesNotContain("Authorization", serialized);
            var arguments = profile.Client == "opencode" ? ((string[])entry["command"]).Skip(1).ToArray() : (string[])entry["args"];
            Assert.Equal(new[] { "--bundle-root", root, "--relay", "http://192.0.2.10:8765/mcp" }, arguments);
            if (profile.Client == "opencode") { Assert.Equal("local", entry["type"]); Assert.Equal(engine, ((string[])entry["command"])[0]); }
            else Assert.Equal(engine, entry["command"]);
        }
        string hostArguments = ConfigCore.Arguments(new ServerSettings { ReleaseKey = "21", Address = "127.0.0.1", Port = 8765, TiaPath = "TIA path" }, Secret, root);
        Assert.DoesNotContain(Secret, hostArguments); Assert.DoesNotContain("--http-api-key", hostArguments);
        Assert.Contains("--http-prefix", hostArguments);
    });

    [Theory]
    [InlineData("claude-code")][InlineData("vscode")][InlineData("gemini")][InlineData("opencode")][InlineData("codex")]
    public void Existing_remote_entries_require_confirmation_and_exact_backup(string client) => wpf.Run(() =>
    {
        string engine = Engine, path = Path.Combine(root, client == "codex" ? "config.toml" : "config.json");
        var profile = new ClientProfile(client, client, path, "");
        string original = client == "codex"
            ? "model = \"keep\"\r\n[mcp_servers.\"tia-portal-vm\"]\r\nurl = \"http://192.0.2.10:8765/mcp\"\r\nhttp_headers = { Authorization = \"Bearer " + Secret + "\" }\r\n[mcp_servers.other]\r\ncommand = \"keep.exe\"\r\n"
            : "{\"theme\":\"keep\",\"" + ClientProfiles.RootKey(profile) + "\":{\"other\":{\"command\":\"keep.exe\"},\"tia-portal-vm\":{\"url\":\"http://192.0.2.10:8765/mcp\",\"headers\":{\"Authorization\":\"Bearer " + Secret + "\"}}}}";
        File.WriteAllText(path, original, new UTF8Encoding(false));
        Assert.True(ClientProfiles.HasRemoteEntry(profile, "http://192.0.2.10:8765/mcp"));
        Assert.False(ClientProfiles.HasRemoteEntry(profile, "http://192.0.2.11:8765/mcp"));
        var change = ClientProfiles.PrepareSave(profile, true, "192.0.2.10", 8765, Secret, engine, "21", null!);
        Assert.True(change.RequiresMigration); Assert.Equal(original, File.ReadAllText(path));
        Assert.Throws<InvalidOperationException>(() => change.Apply(false)); Assert.Null(change.BackupPath);
        bool keySaved = false;
        change.BeforeApply = () => { Assert.Equal(original, File.ReadAllText(change.BackupPath!)); keySaved = true; };
        change.Apply(true);
        Assert.True(keySaved); Assert.Equal(original, File.ReadAllText(change.BackupPath!));
        string saved = File.ReadAllText(path);
        Assert.DoesNotContain(Secret, saved); Assert.DoesNotContain("Authorization", saved);
        Assert.Contains("--relay", saved); Assert.Contains("keep.exe", saved); Assert.Contains("keep", saved);
        Assert.Contains(".bak_", change.BackupPath);
    });

    [Fact]
    public void Protected_store_is_compatible_with_existing_Workbench_DPAPI_and_binds_keys_to_endpoints()
    {
        string existing = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(Secret), null, DataProtectionScope.CurrentUser));
        Assert.Equal(Secret, HttpAccessKeyStore.Unprotect(existing));
        Assert.Equal(Secret, Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(HttpAccessKeyStore.Protect(Secret)), null, DataProtectionScope.CurrentUser)));
        var endpoint = new Uri("http://127.0.0.1:8765/mcp");
        string directory = Path.Combine(root, "config"); HttpAccessKeyStore.Save(directory, endpoint, Secret);
        Assert.Equal(Secret, HttpAccessKeyStore.Read(directory, endpoint));
        Assert.DoesNotContain(Secret, File.ReadAllText(HttpAccessKeyStore.PathFor(directory, endpoint)));
        Assert.Throws<InvalidOperationException>(() => HttpAccessKeyStore.Read(directory, new Uri("http://127.0.0.1:8766/mcp")));
        Directory.CreateDirectory(directory); File.Delete(HttpAccessKeyStore.PathFor(directory, endpoint));
        File.WriteAllText(Path.Combine(directory, "http-v21.json"), ConfigCore.Json().Serialize(new ServerSettings { ReleaseKey = "21", Address = "127.0.0.1", Port = 8765, ProtectedKey = existing }));
        Assert.Equal(Secret, HttpAccessKeyStore.Read(directory, endpoint));
    }

    [Theory]
    [InlineData(AppLanguage.English)][InlineData(AppLanguage.Chinese)]
    public void Proxy_check_compares_routes_without_keys_and_localizes_guidance(AppLanguage language) => wpf.Run(() =>
    {
        var previous = Loc.Current.Language;
        try
        {
            Loc.Current.Language = language;
            string? Env(string name) => name == "HTTP_PROXY" ? "http://user:proxy-secret@127.0.0.1:7897" : null;
            var policy = new RemoteProxyPolicy(Env, new WebProxy(), _ => [IPAddress.Parse("192.0.2.10")]);
            var routes = new List<Uri?>();
            var finding = ProxyConnectionInspector.Inspect(["http://192.168.1.10:8765/mcp"], Env, policy,
                (_, proxy) => { routes.Add(proxy); return proxy == null ? 401 : 502; }, "WinINET: fixture");
            Assert.Equal("ProxyCaptured", finding.Result); Assert.Null(routes[0]); Assert.NotNull(routes[1]);
            Assert.Contains("401", finding.Evidence); Assert.Contains("502", finding.Evidence); Assert.Contains("NO_PROXY", finding.Evidence);
            Assert.DoesNotContain("proxy-secret", finding.Evidence); Assert.DoesNotContain(Secret, finding.Evidence);
            Assert.Equal(Loc.Current["Env.ProxyRemote"], EnvironmentCheckCatalogue.Name("proxy-remote").Resolve());
            Assert.Contains("NO_PROXY", EnvironmentCheckCatalogue.Fix("proxy-remote").Resolve());
            var context = new EnvironmentCheckContext { RemoteEndpoints = ["http://192.168.1.10:8765/mcp"] };
            var sources = new EnvironmentProbeSources { ProxyConnection = _ => finding };
            Assert.Same(finding, new WorkbenchEnvironmentChecks(context, sources).Run().Single(f => f.Id == "proxy-remote"));
        }
        finally { Loc.Current.Language = previous; }
    });

    [Fact]
    public async Task Reachability_probes_use_real_HTTP_without_sending_an_access_key()
    {
        static WebApplication Application(int status, Action<HttpContext> inspect)
        {
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseSetting("urls", "http://127.0.0.1:0");
            var app = builder.Build(); app.Run(context => { inspect(context); context.Response.StatusCode = status; return Task.CompletedTask; }); return app;
        }
        int direct = 0, proxied = 0;
        string? directAuthorization = null, proxyAuthorization = null;
        await using var endpoint = Application(401, context => { directAuthorization = context.Request.Headers.Authorization.ToString(); direct++; });
        await using var proxy = Application(502, context => { proxyAuthorization = context.Request.Headers.Authorization.ToString(); proxied++; });
        await endpoint.StartAsync(); await proxy.StartAsync();
        var target = new Uri(endpoint.Urls.Single() + "/mcp");
        Assert.Equal(401, await Task.Run(() => ProxyConnectionInspector.Probe(target, null)));
        Assert.Equal(502, await Task.Run(() => ProxyConnectionInspector.Probe(target, new Uri(proxy.Urls.Single()))));
        Assert.Equal(1, direct); Assert.Equal(1, proxied);
        Assert.Equal("", directAuthorization); Assert.Equal("", proxyAuthorization);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
