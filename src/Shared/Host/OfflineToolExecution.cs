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

        internal static void WriteNewDocument(FileInfo output, string content, System.Text.Encoding encoding, JsonObject meta)
        {
            // CreateNew rejects an existing target even if another writer won the race after planning.
            using var stream = new FileStream(output.FullName, FileMode.CreateNew, FileAccess.Write);
            meta["filePath"] = output.FullName;
            meta["mayHaveWrittenFiles"] = true;
            using var writer = new StreamWriter(stream, encoding);
            writer.Write(content);
        }

        internal static void RecordUnconfirmedOutput(JsonObject meta, string path)
        {
            meta["writeOutcomeUnknown"] = true;
            RecordOutputCleanup(meta, path);
        }

        internal static void RecordOutputCleanup(JsonObject meta, string path)
        {
            meta["filePath"] = path;
            meta["cleanupRequired"] = new JsonArray(new JsonObject { ["target"] = path,
                ["action"] = "Inspect this output file for incomplete content before retrying." });
        }

        internal static T ReadDocument<T>(string? parameter, Func<T> read)
        {
            try { return read(); }
            catch (Exception error) when (parameter != null
                && error is IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidDataException)
            {
                string reason = error is System.Xml.XmlException ? "Supply a well-formed XML document." : "The supplied document could not be read.";
                throw new AdapterPreconditionException(reason, parameter);
            }
        }

        internal static void RequireDocumentFile(string path, string parameter)
        {
            if (!Path.IsPathRooted(path) || !File.Exists(path))
                throw new AdapterPreconditionException("Supply an existing absolute document file.", parameter);
        }

        internal static string ResolveCompareSide(string side, string filePath, string blockPath, string softwarePath, JsonObject meta, out string? tempDir)
        {
            tempDir = null;
            var hasFile = !string.IsNullOrWhiteSpace(filePath);
            var hasBlock = !string.IsNullOrWhiteSpace(blockPath);
            var parameter = side == "document" ? "filePath" : side + "FilePath";
            var blockParameter = side == "document" ? "blockPath" : side + "BlockPath";
            if (hasFile == hasBlock) throw new AdapterPreconditionException("Supply exactly one of " + parameter + " or " + blockParameter + ".");
            if (hasFile)
            {
                RequireDocumentFile(filePath, parameter);
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
                if ((bool?)meta["mayHaveWrittenFiles"] == true && (string?)meta["filePath"] is string output)
                {
                    RecordUnconfirmedOutput(meta, output);
                    var reason = TiaMcp.Logic.V4.HostBehavior.SafeDiagnostic(cause.Message) ?? cause.GetType().Name;
                    meta["nativeMessage"] = reason;
                    ResponseMeta.Failed(meta);
                    return new ResponseMessage { Message = "Inspect the output file before retrying. " + reason, Meta = meta };
                }
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
                if (cause is PlcOfflineReferences.AmbiguousTarget ambiguous)
                {
                    ResponseMeta.Failed(meta);
                    meta["status"] = "Ambiguous";
                    meta["target"] = ambiguous.Target;
                    meta["candidates"] = JsonSerializer.SerializeToNode(ambiguous.Candidates);
                    meta["mayHaveChanged"] = false;
                    meta["nativeMessage"] = ambiguous.Message;
                    return new ResponseMessage { Message = ambiguous.Message, Meta = meta };
                }
                if ((bool?)meta["mayHaveWrittenFiles"] == false)
                {
                    meta["mayHaveChanged"] = false;
                    if (cause is ArgumentException or InvalidOperationException or JsonException or System.Xml.XmlException
                        or IOException or UnauthorizedAccessException or InvalidDataException)
                    {
                        ResponseMeta.Failed(meta);
                        meta["status"] = "InvalidParams";
                        meta["parameter"] = TiaOpenness.Shared.HostFailurePolicy.Parameter(cause) ?? "arguments";
                        var reason = TiaMcp.Logic.V4.HostBehavior.SafeDiagnostic(cause.Message) ?? "Document input could not be validated.";
                        meta["nativeMessage"] = reason;
                        return new ResponseMessage { Message = toolName + " refused the document: " + reason, Meta = meta };
                    }
                }
                return AnalysisFailure(toolName, meta, cause);
            }
        }

        internal static ResponseMessage AnalysisFailure(string toolName, JsonObject meta, Exception cause)
        {
            if (toolName == "ComparePlcBlockDocuments"
                && TiaMcp.Logic.V4.HostBehavior.WorkerReplyEvidence(toolName, cause)?.TryGetValue("nativeMessage", out var reason) == true
                && reason.ValueKind == JsonValueKind.String)
                meta["nativeMessage"] = reason.GetString();
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
