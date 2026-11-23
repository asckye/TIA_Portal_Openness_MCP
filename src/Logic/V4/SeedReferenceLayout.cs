using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaOpenness.Shared;

namespace TiaMcp.Logic.V4
{
    internal sealed class SeedReferenceLayout
    {
        internal const string Condition = "seed-reference-layout";
        internal static AdapterPreconditionException Missing() => AdapterPreconditionException.Failed(
            "Provide manifest.json or at least one XML file directly under plc/types, plc/blocks, hmi/tags or hmi/screens.", Condition, "referenceDir");
        internal JsonObject? Manifest { get; private set; }
        internal string? ManifestError { get; private set; }
        internal JsonArray PlannedFiles { get; } = new JsonArray();
        private List<(string Source, string Destination, string Hash)> Imports { get; } = new();

        private static string Hash(Stream stream)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        internal static SeedReferenceLayout Read(string root, string release, JsonObject? placeholders) => NativeInputPolicy.Read("referenceDir", () =>
        {
            NativeInputPolicy.RequireLocal(root, "referenceDir");
            var layout = new SeedReferenceLayout();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) throw Missing();
            string manifest = Path.Combine(root, "manifest.json");
            bool hasManifest = File.Exists(manifest);
            if (hasManifest)
            {
                using var stream = NativeInputPolicy.OpenRead(manifest, "referenceDir");
                layout.PlannedFiles.Add(new JsonObject { ["name"] = "manifest.json", ["sha256"] = Hash(stream) });
                stream.Position = 0;
                using var reader = new StreamReader(stream, Encoding.UTF8, true);
                try { layout.Manifest = JsonNode.Parse(reader.ReadToEnd()) as JsonObject; }
                catch (JsonException error) { layout.ManifestError = error.Message; }
            }
            void Add(string key, string destination)
            {
                string directory = layout.Manifest?[key]?.ToString() ?? destination;
                string source = Path.GetFullPath(Path.Combine(root, directory));
                NativeInputPolicy.RequireLocal(source, "referenceDir");
                CallerInputFiles.RefuseHumanSettingsTargets(new JsonObject { ["referenceDir"] = source });
                if (!Directory.Exists(source)) return;
                foreach (string file in Directory.EnumerateFiles(source, "*.xml", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.Ordinal))
                {
                    using var stream = NativeInputPolicy.OpenRead(file, "referenceDir");
                    string hash = Hash(stream);
                    if (destination == "plc/blocks")
                    {
                        stream.Position = 0;
                        var document = SimaticMlImportWindow.Load(Encoding.UTF8.GetBytes(ReadSeedText(stream, placeholders)));
                        SimaticMlImportWindow.RequireBlockProgram(release, document, "referenceDir");
                    }
                    string filename = Path.GetFileName(file);
                    string target = destination + "/" + filename;
                    layout.PlannedFiles.Add(new JsonObject { ["name"] = Path.Combine(directory, filename).Replace('\\', '/'),
                        ["destination"] = target, ["sha256"] = hash });
                    layout.Imports.Add((file, target, hash));
                }
            }
            Add("plcTypesDir", "plc/types");
            Add("plcBlocksDir", "plc/blocks");
            Add("hmiTagTablesDir", "hmi/tags");
            Add("hmiScreensDir", "hmi/screens");
            if (!hasManifest && layout.Imports.Count == 0) throw Missing();
            return layout;
        });

        private static string ReadSeedText(Stream stream, JsonObject? placeholders)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, true, 1024, leaveOpen: true);
            string text = reader.ReadToEnd();
            if (placeholders != null)
                foreach (var pair in placeholders) text = text.Replace("{{" + pair.Key + "}}", pair.Value?.ToString() ?? "");
            return text;
        }

        internal void Stage(string directory, JsonObject placeholders)
        {
            foreach (var file in Imports)
            {
                using var stream = NativeInputPolicy.OpenRead(file.Source, "referenceDir");
                if (Hash(stream) != file.Hash)
                    throw AdapterPreconditionException.Failed("Seed file changed since preview. Preview again.", "seed-input-changed", "referenceDir");
                stream.Position = 0;
                string text = ReadSeedText(stream, placeholders);
                string output = Path.Combine(directory, file.Destination);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.WriteAllText(output, text, Encoding.UTF8);
            }
        }
    }
}
