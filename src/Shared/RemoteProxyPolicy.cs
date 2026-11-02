using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace TiaOpenness.Shared
{
    // Never send local/private destinations to a forward proxy. Other destinations retain env/WinINET policy.
    internal sealed class RemoteProxyPolicy : IWebProxy
    {
        private readonly Func<string, string?> environment;
        private readonly IWebProxy system;
        private readonly Func<string, IPAddress[]> resolve;
        internal RemoteProxyPolicy(Func<string, string?>? environment = null, IWebProxy? system = null, Func<string, IPAddress[]>? resolve = null)
        {
            this.environment = environment ?? Environment.GetEnvironmentVariable;
            this.system = system ?? WebRequest.GetSystemWebProxy();
            this.resolve = resolve ?? Dns.GetHostAddresses;
            Credentials = new EnvironmentCredentials(this.environment, this.system.Credentials);
        }
        public ICredentials? Credentials { get; set; }

        private sealed class EnvironmentCredentials(Func<string, string?> environment, ICredentials? fallback) : ICredentials
        {
            public NetworkCredential? GetCredential(Uri uri, string authType)
            {
                foreach (string name in new[] { "http_proxy", "HTTP_PROXY", "https_proxy", "HTTPS_PROXY", "all_proxy", "ALL_PROXY" })
                {
                    string value = environment(name) ?? "";
                    if (!value.Contains("://")) value = "http://" + value;
                    if (!Uri.TryCreate(value, UriKind.Absolute, out var proxy) || proxy.Host != uri.Host || proxy.Port != uri.Port || proxy.UserInfo
                            .Length == 0)
                        continue;
                    var parts = proxy.UserInfo.Split(new[] { ':' }, 2);
                    return new NetworkCredential(Uri.UnescapeDataString(parts[0]), parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : "");
                }
                return fallback?.GetCredential(uri, authType);
            }
        }

        internal static bool PrivateAddress(IPAddress address)
        {
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            if (IPAddress.IsLoopback(address)) return true;
            var bytes = address.GetAddressBytes();
            if (address.AddressFamily == AddressFamily.InterNetwork)
                return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                    || (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 169 && bytes[1] == 254);
            return address.IsIPv6LinkLocal || (bytes[0] & 0xfe) == 0xfc;
        }

        internal bool PrivateDestination(Uri destination)
        {
            string host = destination.DnsSafeHost;
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return true;
            if (IPAddress.TryParse(host, out var address)) return PrivateAddress(address);
            try { return resolve(host).Any(PrivateAddress); }
            catch (SocketException) /* swallow(env-probe): a proxy may resolve a public host even when local DNS cannot */ { return false; }
        }

        private string Variable(string name) => environment(name.ToLowerInvariant()) ?? environment(name) ?? "";

        internal bool Excluded(Uri destination)
        {
            foreach (string item in Variable("NO_PROXY").Split(','))
            {
                string token = item.Trim(), host = destination.DnsSafeHost.TrimEnd('.');
                if (token == "*") return true;
                if (token.Length == 0) continue;
                bool suffix = token.StartsWith(".", StringComparison.Ordinal);
                bool explicitPort = token.StartsWith("[", StringComparison.Ordinal) ? token.Contains("]:") : token.Count(c => c == ':') == 1;
                // Bracketed IPv6 optionally followed by a port; unbracketed IPv6 is a host.
                if (Uri.TryCreate("http://" + token.TrimStart('.'), UriKind.Absolute, out var entry)
                        && (token.StartsWith("[", StringComparison.Ordinal) || token.Count(c => c == ':') == 1))
                {
                    if (explicitPort && entry.Port != destination.Port) continue;
                    token = (suffix ? "." : "") + entry.DnsSafeHost;
                }
                token = token.Trim('[', ']').TrimEnd('.');
                if (host.Equals(token.TrimStart('.'), StringComparison.OrdinalIgnoreCase)
                    || (token.StartsWith(".", StringComparison.Ordinal) && host.EndsWith(token, StringComparison.OrdinalIgnoreCase))) return true;
            }
            return false;
        }

        internal Uri ConventionalProxy(Uri destination)
        {
            if (Excluded(destination)) return destination;
            string value = Variable(destination.Scheme == "https" ? "HTTPS_PROXY" : "HTTP_PROXY");
            if (value.Length == 0) value = Variable("ALL_PROXY");
            if (value.Length != 0)
            {
                if (!value.Contains("://")) value = "http://" + value;
                if (!Uri.TryCreate(value, UriKind.Absolute, out var proxy) || (proxy.Scheme != "http" && proxy.Scheme != "https"))
                    throw new InvalidOperationException("Invalid HTTP(S) proxy setting.");
                return proxy;
            }
            return system.IsBypassed(destination) ? destination : system.GetProxy(destination) ?? destination;
        }

        public bool IsBypassed(Uri host) => PrivateDestination(host) || ConventionalProxy(host) == host;
        public Uri GetProxy(Uri destination) => PrivateDestination(destination) ? destination : ConventionalProxy(destination);
    }
}
