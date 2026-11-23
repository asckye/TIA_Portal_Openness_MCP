using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;

namespace TiaMcpServer.ModelContextProtocol
{
    // Injectable boundary permits failure-order tests without loading Openness.
    public static class PlcVerifiedImport
    {
        private static readonly string Session = Guid.NewGuid().ToString("N");

        public static object? PlainValue(JsonNode? node)
        {
            if (node is JsonObject obj) return obj.ToDictionary(pair => pair.Key, pair => PlainValue(pair.Value), StringComparer.Ordinal);
            if (node is JsonArray array) return array.Select(PlainValue).ToArray();
            if (node is not JsonValue) return null;
            var value = JsonSerializer.SerializeToElement(node);
            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
            if (value.ValueKind == JsonValueKind.Number)
            {
                if (value.TryGetInt64(out var integer)) return integer;
                return value.GetDouble();
            }
            return value.GetString();
        }

        public static string CandidateName(string xml) => Candidate(() => PlcDocumentEditing.Name(PlcDocumentEditing.ParseBlockImport(xml)));

        private static T Candidate<T>(Func<T> read)
        {
            try { return read(); }
            catch (Exception error) when (error is ArgumentException or System.Xml.XmlException or InvalidOperationException)
            { throw new AdapterPreconditionException(error.Message, "importPath"); }
        }

