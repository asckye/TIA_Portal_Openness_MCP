using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using Microsoft.Win32;
using TiaOpenness.Core.Environment;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Shared;

namespace TiaOpenness.Gui.Services;

internal static class ProxyConnectionInspector
{
    internal static EnvironmentProbeSources Sources()
    {
        var sources = WindowsEnvironmentSources.Create();
        sources.ProxyConnection = endpoints => Inspect(endpoints, Environment.GetEnvironmentVariable, new RemoteProxyPolicy(), Probe, WinInet());
        return sources;
    }

    internal static string WinInet()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        var proxies = Convert.ToString(key?.GetValue("ProxyServer")) ?? "";
        // Include auto-config presence, but never print a PAC URL (it can contain credentials).
        return "WinINET: enabled=" + Convert.ToString(key?.GetValue("ProxyEnable"))
            + "; proxy=" + string
                .Join(";", proxies.Split(';').Where(p => p.Length != 0).Select(p => SafeProxy(p.Contains('=') ? p[(p.IndexOf('=') + 1)..] : p)))
            + "; bypass=" + Convert.ToString(key?.GetValue("ProxyOverride")) + "; PAC=" + (key?.GetValue("AutoConfigURL") != null);
    }

    internal static string SafeProxy(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "<unset>";
        if (!value.Contains("://", StringComparison.Ordinal)) value = "http://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return "<invalid>";
        return uri.Scheme + "://" + uri.Host + ":" + uri.Port;
    }

    internal static EnvironmentFinding Inspect(string[] endpoints, Func<string, string?> environment, RemoteProxyPolicy policy,
        Func<Uri, Uri?, int> probe, string winInet)
    {
        var evidence = new List<string>();
        foreach (string name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" })
            evidence.Add(name + "=" + SafeProxy(environment(name) ?? ""));
        evidence.Add("NO_PROXY=" + (environment("no_proxy") ?? environment("NO_PROXY") ?? "<unset>"));
        evidence.Add(winInet);
        bool captured = false, failed = false;
        foreach (string value in endpoints.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var endpoint = HttpAccessKeyStore.Endpoint(value);
            var proxy = policy.ConventionalProxy(endpoint);
            bool proxied = proxy != endpoint;
            captured |= proxied;
            int direct = probe(endpoint, null), viaProxy = proxied ? probe(endpoint, proxy) : direct;
            failed |= direct != 401 && viaProxy != 401;
            evidence.Add(Loc.Current.T("Env.ProxyEvidence", endpoint.AbsoluteUri,
                proxied ? SafeProxy(proxy.AbsoluteUri) : Loc.Current["Env.ProxyDirect"],
                direct, viaProxy, policy.IsBypassed(endpoint) ? Loc.Current["Env.ProxyDirect"] : Loc.Current["Env.ProxySystem"]));
        }
        evidence.Add(Loc.Current["Env.FixProxyRemote"]);
        return new EnvironmentFinding("proxy-remote", failed ? EnvironmentFindingStatus.Fail : captured || endpoints.Length == 0 ? EnvironmentFindingStatus
                .Guidance : EnvironmentFindingStatus.Pass,
            failed ? "Fail" : captured ? "ProxyCaptured" : endpoints.Length == 0 ? "NoEndpoint" : "Pass", string.Join("\n", evidence));
    }

    internal static int Probe(Uri endpoint, Uri? proxy)
    {
        using var handler = new HttpClientHandler { UseProxy = proxy != null, Proxy = proxy == null ? null : new WebProxy(proxy), AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        try
        {
            // A protected endpoint responds 401 before MCP dispatch. No key is read or sent for this check.
            using var response = client.Send(new HttpRequestMessage(HttpMethod.Get, endpoint), HttpCompletionOption.ResponseHeadersRead);
            return (int)response.StatusCode;
        }
        catch (HttpRequestException) /* swallow(env-probe): unreachable routes are shown as status zero with guidance */ { return 0; }
        catch (OperationCanceledException) /* swallow(env-probe): the production probe is bounded; tests inject the result */ { return 0; }
    }
}
