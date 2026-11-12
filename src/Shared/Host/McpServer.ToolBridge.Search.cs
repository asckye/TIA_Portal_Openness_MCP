using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {

        [Description(
            "[L0][Meta][READ] The tool taxonomy: 7 categories (session, project, plc, plc-online, hardware, hmi, runtime), " +
            "their domains (the [L?][Domain] tag every tool description starts with), the meaning of layers L0/L1/L2, and live tool counts per " +
                "category/domain/operation. "
                +
            "Call this first to orient, then FindTools(category=… or domain=…) to browse one area.")]
        public static ResponseStringList ListToolCategories()
        {
            try
            {
                var all = AllToolDescriptors();
                var parsed = all.Select(kv => (Name: kv.Key, Tag: ToolTaxonomy.For(kv.Key), Op: ToolTaxonomy.OperationOf(kv.Key, ToolDescription(kv.Value))))
                    .ToList();
                var lines = new List<string>();
                var categories = new JsonArray();
                foreach (var c in ToolTaxonomy.Categories)
                {
                    var inCategory = parsed.Where(p => ToolTaxonomy.CategoryOf(p.Tag.Domain) == c.Key).ToList();
                    lines.Add($"{c.Key} — {c.NameZh} / {c.NameEn} ({inCategory.Count} tools): {c.Description}");
                    var domains = new JsonArray();
                    foreach (var d in c.Domains)
                    {
                        var inDomain = inCategory.Where(p => string.Equals(p.Tag.Domain, d, StringComparison.OrdinalIgnoreCase)).ToList();
                        lines
                            .Add($"    [{d}] {inDomain.Count} tools; layers " + string
                                .Join("/", inDomain.GroupBy(p => p.Tag.Layer).OrderBy(g => g.Key).Select(g => g.Key + "=" + g.Count())));
                        domains.Add(new JsonObject { ["domain"] = d, ["tools"] = inDomain.Count,
                            ["operations"] =
                                new JsonObject(inDomain.GroupBy(p => p.Op.Operation).OrderBy(g => g.Key)
                                    .Select(g => new KeyValuePair<string, JsonNode?>(g.Key, g.Count()))),
                                ["operationsInferred"] = inDomain.Count(p => p.Op.Inferred) });
                    }
                    categories
                        .Add(new JsonObject {
                            ["key"] = c.Key, ["nameZh"] = c.NameZh, ["nameEn"] = c.NameEn, ["description"] = c.Description, ["tools"] = inCategory.Count,
                                ["domains"] = domains
                        });
                }
                var unknown = parsed.Where(p => !ToolTaxonomy.IsKnownDomain(p.Tag.Domain)).Select(p => p.Name + "(" + p.Tag.Domain + ")").ToList();
                var meta = BridgeMeta(true);
                meta["categories"] = categories;
                meta["layers"] = new JsonObject(ToolTaxonomy.LayerMeaning.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value)));
                meta["operations"] = new JsonObject(ToolTaxonomy.OperationMeaning.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value)));
                meta["uncategorized"] = new JsonArray(unknown.Select(u => (JsonNode)u).ToArray());
                meta["toolCount"] = all.Count;
                return new ResponseStringList
                {
                    Message = $"{all.Count} tools in {ToolTaxonomy.Categories.Count} categories"
                        + (unknown.Count > 0 ? $"; {unknown.Count} tool(s) carry an unregistered domain tag: {string.Join(", ", unknown)}" : "")
                        + ". Use FindTools(category=…) or FindTools(domain=…) to list one area.",
                    Items = lines,
                    Meta = meta,
                };
            }
            catch (Exception ex)
            {
                return new ResponseStringList { Message = "ListToolCategories failed: " + ex.Message, Meta = BridgeMeta(false) };
            }
        }

        [Description(
            "[L0][Meta][READ] Search the FULL tool roster, including tools not listed in this session. " +
            "The server ships a small 'lite' roster by default so every host can load it; everything else is reached through this tool plus CallTool. " +
            "USE THIS whenever the visible tools do not cover what you need, before concluding the server cannot do something. " +
            "Search by capability words, not exact names: 'watch table', 'HMI screen', 'download', 'cross reference', 'GSD'. " +
            "Optionally restrict to one category (session/project/plc/plc-online/hardware/hmi/runtime) or one domain tag (e.g. HMI-Unified, PLC-Online) — see ListToolCategories. "
                +
            "Returns each match's exact name, parameter signature with defaults, and full description; then invoke it with CallTool.")]
        public static ResponseStringList FindTools(
            [Description("query: space-separated words matched against tool names and descriptions, e.g. 'export watch table'. Empty lists the whole roster " +
                "(or the whole category/domain when one is given).")] string query =
                "",
            [Description("limit: max tools to return (default 12). Raise it for a broad survey.")] int limit = 12,
            [Description("category: optional category key from ListToolCategories (session, project, plc, plc-online, hardware, hmi, " +
                "runtime).")] string category =
                "",
            [Description("domain: optional exact domain tag from ListToolCategories, e.g. 'HMI-Unified' or 'PLC-Software'. Case-insensitive.")] string domain =
                "", int offset = 0)
        {
            try
            {
                var all = AllToolDescriptors();
                if (limit <= 0) limit = 12;
                if (!string.IsNullOrWhiteSpace(category) && ToolTaxonomy.FindCategory(category) == null)
                    return new ResponseStringList {
                        Message = "Unknown category '" + category + "'. Valid keys: " + string.Join(", ", ToolTaxonomy.Categories.Select(c => c.Key)) + ".",
                            Meta = BridgeMeta(false)
                    };
                if (!string.IsNullOrWhiteSpace(domain) && !ToolTaxonomy.IsKnownDomain(domain))
                    return new ResponseStringList {
                        Message = "Unknown domain '" + domain + "'. Call ListToolCategories for the registered domain tags.", Meta = BridgeMeta(false) };
                if (!string.IsNullOrWhiteSpace(category) || !string.IsNullOrWhiteSpace(domain))
                {
                    var filtered = new Dictionary<string, ToolDescriptor>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in all)
                    {
                        var tag = ToolTaxonomy.For(kv.Key);
                        if (!string.IsNullOrWhiteSpace(domain) && !string.Equals(tag.Domain, domain.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                        if (!string.IsNullOrWhiteSpace(category) && !string
                                .Equals(ToolTaxonomy.CategoryOf(tag.Domain), category.Trim(), StringComparison.OrdinalIgnoreCase))
                            continue;
                        filtered[kv.Key] = kv.Value;
                    }
                    all = filtered;
                }

                var terms = (query ?? "")
                    .Split(new[] { ' ', ',', ';', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => t.Trim().ToLowerInvariant())
                    .Where(t => t.Length > 0)
                    .ToArray();

                var scored = new List<KeyValuePair<int, string>>();
                foreach (var kv in all)
                {
                    string lname = kv.Key.ToLowerInvariant();
                    string desc = (ToolDescription(kv.Value) + " " + ToolMetadata.SearchAliases(kv.Key)).ToLowerInvariant();
                    int score = terms.Length == 0 ? 1 : 0;
                    int matched = 0;
                    bool capabilityHit = false;
                    string aliases = ToolMetadata.SearchAliases(kv.Key).ToLowerInvariant();
                    foreach (var t in terms)
                    {
                        int weight = lname.Contains(t) ? 20 : aliases.Contains(t) ? 12 : desc.Contains(t) ? 3 : 0;
                        score += weight;
                        if (weight > 0) matched++;
                        if (weight >= 12) capabilityHit = true;
                    }
                    // Single words retain description recall; multi-word queries require
                    // every word and a name/alias hit. Exact registered names always win.
                    bool exact = string.Equals(kv.Key, query?.Trim(), StringComparison.OrdinalIgnoreCase);
                    if (exact) score = int.MaxValue;
                    if (terms.Length == 0 || exact || matched == terms.Length && score >= (terms.Length == 1 ? 3 : 6)
                        && (terms.Length == 1 || capabilityHit))
                        scored.Add(new KeyValuePair<int, string>(score, kv.Key));
                }

                var hits = scored
                    .OrderByDescending(x => x.Key).ThenBy(x => x.Value, StringComparer.Ordinal)
                    .Skip(offset).Take(limit).ToList();

                bool lite = BridgeIsLiteProfile();
                var lines = new List<string>();
                foreach (var h in hits)
                {
                    var m = all[h.Value];
                    bool listed = !lite || BridgeLiteToolNames.Contains(h.Value);
                    lines.Add(m.Signature
                              + (listed ? "  [already listed - call it directly]" : "  [call via CallTool]"));
                    lines.Add("    " + ToolDescription(m));
                    // A worked call next to the signature is what stops the guess-and-retry loop.
                    lines.Add("    " + ToolExamples.Render(ToolExamples.FindOrDerive(h.Value, SpecsOf(m), ReleaseKey)));
                }

                var message = (hits.Count == 0 ? "0" : "Showing " + (offset + 1) + "-" + (offset + hits.Count))
                              + " of " + scored.Count + " matching tools (roster: " + AllToolDescriptors().Count + " total). " +
                              "Tools marked [call via CallTool] are not in this session's tool list - " +
                              "invoke them with CallTool(name, arguments); PreviewToolCall(name, arguments) checks a planned call without executing it.";
                if (scored.Count == 0 && !string.IsNullOrEmpty(query))
                    message += " No tool matches '" + query + "'. Try fewer or more general words "
                        + "(e.g. 'watch table' instead of 'ExportPlcWatchTableToCsv'), "
                        + "or call FindTools with an empty query to list everything.";
                return new ResponseStringList
                {
                    Message = message,
                    Items = lines,
                    Meta = new JsonObject { ["success"] = true, ["matchingCount"] = scored.Count },
                };
            }
            catch (Exception ex)
            {
                return new ResponseStringList { Message = "FindTools failed: " + ex.Message, Meta = BridgeMeta(false) };
            }
        }
    }
}
