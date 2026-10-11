using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;

internal static class CheckModeSelfTests
{
    internal static int Run(Assembly engine)
    {
        int passed = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); passed++; Console.WriteLine("PASS " + message); }
        void RequireCoverageFailure(IDictionary table)
        {
            bool refused = false;
            ToolDescriptorChecks.CheckCoverage(new[] { "EngineTool" }, new[] { "HostTool" }, table, (ok, _) => refused = !ok);
            Check(refused, "descriptor coverage refuses missing/extra execution entries");
        }
        ToolDescriptorChecks.CheckCoverage(new[] { "EngineTool" }, new[] { "HostTool" },
            new Hashtable { ["EngineTool"] = "worker", ["HostTool"] = "host" }, Check);
        RequireCoverageFailure(new Hashtable { ["HostTool"] = "host" });
        RequireCoverageFailure(new Hashtable { ["EngineTool"] = "worker", ["HostTool"] = "host", ["Extra"] = "host" });
        RequireCoverageFailure(new Hashtable { ["EngineTool"] = "worker", ["Extra"] = "host" });
        string directory = Path.Combine(Path.GetTempPath(), "campaign-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string catalog = Path.Combine(directory, "catalog.json"), inputs = Path.Combine(directory, "inputs.json"), result = Path.Combine(directory, "result.json");
            File.WriteAllText(catalog, "[{\"name\":\"Fixture\",\"inputSchema\":{\"type\":\"object\",\"properties\":{\"count\":{\"type\":\"integer\"}},\"required\":[\"count\"],\"additionalProperties\":false}}]");
            JsonObject Step(JsonObject arguments, bool negative = false) => new JsonObject { ["tool"] = "Fixture", ["label"] = "fixture:1", ["args"] = arguments, ["negativeInput"] = negative };
            int Run(JsonObject step)
            {
                File.WriteAllText(inputs, new JsonArray(step).ToJsonString());
                return ScriptInputChecks.Run(engine, catalog, inputs, result);
            }
            Check(Run(Step(new JsonObject { ["count"] = 1 })) == 0, "valid campaign input exits zero");
            Check(Run(Step(new JsonObject { ["count"] = "bad" })) == 1 && (int)JsonNode.Parse(File.ReadAllText(result))!["failed"]! == 1,
                "one bad campaign input exits nonzero and reports its failure");
            Check(Run(Step(new JsonObject { ["count"] = "bad" }, true)) == 0 && (int)JsonNode.Parse(File.ReadAllText(result))!["explicitRejections"]! == 1,
                "explicit schema rejection remains validated and counted");
            var missing = Step(new JsonObject()); missing["tool"] = "Unavailable";
            Check(Run(missing) == 1, "unregistered campaign tool without applicability fails");
            string release = (string)engine.GetType("TiaMcpServer.ModelContextProtocol.McpServer", true)!.GetProperty("ReleaseKey", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var notApplicable = Step(new JsonObject()); notApplicable["tool"] = "Unavailable";
            notApplicable["applicability"] = new JsonObject { [release] = new JsonObject { ["applicable"] = false, ["reason"] = "Absent in this release's fixture catalog." } };
            Check(Run(notApplicable) == 0 && (int)JsonNode.Parse(File.ReadAllText(result))!["notApplicable"]! == 1, "explicit absent tool is retained and counted");
            var hidden = Step(new JsonObject { ["count"] = 1 }); hidden["applicability"] = notApplicable["applicability"]!.DeepClone();
            Check(Run(hidden) == 1, "available campaign tools cannot be hidden by applicability");
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("COMPLETE: " + passed + " check mode self-tests passed");
        return 0;
    }
}
