using System;
using System.IO;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace TiaMcp.Adapters.Engineering
{
    internal static class EngineeringFileOutput
    {
        private static string Hash(Stream stream)
        {
            using var hash = SHA256.Create();
            return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        internal static FileInfo Plan(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) throw new ArgumentException("Absolute output file path required.");
            var file = new FileInfo(path);
            if (file.Exists || Directory.Exists(file.FullName)) throw new IOException("Output already exists; overwrite refused.");
            if (file.Directory?.Exists != true) throw new DirectoryNotFoundException("Output parent must already exist.");
            return file;
        }
        internal static Dictionary<string, object?> Verify(FileInfo file)
        {
            file.Refresh(); if (!file.Exists || file.Length == 0) throw new IOException("Native output missing or empty: " + file.FullName);
            using var stream = file.OpenRead();
            return new Dictionary<string, object?> { ["path"] = file.FullName, ["bytes"] = file.Length,
                ["sha256"] = Hash(stream), ["contentSemanticsVerified"] = false };
        }
    }
}
