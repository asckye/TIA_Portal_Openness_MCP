using System.Text.Json.Nodes;
using Mono.Cecil;

namespace TiaMcp.ReleaseTool;

internal static class ScorecardCoverage
{
    internal static (int Called, int Documented) Count(IEnumerable<string> xmlFiles, JsonNode inventory)
    {
        var documented = CatalogAudits.DocumentedMembers(xmlFiles).Members;
        var calls = CatalogAudits.CompiledMembers(inventory);
        return (documented.Intersect(calls).Count(), documented.Count);
    }

    internal static string? Adapter(string root, string primary, string key)
    {
        string name = "TiaMcp.Adapter." + key + ".dll";
        foreach (string checkout in new[] { root, primary }.Distinct())
        {
            string candidate = Path.Combine(checkout, "src/PlcWorker/bin", key, "Release/net48", name);
            if (File.Exists(candidate)) return candidate;
        }
        return Scorecard.Prepared(primary, Path.Combine("runtime", "v" + key, "worker", name), delivery: true);
    }

    internal static void Collect(Scorecard context)
    {
        foreach (string key in CatalogJson.Keys)
        {
            string api = Path.Combine(context.Api, ReleaseCommands.PlcWorkerApi(key).Directory);
            context.Report.Capture("Official API coverage", key, api, () =>
            {
                var files = Directory.GetFiles(api, "Siemens.Engineering*.xml")
                    .Where(path => !Path.GetFileName(path).Contains(".AddIn", StringComparison.Ordinal)).ToArray();
                if (files.Length == 0) throw new IOException("No official XML files: " + api);
                string adapter = Adapter(context.Root, context.Primary, key) ?? throw new IOException("No compiled adapter DLL");
                using var module = ModuleDefinition.ReadModule(adapter);
                var resource = module.Resources.OfType<EmbeddedResource>()
                    .Single(item => item.Name == "TiaMcp.NativeCallCoverage.json");
                using var stream = resource.GetResourceStream();
                var (called, documented) = Count(files, JsonNode.Parse(stream)!);
                if (documented == 0) throw new IOException("Official XML has no eligible members");
                context.Report.Add("Official API coverage", "official-api-share", key, (double)called / documented,
                    api + "; " + adapter, $"{called}/{documented}; DLL UTC {File.GetLastWriteTimeUtc(adapter):O}", "share");
            }, "official-api-share");
        }
    }
}
