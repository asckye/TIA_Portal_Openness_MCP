using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    internal static class NativeFileOutput
    {
        internal static FileInfo Plan(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) throw new ArgumentException("Absolute output file path required.");
            var file = new FileInfo(path);
            if (file.Exists || Directory.Exists(file.FullName)) throw new IOException("Output already exists; overwrite refused.");
            if (file.Directory?.Exists != true) throw new DirectoryNotFoundException("Output parent must already exist.");
            return file;
        }
        internal static JsonObject Verify(FileInfo file)
        {
            file.Refresh(); if (!file.Exists || file.Length == 0) throw new IOException("Native output missing or empty: " + file.FullName);
            using var stream = file.OpenRead(); using var sha = SHA256.Create();
            return new JsonObject { ["path"] = file.FullName, ["bytes"] = file.Length,
                ["sha256"] = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(), ["contentSemanticsVerified"] = false };
        }
    }
}
