using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    internal static class OfflineToolExecution
    {
        internal static FileInfo PlanOutput(string path, string parameter, string? invalidPathMessage = null)
        {
            try { return NativeFileOutput.Plan(path, parameter); }
            catch (Exception error) when (error is AdapterPreconditionException { Condition: "output-parent-missing" }
                || invalidPathMessage != null && error is (ArgumentException or IOException))
            {
                throw new AdapterPreconditionException(invalidPathMessage ?? $"Supply a new absolute {parameter} in an existing directory.", parameter);
            }
        }

        internal static string ResolveCompareSide(string side, string filePath, string blockPath, string softwarePath, JsonObject meta, out string? tempDir)
        {
            tempDir = null;
            var hasFile = !string.IsNullOrWhiteSpace(filePath);
            var hasBlock = !string.IsNullOrWhiteSpace(blockPath);
            if (hasFile == hasBlock) throw new ArgumentException("Exactly one of " + side + "FilePath or " + side + "BlockPath must be given.");
            if (hasFile)
            {
                if (!Path.IsPathRooted(filePath)) throw new ArgumentException(side + "FilePath must be absolute.");
                if (!File.Exists(filePath)) throw new FileNotFoundException(side + "FilePath not found: " + filePath, filePath);
                meta[side + "Source"] = new JsonObject { ["mode"] = "file", ["path"] = filePath };
                return filePath;
            }
            var export = HostToolServices.ExportBlockDocument(softwarePath, blockPath);
            tempDir = export.TempDir;
            meta[side + "Source"] = new JsonObject {
                ["mode"] = "block", ["softwarePath"] = softwarePath, ["blockPath"] = blockPath, ["tempExportDeleted"] = true };
            return export.XmlPath;
        }

        internal static void DeleteAnalysisTempDir(string? tempDir)
        {
            if (string.IsNullOrWhiteSpace(tempDir)) return;
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { /* swallow(cleanup): Failure to delete an analysis export must not replace the comparison result or its original error. */ /* best-effort cleanup of our own temp export */ }
        }

        internal static ResponseMessage RunOfflineAnalysisTool(string toolName, Func<JsonObject, string> action)
        {
            var meta = ResponseMeta.Step(toolName, ("offlineOnly", true));
            try
            {
                var message = action(meta);
                ResponseMeta.Complete(meta, true);
                return new ResponseMessage { Message = message, Meta = meta };
            }
            catch (Exception ex)
            {
                var cause = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                if (cause is TiaOpenness.Shared.BundleResourceUnavailableException) throw;
                if (cause is TiaOpenness.Shared.DataLocationIOException) throw;
                if (cause is AdapterPreconditionException { Condition: "output-parent-missing" } missing
                    && toolName == "ConvertEngineeringTable")
                {
                    ResponseMeta.Failed(meta);
                    meta["status"] = "PreconditionFailed"; meta["precondition"] = missing.Condition;
                    meta["parameter"] = missing.ParamName;
                    const string reason = "The parent directory for exportFile does not exist. Select an existing output directory.";
                    meta["refusal"] = reason;
                    return new ResponseMessage { Message = reason, Meta = meta };
                }
                if (cause is TiaMcp.Adapters.Contracts.AdapterPreconditionException) throw;
                meta["error"] = cause.ToString();
                if (cause is ArgumentException || cause is JsonException)
                    meta["parameter"] = TiaOpenness.Shared.HostFailurePolicy.Parameter(cause) ?? "arguments";
                ResponseMeta.Failed(meta);
                meta["status"] = cause is PortalException pex ? pex.Code.ToString()
                    : cause is ArgumentException || cause is FileNotFoundException || cause is DirectoryNotFoundException || cause is JsonException
                        ? "InvalidParams"
                    : "ReadOrWriteFailed";
                return new ResponseMessage { Message = toolName + " failed: " + cause.Message, Meta = meta };
            }
        }
    }
}
