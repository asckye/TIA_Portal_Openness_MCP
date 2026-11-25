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
            Func<string, string, bool>? exportType = null, Action<string>? exportNone = null,
            Func<string[]?>? readRefusalGroupNames = null)
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
            var restored = new List<XElement>();
            var planned = Candidate(() => PlcDocumentEditing.PrepareImport(before, candidateXml, preserved, restored));
            var verificationPlan = PlcDocumentEditing.Parse(planned);
            var expected = PlcDocumentEditing.Canonical(verificationPlan, verificationPlan);
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
            try { import(plannedPath); }
            catch (Exception error) when (error.Data["verifiedInventoryInterrupted"] is true)
            {
                meta["phase"] = "readGroupNames"; meta["mayHaveChanged"] = false; meta.Remove("cleanupRequired");
                throw;
            }
            catch (Exception error) when (error.Data["nativeImportThrew"] is true && error.Data["verifiedGroupNamesBeforeImport"] is string[] beforeNames
                && readRefusalGroupNames != null
                && NativeRefusalProbe.AllowsRefusalProof(error) && TiaOpenness.Shared.ProcessLossPolicy.Reason(error) == null)
            {
                if (RefusalVerified(before, folder, meta, verifyBinding, beforeNames, readRefusalGroupNames!, export))
                {
                    meta["mayHaveChanged"] = false; meta["apiCallSuccess"] = false;
                    meta.Remove("cleanupRequired");
                    meta["refusalProof"] = "The same session read back the full target block including dates and the target group block names "
                        + "under the original exclusive binding. "
                        + "Other objects, UDTs and libraries were outside the comparison.";
                    error.Data["foundationKnownNoMutation"] = true;
                }
                throw;
            }
            meta["writeReturned"] = true; meta["importReturned"] = true;
            if (compile != null)
            {
                meta["phase"] = "compile";
                verifyBinding();
                try { compile(); }
                catch (PlcSoftwareException error) when (error.Code == "CompileErrors")
                {
                    meta["compileReturned"] = true; meta["status"] = error.Code; meta["dataComplete"] = false;
                    return error.Message;
                }
                meta["compileReturned"] = true;
            }
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
            var actual = PlcDocumentEditing.Canonical(readback, verificationPlan);
            string? difference = null;
            bool removedExpansion = false;
            if (compile != null) actual = PlcDocumentEditing.NormalizeCompiledReadback(actual, expected, declarations, out difference, out removedExpansion);
            bool fullMatch = expected == actual;
            bool matches = fullMatch;
            string? noneMember = null;
            meta["afterPath"] = afterPath; meta["actualFingerprint"] = PlcDocumentEditing.HashText(actual);
            var comparison = new JsonObject { ["method"] = "plannedWithDefaults" };
            meta["comparison"] = comparison;
            if ((!fullMatch || removedExpansion) && exportNone != null)
            {
                comparison["method"] = "candidateWithNone";
                var reason = CompareNone(Path.Combine(folder, "after-none.xml"), candidateXml, readback, restored, preserved, verifyBinding, exportNone,
                    fullMatch, comparison, out noneMember);
                matches = reason == null;
                if (reason != null) comparison["reason"] = reason;
            }
            meta["verificationSuccess"] = matches; meta["operationSuccess"] = matches;
            meta["status"] = matches ? "DocumentReadbackMatched" : "DocumentReadbackMismatch";
            if (matches) meta.Remove("cleanupRequired");
            else if (fullMatch) meta["readbackDifference"] = comparison["reason"]!.DeepClone();
            else if (difference != null) meta["readbackDifference"] = noneMember ?? difference;
            else
            {
                var full = PlcDocumentEditing.ReadbackDetail(expected, actual);
                meta["readbackDifference"] = full.ExtraInUdt && noneMember != null ? noneMember : full.Label;
            }
            meta["phase"] = "complete";
            return matches
                ? "Imported and re-exported document matches after identifier normalization. Inspect compileRequested/compileReturned for compilation; " +
                "project was not saved."
                : "Import returned, but document readback differs. Original backup and both documents retained. No automatic repair or rollback was attempted.";
        }

        private static bool RefusalVerified(string before, string folder, JsonObject meta, Action verifyBinding, string[] beforeNames,
            Func<string[]?> readNames, Action<string> export)
        {
            try
            {
                verifyBinding();
                var names = readNames();
                if (names == null || beforeNames.Any(string.IsNullOrEmpty) || names.Any(string.IsNullOrEmpty)
                    || !beforeNames.OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(names.OrderBy(n => n, StringComparer.Ordinal)))
                    return false;
                var path = Path.Combine(folder, "after-refusal.xml");
                export(path);
                meta["refusalReadbackPath"] = path;
                return XNode.DeepEquals(PlcDocumentEditing.RawBlock(before), PlcDocumentEditing.RawBlock(PlcDocumentEditing.Read(path)));
            }
            catch (Exception probe)
            {
                if (NativeRefusalProbe.UncertainConnection(probe) || TiaOpenness.Shared.ProcessLossPolicy.Reason(probe) != null)
                    meta["connectionUnavailable"] = true;
                return false;
            }
        }

        private static string? CompareNone(string path, string candidateXml, XDocument readback, List<XElement> restored, JsonArray preserved,
            Action verifyBinding, Action<string> exportNone, bool verifyUnexpanded, JsonObject comparison, out string? noneMember)
        {
            noneMember = null;
            try
            {
                verifyBinding();
                exportNone(path);
                var candidate = PlcDocumentEditing.Parse(candidateXml);
                var none = PlcDocumentEditing.Parse(PlcDocumentEditing.Read(path));
                string expected = PlcDocumentEditing.Canonical(candidate);
                string actual = PlcDocumentEditing.Canonical(none, candidate, restored);
                if (expected != actual)
                {
                    var detail = PlcDocumentEditing.ReadbackDetail(expected, actual);
                    comparison["noneDifference"] = detail.Label;
                    var input = PlcDocumentEditing.Child(PlcDocumentEditing.Block(candidate), "AttributeList");
                    if (detail.NamesMember && !PlcDocumentEditing.Child(input, "Interface").Descendants().Where(PlcDocumentEditing.IsInterfaceMember)
                        .Any(m => m.Elements().Any(e => e.Name.LocalName == "AttributeList"))) noneMember = detail.Label;
                    if (!verifyUnexpanded) return "Candidate differs from after-none.xml.";
                }
                if (verifyUnexpanded)
                {
                    if (PlcDocumentEditing.Block(candidate).Name != PlcDocumentEditing.Block(none).Name
                        || PlcDocumentEditing.Name(candidate) != PlcDocumentEditing.Name(none)) return "Readbacks identify different blocks.";
                    // The full document already matched. None must show that each omitted instance expansion was reset.
                    return PlcDocumentEditing.UnexpandedReadbackDifference(candidate, none);
                }
                if (PlcDocumentEditing.Block(candidate).Name != PlcDocumentEditing.Block(readback).Name
                    || PlcDocumentEditing.Name(candidate) != PlcDocumentEditing.Name(readback)) return "Readbacks identify different blocks.";
                return FullReadbackDifference(candidate, readback, restored, preserved);
            }
            catch (Exception error) when (error is not OperationCanceledException && !HmiReadPolicy.ConnectionUnavailable(error)
                && TiaOpenness.Shared.ProcessLossPolicy.Reason(error) == null)
            {
                // The import returned, but the required supplemental readback could not verify it.
                return "Same-style verification failed: " + NativeFailureText.Compose(error.Message, null, Array.Empty<string>());
            }
        }

        private static string? FullReadbackDifference(XDocument candidate, XDocument readback, List<XElement> restored, JsonArray preserved)
        {
            var find = PlcDocumentEditing.IndexReadbackAttributes(readback);
            if (restored.Count > 0)
            {
                var planned = PlcDocumentEditing.Child(PlcDocumentEditing.Block(restored[0].Document!), "AttributeList");
                for (int i = 0; i < restored.Count; i++)
                {
                    var item = restored[i];
                    if (item.Parent == planned && PlcDocumentEditing.IgnoreInBlockComparison(item)) continue;
                    var actual = find(item, planned);
                    if (actual == null || (item.HasElements || actual.HasElements
                        ? !XNode.DeepEquals(PlcDocumentEditing.NormalizeEmptyElements(item), PlcDocumentEditing.NormalizeEmptyElements(actual))
                        : item.Value != actual.Value))
                        return "Restored attribute differs in after.xml: " + (string?)preserved[i];
                }
            }
            var input = PlcDocumentEditing.Child(PlcDocumentEditing.Block(candidate), "AttributeList");
            foreach (var member in PlcDocumentEditing.Child(input, "Interface").Descendants().Where(PlcDocumentEditing.IsInterfaceMember))
            {
                foreach (var (name, _) in PlcDocumentEditing.MemberExportDefaults)
                    if (member.Attribute(name) is XAttribute stated && (string?)find(member, input)?.Attribute(name) != stated.Value)
                        return "Explicit member attribute differs in after.xml: " + (string?)member.Attribute("Name") + "/" + name;
            }
            return null;
        }
    }
}
