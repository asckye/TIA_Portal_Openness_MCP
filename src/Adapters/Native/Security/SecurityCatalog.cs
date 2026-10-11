using System;
using System.Linq;
namespace TiaMcp.Adapters.Native.Security;
internal static partial class SecurityCatalog
{
    internal static readonly string[] CertificateUsages = { "Tls", "WebServer", "OpcUaServer", "OpcUaClient", "OpcUaClientServer" };
    internal static readonly string[] SignatureAlgorithms = { "Sha1RSA", "Sha256RSA" };
    internal static readonly string[] SubjectAlternativeNameTypes = { "Dns", "Email", "IP", "Uri" };
    internal static string RequireOneOf(string value, string[] allowed, string parameter)
    { if (string.IsNullOrWhiteSpace(value) || !allowed.Contains(value, StringComparer.Ordinal)) throw new ArgumentException(parameter + " must be one of: " + string.Join("/", allowed) + " (case-sensitive)."); return value; }
}
