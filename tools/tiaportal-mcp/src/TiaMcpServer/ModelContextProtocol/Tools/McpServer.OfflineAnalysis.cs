using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        private static string ResolveCompareSide(string side, string filePath, string blockPath, string softwarePath, JsonObject meta, out string? tempDir)
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
            var export = Portal.ExportBlockDocumentForAnalysis(softwarePath, blockPath);
            tempDir = export.TempDir;
            meta[side + "Source"] = new JsonObject { ["mode"] = "block", ["softwarePath"] = softwarePath, ["blockPath"] = blockPath, ["tempExportDeleted"] = true };
            return export.XmlPath;
        }

        private static void DeleteAnalysisTempDir(string? tempDir)
        {
            if (string.IsNullOrWhiteSpace(tempDir)) return;
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { /* swallow(cleanup): Failure to delete an analysis export must not replace the comparison result or its original error. */ /* best-effort cleanup of our own temp export */ }
        }

        // File-only tools never need a project; mirrors RunHmiStepTool's meta/status contract without touching Portal.
        private static ResponseMessage RunOfflineAnalysisTool(string toolName, Func<JsonObject, string> action)
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
                meta["error"] = cause.ToString();
                ResponseMeta.Failed(meta);
                meta["status"] = cause is PortalException pex ? pex.Code.ToString()
                    : cause is ArgumentException || cause is FileNotFoundException || cause is DirectoryNotFoundException || cause is JsonException ? "InvalidParams"
                    : "ReadOrWriteFailed";
                return new ResponseMessage { Message = toolName + " failed: " + cause.Message, Meta = meta };
            }
        }
    }
}
