using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using YamlDotNet.RepresentationModel;

namespace TiaMcpServer.Siemens
{
    internal static class UnifiedNativeRead
    {
        internal static object ExactType(object projectLibrary, string typePath)
        {
            if (!typePath.StartsWith("/") || typePath.EndsWith("/")) throw new ArgumentException("typePath must be an absolute URI-escaped /Folder/Type path; no recursive library search.");
            var parts = typePath.Substring(1).Split('/'); object folder = MigrationRead.Get(projectLibrary, "TypeFolder")!;
            for (int j = 0; j < parts.Length - 1; j++) folder = MigrationRead.Named(MigrationRead.Get(folder, "Folders")!, Uri.UnescapeDataString(parts[j]));
            return MigrationRead.Named(MigrationRead.Get(folder, "Types")!, Uri.UnescapeDataString(parts[parts.Length - 1]));
        }
        internal static object ExactVersion(object type, string version)
        {
            if (string.IsNullOrWhiteSpace(version)) throw new ArgumentException("An exact version is required; the default version is never substituted.");
            return MigrationRead.Named(MigrationRead.Get(type, "Versions")!, version, "VersionNumber", 1000);
        }
        internal static IEnumerable<JsonObject> ListScripts(object hmi)
            => MigrationRead.Property(hmi, "Scripts", "/Scripts", (v, p) => MigrationRead.Collection(v!, p, ScriptIdentity));
        private static IEnumerable<JsonObject> ScriptIdentity(object module, string evidence)
        {
            var name = MigrationRead.Get(module, "Name")?.ToString();
            yield return new JsonObject { ["path"] = "/Scripts/" + MigrationRead.Segment(name ?? ""), ["kind"] = "scriptModule", ["status"] = "ok",
                ["name"] = name, ["type"] = module.GetType().FullName, ["evidence"] = evidence, ["bodyReadSuccess"] = false,
                ["note"] = "Inventory only. ReadUnifiedGlobalScript must successfully export original text to prove body acquisition." };
        }
        internal static IEnumerable<JsonObject> Script(object hmi, string moduleName)
        {
            var module = UnifiedScriptAccess.Module(hmi, moduleName);
            var path = "/Scripts/" + MigrationRead.Segment(moduleName);
            foreach (var row in ScriptIdentity(module, path)) yield return row;
            foreach (var row in Export(module, path, false)) yield return row;
        }
        internal static JsonObject InstanceLink(object item, string path)
        {
            var serviceType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Siemens.Engineering.Library.Types.LibraryTypeInstanceInfo", false)).FirstOrDefault(t => t != null);
            if (serviceType == null) return MigrationRead.Failure(path, "Unsupported", reason: "LibraryTypeInstanceInfo is not available in the loaded official API.");
            try
            {
                var provider = item.GetType().GetInterface("Siemens.Engineering.IEngineeringServiceProvider");
                var method = (provider?.GetMethods() ?? item.GetType().GetMethods()).FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                var info = method?.MakeGenericMethod(serviceType).Invoke(item, Array.Empty<object>());
                if (info == null) return MigrationRead.Failure(path, "Unsupported", reason: "This object does not expose a connected library version via LibraryTypeInstanceInfo. ContainedType alone is not proof of a version.");
                var version = MigrationRead.Get(info, "LibraryTypeVersion")!;
                var type = MigrationRead.Get(version, "TypeObject")!;
                return new JsonObject { ["path"] = path, ["kind"] = "libraryInstanceLink", ["status"] = "ok",
                    ["version"] = MigrationRead.Get(version, "VersionNumber")?.ToString(), ["versionGuid"] = MigrationRead.Get(version, "Guid")?.ToString(),
                    ["typeName"] = MigrationRead.Get(type, "Name")?.ToString(), ["typeGuid"] = MigrationRead.Get(type, "Guid")?.ToString(),
                    ["evidence"] = "IEngineeringServiceProvider.GetService<LibraryTypeInstanceInfo>().LibraryTypeVersion" };
            }
            catch (Exception ex) { return MigrationRead.Failure(path, "LibraryInstanceReadFailed", ex); }
        }
        internal static IEnumerable<JsonObject> LibraryType(object library, string typePath, string versionNumber)
        {
            var type = ExactType(library, typePath); var version = ExactVersion(type, versionNumber);
            var path = "/ProjectLibrary" + typePath + "/Versions/" + MigrationRead.Segment(versionNumber);
            yield return new JsonObject { ["path"] = path, ["kind"] = "libraryTypeVersion", ["status"] = "ok", ["typeName"] = MigrationRead.Get(type, "Name")?.ToString(),
                ["version"] = MigrationRead.Get(version, "VersionNumber")?.ToString(), ["versionGuid"] = MigrationRead.Get(version, "Guid")?.ToString(), ["type"] = type.GetType().FullName };
            foreach (var name in new[] { "State", "OriginalLibrary" })
                foreach (var row in MigrationRead.Property(version, name, path + "/" + name, (v, p) => MigrationRead.Graph(v, p))) yield return row;
            // Only identifiers of direct dependencies, never recurse into their types.
            foreach (var row in MigrationRead.Property(version, "Dependencies", path + "/Dependencies", (v, p) => Dependencies(v, p))) yield return row;
            foreach (var row in Export(version, path, true)) yield return row;
        }
        private static IEnumerable<JsonObject> Dependencies(object? value, string path)
        {
            if (value is not IEnumerable enumerable) { yield return MigrationRead.Failure(path, "Unsupported", reason: "Dependencies is not enumerable."); yield break; }
            int index = 0;
            foreach (var dependency in enumerable)
            {
                if (index >= 1000) { yield return MigrationRead.Failure(path, "DependencyLimit", reason: "Direct dependency list exceeds 1000; dependencies were not recursively followed."); yield break; }
                var p = path + "/[" + index++ + "]";
                foreach (var name in new[] { "Guid", "VersionNumber" })
                    foreach (var row in MigrationRead.Property(dependency, name, p + "/" + name, (v, q) => MigrationRead.Graph(v, q))) yield return row;
            }
        }
        internal static IEnumerable<JsonObject> Export(object target, string scope, bool libraryVersion)
        {
            var directory = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "tia-mcp-read-" + Guid.NewGuid().ToString("N")));
            directory.Create();
            try
            {
                JsonObject? error = null;
                var capture = NativeExportCapture.Read(target, scope, libraryVersion, directory);
                var reported = capture.Paths;
                bool script = capture.Script, callSuccess = capture.ApiCallSuccess;
                string state = capture.State;
                foreach (var row in capture.Rows) yield return row;
                yield return capture.Status();
                var exported = new SortedDictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
                bool complete = capture.NativeSuccess && callSuccess && capture.InspectionComplete;
                // Inspect only this export directory; never search the project,
                // library or arbitrary paths from the API's returned manifest.
                var candidates = new List<FileInfo>();
                foreach (var path in reported)
                {
                    error = null;
                    try { candidates.Add(new FileInfo(path)); }
                    catch (Exception ex) { error = MigrationRead.Failure(scope + "/ExportedDocuments", "NativeFileLocationRejected", ex); error["reportedPath"] = path; }
                    if (error != null) { complete = false; yield return error; }
                }
                error = null;
                try { ScanFiles(directory, candidates); }
                catch (Exception ex) { error = MigrationRead.Failure(scope, "NativeFileEnumerationFailed", ex); }
                if (error != null) { complete = false; if (error["code"]?.ToString() == "NativeFileEnumerationFailed") yield return error; }
                foreach (var file in candidates)
                {
                    error = null;
                    try { CheckExportFile(directory, file); exported[file.FullName] = file; }
                    catch (Exception ex) { error = MigrationRead.Failure(scope + "/ExportedDocuments", "NativeFileLocationRejected", ex); error["reportedPath"] = file.FullName; }
                    if (error != null) { complete = false; yield return error; }
                }
                yield return new JsonObject { ["path"] = scope, ["kind"] = "nativeFileInventory", ["status"] = "ok", ["outputDirectory"] = directory.FullName,
                    ["exportAttempted"] = capture.ExportAttempted, ["countScope"] = "local native files only; not a count of internal objects or tags",
                    ["reportedCount"] = reported.Count, ["actualCount"] = exported.Count,
                    ["files"] = new JsonArray(exported.Values.Select(f => (JsonNode?)new JsonObject { ["relativePath"] = RelativeFile(directory, f), ["reportedByApi"] = reported.Any(r => string.Equals(r, f.FullName, StringComparison.OrdinalIgnoreCase)) }).ToArray()) };
                if (exported.Count == 0) { complete = false; if (capture.ExportAttempted) yield return MigrationRead.Failure(scope, "NativeExportEmpty", reason: "Export was invoked but no native file was found. This is not evidence that the selected type has no internal objects or tags."); }
                int scripts = 0, filesRead = 0;
                foreach (var file in exported.Values)
                {
                    var path = scope + "/native/" + RelativeFile(directory, file);
                    string? raw = null, sha256 = null; long byteLength = 0; error = null;
                    try
                    {
                        if (file.Length > 1024 * 1024) throw new NotSupportedException("Native file exceeds 1 MiB per-file limit; original export cannot fit the bounded response store with AST evidence. Explicit gap, not a truncated success.");
                        using var input = file.OpenRead();
                        if (input.Length > 1024 * 1024) throw new NotSupportedException("Native file exceeds 1 MiB per-file limit.");
                        var bytes = new byte[(int)input.Length]; int offset = 0;
                        while (offset < bytes.Length) { int read = input.Read(bytes, offset, bytes.Length - offset); if (read == 0) throw new EndOfStreamException("Native file changed during acquisition."); offset += read; }
                        if (input.ReadByte() != -1) throw new IOException("Native file changed during acquisition.");
                        byteLength = bytes.Length;
                        using var hash = SHA256.Create(); sha256 = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                        using var reader = new StreamReader(new MemoryStream(bytes, false), new UTF8Encoding(false, true), true); raw = reader.ReadToEnd();
                    }
                    catch (Exception ex) { error = MigrationRead.Failure(path, "NativeFileReadFailed", ex); }
                    if (error != null) { complete = false; yield return error; continue; }
                    filesRead++;
                    yield return new JsonObject { ["path"] = path, ["kind"] = "nativeFile", ["status"] = "ok", ["byteLength"] = byteLength,
                        ["sha256"] = sha256, ["rawText"] = raw, ["evidence"] = "Official native export of " + scope };
                    if (file.Extension.Equals(".js", StringComparison.OrdinalIgnoreCase))
                    { scripts++; var row = JavaScriptEvidence.Analyze(raw!, path); if (Failed(row)) complete = false; yield return row; }
                    else foreach (var row in Document(raw!, path, file.Extension)) { if (Failed(row)) complete = false; yield return row; }
                }
                if (script && scripts == 0) { complete = false; yield return MigrationRead.Failure(scope, "ScriptBodyNotExported", reason: "Native export returned no readable .js file; module names or YAML alone are not body-read success."); }
                bool nativeFilesComplete = complete;
                if (capture.LibraryXml && filesRead > 0)
                {
                    complete = false;
                    yield return MigrationRead.Failure(scope, "LibraryXmlContentUnverified", reason: "The official library XML was read and retained. This action can export version information alone; XML presence or metadata is not proof of internal interfaces, objects, bindings, events or scripts. Review the raw XML evidence before treating internal migration coverage as complete.");
                }
                yield return new JsonObject { ["path"] = scope, ["kind"] = "nativeExportSummary", ["status"] = complete ? "ok" : "failed",
                    ["operationId"] = capture.OperationId, ["apiCallSuccess"] = callSuccess, ["dataComplete"] = complete, ["nativeState"] = state,
                    ["exportAttempted"] = capture.ExportAttempted, ["method"] = capture.Method, ["nativeFilesComplete"] = nativeFilesComplete,
                    ["internalContentStatus"] = capture.LibraryXml ? "unverified" : "seeNativeEvidence",
                    ["internalObjectCount"] = null, ["internalTagCount"] = null,
                    ["resultInspectionComplete"] = capture.InspectionComplete, ["failurePhase"] = capture.FailurePhase,
                    ["connectionUnavailable"] = capture.ConnectionUnavailable,
                    ["fileCount"] = exported.Count, ["filesRead"] = filesRead, ["scriptFileCount"] = scripts,
                    ["bodyReadSuccess"] = script ? JsonValue.Create(scripts > 0) : null,
                    ["completenessScope"] = "selected native export and its parsed evidence; not proof of transitive dependencies or a complete migration mapping" };
            }
            finally
            {
                // Only the unique directory created above, never a caller-provided path.
                try { directory.Delete(true); } catch { /* File cleanup cannot imply complete project data. */ }
            }
        }
        private static bool Failed(JsonObject row) => row["status"]?.ToString() == "failed" || row["status"]?.ToString() == "unsupported";
        private static string RelativeFile(DirectoryInfo root, FileInfo file) => file.FullName.Substring(root.FullName.Length + 1).Replace('\\', '/');
        private static void CheckExportFile(DirectoryInfo root, FileInfo file)
        {
            if (!file.FullName.StartsWith(root.FullName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Exported file is outside the unique export directory; it was not read.");
            if (!file.Exists) throw new FileNotFoundException("The API reported a native document that does not exist.", file.FullName);
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0) throw new NotSupportedException("Exported file is a reparse point.");
            for (var parent = file.Directory; parent != null && parent.FullName.Length >= root.FullName.Length; parent = parent.Parent)
                if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new NotSupportedException("Exported document directory is a reparse point.");
        }
        private static void ScanFiles(DirectoryInfo current, List<FileInfo> files, int depth = 0)
        {
            if (depth > 64 || (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new NotSupportedException("Native directory depth or reparse-point limit reached.");
            foreach (var file in current.EnumerateFiles())
            { if (files.Count >= 8192) throw new NotSupportedException("Native file scan limit reached; explicit gap."); files.Add(file); }
            foreach (var child in current.EnumerateDirectories()) ScanFiles(child, files, depth + 1);
        }
        internal static IEnumerable<JsonObject> Document(string text, string path, string extension)
        {
            object? doc = null; JsonObject? error = null;
            try
            {
                if (extension.Equals(".yml", StringComparison.OrdinalIgnoreCase) || extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase))
                { var stream = new YamlStream(); stream.Load(new StringReader(text)); doc = stream; }
                else if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
                { using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }); doc = XDocument.Load(reader, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace); }
                else error = MigrationRead.Failure(path, "Unsupported", reason: "Native file format is retained as raw text but has no structured reader: " + extension);
            }
            catch (Exception ex) { error = MigrationRead.Failure(path, "NativeParseFailed", ex); }
            if (error != null) { yield return error; yield break; }
            if (doc is YamlStream yaml) for (int i = 0; i < yaml.Documents.Count; i++)
                foreach (var row in Yaml(yaml.Documents[i].RootNode, path + "/documents/" + i, new HashSet<YamlNode>())) yield return row;
            if (doc is XDocument xml && xml.Root != null) foreach (var row in Xml(xml.Root, path + "/" + xml.Root.Name)) yield return row;
        }
        private static IEnumerable<JsonObject> Yaml(YamlNode node, string path, HashSet<YamlNode> ancestors)
        {
            if (ancestors.Count >= 128 || !ancestors.Add(node)) { yield return MigrationRead.Failure(path, "NativeCycleOrDepthLimit"); yield break; }
            try
            {
                if (node is YamlScalarNode scalar)
                { var row = MigrationRead.Scalar(path, scalar.Value, "nativeValue"); row["line"] = node.Start.Line; row["column"] = node.Start.Column; row["nativeType"] = node.Tag.ToString(); yield return row; }
                else if (node is YamlMappingNode map) foreach (var pair in map.Children)
                    foreach (var row in Yaml(pair.Value, path + "/" + MigrationRead.Segment(((YamlScalarNode)pair.Key).Value ?? ""), ancestors)) yield return row;
                else if (node is YamlSequenceNode seq) for (int i = 0; i < seq.Children.Count; i++)
                    foreach (var row in Yaml(seq.Children[i], path + "/[" + i + "]", ancestors)) yield return row;
            }
            finally { ancestors.Remove(node); }
        }
        private static IEnumerable<JsonObject> Xml(XElement element, string path, int depth = 0)
        {
            if (depth >= 128) { yield return MigrationRead.Failure(path, "NativeDepthLimit"); yield break; }
            foreach (var a in element.Attributes()) yield return MigrationRead.Scalar(path + "/@" + a.Name, a.Value, "nativeValue");
            if (!element.HasElements) yield return MigrationRead.Scalar(path, element.Value, "nativeValue");
            int i = 0; foreach (var child in element.Elements()) foreach (var row in Xml(child, path + "/" + child.Name + "[" + i++ + "]", depth + 1)) yield return row;
        }
    }
}
