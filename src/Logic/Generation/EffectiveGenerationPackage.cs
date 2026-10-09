using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.Logic.Generation
{
    public static class EffectiveGenerationPackage
    {
        // Package discovery/range solving belongs to package management. The caller supplies pinned parents.
        public static StandardPackage Resolve(StandardPackage package, IEnumerable<StandardPackage> parents)
        {
            if (package.Manifest.Extends == null) return package;
            var available = parents.Append(package).ToArray();
            var chain = new List<StandardPackage>();
            var visiting = new HashSet<string>(StringComparer.Ordinal);
            Visit(package);
            var resources = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var parts = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            var rules = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            var packageIds = new HashSet<string>(chain.Select(p => p.Manifest.Id), StringComparer.Ordinal);
            foreach (var current in chain)
            {
                var prefix = "packages/" + current.Manifest.Id + "/" + current.Manifest.Version + "/";
                foreach (var file in current.FileNames) resources.Add(prefix + file, current.ReadFile(file));
                var partReferences = current.Documents["package.json"].GetProperty("parts");
                foreach (var part in partReferences.EnumerateObject())
                {
                    if (part.Name == "rules")
                    {
                        foreach (var path in part.Value.EnumerateArray())
                        {
                            var rule = Rewrite(current.Documents[path.GetString()!], current, prefix).AsObject();
                            rules[rule["deviceType"]!.GetValue<string>()] = rule;
                        }
                        continue;
                    }
                    var content = Rewrite(current.Documents[part.Value.GetString()!], current, prefix).AsObject();
                    if (!parts.TryGetValue(part.Name, out var merged)) { parts.Add(part.Name, content); continue; }
                    foreach (var property in content)
                    {
                        if (property.Value is JsonArray incoming)
                        {
                            var byId = (merged[property.Key] as JsonArray ?? new JsonArray()).Select(n => n!.AsObject()).ToDictionary(n => n["id"]!.GetValue<string>(), n => n, StringComparer.Ordinal);
                            foreach (var entry in incoming.Select(n => n!.AsObject())) byId[entry["id"]!.GetValue<string>()] = entry;
                            merged[property.Key] = new JsonArray(byId.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value.DeepClone()).ToArray());
                        }
                        else if (part.Name == "alarms" && property.Key == "texts" && property.Value is JsonObject texts)
                        {
                            var allTexts = merged["texts"]!.AsObject();
                            foreach (var text in texts) allTexts[text.Key] = text.Value?.DeepClone();
                        }
                        else merged[property.Key] = property.Value?.DeepClone();
                    }
                }
            }
            var manifest = JsonNode.Parse(package.Documents["package.json"].GetRawText())!.AsObject();
            manifest.Remove("extends");
            var references = new JsonObject();
            foreach (var part in parts.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var path = "effective/" + part.Key + ".json"; references[part.Key] = path;
                resources.Add(path, CanonicalJson.Encode(CanonicalJson.Parse(part.Value.ToJsonString())));
            }
            var rulePaths = new JsonArray();
            foreach (var rule in rules.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var path = "effective/rules/" + CanonicalJson.HashBytes(System.Text.Encoding.UTF8.GetBytes(rule.Key)) + ".json";
                rulePaths.Add(path); resources.Add(path, CanonicalJson.Encode(CanonicalJson.Parse(rule.Value.ToJsonString())));
            }
            references["rules"] = rulePaths; manifest["parts"] = references;
            manifest["files"] = new JsonArray(resources.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (JsonNode)new JsonObject { ["path"] = p.Key, ["sha256"] = CanonicalJson.HashBytes(p.Value) }).ToArray());
            resources.Add("package.json", CanonicalJson.Encode(CanonicalJson.Parse(manifest.ToJsonString())));
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
                foreach (var resource in resources) { using var entry = zip.CreateEntry(resource.Key).Open(); entry.Write(resource.Value, 0, resource.Value.Length); }
            stream.Position = 0;
            return StandardPackageLoader.LoadZip(stream);

            void Visit(StandardPackage item)
            {
                if (!visiting.Add(item.Manifest.Id)) throw CanonicalJson.Failure("/extends", "dependency", "Cyclic package inheritance.");
                if (item.Manifest.Extends != null)
                {
                    var reference = item.Manifest.Extends;
                    var matches = available.Where(p => p.Manifest.Id == reference.Package && GenerationPlanner.VersionMatches(reference.Version, p.Manifest.Version)).ToArray();
                    if (matches.Length != 1) throw CanonicalJson.Failure("/extends", "reference", "Supply exactly one pinned parent satisfying " + reference.Package + " " + reference.Version + ".");
                    Visit(matches[0]);
                }
                chain.Add(item);
            }
            JsonNode Rewrite(JsonElement value, StandardPackage owner, string prefix, string field = "")
            {
                if (value.ValueKind == JsonValueKind.Object)
                {
                    var result = new JsonObject();
                    foreach (var property in value.EnumerateObject()) result[property.Name] = Rewrite(property.Value, owner, prefix, property.Name);
                    return result;
                }
                if (value.ValueKind == JsonValueKind.Array) return new JsonArray(value.EnumerateArray().Select(v => Rewrite(v, owner, prefix, field)).ToArray());
                if (value.ValueKind == JsonValueKind.String)
                {
                    var text = value.GetString()!;
                    if ((field == "template" || field == "files" || field == "fileNames" || owner.Manifest.Files.Any(f => f.Path == text)) && owner.FileNames.Contains(text)) text = prefix + text;
                    if (text.StartsWith("lib:", StringComparison.Ordinal))
                    {
                        var slash = text.IndexOf('/');
                        if (slash > 4 && packageIds.Contains(text.Substring(4, slash - 4))) text = "lib:" + package.Manifest.Id + text.Substring(slash);
                    }
                    return JsonValue.Create(text)!;
                }
                return JsonNode.Parse(value.GetRawText())!;
            }
        }
    }
}
