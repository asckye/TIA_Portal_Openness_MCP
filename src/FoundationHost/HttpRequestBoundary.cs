using Microsoft.AspNetCore.Http;
using TiaOpenness.Shared;

namespace TiaMcp.FoundationHost;

internal sealed class HttpRequestBoundary
{
    private readonly HashSet<string> origins = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly string release;
    private readonly bool dynamicPort;
    private readonly Action<string> audit;
    internal HttpRequestBoundary(HostOptions options, Action<string>? audit = null)
    {
        release = options.ReleaseKey;
        this.audit = audit ?? (outcome => AuditLog.Current.Append("end", Guid.NewGuid().ToString("N"), "http", release, "http-boundary", outcome: outcome));
        var endpoint = new Uri(options.HttpPrefix);
        dynamicPort = endpoint.Port == 0;
        Add(endpoint);
        foreach (var loopback in new[] { "127.0.0.1", "localhost", "[::1]" }) Add(new Uri(endpoint.Scheme + "://" + loopback + ":" + endpoint.Port));
        foreach (string allowed in options.HttpAllowedOrigins) Add(new Uri(allowed));
    }

    private void Add(Uri uri)
    {
        origins.Add(uri.GetLeftPart(UriPartial.Authority));
        hosts.Add(HostString.FromUriComponent(uri).Value!);
        if (uri.IsDefaultPort) hosts.Add(uri.Host + ":" + uri.Port);
    }

    internal string? Rejection(HttpRequest request)
    {
        bool boundPort = dynamicPort && request.Host.Port == request.HttpContext.Connection.LocalPort;
        if (!hosts.Contains(request.Host.Value ?? "") && !(boundPort && hosts.Contains(request.Host.Host + ":0"))) return "FORBIDDEN_HOST";
        var origin = request.Headers.Origin;
        if (origin.Count == 0) return null;
        if (origin.Count != 1 || !Uri.TryCreate(origin[0], UriKind.Absolute, out var parsed)
            || parsed.UserInfo.Length != 0 || parsed.AbsolutePath != "/" || parsed.Query.Length != 0 || parsed.Fragment.Length != 0
            || !(origins.Contains(parsed.GetLeftPart(UriPartial.Authority)) || (boundPort && parsed.Port == request.HttpContext.Connection.LocalPort
                && origins.Contains(new UriBuilder(parsed) { Port = 0 }.Uri.GetLeftPart(UriPartial.Authority))))) return "FORBIDDEN_ORIGIN";
        return null;
    }

    internal async Task Invoke(HttpContext context, RequestDelegate next)
    {
        if (Rejection(context.Request) is { } rejected)
        {
            audit(rejected);
            context.Response.StatusCode = 403; return;
        }
        await next(context);
    }
}
