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
        var envelopes = document.RootElement.EnumerateArray().Select(e => JsonNode.Parse(e.GetRawText())!.AsObject()).ToList();
        if (Environment.GetEnvironmentVariable("TIA_MCP_UPDATE_GOLDEN") == "1"
            && !envelopes.Any(envelope => (string?)envelope["meta"]?["outcome"] == "in-progress"))
            envelopes.Add(JsonNode.Parse(V4Json.Serialize(Envelope.Create(new { activeRequestId = "running-call" },
                new Error("The call is still running. Read GetCallResult(requestId).", new OperationInProgressDetails("running-call")),
                new Meta(DateTimeOffset.Parse("2026-11-12T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture), "21",
                    "SaveProject", "running-call", Outcome.InProgress, Execution.Unknown, false, BehaviorPolicy.NotApplicable,
                    Completeness.Unknown, null, Array.Empty<Warning>()))))!.AsObject());
        if (Environment.GetEnvironmentVariable("TIA_MCP_UPDATE_GOLDEN") == "1") Regenerate(envelopes);
        foreach (var envelope in envelopes) yield return JsonSerializer.SerializeToElement(envelope);
    }

    private static void Regenerate(List<JsonObject> envelopes, [CallerFilePath] string sourceFile = "")
    {
        for (int i = 0; i < envelopes.Count; i++)
        {
            var envelope = envelopes[i];
            if (envelope["error"]?["details"] is JsonObject details)
            {
                switch ((string?)envelope["error"]!["code"])
                {
                    case "INVALID_ARGUMENT": RegenerateArguments(envelope["error"]!.AsObject()); break;
                    case "PLAN_STALE": details["previewToken"] = null; break;
                    case "PRECONDITION_FAILED": details["parameter"] = null; break;
                    case "OUTCOME_UNKNOWN": details["reason"] = null; break;
                }
            }
            envelopes[i] = JsonNode.Parse(V4Json.Serialize(V4Json.Deserialize<Envelope>(envelope.ToJsonString())))!.AsObject();
        }
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(sourceFile)!, "V4Supplemental.json"),
            JsonSerializer.Serialize(envelopes, new JsonSerializerOptions { WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }).Replace("\r\n", "\n") + "\n",
            new System.Text.UTF8Encoding(false));
        string realPath = Path.Combine(Path.GetDirectoryName(sourceFile)!, "V4RealEnvelopes.json");
        var real = JsonNode.Parse(File.ReadAllText(realPath))!;
        RegenerateArguments(real);
        var releases = real.AsObject().ToArray();
        var rows = new List<string> { "{" };
        for (int index = 0; index < releases.Length; index++)
        {
            rows.Add(JsonSerializer.Serialize(releases[index].Key) + ": [");
            rows.Add(string.Join(",\n", releases[index].Value!.AsArray().Select(row => row!.ToJsonString(new JsonSerializerOptions {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))));
            rows.Add(index + 1 == releases.Length ? "]" : "],");
        }
        rows.Add("}");
        File.WriteAllText(realPath, string.Join("\n", rows) + "\n", new System.Text.UTF8Encoding(false));
    }

    private static void RegenerateArguments(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if ((string?)obj["code"] == "INVALID_ARGUMENT" && obj["details"] is JsonObject details)
            {
                string Pointer(string? field) => field == "arguments" ? "" : field ?? "";
                var allowed = details["allowedValues"]!.AsArray().Select(v => (string)v!).ToArray();
                var parameters = details["parameters"]?.AsArray().Select(v => Pointer((string?)v)).ToArray();
                var error = new Error((string)obj["message"]!, new InvalidArgumentDetails(Pointer((string?)details["parameter"]),
                    allowed, parameters, (string?)details["expected"] ?? (allowed.Length > 0 ? "one of the allowed values" : null)));
                obj["details"] = JsonNode.Parse(V4Json.Serialize(error))!["details"]!.DeepClone();
            }
            foreach (var value in obj.ToArray()) if (value.Value != null) RegenerateArguments(value.Value);
        }
        else if (node is JsonArray array)
            foreach (var value in array) if (value != null) RegenerateArguments(value);
    }
}
