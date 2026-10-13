using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using TiaMcp.Adapters.Native.Hmi;

namespace TiaMcp.Adapters.Native.Options;

internal static class OptionalProductFiles
{
    internal static FileInfo Plan(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) throw new ArgumentException("Absolute output file path required.");
        var file = new FileInfo(path);
        if (file.Exists || Directory.Exists(file.FullName)) throw new IOException("Output already exists; overwrite refused.");
        if (file.Directory?.Exists != true) throw new DirectoryNotFoundException("Output parent must already exist.");
        return file;
    }
    internal static HmiObject Verify(FileInfo file)
    {
        file.Refresh();
        if (!file.Exists || file.Length == 0) throw new IOException("Native output missing or empty: " + file.FullName);
        using var stream = file.OpenRead();
        using var hash = SHA256.Create();
        return new HmiObject { ["path"] = file.FullName, ["bytes"] = file.Length,
            ["sha256"] = string.Concat(hash.ComputeHash(stream).Select(value => value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture))),
            ["contentSemanticsVerified"] = false };
    }
}
