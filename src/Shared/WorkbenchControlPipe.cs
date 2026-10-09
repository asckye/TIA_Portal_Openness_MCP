using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace TiaOpenness.Shared
{
    internal static class WorkbenchControlPipe
    {
        internal const int MaximumInstances = 8;
        internal const int ConnectTimeoutMilliseconds = 250;
        internal static string Name(string scope, string sid)
        {
            using (var sha = SHA256.Create())
                return "TiaMcp.Workbench.v1." + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(
                    sid + "\n" + Path.GetFullPath(scope).ToUpperInvariant() + "\nworkbench-control"))).Replace("-", "").ToLowerInvariant();
        }
        internal static string CurrentName => Name(ApprovalSettings.SettingsPath, LocalPipeSecurity.CurrentSid);
        internal static NamedPipeServerStream CreateServer(string name, string sid, bool first)
            => LocalPipeSecurity.CreateServer(name, sid, first, MaximumInstances);
        // P8-20b checks the peer after reading; P8-20c checks the owner before writing.
        // Paths must be resolved by BundleLayout for the selected bundle, never from a request.
        internal static bool PeerMatches(NamedPipeServerStream pipe, string sid, string expectedHostImage)
            => LocalPipeSecurity.PeerMatches(pipe, sid, expectedHostImage);
        internal static bool ServerMatches(NamedPipeClientStream pipe, string sid, string expectedWorkbenchImage)
            => LocalPipeSecurity.ServerMatches(pipe, sid, expectedWorkbenchImage);
    }
}
