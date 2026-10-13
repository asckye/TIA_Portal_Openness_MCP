using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Plc;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace TiaMcp.Adapters.Native.Hmi
{
    // Only a selected, already-existing module is exported/imported. Native YAML
    // is preserved byte-for-byte; unknown native shapes are refused, not guessed.
    internal static class UnifiedGlobalScriptEdit
    {
        private const int MaxBytes = 1024 * 1024;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        private static string Hash(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
        private static void RequirePlainFile(string path, string directory) => UnifiedCodecs.RequirePlainFile(path, directory);
        private static UnifiedScriptPair ReadPair(string directory, string moduleName) => UnifiedCodecs.ReadPair(directory, moduleName);
        private static UnifiedScriptPair? Export(Func<object> resolveHmi, string moduleName, string directory, string phase, HmiObject meta)
        {
            meta["phase"] = phase;
            var module = UnifiedScriptAccess.Module(resolveHmi(), moduleName);
            Directory.CreateDirectory(directory);
            var capture = NativeExportCapture.Read(module, "/Scripts/" + MigrationRead.Segment(moduleName), false, new DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(directory)));
            meta[phase] = capture.Status();
            if (!capture.ApiCallSuccess || !capture.NativeSuccess || !capture.InspectionComplete)
            {
                meta["status"] = "NativeExportFailed";
                meta["failures"] = new HmiArray(capture.Rows.Select(r => (object)r.DeepClone()).ToArray());
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
            string scriptCode, bool dryRun, string expectedToken, HmiObject meta)
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
            UnifiedCodecs.ValidateScript(scriptCode);
            meta["syntaxValidation"] = "Esprima parse only; no script execution or TIA SyntaxCheck. Runtime references remain unverified.";
            if (!dryRun && string.IsNullOrWhiteSpace(expectedToken)) throw new InvalidOperationException("A preview expectedToken is required before import.");
            var work = Path.Combine(TiaOpenness.Shared.DataLocations.Current.TempDirectory, "TiaMcpServer", "GlobalScriptEdits", Guid.NewGuid().ToString("N"));
            var backup = Path.Combine(work, "before");
            meta["backupDirectory"] = backup;
            meta["backupRetention"] = "Native files stay on the MCP machine for recovery; they may contain private project source. Remove manually when no longer needed.";
            var before = Export(resolveHmi, moduleName, backup, "exportBefore", meta);
            if (before == null) return "Module export failed; no import attempted. Inspect exportBefore and failures.";
            var candidateBytes = before.Encode(scriptCode);
            if (candidateBytes.Length > MaxBytes) throw new NotSupportedException("Encoded script exceeds the 1 MiB edit limit.");
            meta["before"] = before.Evidence();
            meta["after"] = new HmiObject { ["scriptCode"] = scriptCode, ["scriptSha256"] = Hash(candidateBytes), ["yamlSha256"] = Hash(before.Yaml) };
            var scope = new HmiObject { ["project"] = project, ["softwarePath"] = softwarePath, ["moduleName"] = moduleName,
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
            bool returned = (bool)import.Invoke(scripts, new object[] { new DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(candidate)) })!;
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
