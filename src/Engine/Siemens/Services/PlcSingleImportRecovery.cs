using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using ModelContextProtocol.Protocol;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Plc;
using TiaMcp.Logic.V4;
using TiaMcp.PlcFoundation;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens.Services
{
    internal static class PlcSingleImportRecovery
    {
        internal static CallToolResult Run(IEngineeringSession session, string tool, string softwarePath, string groupPath, string path, string kind, Func<CallToolResult> import)
        {
            string? recovery = null; bool entered = false; string? warning = null;
            var files = new JsonObject();
            try
            {
                var input = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(path));
                TiaOpenness.Shared.NativeInputPolicy.RequireReadable(input.FullName, "importPath");
                TiaOpenness.Shared.NativeInputPolicy.Read("importPath", () =>
                {
                    for (FileSystemInfo? ancestor = input; ancestor != null; ancestor = ancestor is DirectoryInfo directory ? directory.Parent : ((FileInfo)ancestor).Directory)
                        if ((ancestor.Attributes & FileAttributes.ReparsePoint) != 0) throw new AdapterPreconditionException("Import input ancestry contains a reparse point.", "importPath");
                    return true;
                });
                using var locked = TiaOpenness.Shared.NativeInputPolicy.OpenRead(input.FullName, "importPath");
                var document = TiaOpenness.Shared.NativeInputPolicy.Read("importPath", () =>
                {
                    using var reader = XmlReader.Create(locked, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }, input.FullName);
                    try { return XDocument.Load(reader); }
                    catch (XmlException error) { throw new AdapterPreconditionException("Import input must be valid Document XML.", "importPath", true, error); }
                });
                if (document.Root?.Name != XName.Get("Document")) throw new AdapterPreconditionException("Import XML requires the Openness Document root.", "importPath");
                string prefix = kind == "UDT" ? "SW.Types." : kind == "TagTable" ? "SW.Tags." : "SW.Blocks.";
                var names = document.Root!.Elements().Where(x => x.Name.LocalName.StartsWith(prefix, StringComparison.Ordinal))
                    .SelectMany(x => x.Elements("AttributeList").Elements("Name")).Select(x => x.Value).ToArray();
                var software = session.ExactPlcForEngineering(softwarePath, true);
                void Check() { session.VerifyBinding(tool); if (!object.Equals(software, session.ExactPlcForEngineering(softwarePath, true))) throw new AdapterPreconditionException("PLC binding changed.", "softwarePath", false); }
                var adapter = new PlcImportAdapter(McpServer.ReleaseKey, () => throw new NotSupportedException(), () => software, Check, true);
                IEnumerable<TiaOpenness.Shared.NativeExportPolicy.RecoveryTarget> Backups()
                {
                    foreach (var target in adapter.ReadRecoveryTargets(kind, groupPath, names))
                        yield return new TiaOpenness.Shared.NativeExportPolicy.RecoveryTarget { Object = target.GroupPath + "/" + target.Name,
                            Blocker = adapter.RecoveryBlocker(target), Export = file => { Check(); adapter.ExportRecovery(target, file); } };
                }
                var saved = TiaOpenness.Shared.NativeExportPolicy.SingleImportRecovery(Backups(),
                    PlcBatchImportRunner.SingleImportRecoveryDirectory, TiaOpenness.Shared.DataLocations.Current.RecoveryAttemptedPath);
                recovery = saved.Directory; warning = saved.Warning;
                foreach (var file in saved.Files) files[file.Key] = file.Value;
                Check(); entered = true;
                return Evidence(import(), recovery, files, warning);
            }
            catch (AdapterPreconditionException ex) when (!entered)
            { return Evidence(McpServer.TargetFailure(tool, ex, false), recovery ?? ex.Data["recoveryDirectory"] as string, ex.Data["recoveryFiles"] is Dictionary<string, string> retained ? System.Text.Json.JsonSerializer.SerializeToNode(retained)!.AsObject() : files, warning); }
            catch (Exception ex)
            { return Evidence(McpServer.TargetFailure(tool, ex, entered), recovery, files, warning); }
        }
        private static CallToolResult Evidence(CallToolResult result, string? directory, JsonObject files, string? warning = null)
        {
            if (directory == null && warning == null) return result;
            var body = McpServer.ResultBody(result)!.DeepClone().AsObject();
            if (body["data"] == null) body["data"] = new JsonObject();
            body["data"]!["recoveryDirectory"] = directory; body["data"]!["recoveryFiles"] = files.DeepClone();
            body["data"]!["recoveryStatus"] = warning != null ? "backup-skipped" : (string?)body["error"]?["code"] == "PRECONDITION_FAILED" ? "backup-failed-before-import" : "backup-ready";
            if (warning != null) body["meta"]!["warnings"]!.AsArray().Add(new JsonObject { ["code"] = "BACKUP_SKIPPED", ["message"] = warning, ["details"] = new JsonObject() });
            if ((string?)body["error"]?["code"] is "OUTCOME_UNKNOWN" or "NATIVE_OPERATION_FAILED" && body["error"]?["details"] is JsonObject details)
            {
                if (details["evidence"] == null) details["evidence"] = new JsonObject();
                details["evidence"]!["recoveryDirectory"] = directory; details["evidence"]!["recoveryFiles"] = files.DeepClone();
            }
            return new CallToolResult { IsError = result.IsError, StructuredContent = body, Content = new[] { new TextContentBlock { Text = body.ToJsonString() } } };
        }
    }
}
