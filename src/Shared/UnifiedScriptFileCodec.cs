using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Esprima;
using YamlDotNet.RepresentationModel;

namespace TiaMcpServer.Siemens.Services
{
    // Only a selected, already-existing module is exported/imported. Native YAML
    // is preserved byte-for-byte; unknown native shapes are refused, not guessed.
    internal static class UnifiedScriptFileCodec
    {
        private const int MaxBytes = 1024 * 1024;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
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
        internal static void RequirePlainFile(string path, string directory)
        {
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), directory, StringComparison.OrdinalIgnoreCase)
                || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new NotSupportedException("Native export contains a file outside its isolated directory or a reparse point.");
            if (new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(path)).Length > MaxBytes) throw new NotSupportedException("Native file exceeds the 1 MiB edit limit.");
        }
        internal static TiaMcp.Adapters.Contracts.UnifiedScriptPair ReadPair(string directory, string moduleName)
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
            var pair = new TiaMcp.Adapters.Contracts.UnifiedScriptPair { JsName = jsName, YamlName = Path.GetFileName(yamlPath), Js = File.ReadAllBytes(jsPath), Yaml = yaml };
            pair.Text = Decode(pair.Js, out pair.Encoding, out pair.Bom);
            return pair;
        }
        internal static void ValidateScript(string scriptCode)
            => new JavaScriptParser(new ParserOptions { Tolerant = false }).ParseModule(scriptCode);
    }
}