        public static string Execute(string candidateXml, string binding, string evidenceRoot, bool dryRun, string expectedToken,
            Action<string> export, Action<string> import, Action verifyBinding, JsonObject meta, Action? compile = null,
            Func<string, string, bool>? exportType = null)
        {
            meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["operationSuccess"] = false;
            meta["verificationSuccess"] = false; meta["automaticRollback"] = false; meta["compileRequested"] = compile != null;
            meta["phase"] = "plan";
            CandidateName(candidateXml);
            if (!dryRun && string.IsNullOrWhiteSpace(expectedToken))
                throw new AdapterPreconditionException("Preview previewToken is required.", "previewToken");
            if (!Path.IsPathRooted(evidenceRoot)) throw new ArgumentException("Absolute evidence directory required.");
            var root = new DirectoryInfo(Path.GetFullPath(evidenceRoot));
            for (var p = root; p != null; p = p.Parent)
                if (p.Exists && (p.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Reparse evidence paths refused.");
            var folder = Path.Combine(root.FullName, "plc-import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            meta["evidenceDirectory"] = folder;
            var beforePath = Path.Combine(folder, "before.xml");
            var plannedPath = Path.Combine(folder, "planned.xml");
            var afterPath = Path.Combine(folder, "after.xml");
            meta["phase"] = "exportBefore"; verifyBinding();
            try { export(beforePath); }
            catch (PlcSoftwareException error) when (error.Code == "BlockInconsistent")
            { throw AdapterPreconditionException.Failed(error.Message, "block-inconsistent", "blockPath"); }
            meta["phase"] = "plan";
            var before = PlcDocumentEditing.Read(beforePath);
            meta["backup"] = new JsonObject { ["path"] = beforePath, ["sha256"] = PlcDocumentEditing.Hash(File.ReadAllBytes(beforePath)) };
            var preserved = new JsonArray();
            var planned = Candidate(() => PlcDocumentEditing.PrepareImport(before, candidateXml, preserved));
            var expected = PlcDocumentEditing.Canonical(PlcDocumentEditing.Parse(planned));
            var tokenData = new JsonObject { ["session"] = Session, ["binding"] = binding, ["compileRequested"] = compile != null,
                ["before"] = PlcDocumentEditing.Canonical(PlcDocumentEditing.Parse(before)), ["planned"] = expected };
            var declarations = new Dictionary<string, XElement>(StringComparer.Ordinal);
            if (compile != null && exportType != null)
            {
                var typeHashes = new JsonObject();
                foreach (string name in PlcDocumentEditing.UnexpandedTypes(expected))
                {
                    string path = Path.Combine(folder, $"type-{typeHashes.Count}.xml");
                    var section = exportType(name, path) ? PlcDocumentEditing.TypeDeclaration(PlcDocumentEditing.Read(path), name) : null;
                    if (section != null) declarations.Add(name, section);
                    typeHashes[name] = section == null ? null : PlcDocumentEditing.DeclarationFingerprint(section);
                }
                if (typeHashes.Count > 0) tokenData["typeDeclarations"] = typeHashes;
            }
            var token = PlcDocumentEditing.HashText(tokenData.ToJsonString());
            meta["token"] = token; meta["preservedOmittedBlockAttributes"] = preserved;
            meta["plannedFingerprint"] = PlcDocumentEditing.HashText(expected);
            using (var file = new FileStream(plannedPath, FileMode.CreateNew, FileAccess.Write))
            {
                var bytes = new UTF8Encoding(true).GetPreamble(); file.Write(bytes, 0, bytes.Length);
                bytes = Encoding.UTF8.GetBytes(planned); file.Write(bytes, 0, bytes.Length); file.Flush(true);
            }
            meta["plannedPath"] = plannedPath;
            meta["scope"] =
                "Existing non-library root/user-group PLC block. Full replacement; missing scalar block attributes are preserved, but missing interface " +
                "members/networks are intentional deletions. Compile only when explicitly requested. No save, download, automatic retry or rollback.";
            if (dryRun) {
                meta["phase"] = "preview"; meta["operationSuccess"] = true;
                return "Preview and original backup retained. Inspect planned.xml; apply the same candidate and token to overwrite.";
            }
            meta["phase"] = "tokenCheck";
            if (!string.Equals(token, expectedToken, StringComparison.Ordinal))
            {
                meta["tokenInputs"] = new JsonObject {
                    ["binding"] = PlcDocumentEditing.HashText(binding), ["before"] = PlcDocumentEditing.HashText((string)tokenData["before"]!),
                    ["planned"] = meta["plannedFingerprint"]!.DeepClone() };
                throw AdapterPreconditionException.Failed(
                    "The binding, current block, candidate, or referenced UDT declaration differs from the preview. No import attempted.",
                    "preview-stale", "previewToken");
            }
            verifyBinding();
            meta["phase"] = "import"; meta["mayHaveChanged"] = true;
            string target = (string?)meta["blockPath"] ?? CandidateName(planned);
            if (meta["softwarePath"] is JsonValue software && software.TryGetValue<string>(out var pathPrefix) && !string.IsNullOrWhiteSpace(pathPrefix))
                target = pathPrefix + "/" + target;
            else if (meta["blockPath"] == null && binding.IndexOf('/') > 0 && !binding.Any(char.IsControl)
                && binding.EndsWith("/" + target, StringComparison.Ordinal))
                target = binding;
            meta["cleanupRequired"] = new JsonArray(new JsonObject { ["target"] = target, ["restoreFrom"] = beforePath,
                ["action"] =
                    "Compare the exact block with the retained original backup and restore it manually if needed; do not replay an unconfirmed import." });
            import(plannedPath); // An exception exits immediately: no second native call or automatic retry.
            meta["writeReturned"] = true; meta["importReturned"] = true;
            if (compile != null) { meta["phase"] = "compile"; verifyBinding(); compile(); meta["compileReturned"] = true; }
            meta["phase"] = "exportReadback";
            verifyBinding();
            try { export(afterPath); }
            catch (PlcSoftwareException error) when (compile == null && error.Code == "BlockInconsistent")
            {
                meta["readbackFailed"] = true; meta["dataComplete"] = false; meta["operationSuccess"] = true;
                meta["nativeMessage"] = NativeFailureText.Compose(error.Message, null, Array.Empty<string>());
                return "Block imported, content unverifiable until compiled. Original backup retained.";
            }
            var readback = PlcDocumentEditing.Parse(PlcDocumentEditing.Read(afterPath));
            var actual = PlcDocumentEditing.Canonical(readback);
            string? difference = null;
            if (compile != null) actual = PlcDocumentEditing.NormalizeCompiledReadback(actual, expected, declarations, out difference);
            var matches = expected == actual;
            meta["afterPath"] = afterPath; meta["actualFingerprint"] = PlcDocumentEditing.HashText(actual);
            meta["verificationSuccess"] = matches; meta["operationSuccess"] = matches;
            meta["status"] = matches ? "DocumentReadbackMatched" : "DocumentReadbackMismatch";
            if (matches) meta.Remove("cleanupRequired");
            else meta["readbackDifference"] = difference ?? PlcDocumentEditing.ReadbackDifference(expected, actual);
            meta["phase"] = "complete";
            return matches
                ? "Imported and re-exported document matches after identifier normalization. Inspect compileRequested/compileReturned for compilation; " +
                "project was not saved."
                : "Import returned, but document readback differs. Original backup and both documents retained. No automatic repair or rollback was attempted.";
        }
    }
}
