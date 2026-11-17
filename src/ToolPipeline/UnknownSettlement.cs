using TiaMcp.Adapters.Contracts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TiaMcp.FoundationHost;

// Only target identity is retained: never an import document, source text or a complete argument object.
public sealed record SettlementTarget(string? ReadOperation, string Scope, string Name, string? Table = null,
    string? DataType = null, string? Value = null);

public sealed record CallSettlement(string RequestId, string Tool, string Outcome, DateTimeOffset Utc)
{
    public JsonObject ToJson() => new() { ["requestId"] = RequestId, ["tool"] = Tool, ["outcome"] = Outcome, ["utc"] = Utc.ToString("O") };
}

public static class UnknownSettlement
{
    // One optional read adds at most five seconds to the caller's existing operation budget.
    public static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(5);
    public const int RecoveryLimit = 1;

    public static SettlementTarget? Capture(string tool, JsonObject arguments)
    {
        string Text(string key) => (string?)arguments[key] ?? "";
        try
        {
            return tool switch
            {
                // ReadTagTableNames sees the root only. A grouped creation has no definite read-back row.
                "CreatePlcTagTable" when Text("group") == "" => new("ReadTagTableNames", Text("plc"), Text("name")),
                "CreatePlcTag" => new("ListTags", Text("plc"), Text("name"), Text("table"), Text("dataType"), Text("address")),
                "CreatePlcUserConstant" => new("ListUserConstants", Text("plc"), Text("name"), Text("table"), Text("dataType"), Text("value")),
                "DeletePlcExternalSource" when Text("groupPath") == "" => new("ReadExternalSourceNames", Text("softwarePath"),
                    Text("externalSourceName")),
                "ImportPlcExternalSource" when Text("groupPath") == "" && (bool?)arguments["overwrite"] != true
                    => new("ReadExternalSourceNames", Text("softwarePath"), Path.GetFileName(Text("filePath"))),
                "ExportPlcBlock" or "ExportPlcType" => new(null, ExportFile(arguments, Text(tool == "ExportPlcBlock" ? "blockPath" : "typePath")), ""),
                "ExportPlcTagTable" or "ExportPlcWatchTable" or "ExportTechnologyObject" => new(null, Path.GetFullPath(Text("exportPath")), ""),
                "ExportProjectTexts" => new(null, Path.GetFullPath(Text("filePath")), ""),
                _ => null
            };
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or NotSupportedException)
        {
            // An incomplete target cannot supply definite evidence. Keep the original unknown call.
            return null;
        }
    }

    private static string ExportFile(JsonObject arguments, string objectPath)
    {
        var parts = objectPath.Split('/').Select(ObjectPathEncoding.DecodeSegment).ToArray();
        string relative = (bool?)arguments["preservePath"] == true ? Path.Combine(parts) : parts.Last();
        return Path.GetFullPath(Path.Combine((string)arguments["exportPath"]!, relative + ".xml"));
    }

    public static async Task RunPending(UnsettledCalls calls, LateCalls results, Func<string, JsonObject, Task<JsonNode?>> read,
        CancellationToken cancellationToken = default)
    {
        foreach (var call in calls.RecoverySettlements(RecoveryLimit))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Run(calls, results, call.RequestId, read, cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task<CallSettlement?> Run(UnsettledCalls calls, LateCalls results, string requestId,
        Func<string, JsonObject, Task<JsonNode?>> read, CancellationToken cancellationToken = default)
    {
        var call = calls.Snapshot().FirstOrDefault(item => item.RequestId == requestId);
        if (call?.Target is not { } target) return calls.Settlement(requestId);
        string? outcome;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (target.ReadOperation == null) outcome = FileOutcome(target.Scope, call.Tool);
            else
            {
                var arguments = target.Table == null ? new JsonObject { ["softwarePath"] = target.Scope }
                    : new JsonObject { ["plc"] = target.Scope, ["table"] = target.Table };
                var observed = await read(target.ReadOperation, arguments).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                outcome = Decide(call.Tool, target, observed);
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        catch (Exception) /* swallow(probe-optional): a failed read supplies no evidence and must leave the call unknown */
        { return null; }
        if (outcome == null) return null;
        var settlement = calls.SettleByReadBack(requestId, outcome);
        if (settlement != null) results.ObserveSettlement(settlement);
        return settlement;
    }

    private static string? FileOutcome(string path, string tool)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) return null;
            return tool != "ExportProjectTexts" && new FileInfo(path).Length > 0 ? "applied" : null;
        }
        catch (FileNotFoundException) /* swallow(env-probe): a missing target file proves the export was not applied */
        { return "not-applied"; }
        catch (DirectoryNotFoundException) /* swallow(env-probe): a missing parent proves the export target is absent */
        { return "not-applied"; }
    }

