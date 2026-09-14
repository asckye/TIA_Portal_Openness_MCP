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
            var module = MigrationRead.Named(MigrationRead.Get(hmi, "Scripts")!, moduleName);
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
                var diagnostics = new List<JsonObject>();
                var reported = new List<FileInfo>();
                bool script = !libraryVersion, callSuccess = false, exportSuccess = true;
                string? format = null;
                string state = "Unknown";
                try
                {
                    if (!libraryVersion)
                    {
                        var method = target.GetType().GetMethod("Export", new[] { typeof(DirectoryInfo), typeof(string) })
                            ?? throw new NotSupportedException("Official Export(DirectoryInfo, string) is unavailable.");
                        // Some export implementations enumerate lazily; consume the result.
                        var files = method.Invoke(target, new object[] { directory, "Module" });
                        callSuccess = true;
                        ReadExportFiles(files, reported);
                        state = "Returned";
                    }
                    else
                    {
                        var type = MigrationRead.Get(target, "TypeObject") ?? throw new NotSupportedException("The selected version has no TypeObject.");
                        script = type.GetType().FullName == "Siemens.Engineering.HmiUnified.Library.ScriptModuleType";
                        var query = type.GetType().GetMethod("GetSupportedExportFormats", Type.EmptyTypes)
                            ?? throw new NotSupportedException("Official LibraryType.GetSupportedExportFormats() is unavailable; no format was guessed.");
                        var formats = ReadFormats(query.Invoke(type, Array.Empty<object>()));
                        // Use the domain's first advertised native format, as in the
                        // V21 official example. Never assume Unified types use WinCCML.
                        format = formats.FirstOrDefault();
                        diagnostics.Add(new JsonObject { ["path"] = scope, ["kind"] = "nativeExportPlan", ["status"] = "ok",
                            ["type"] = type.GetType().FullName, ["versionType"] = target.GetType().FullName,
                            ["supportedFormats"] = new JsonArray(formats.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
                            ["selectedFormat"] = format, ["formatEvidence"] = "LibraryType.GetSupportedExportFormats()",
                            ["method"] = "LibraryTypeVersion.ExportAsDocuments(DirectoryInfo, string, string, LibraryExportOptions)",
                            ["libraryExportOptions"] = "None", ["baseFileName"] = "Type", ["outputDirectory"] = directory.FullName });
                        if (format == null) throw new NotSupportedException("The selected library type advertises no supported native export format. No type was edited or instantiated.");
                        var method = target.GetType().GetMethods().SingleOrDefault(m => m.Name == "ExportAsDocuments" && m.GetParameters().Length == 4
                            && m.GetParameters()[0].ParameterType == typeof(DirectoryInfo) && m.GetParameters()[1].ParameterType == typeof(string)
                            && m.GetParameters()[2].ParameterType == typeof(string) && m.GetParameters()[3].ParameterType.IsEnum)
                            ?? throw new NotSupportedException("Official ExportAsDocuments is unavailable for this version.");
                        var result = method.Invoke(target, new object[] { directory, "Type", format, Enum.Parse(method.GetParameters()[3].ParameterType, "None") });
                        callSuccess = true;
                        if (result == null) throw new InvalidOperationException("Native export returned no ExportTransferResult.");
                        // Consume the returned file enumeration before scanning the
                        // directory. Some official export enumerations are lazy.
                        try { ReadExportFiles(MigrationRead.Get(result, "ExportedDocuments"), reported); }
                        catch (Exception ex) { diagnostics.Add(MigrationRead.Failure(scope + "/ExportedDocuments", "NativeFileListFailed", ex)); }
                        try { state = MigrationRead.Get(result, "TransferResultState")?.ToString() ?? "Unknown"; }
                        catch (Exception ex) { diagnostics.Add(MigrationRead.Failure(scope + "/TransferResultState", "NativeStatusReadFailed", ex)); }
                        foreach (var row in MigrationRead.Property(result, "Messages", scope + "/ExportResult/Messages", ExportMessages))
                            diagnostics.Add(row);
                        exportSuccess = state == "Success";
                    }
                }
                catch (Exception ex) { error = MigrationRead.Failure(scope, MigrationRead.Cause(ex) is NotSupportedException ? "Unsupported" : "NativeExportFailed",
                    reason: "Native read/export failed; no type was edited or instantiated: " + MigrationRead.Cause(ex).Message); exportSuccess = false; }
                foreach (var row in diagnostics) yield return row;
                if (error != null) yield return error;
                yield return new JsonObject { ["path"] = scope, ["kind"] = "nativeExportStatus", ["status"] = exportSuccess && callSuccess ? "ok" : "failed",
                    ["apiCallSuccess"] = callSuccess, ["nativeState"] = state, ["selectedFormat"] = format,
                    ["reason"] = exportSuccess && callSuccess ? null : "Native export did not report unconditional success. Inspect ExportResult/Messages and the file inventory; API completion alone does not prove exported data." };
                var exported = new SortedDictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
                bool complete = exportSuccess && callSuccess && !diagnostics.Any(Failed) && error == null;
                // Inspect only this export directory; never search the project,
                // library or arbitrary paths from the API's returned manifest.
                var candidates = new List<FileInfo>(reported);
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
                    ["reportedCount"] = reported.Count, ["actualCount"] = exported.Count,
                    ["files"] = new JsonArray(exported.Values.Select(f => (JsonNode?)new JsonObject { ["relativePath"] = RelativeFile(directory, f), ["reportedByApi"] = reported.Any(r => string.Equals(r.FullName, f.FullName, StringComparison.OrdinalIgnoreCase)) }).ToArray()) };
                if (exported.Count == 0) { complete = false; yield return MigrationRead.Failure(scope, "NativeExportEmpty", reason: "No native file was found in the returned document list or the unique export directory and its subdirectories."); }
                int scripts = 0, filesRead = 0;
                foreach (var file in exported.Values)
                {
                    var path = scope + "/native/" + RelativeFile(directory, file);
                    string? raw = null; error = null;
                    try
                    {
                        if (file.Length > 1024 * 1024) throw new NotSupportedException("Native file exceeds 1 MiB per-file limit; original export cannot fit the bounded response store with AST evidence. Explicit gap, not a truncated success.");
                        using var reader = new StreamReader(file.FullName, new UTF8Encoding(false, true), true); raw = reader.ReadToEnd();
                    }
                    catch (Exception ex) { error = MigrationRead.Failure(path, "NativeFileReadFailed", ex); }
                    if (error != null) { complete = false; yield return error; continue; }
                    filesRead++;
                    using var hash = SHA256.Create();
                    using var input = file.OpenRead();
                    yield return new JsonObject { ["path"] = path, ["kind"] = "nativeFile", ["status"] = "ok", ["byteLength"] = file.Length,
                        ["sha256"] = BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "").ToLowerInvariant(), ["rawText"] = raw, ["evidence"] = "Official native export of " + scope };
                    if (file.Extension.Equals(".js", StringComparison.OrdinalIgnoreCase))
                    { scripts++; var row = JavaScriptEvidence.Analyze(raw!, path); if (Failed(row)) complete = false; yield return row; }
                    else foreach (var row in Document(raw!, path, file.Extension)) { if (Failed(row)) complete = false; yield return row; }
                }
                if (script && scripts == 0) { complete = false; yield return MigrationRead.Failure(scope, "ScriptBodyNotExported", reason: "Native export returned no readable .js file; module names or YAML alone are not body-read success."); }
                yield return new JsonObject { ["path"] = scope, ["kind"] = "nativeExportSummary", ["status"] = complete ? "ok" : "failed",
                    ["apiCallSuccess"] = callSuccess, ["dataComplete"] = complete, ["nativeState"] = state,
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
        private static IEnumerable<JsonObject> ExportMessages(object? messages, string path)
        {
            if (messages == null) { yield return MigrationRead.Failure(path, "NativeDiagnosticsMissing"); yield break; }
            int count = MigrationRead.Count(messages);
            yield return new JsonObject { ["path"] = path, ["kind"] = "collection", ["status"] = "ok", ["expectedCount"] = count };
            for (int i = 0; i < Math.Min(count, 1000); i++)
            {
                var message = MigrationRead.At(messages, i);
                foreach (var row in MigrationRead.Property(message, "Message", path + "/[" + i + "]/Message", (v, p) => MigrationRead.Graph(v, p))) yield return row;
            }
            if (count > 1000) yield return MigrationRead.Failure(path, "NativeDiagnosticLimit", reason: "Export diagnostics exceed 1000 messages; diagnostic evidence is incomplete.");
        }
        private static string[] ReadFormats(object? result)
        {
            if (result is not IEnumerable values) throw new NotSupportedException("Supported export formats are not enumerable.");
            var formats = new List<string>();
            foreach (var value in values)
            {
                if (formats.Count >= 32) throw new NotSupportedException("Supported format list exceeds 32; no unbounded format probing is performed.");
                if (value is not string format || string.IsNullOrWhiteSpace(format)) throw new NotSupportedException("The API returned an invalid export format identifier.");
                formats.Add(format);
            }
            return formats.ToArray();
        }
        private static void ReadExportFiles(object? result, List<FileInfo> files)
        {
            if (result is not IEnumerable values) throw new NotSupportedException("Native exported document list is not enumerable.");
            foreach (var value in values)
            {
                if (files.Count >= 4096) throw new NotSupportedException("Native document list exceeds 4096; explicit gap, not a truncated success.");
                if (value is not FileInfo file) throw new NotSupportedException("ExportedDocuments contains a value other than FileInfo.");
                files.Add(file);
            }
        }
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
