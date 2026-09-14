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
                JsonObject? error = null; JsonObject? exportStatus = null;
                try
                {
                    if (!libraryVersion)
                    {
                        var method = target.GetType().GetMethod("Export", new[] { typeof(DirectoryInfo), typeof(string) })
                            ?? throw new NotSupportedException("Official Export(DirectoryInfo, string) is unavailable.");
                        // Some export implementations enumerate lazily; consume the result.
                        var files = method.Invoke(target, new object[] { directory, "Module" });
                        if (files is IEnumerable enumerable) foreach (var unused in enumerable) { }
                    }
                    else
                    {
                        var method = target.GetType().GetMethods().SingleOrDefault(m => m.Name == "ExportAsDocuments" && m.GetParameters().Length == 4)
                            ?? throw new NotSupportedException("Official ExportAsDocuments is unavailable for this version.");
                        var result = method.Invoke(target, new object[] { directory, "Type", "WinCCML", Enum.Parse(method.GetParameters()[3].ParameterType, "None") });
                        string state = result == null ? "Unknown" : MigrationRead.Get(result, "TransferResultState")?.ToString() ?? "Unknown";
                        exportStatus = new JsonObject { ["path"] = scope, ["kind"] = "nativeExportStatus", ["status"] = state == "Success" ? "ok" : "failed", ["nativeState"] = state,
                            ["reason"] = state == "Success" ? null : "Native export did not report unconditional success. Returned files may be incomplete." };
                    }
                }
                catch (Exception ex) { error = MigrationRead.Failure(scope, "Unsupported", reason: "Native read/export failed; no type was edited or instantiated: " + MigrationRead.Cause(ex).Message); }
                if (error != null) { yield return error; yield break; }
                if (exportStatus != null) yield return exportStatus;
                var exported = directory.GetFiles("*", SearchOption.AllDirectories).OrderBy(f => f.FullName, StringComparer.Ordinal).ToArray();
                if (exported.Length == 0) { yield return MigrationRead.Failure(scope, "NativeExportEmpty"); yield break; }
                int scripts = 0;
                foreach (var file in exported)
                {
                    var path = scope + "/native/" + file.FullName.Substring(directory.FullName.Length + 1).Replace('\\', '/');
                    string? raw = null; error = null;
                    try
                    {
                        if (file.Length > 1024 * 1024) throw new NotSupportedException("Native file exceeds 1 MiB per-file limit; original export cannot fit the bounded response store with AST evidence. Explicit gap, not a truncated success.");
                        using var reader = new StreamReader(file.FullName, new UTF8Encoding(false, true), true); raw = reader.ReadToEnd();
                    }
                    catch (Exception ex) { error = MigrationRead.Failure(path, "NativeFileReadFailed", ex); }
                    if (error != null) { yield return error; continue; }
                    using var hash = SHA256.Create();
                    using var input = file.OpenRead();
                    yield return new JsonObject { ["path"] = path, ["kind"] = "nativeFile", ["status"] = "ok", ["byteLength"] = file.Length,
                        ["sha256"] = BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "").ToLowerInvariant(), ["rawText"] = raw, ["evidence"] = "Official native export of " + scope };
                    if (file.Extension.Equals(".js", StringComparison.OrdinalIgnoreCase)) { scripts++; yield return JavaScriptEvidence.Analyze(raw!, path); }
                    else foreach (var row in Document(raw!, path, file.Extension)) yield return row;
                }
                if (!libraryVersion && scripts == 0) yield return MigrationRead.Failure(scope, "ScriptBodyNotExported", reason: "Native export returned no .js file; module names or YAML alone are not body-read success.");
            }
            finally
            {
                // Only the unique directory created above, never a caller-provided path.
                try { directory.Delete(true); } catch { /* File cleanup cannot imply complete project data. */ }
            }
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
