using System;
using TiaMcp.Adapters.Contracts;
using System.IO;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    internal static class NativeFileOutput
    {
        internal static FileInfo Plan(string path, string parameter = "filePath")
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) throw new ArgumentException("Absolute output file path required.");
            var file = new FileInfo(path);
            if (file.Exists || Directory.Exists(file.FullName)) throw new IOException("Output already exists; overwrite refused.");
            if (file.Directory?.Exists != true) throw AdapterPreconditionException.OutputParentMissing(parameter);
            return file;
        }
        internal static void RecordFailure(JsonObject result, FileInfo output, string phase, Exception error, Func<string, FileAttributes>? attributes = null)
        {
            bool? exists;
            try { (attributes ?? File.GetAttributes)(output.FullName); exists = true; }
            catch (FileNotFoundException) /* swallow(env-probe): a missing output is the required post-failure observation */ { exists = false; }
            catch (DirectoryNotFoundException) /* swallow(env-probe): a missing parent proves the output is absent */ { exists = false; }
            catch (Exception probe) when (probe is IOException or UnauthorizedAccessException or System.Security.SecurityException
                or ArgumentException or NotSupportedException) { exists = null; }
            result["phase"] = phase; result["success"] = false; result["operationSuccess"] = false;
            result["nativeMessage"] = error.GetBaseException().Message; result["outputExists"] = exists;
            result["mayHaveChanged"] = exists != false; result["mayHaveWrittenFiles"] = exists != false;
            if (exists == null) result["writeOutcomeUnknown"] = true;
            if (exists == true) result["cleanupRequired"] = new JsonArray(new JsonObject { ["target"] = output.Name,
                ["action"] = "Inspect the output file. Remove it only if it is a confirmed incomplete result of this call." });
        }

        internal static JsonObject Verify(FileInfo file)
        {
            file.Refresh(); if (!file.Exists || file.Length == 0) throw new IOException("Native output missing or empty: " + file.FullName);
            using var stream = file.OpenRead();
            return new JsonObject { ["path"] = file.FullName, ["bytes"] = file.Length,
                ["sha256"] = ArgumentRules.Hash(stream), ["contentSemanticsVerified"] = false };
        }
    }
}
