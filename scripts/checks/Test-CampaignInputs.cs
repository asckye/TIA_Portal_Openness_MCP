#!/usr/bin/env dotnet
// The campaign harness's schema-only checks on the kept production validator; no engine or TIA.
#:property PublishAot=false
#:property NuGetAudit=false
#:project ../../src/Logic/TiaMcp.Logic.csproj

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using TiaMcp.Logic.V4.Inputs;

if (args is ["--self-test"]) return CampaignInputChecks.SelfTest();
if (args.Length != 4) throw new ArgumentException("Expected release, tools.json, inputs.json, result.json");
return CampaignInputChecks.Run(args[0], File.ReadAllText(args[1]), File.ReadAllText(args[2]), args[3]);

internal static class CampaignInputChecks
{
    // Keep the harness's exact input budget/parser without making a product API public.
    private static readonly MethodInfo Parser = typeof(InputSchema).Assembly.GetType("TiaMcp.Logic.V4.V4Json", true)!
        .GetMethod("ParseInput", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static JsonElement Parse(string text) => (JsonElement)Parser.Invoke(null, new object[] { text })!;

    internal static int Run(string release, string catalog, string inputs, string resultPath)
    {
        var schemas = JsonNode.Parse(catalog)!.AsArray().ToDictionary(t => (string)t!["name"]!, t =>
            new InputSchema(Parse(t!["inputSchema"]!.ToJsonString())));
        int passed = 0, rejected = 0, failed = 0, notApplicable = 0;
        var results = new JsonArray();
        foreach (var node in JsonNode.Parse(inputs)!.AsArray())
        {
            var item = node!.AsObject();
            string tool = (string)item["tool"]!, label = (string)item["label"]!;
            var applicability = item["applicability"]?[release];
            bool applicable = applicability == null || (bool)applicability["applicable"]!;
            string reason = (string?)applicability?["reason"] ?? "";
            string status, detail;
            if (!applicable && !schemas.ContainsKey(tool) && !string.IsNullOrWhiteSpace(reason))
            {
                notApplicable++; status = "not-applicable"; detail = reason;
                Console.WriteLine("NOT APPLICABLE V" + release + " " + label + " " + tool + ": " + reason);
            }
            else
            {
                bool negative = (bool)item["negativeInput"]!;
                detail = !applicable ? "An available tool cannot be marked not applicable."
                    : !schemas.ContainsKey(tool) ? "Missing tool requires explicit release applicability and a reason." : "";
                if (detail.Length == 0)
                {
                    var input = Parse(item["args"]!.ToJsonString());
                    var error = schemas[tool].Validate(input, "arguments");
                    if ((error != null) != negative) detail = error?.ToString() ?? "expected rejection";
                }
                if (detail.Length != 0)
                {
                    failed++; status = "failed";
                    Console.WriteLine("FAIL " + label + " " + tool + ": " + detail);
                }
                else { passed++; status = "passed"; if (negative) rejected++; }
            }
            results.Add(new JsonObject { ["label"] = label, ["tool"] = tool, ["status"] = status, ["reason"] = detail });
        }
        var summary = new JsonObject { ["release"] = release, ["passed"] = passed, ["failed"] = failed,
            ["explicitRejections"] = rejected, ["notApplicable"] = notApplicable, ["total"] = passed + failed + notApplicable, ["results"] = results };
        File.WriteAllText(resultPath, summary.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
        Console.WriteLine("COMPLETE: " + passed + " script input checks passed (" + rejected + " explicit rejections); " + failed + " failed; " + notApplicable + " not applicable; no dispatch");
        return failed == 0 ? 0 : 1;
    }

    internal static int SelfTest()
    {
        var root = Environment.CurrentDirectory;
        while (!File.Exists(Path.Combine(root, "Version.props")))
            root = Directory.GetParent(root)?.FullName ?? throw new InvalidOperationException("Worktree not found");
        var folder = Path.Combine(root, "bin-build", "campaign-inputs-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            const string catalog = """[{"name":"Fixture","inputSchema":{"type":"object","properties":{"count":{"type":"integer"}},"required":["count"],"additionalProperties":false}}]""";
            int count = 0;
            foreach (string release in new[] { "20", "21" })
            foreach (var (tool, arguments, negative, applicable, reason, expected, field) in new[]
            {
                ("Fixture", "{\"count\":1}", false, true, "present", 0, "passed"),
                ("Fixture", "{\"count\":\"bad\"}", false, true, "present", 1, "failed"),
                ("Fixture", "{\"count\":\"bad\"}", true, true, "present", 0, "explicitRejections"),
                ("Unavailable", "{}", false, true, "present", 1, "failed"),
                ("Unavailable", "{}", false, false, "absent", 0, "notApplicable"),
                ("Fixture", "{\"count\":1}", false, false, "hidden", 1, "failed"),
                ("Fixture", "{\"count\":1}", true, true, "present", 1, "failed")
            })
            {
                var step = new JsonObject { ["tool"] = tool, ["label"] = "fixture:1", ["args"] = JsonNode.Parse(arguments),
                    ["negativeInput"] = negative, ["applicability"] = new JsonObject { [release] = new JsonObject { ["applicable"] = applicable, ["reason"] = reason } } };
                var result = Path.Combine(folder, "result.json");
                if (Run(release, catalog, new JsonArray(step).ToJsonString(), result) != expected ||
                    (int)JsonNode.Parse(File.ReadAllText(result))![field]! != 1)
                    throw new InvalidOperationException("Campaign schema-only regression: " + field);
                count++;
            }
            Console.WriteLine($"COMPLETE: {count} kept-validator campaign self-tests passed; no dispatch");
            return 0;
        }
        finally { Directory.Delete(folder, true); }
    }
}
