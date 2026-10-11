using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

internal static class ScriptInputChecks
{
    internal static int Run(Assembly engine, string catalogPath, string inputsPath, string resultPath = "")
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        var schemaType = Program.FindServerType(engine, "TiaMcp.Logic.V4.Inputs.InputSchema");
        var parse = Program.FindServerType(engine, "TiaMcp.Logic.V4.V4Json").GetMethod("ParseInput", flags)!;
        var schemas = JsonNode.Parse(File.ReadAllText(catalogPath))!.AsArray().ToDictionary(t => (string)t!["name"]!, t =>
            Activator.CreateInstance(schemaType, new[] { parse.Invoke(null, new object[] { t!["inputSchema"]!.ToJsonString() }) })!);
        string release = (string)engine.GetType("TiaMcpServer.ModelContextProtocol.McpServer", true)!.GetProperty("ReleaseKey", flags)!.GetValue(null)!;
        int passed = 0, rejected = 0, failed = 0, notApplicable = 0;
        var results = new JsonArray();
        foreach (var node in JsonNode.Parse(File.ReadAllText(inputsPath))!.AsArray())
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
                object? error = null;
                detail = !applicable ? "An available tool cannot be marked not applicable."
                    : !schemas.ContainsKey(tool) ? "Missing tool requires explicit release applicability and a reason." : "";
                if (detail.Length == 0)
                {
                    var input = parse.Invoke(null, new object[] { item["args"]!.ToJsonString() });
                    error = schemaType.GetMethod("Validate")!.Invoke(schemas[tool], new[] { input, "arguments" });
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
        if (resultPath.Length != 0) File.WriteAllText(resultPath, summary.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
        Console.WriteLine("COMPLETE: " + passed + " script input checks passed (" + rejected + " explicit rejections); " + failed + " failed; " + notApplicable + " not applicable; no dispatch");
        return failed == 0 ? 0 : 1;
    }
}
