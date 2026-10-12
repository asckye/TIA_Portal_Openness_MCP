using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.V4;

namespace TiaOpenness.Tests;

internal static class V4Fixtures
{
    internal static IEnumerable<JsonElement> Supplemental()
    {
        var assembly = typeof(V4Fixtures).Assembly;
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n => n.EndsWith("V4Supplemental.json")))!;
        using var document = JsonDocument.Parse(stream);
        var envelopes = document.RootElement.EnumerateArray().Select(e => JsonNode.Parse(e.GetRawText())!.AsObject()).ToArray();
        if (Environment.GetEnvironmentVariable("TIA_MCP_UPDATE_GOLDEN") == "1") Regenerate(envelopes);
        foreach (var envelope in envelopes) yield return JsonSerializer.SerializeToElement(envelope);
    }

    private static void Regenerate(JsonObject[] envelopes, [CallerFilePath] string sourceFile = "")
    {
        for (int i = 0; i < envelopes.Length; i++)
        {
            var envelope = envelopes[i];
            if (envelope["error"]?["details"] is JsonObject details)
            {
                switch ((string?)envelope["error"]!["code"])
                {
                    case "INVALID_ARGUMENT": details["parameters"] = new JsonArray((string)details["parameter"]!); break;
                    case "PLAN_STALE": details["previewToken"] = null; break;
                    case "PRECONDITION_FAILED": details["parameter"] = null; break;
                    case "OUTCOME_UNKNOWN": details["reason"] = null; break;
                }
            }
            envelopes[i] = JsonNode.Parse(V4Json.Serialize(V4Json.Deserialize<Envelope>(envelope.ToJsonString())))!.AsObject();
        }
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(sourceFile)!, "V4Supplemental.json"),
            JsonSerializer.Serialize(envelopes, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n",
            new System.Text.UTF8Encoding(false));
    }
}