    private static string? Decide(string tool, SettlementTarget target, JsonNode? observed)
    {
        if (observed is not JsonArray rows || string.IsNullOrWhiteSpace(target.Scope) || string.IsNullOrWhiteSpace(target.Name)) return null;
        if (target.Table == null)
        {
            if (rows.Any(row => row is not JsonValue value || !value.TryGetValue<string>(out var name) || string.IsNullOrWhiteSpace(name))) return null;
            var names = rows.Select(row => (string)row!).ToArray();
            if (names.Distinct(StringComparer.Ordinal).Count() != names.Length) return null;
            bool present = names.Contains(target.Name, StringComparer.Ordinal);
            if (tool == "ImportPlcExternalSource" && !present) return null;
            return present != (tool == "DeletePlcExternalSource") ? "applied" : "not-applied";
        }
        var declarations = new List<JsonObject>();
        string kind = tool == "CreatePlcTag" ? "tag" : "user-constant";
        foreach (var row in rows)
        {
            if (row is not JsonObject item || new[] { "Name", "Path", "Kind", "DataType", "Value" }
                .Any(key => item[key] is not JsonValue value || !value.TryGetValue<string>(out _))) return null;
            string name = (string)item["Name"]!, path = (string)item["Path"]!;
            string suffix = "/" + ObjectPathEncoding.EncodeSegment(name);
            bool selected = path == ObjectPathEncoding.NormalizePath(target.Table) + suffix
                || !target.Table.Contains('/') && path == ObjectPathEncoding.EncodeSegment(target.Table) + suffix;
            if (string.IsNullOrWhiteSpace(name) || !selected || (string?)item["Kind"] != kind) return null;
            declarations.Add(item);
        }
        if (declarations.Select(row => (string)row["Name"]!).Distinct(StringComparer.Ordinal).Count() != declarations.Count) return null;
        var matches = declarations.Where(row => (string?)row["Name"] == target.Name).ToArray();
        if (matches.Length == 0) return "not-applied";
        return (string?)matches[0]["DataType"] == target.DataType && (string?)matches[0]["Value"] == target.Value ? "applied" : null;
    }

    public static JsonNode Decorate(JsonNode original, CallSettlement settlement)
    {
        var body = original.DeepClone();
        DecorateEnvelope(body, settlement);
        return body;
    }

    private static void DecorateEnvelope(JsonNode node, CallSettlement settlement)
    {
        if (node is JsonObject envelope)
        {
            if (envelope["meta"] is JsonObject metadata && metadata["requestId"] is JsonValue id
                && id.TryGetValue<string>(out var requestId) && requestId == settlement.RequestId)
            {
                if (envelope["data"] is not JsonObject) envelope["data"] = new JsonObject();
                envelope["data"]!["settlement"] = settlement.ToJson();
                return;
            }
            foreach (var field in envelope)
                if (field.Value != null) DecorateEnvelope(field.Value, settlement);
        }
        else if (node is JsonArray items)
            foreach (var item in items)
                if (item != null) DecorateEnvelope(item, settlement);
    }
}
