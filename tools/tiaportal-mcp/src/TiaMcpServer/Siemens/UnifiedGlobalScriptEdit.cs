using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Esprima;
using YamlDotNet.RepresentationModel;

namespace TiaMcpServer.Siemens
{
    // Only a selected, already-existing module is exported/imported. Native YAML
    // is preserved byte-for-byte; unknown native shapes are refused, not guessed.
    internal static class UnifiedGlobalScriptEdit
    {
        private const int MaxBytes = 1024 * 1024;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        internal sealed class NativePair
        {
            internal string JsName = "", YamlName = "", Text = "";
            internal byte[] Js = Array.Empty<byte>(), Yaml = Array.Empty<byte>();
            internal Encoding Encoding = Utf8;
            internal bool Bom;
            internal byte[] Encode(string text) => (Bom ? Encoding.GetPreamble() : Array.Empty<byte>()).Concat(Encoding.GetBytes(text)).ToArray();
            internal JsonObject Evidence() => new JsonObject { ["scriptFile"] = JsName, ["yamlFile"] = YamlName,
                ["scriptSha256"] = Hash(Js), ["yamlSha256"] = Hash(Yaml), ["scriptCode"] = Text };
        }
        private static string Hash(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
        private static string Decode(byte[] bytes, out Encoding encoding, out bool bom)
        {
            encoding = Utf8; int skip = 0;
            if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf) { encoding = new UTF8Encoding(true, true); skip = 3; }
            else if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe) { encoding = new UnicodeEncoding(false, true, true); skip = 2; }
            else if (bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff) { encoding = new UnicodeEncoding(true, true, true); skip = 2; }
            bom = skip != 0;
            return encoding.GetString(bytes, skip, bytes.Length - skip);
        }
        private static YamlNode Child(YamlMappingNode node, string name)
        {
            if (!node.Children.TryGetValue(new YamlScalarNode(name), out var value)) throw new NotSupportedException("Native YAML missing " + name);
            return value;
        }
        private static void RequirePlainFile(string path, string directory)
        {
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), directory, StringComparison.OrdinalIgnoreCase)
                || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new NotSupportedException("Native export contains a file outside its isolated directory or a reparse point.");
            if (new FileInfo(path).Length > MaxBytes) throw new NotSupportedException("Native file exceeds the 1 MiB edit limit.");
        }
        internal static NativePair ReadPair(string directory, string moduleName)
        {
            directory = Path.GetFullPath(directory);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 || Directory.EnumerateDirectories(directory).Any())
                throw new NotSupportedException("Nested or redirected native export folders are not supported by the single-module editor.");
            var files = Directory.EnumerateFiles(directory).Take(3).ToArray();
            if (files.Length != 2) throw new NotSupportedException("Expected exactly one .hmi.yml and one .hmi.js; export is empty, incomplete or has extra files.");
            foreach (var file in files) RequirePlainFile(file, directory);
            var yamlPath = files.SingleOrDefault(f => f.EndsWith(".hmi.yml", StringComparison.OrdinalIgnoreCase))
                ?? throw new NotSupportedException("A native .hmi.yml manifest is required.");
            var yaml = File.ReadAllBytes(yamlPath);
            var stream = new YamlStream();
            using (var reader = new StringReader(Decode(yaml, out _, out _))) stream.Load(reader);
            if (stream.Documents.Count != 1 || !(stream.Documents[0].RootNode is YamlMappingNode root) || root.Children.Count != 1
                || !(Child(root, "ScriptModules") is YamlMappingNode modules) || modules.Children.Count != 1
                || !(Child(modules, moduleName) is YamlMappingNode module) || module.Children.Count != 1
                || !(Child(module, "ScriptFile") is YamlScalarNode script) || string.IsNullOrWhiteSpace(script.Value))
                throw new NotSupportedException("Only a single exact module with one ScriptFile is supported; metadata was not rewritten.");
            var jsName = script.Value!;
            if (jsName != Path.GetFileName(jsName) || jsName.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || !jsName.EndsWith(".hmi.js", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("ScriptFile must be a plain relative .hmi.js filename.");
            var jsPath = Path.Combine(directory, jsName);
            if (!files.Contains(jsPath, StringComparer.OrdinalIgnoreCase)) throw new NotSupportedException("Manifest ScriptFile was not exported.");
            var pair = new NativePair { JsName = jsName, YamlName = Path.GetFileName(yamlPath), Js = File.ReadAllBytes(jsPath), Yaml = yaml };
            pair.Text = Decode(pair.Js, out pair.Encoding, out pair.Bom);
            return pair;
        }
        private static NativePair? Export(Func<object> resolveHmi, string moduleName, string directory, string phase, JsonObject meta)
        {
            meta["phase"] = phase;
            var module = UnifiedScriptAccess.Module(resolveHmi(), moduleName);
            Directory.CreateDirectory(directory);
            var capture = NativeExportCapture.Read(module, "/Scripts/" + MigrationRead.Segment(moduleName), false, new DirectoryInfo(directory));
            meta[phase] = capture.Status();
            if (!capture.ApiCallSuccess || !capture.NativeSuccess || !capture.InspectionComplete)
            {
                meta["status"] = "NativeExportFailed";
                meta["failures"] = new JsonArray(capture.Rows.Select(r => (JsonNode)r.DeepClone()).ToArray());
                meta["connectionUnavailable"] = capture.ConnectionUnavailable;
                meta["remoteInspectionStopped"] = capture.RemoteInspectionStopped;
                return null;
            }
            // Validate both the returned paths and the on-disk pair. Never follow
            // an unexpected export path or rely solely on the native return state.
            if (capture.Paths.Count != 2) throw new NotSupportedException("Export must return both native files before a module can be updated.");
            foreach (var path in capture.Paths) RequirePlainFile(path, directory);
            var pair = ReadPair(directory, moduleName);
            if (!capture.Paths.Select(Path.GetFullPath).Contains(Path.Combine(directory, pair.JsName), StringComparer.OrdinalIgnoreCase)
                || !capture.Paths.Select(Path.GetFullPath).Contains(Path.Combine(directory, pair.YamlName), StringComparer.OrdinalIgnoreCase))
                throw new NotSupportedException("Native returned paths do not match the exported JS/YAML pair.");
            return pair;
        }
        internal static string Execute(Func<object> resolveHmi, string project, string softwarePath, string moduleName,
            string scriptCode, bool dryRun, string expectedToken, JsonObject meta)
        {
            meta["operationSuccess"] = false; meta["apiCallSuccess"] = false; meta["verificationSuccess"] = false;
            meta["importAttempted"] = false; meta["mayHaveChanged"] = false; meta["dryRun"] = dryRun;
            meta["expectedProject"] = project; meta["softwarePath"] = softwarePath;
            meta["moduleName"] = moduleName; meta["modulePath"] = "/Scripts/" + MigrationRead.Segment(moduleName);
            meta["persistence"] = "No project save, compile, download or page deletion is performed.";
            meta["navigationTargetsVerified"] = false;
            if (string.IsNullOrWhiteSpace(moduleName)) throw new ArgumentException("An exact moduleName is required.");
            if (string.IsNullOrWhiteSpace(scriptCode) || scriptCode.Length > MaxBytes || Utf8.GetByteCount(scriptCode) > MaxBytes)
                throw new ArgumentException("Supply the COMPLETE nonempty native .hmi.js text, at most 1 MiB; not just a function body.");
            meta["phase"] = "parseProposedText";
            new JavaScriptParser(new ParserOptions { Tolerant = false }).ParseModule(scriptCode);
            meta["syntaxValidation"] = "Esprima parse only; no script execution or TIA SyntaxCheck. Runtime references remain unverified.";
            if (!dryRun && string.IsNullOrWhiteSpace(expectedToken)) throw new InvalidOperationException("A preview expectedToken is required before import.");
            var work = Path.Combine(Path.GetTempPath(), "TiaMcpServer", "GlobalScriptEdits", Guid.NewGuid().ToString("N"));
            var backup = Path.Combine(work, "before");
            meta["backupDirectory"] = backup;
            meta["backupRetention"] = "Native files stay on the MCP machine for recovery; they may contain private project source. Remove manually when no longer needed.";
            var before = Export(resolveHmi, moduleName, backup, "exportBefore", meta);
            if (before == null) return "Module export failed; no import attempted. Inspect exportBefore and failures.";
            var candidateBytes = before.Encode(scriptCode);
            if (candidateBytes.Length > MaxBytes) throw new NotSupportedException("Encoded script exceeds the 1 MiB edit limit.");
            meta["before"] = before.Evidence();
            meta["after"] = new JsonObject { ["scriptCode"] = scriptCode, ["scriptSha256"] = Hash(candidateBytes), ["yamlSha256"] = Hash(before.Yaml) };
            var scope = new JsonObject { ["project"] = project, ["softwarePath"] = softwarePath, ["moduleName"] = moduleName,
                ["beforeJs"] = Hash(before.Js), ["beforeYaml"] = Hash(before.Yaml), ["afterJs"] = Hash(candidateBytes) };
            var token = HmiExactAccess.Token("UpdateUnifiedGlobalScript/v1", scope);
            meta["token"] = token; meta["apiCallSuccess"] = true;
            if (dryRun)
            {
                meta["status"] = "Preview"; meta["operationSuccess"] = true;
                return "Review the complete before/after text. No import performed; apply the SAME content with dryRun=false and this expectedToken.";
            }
            HmiExactAccess.RequireToken(expectedToken, token);
            if (before.Text == scriptCode)
            {
                meta["status"] = "Unchanged"; meta["operationSuccess"] = true; meta["verificationSuccess"] = true;
                return "Exact module already contains the requested text; no import performed.";
            }
            var candidate = Path.Combine(work, "candidate"); Directory.CreateDirectory(candidate);
            File.WriteAllBytes(Path.Combine(candidate, before.YamlName), before.Yaml);
            File.WriteAllBytes(Path.Combine(candidate, before.JsName), candidateBytes);
            ReadPair(candidate, moduleName);
            meta["candidateDirectory"] = candidate;
            // Re-resolve the composition instead of retaining a module/result proxy
            // from Export. Import only this isolated, validated one-module folder.
            var scripts = UnifiedScriptAccess.Scripts(resolveHmi());
            var import = scripts.GetType().GetMethod("Import", new[] { typeof(DirectoryInfo) });
            if (import == null || import.ReturnType != typeof(bool)) throw new NotSupportedException("Official Scripts.Import(DirectoryInfo) -> bool is unavailable.");
            meta["phase"] = "import"; meta["importMethod"] = "HmiSoftware.Scripts.Import(DirectoryInfo)";
            meta["apiCallSuccess"] = false; meta["importAttempted"] = true; meta["mayHaveChanged"] = true;
            bool returned = (bool)import.Invoke(scripts, new object[] { new DirectoryInfo(candidate) })!;
            meta["apiCallSuccess"] = true; meta["importReturned"] = returned;
            if (!returned)
            {
                meta["status"] = "ImportRejected";
                return "Native Import returned false. Partial changes cannot be excluded; inspect TIA and the backup. No retry, delete/recreate or rollback was attempted.";
            }
            var verified = Export(resolveHmi, moduleName, Path.Combine(work, "verify"), "exportAfter", meta);
            if (verified == null) return "Import returned true, but readback export failed. Changes may exist; success is not established. Do not retry automatically.";
            meta["readback"] = verified.Evidence();
            bool match = string.Equals(verified.Text, scriptCode, StringComparison.Ordinal);
            meta["verificationSuccess"] = match; meta["operationSuccess"] = match;
            meta["status"] = match ? "Verified" : "ReadbackMismatch";
            return match ? "Selected module imported and exact full-text readback verified. Project remains unsaved; navigation targets still require separate review."
                : "Import returned true but complete script text differs on readback. Inspect before/after/readback; no automatic retry or rollback.";
        }
    }
}
