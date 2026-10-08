using System.Text.Json.Nodes;

namespace TiaMcp.FoundationHost;

internal static class BatchImportContract
{
    internal static TiaMcp.Logic.V4.Envelope ValidateCandidate(JsonNode? payload, JsonObject request)
    {
        using var json = System.Text.Json.JsonDocument.Parse(payload?.ToJsonString() ?? throw new InvalidDataException("Missing import candidate result."));
        var result = TiaMcp.Logic.V4.V4Json.Deserialize<TiaMcp.Logic.V4.Envelope>(json.RootElement.GetRawText());
        string tool = (string)request["tool"]!;
        if (!TiaMcp.Logic.V4.PlcImportContract.Entries.Contains(tool, StringComparer.Ordinal) || result.Meta.Tool != tool
            || result.Meta.RequestId != (string?)request["requestId"] || result.Meta.BehaviorPolicy != TiaMcp.Logic.V4.BehaviorPolicy.SafeV4
            || result.Meta.ReleaseKey is not ("14sp1" or "15.1" or "16" or "17" or "18" or "19" or "20" or "21")) throw new InvalidDataException("Import candidate response identity mismatch.");
        bool apply = (string?)request["mode"] == "apply";
        if (!apply && result.Meta.Execution is not (TiaMcp.Logic.V4.Execution.ReadOnly or TiaMcp.Logic.V4.Execution.NotStarted)) throw new InvalidDataException("Import preview mutated the project.");
        if (result.Data == null) { if (result.Ok) throw new InvalidDataException("Import success requires evidence."); return result; }
        var data = JsonNode.Parse(result.Data.Value.GetRawText())!.AsObject();
        if (data["plan"] != null)
        {
            var plan = TiaMcp.Logic.V4.V4Json.Deserialize<TiaMcp.Logic.V4.Plan>(data["plan"]!.ToJsonString());
            var normalized = System.Text.Json.JsonSerializer.Deserialize<TiaMcp.Adapters.Contracts.Candidates.PlcImportRequest>(request.ToJsonString(),
                new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase })!;
            if (plan.ArgumentsHash != TiaMcp.Logic.V4.DeviceCreationSession.Hash(normalized)
                || plan.InventoryHash != TiaMcp.Logic.V4.DeviceCreationSession.Hash(new JsonObject { ["inventory"] = data["inventory"]?.DeepClone(), ["targets"] = data["targets"]?.DeepClone() })
                || plan.Hash != TiaMcp.Logic.V4.DeviceCreationSession.Hash(new { releaseKey = plan.ReleaseKey, tool = plan.Tool, argumentsHash = plan.ArgumentsHash,
                    identity = plan.Identity, inputHashes = plan.InputHashes, inventoryHash = plan.InventoryHash, operations = plan.Operations, warnings = plan.Warnings }))
                throw new InvalidDataException("Import plan hash/arguments/evidence mismatch.");
            if (plan.Tool != tool || plan.ReleaseKey != result.Meta.ReleaseKey || plan.Operations.Count < 1
                || plan.Operations.Count > (request["maxItems"]?.GetValue<int>() ?? 128) || plan.Operations.Any(o => o.Tool != tool)
                || plan.InputHashes.Count != plan.Identity.Files.Count || plan.Identity.Files.Any(f => !f.Exists || !plan.InputHashes.TryGetValue(f.Path, out var h) || h != f.Sha256)) throw new InvalidDataException("Import plan scope/files mismatch.");
            if (apply && (request["confirm"]?.GetValue<bool>() != true || plan.Hash != (string?)request["expectedPlanHash"]
                || TiaMcp.Logic.V4.DeviceCreationSession.CanonicalProject(plan.Identity.ProjectFile!) != TiaMcp.Logic.V4.DeviceCreationSession.CanonicalProject((string)request["expectedProjectFile"]!))) throw new InvalidDataException("Import confirmation mismatch.");
            foreach (var operation in plan.Operations)
            {
                var target = operation.Arguments.GetProperty("target"); string kind = target.GetProperty("kind").GetString()!;
                string group = target.GetProperty("groupPath").GetString()!;
                string expected = (string?)request[kind == "UDT" ? "typeGroupPath" : kind == "TagTable" ? "tagFolderPath" : "blockGroupPath"] ?? "";
                if (group != expected || operation.Arguments.GetProperty("overwrite").GetBoolean() != (request["overwrite"]?.GetValue<bool>() ?? false)) throw new InvalidDataException("Import target/options mismatch.");
            }
            if (!apply && data["importIssued"]?.GetValue<bool>() != false) throw new InvalidDataException("Preview import evidence conflicts.");
        }
        else if (result.Ok) throw new InvalidDataException("Import success requires a plan.");
        if (apply && data["items"] is JsonArray items)
        {
            var children = TiaMcp.Logic.V4.V4Json.Deserialize<TiaMcp.Logic.V4.BatchItem[]>(items.ToJsonString());
            var plan = TiaMcp.Logic.V4.V4Json.Deserialize<TiaMcp.Logic.V4.Plan>(data["plan"]!.ToJsonString());
            if (children.Length != plan.Operations.Count || children.Any(c => c.Target != plan.Operations[c.Index].Arguments.GetProperty("inputPath").GetString())) throw new InvalidDataException("Import children do not match the reviewed order.");
            _ = new TiaMcp.Logic.V4.BatchData(children); bool stopped = false; int cause = -1;
            foreach (var child in children)
            {
                if (child.Result.Meta.Tool != tool || child.Result.Meta.ReleaseKey != result.Meta.ReleaseKey || child.Result.Meta.RequestId != result.Meta.RequestId) throw new InvalidDataException("Import child identity mismatch.");
                if (child.Result.Ok)
                {
                    if (stopped || child.Result.Data?.GetProperty("contentVerified").GetBoolean() != true || child.Result.Data?.GetProperty("nativeImportCalls").GetInt32() != 1) throw new InvalidDataException("Import success lacks ordered content readback.");
                }
                else if (!stopped) { stopped = true; cause = child.Index; }
                else if (child.Result.Error?.Details is not TiaMcp.Logic.V4.NotExecutedDetails skipped || skipped.CauseIndex != cause) throw new InvalidDataException("Import continued after failure.");
            }
            if (children.Any(c => c.Result.Meta.Outcome == TiaMcp.Logic.V4.Outcome.Unknown) && result.Meta.Outcome != TiaMcp.Logic.V4.Outcome.Unknown
                || result.Ok && children.Any(c => !c.Result.Ok)) throw new InvalidDataException("Import aggregate lost child outcomes.");
        }
        else if (apply && result.Ok) throw new InvalidDataException("Apply lacks per-item evidence.");
        return result;
    }
    internal static JsonObject Validate(JsonNode? payload,bool dryRun)
    {
        if(payload is not JsonObject result) throw new InvalidDataException("Missing batch import outcome.");
        string Text(JsonObject o,string key)=>o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new InvalidDataException("Missing batch text: "+key);
        bool Flag(JsonObject o,string key)=>o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : throw new InvalidDataException("Missing batch flag: "+key);
        int Count(string key)=>result[key] is JsonValue v && v.TryGetValue<int>(out var n) ? n : throw new InvalidDataException("Missing batch count.");
        void Hash(string value) {if(value.Length!=64 || value.Any(x=>!"0123456789abcdef".Contains(x))) throw new InvalidDataException("Invalid batch hash.");}
        void Object(JsonObject o,bool planned=true)
        {
            if(planned && (string.IsNullOrWhiteSpace(Text(o,"Name")) || Text(o,"Kind") is not ("FC" or "FB" or "OB" or "GlobalDB" or "InstanceDB" or "UDT" or "TagTable"))) throw new InvalidDataException("Invalid native identity.");
            Text(o,"Name");Text(o,"Kind");Text(o,"GroupPath"); if(o["Number"]!=null && (o["Number"] is not JsonValue n || !n.TryGetValue<int>(out var number) || number<0)) throw new InvalidDataException("Invalid native number.");
        }
        bool blocked = result["Items"] is JsonArray plan && plan.Any(x => (string?)x?["Status"] == "replace-blocked");
        if((Flag(result,"Executed")==dryRun && !(blocked && !Flag(result,"Executed"))) || string.IsNullOrWhiteSpace(Text(result,"ProjectFile")) || string.IsNullOrWhiteSpace(Text(result,"SoftwarePath")) || Text(result,"DependencyStatus")!="unverified-caller-order-required") throw new InvalidDataException("Conflicting batch identity/state.");
        if(Text(result,"Release") is not ("14sp1" or "15.1" or "16" or "17" or "18" or "19" or "20" or "21")) throw new InvalidDataException("Unknown release.");
        Flag(result,"Recursive");Hash(Text(result,"PlanHash"));
        if(result["Items"] is not JsonArray items || items.Count is <1 or >256) throw new InvalidDataException("Invalid batch item count.");
        var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);bool failed=false;int imported=0,failures=0;
        foreach(var node in items)
        {
            if(node is not JsonObject item || item["Planned"] is not JsonObject planned || item["ReturnedObjects"] is not JsonArray returned) throw new InvalidDataException("Malformed batch item.");
            var path=Text(item,"RelativePath"); if(!paths.Add(path) || Path.IsPathRooted(path) || path.Contains('\\') || path.Split('/').Any(x=>x is "" or "." or "..")) throw new InvalidDataException("Invalid manifest path.");
            if(item["Action"] is JsonValue action)
            {
                string value=action.GetValue<string>();
                if(value is not ("create" or "replace" or "replace-blocked: inconsistent" or "replace-blocked: know-how-protected" or "replace-blocked: unknown-consistency") || value.StartsWith("replace",StringComparison.Ordinal) && item["Replaced"] is not JsonObject) throw new InvalidDataException("Invalid replacement action/evidence.");
                if(item["Replaced"] is JsonObject replaced) Object(replaced);
                if(value.StartsWith("replace",StringComparison.Ordinal) && item["Replaced"] is JsonObject old)
                    foreach(var key in new[]{"Name","Kind","GroupPath"}) if(Text(old,key)!=Text(planned,key)) throw new InvalidDataException("Replacement identity differs from the planned namespace target.");
                if(item["RecoverySha256"] is JsonValue recoveryHash && recoveryHash.GetValue<string>()!="") Hash(recoveryHash.GetValue<string>());
                if((string?)item["RestoreStatus"]=="restored" && (value!="replace" || (string?)item["RecoveryPath"]=="" || (string?)item["RecoverySha256"]=="")) throw new InvalidDataException("Restoration lacks backup evidence.");
                if(item["RestoreStatus"] is JsonValue restore && restore.GetValue<string>() is not ("not-needed" or "backup-ready" or "restored" or "restore-failed" or "not-attempted-unknown-outcome" or "not-attempted-dependency-unverified")) throw new InvalidDataException("Unknown restoration state.");
                if((string?)item["RecoveryPath"] is string backup && backup!="")
                {
                    var directory=(string?)result["RecoveryDirectory"];
                    if(string.IsNullOrEmpty(directory) || !Path.IsPathRooted(directory) || !Path.IsPathRooted(backup)
                        || !Path.GetFullPath(backup).StartsWith(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Backup path escapes its recovery directory.");
                }
            }
            Object(planned);Hash(Text(item,"InputSha256"));var status=Text(item,"Status");var attempted=Flag(item,"Attempted");var failure=Text(item,"Failure");
            foreach(var actual in returned) {if(actual==null && status=="failed" && attempted) continue;if(actual is not JsonObject obj) throw new InvalidDataException("Malformed returned identity.");Object(obj,status!="failed");}
            if(dryRun || blocked)
            {
                if(attempted || returned.Count!=0 || status is not ("planned" or "replace-blocked")
                    || status=="planned" && failure!="" || status=="replace-blocked" && (failure is not ("inconsistent" or "know-how-protected" or "unknown-consistency")
                        || (string?)item["Action"]!="replace-blocked: "+failure || (string?)item["Replaced"]?["BackupBlocker"]!=failure)) throw new InvalidDataException("Blocked/preview plan mutated or has conflicting blockers.");
                continue;
            }
            if(status=="imported" || status=="rolled-back")
            {
                if(failed || !attempted || returned.Count!=1 || failure!="") throw new InvalidDataException("Invalid import success sequence.");
                var actual=returned[0]!.AsObject();
                foreach(var key in new[]{"Name","Kind","GroupPath"}) if(Text(actual,key)!=Text(planned,key)) throw new InvalidDataException("Returned identity mismatch.");
                if((Text(planned,"Kind") is "FC" or "FB" or "OB" or "GlobalDB" or "InstanceDB" && actual["Number"]==null) || (planned["Number"]!=null && !JsonNode.DeepEquals(planned["Number"],actual["Number"]))) throw new InvalidDataException("Returned number mismatch.");
                if(status=="rolled-back" && (string?)item["RestoreStatus"]!="restored") throw new InvalidDataException("Rolled-back item lacks verified restoration.");
                if(status=="imported") imported++;
            }
            else if(status=="failed") {if(failed || (attempted ? failure is not ("native-outcome-uncertain" or "native-import-or-verification-failed") : failure is not ("target-recheck-failed" or "backup-failed"))) throw new InvalidDataException("Invalid failure sequence.");failed=true;failures++;}
            else if(status=="not-attempted") {if(!failed && string.IsNullOrEmpty((string?)result["RecoveryFailure"]) || attempted || returned.Count!=0 || failure!="") throw new InvalidDataException("Invalid skipped outcome.");}
            else throw new InvalidDataException("Invalid batch status.");
        }
        if(Count("ImportedCount")!=imported || Count("FailedCount")!=failures) throw new InvalidDataException("Batch counts/reset disagree with outcomes.");
        if((bool?)result["NativeOutcomeUnknown"]==true && !Flag(result,"RequiresSessionReset")) throw new InvalidDataException("Unknown native outcome requires session reset.");
        if(items.Any(x=>(string?)x?["Failure"]=="native-outcome-uncertain" || (string?)x?["RestoreStatus"]=="restore-failed") && !Flag(result,"RequiresSessionReset")) throw new InvalidDataException("Unknown item outcome requires session reset.");
        if(result["Items"]!.AsArray().Any(x=>(string?)x?["Action"]=="replace" && (bool?)x?["Attempted"]==true) && string.IsNullOrWhiteSpace((string?)result["RecoveryDirectory"])) throw new InvalidDataException("Replacement lacks recovery directory.");
        if(result["Imported"] is not JsonArray importedNames || !importedNames.Select(x=>x?.GetValue<string>()).SequenceEqual(items.OfType<JsonObject>().Where(x=>Text(x,"Status")=="imported").SelectMany(x=>x["ReturnedObjects"]!.AsArray().Select(o=>o!["Name"]!.GetValue<string>())))) throw new InvalidDataException("Imported names disagree with actual returned objects.");
        if(result["Restored"] is JsonArray restored && !JsonNode.DeepEquals(restored,new JsonArray(items.Where(x=>(string?)x?["RestoreStatus"]=="restored").Select(x=>x!["Replaced"]!.DeepClone()).ToArray()))) throw new InvalidDataException("Restored summary disagrees with item evidence.");
        if(result["RemainingChanged"] is JsonArray remaining && !JsonNode.DeepEquals(remaining,new JsonArray(items.Where(x=>(bool?)x?["Attempted"]==true && (string?)x?["RestoreStatus"]!="restored").Select(x=>x!["Planned"]!.DeepClone()).ToArray()))) throw new InvalidDataException("Remaining-change summary disagrees with item evidence.");
        if(result["Failed"] is not JsonArray failedItems || failedItems.Count!=failures) throw new InvalidDataException("Failure summary disagrees with outcomes.");
        foreach(var node in failedItems)
        {
            if(node is not JsonObject summary || !items.OfType<JsonObject>().Any(x=>Text(x,"Status")=="failed" && Text(x,"RelativePath")==Text(summary,"Path") && Text(x,"Failure")==Text(summary,"Error"))) throw new InvalidDataException("Invalid failure summary.");
        }
        return result;
    }
}
