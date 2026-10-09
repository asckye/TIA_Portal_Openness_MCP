using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Siemens.Engineering.Cax;

namespace TiaMcp.Adapters
{
    internal static class HardwareAmlPolicy
    {
        internal static CaxImportOptions ImportOption(string value)
        {
            Hardware.HardwareServicesPolicy.RequireOneOf(value, Hardware.HardwareServicesPolicy.CaxImportOptions, "importOption");
            return (CaxImportOptions)Enum.Parse(typeof(CaxImportOptions), value);
        }

        internal static void RequireConfirmation(bool confirm, bool dryRun)
        {
            if (!dryRun && !confirm) throw new ArgumentException("confirmImport=true is required for real execution; preview does not need it.");
        }

        internal static FileInfo InputFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
                throw new ArgumentException("filePath must be an absolute file path.");
            var file = new FileInfo(path);
            if (!file.Exists || file.Length == 0) throw new FileNotFoundException("filePath does not exist or is empty.", path);
            return file;
        }

        internal static FileInfo Plan(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) throw new ArgumentException("Absolute output file path required.");
            var file = new FileInfo(path);
            if (file.Exists || Directory.Exists(file.FullName)) throw new IOException("Output already exists; overwrite refused.");
            if (file.Directory?.Exists != true) throw new DirectoryNotFoundException("Output parent must already exist.");
            return file;
        }

        internal static void RequireExportTarget(string path)
        {
            if (!Path.GetExtension(path).Equals(".aml", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("exportPath must end with .aml.", "exportPath");
            Plan(path);
        }

        internal static Dictionary<string, object?> Verify(FileInfo file)
        {
            file.Refresh(); if (!file.Exists || file.Length == 0) throw new IOException("Native output missing or empty: " + file.FullName);
            using var stream = file.OpenRead();
            using var sha = SHA256.Create();
            return new Dictionary<string, object?> { ["path"] = file.FullName, ["bytes"] = file.Length,
                ["sha256"] = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(),
                ["contentSemanticsVerified"] = false };
        }
    }
}
