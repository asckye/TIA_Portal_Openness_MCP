using System;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace TiaOpenness.Shared
{
    internal static class OperatorPipe
    {
        internal const int MaximumInstances = 8;
        internal static string Name(string sid, string dataRoot, int pid, long startTicks)
        {
            using (var sha = SHA256.Create())
                return "TiaMcp.Operator.v1." + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(
                    sid + "\n" + Path.GetFullPath(dataRoot).ToUpperInvariant() + "\n" + pid.ToString(CultureInfo.InvariantCulture)
                    + "\n" + startTicks.ToString(CultureInfo.InvariantCulture)))).Replace("-", "").ToLowerInvariant();
        }
        internal static NamedPipeServerStream CreateServer(string name, string sid, bool first)
            => LocalPipeSecurity.CreateServer(name, sid, first, MaximumInstances);
        // Process-token checks work before reading handshake bytes; impersonation requires a prior read.
        internal static int? VerifiedClient(NamedPipeServerStream pipe, string sid, string image)
            => LocalPipeSecurity.VerifiedClientProcessId(pipe, sid, image);
    }
}
