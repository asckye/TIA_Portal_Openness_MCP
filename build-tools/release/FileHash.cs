using System.Security.Cryptography;

namespace TiaMcp.ReleaseTool;

internal static class FileHash
{
    internal static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
