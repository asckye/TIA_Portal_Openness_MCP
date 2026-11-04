using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using OpenPipeRequest = TiaMcp.Logic.V4.Domain.OpenPipeRequest;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.Runtime;

namespace TiaMcpServer.ModelContextProtocol
{
    internal sealed partial class RuntimeChannelTools
    {

        private static JsonArray OpenPipeTags(OpenPipeResponse resp, bool includeValues)
        {
            var arr = new JsonArray();
            foreach (var t in resp.Tags)
            {
                var o = new JsonObject { ["name"] = t.Name };
                if (includeValues)
                {
                    o["value"] = t.Value;
                    o["quality"] = t.Quality;
                    o["qualityCode"] = t.QualityCode;
                    o["timeStamp"] = t.TimeStamp;
                }
                o["errorCode"] = t.ErrorCode;
                if (!t.Ok) o["error"] = string.IsNullOrEmpty(t.ErrorDescription) ? ("Open Pipe error code " + t.ErrorCode) : t.ErrorDescription;
                arr.Add(o);
            }
            return arr;
        }

        [McpServerTool(Name = "GetUnifiedRuntimeTags"),
                Description("[L2][Online-Monitoring][ONLINE] Read current values of WinCC Unified RUNTIME tags (HMI tags incl. their PLC-connected values) " +
                "through the official WinCC Unified Open Pipe (local named pipe \\\\.\\pipe\\HmiRuntime of the running Runtime; no ODK license, no TIA " +
                "Openness). Sends one ReadTag request and returns value, quality, quality code and time stamp per tag; per-tag errors (e.g. 'Tag does not " +
                "exist') are in items[].error. Tag names are the runtime names, e.g. 'Tag_1' or 'HMI_RT_1::Tag_1'. Read-only, opens no subscription. " +
                "Requires: this MCP server runs ON the Runtime PC and its user is in the 'SIMATIC HMI' group.")]
        public CallToolResult ReadUnifiedRuntimeTagsV4(
            [Description("tags: structured string array input.")] string[] tags,
            [Description("pipeName: named pipe of the Runtime; default \\\\.\\pipe\\HmiRuntime (bare 'HmiRuntime' also accepted). Remote machines are " +
                "refused.")] string pipeName =
                RuntimeChannelsLogic.DefaultOpenPipeName,
            [Description("timeoutMs: connect + response timeout in ms (500..120000).")] int timeoutMs = 5000)
            => RuntimeToolContract.Run("GetUnifiedRuntimeTags", true, false, () =>
            {
                return ReadUnifiedRuntimeTags(RuntimeToolContract.Names(tags, "tags", false), pipeName, timeoutMs);
            });

        public ResponseJsonReport ReadUnifiedRuntimeTags(string tagsJson, string pipeName = RuntimeChannelsLogic.DefaultOpenPipeName, int timeoutMs = 5000)
        {
            try
            {
                List<string> tags;
                try { tags = RuntimeChannelsLogic.ParseNameList(tagsJson, nameof(tagsJson)); RuntimeChannelsLogic.ParsePipeName(pipeName); }
                catch (ArgumentException bad) { return RuntimeRefusal(bad.Message); }

                string cookie = RuntimeChannelsLogic.NewClientCookie("read");
                string line = RuntimeChannelsLogic.BuildReadTagRequest(tags, cookie);
                var ex = UnifiedOpenPipeChannel.Exchange(pipeName, line, cookie, "ReadTag", timeoutMs);
                string? failure = OpenPipeFailure(ex, "ReadTag");
                var data = new JsonObject
                {
                    ["pipeName"] = pipeName,
                    ["channel"] = "WinCC Unified Open Pipe (ReadTag, read-only)",
                    ["connected"] = ex.Connected,
                    ["elapsedMs"] = ex.ElapsedMs,
                    ["items"] = ex.Response != null ? OpenPipeTags(ex.Response, true) : new JsonArray(),
                    ["safety"] = OpenPipeSafety(false)
                };
                if (ex.SkippedLines.Count > 0) data["skippedLines"] = new JsonArray(ex.SkippedLines.Select(s => (JsonNode)s).ToArray());
                if (failure != null) data["error"] = failure;
                bool ok = failure == null && ex.Response != null && ex.Response.Tags.All(t => t.Ok);
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = failure ?? (ok
                        ? $"Read {ex.Response!.Tags.Count} runtime tag(s) via Open Pipe in {ex.ElapsedMs} ms."
                        : $"Open Pipe answered, but {ex.Response!.Tags.Count(t => !t.Ok)} of {ex.Response.Tags.Count} tag(s) failed (see items[].error)."),
                    Data = data,
                    Meta = RuntimeMeta(ok)
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"GetUnifiedRuntimeTags failed: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "WriteUnifiedRuntimeTags"),
                Description("[L2][Online-Monitoring][ONLINE-WRITE] Write values to WinCC Unified RUNTIME tags through WinCC Unified Open Pipe (WriteTag on " +
                "\\\\.\\pipe\\HmiRuntime). PLC-connected HMI tags forward the value to the PLC, so this changes a running system. Defaults to PREVIEW " +
                "(dryRun=true): validates the writes and reads the CURRENT values (ReadTag) only. A real write needs dryRun=false AND confirmWrite=true; " +
                "anything else is refused. After WriteTag the tags are read back and compared (items[].verified); a mismatch or a per-tag WriteTag error is " +
                "reported as failure. Values: JSON bool/number/string scalars (the Runtime converts to the tag's data type). Local only; user must be in the " +
                "'SIMATIC HMI' group. No TIA project change. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult WriteUnifiedRuntimeTagsV4(
            [Description("writes: array of {name, value} writes; scalar values only, maximum 500.")] WriteValue[] writes,
            [Description("pipeName: named pipe of the Runtime; default \\\\.\\pipe\\HmiRuntime.")] string pipeName = RuntimeChannelsLogic.DefaultOpenPipeName,
            [Description("timeoutMs: connect + response timeout in ms (500..120000).")] int timeoutMs = 5000,
            [Description("confirmWrite: must be true (with dryRun=false) to actually write; default false refuses.")] bool confirmWrite = false,
            [Description("dryRun: true (default) previews and reads current values only.")] bool dryRun = true)
            => RuntimeToolContract.Run("WriteUnifiedRuntimeTags", dryRun, true, () =>
            {
                RuntimeToolContract.Confirm(dryRun, confirmWrite);
                return WriteUnifiedRuntimeTags(RuntimeToolContract.Writes(writes, "writes", dryRun, confirmWrite), pipeName, timeoutMs, confirmWrite, dryRun);
            });

        public ResponseJsonReport WriteUnifiedRuntimeTags(string writesJson, string pipeName = RuntimeChannelsLogic.DefaultOpenPipeName, int timeoutMs = 5000,
                bool confirmWrite = false, bool dryRun = true)
        {
            try
            {
                List<RuntimeWriteItem> writes;
                try { writes = RuntimeChannelsLogic.ParseWriteList(writesJson, nameof(writesJson)); RuntimeChannelsLogic.ParsePipeName(pipeName); }
                catch (ArgumentException bad) { return RuntimeRefusal(bad.Message); }
                if (!dryRun && !confirmWrite)
                    return RuntimeRefusal($"Refused: writing {writes.Count} runtime tag(s) requires confirmWrite=true together with dryRun=false. Nothing was written.",
                        new JsonObject { ["plannedWrites"] = PlannedWrites(writes), ["safety"] = OpenPipeSafety(false) });

                var names = writes.Select(w => w.Name).ToList();
                string readCookie = RuntimeChannelsLogic.NewClientCookie("before");
                var before = UnifiedOpenPipeChannel
                    .Exchange(pipeName, RuntimeChannelsLogic.BuildReadTagRequest(names, readCookie), readCookie, "ReadTag", timeoutMs);
                string? failure = OpenPipeFailure(before, "ReadTag");
                var data = new JsonObject
                {
                    ["pipeName"] = pipeName,
                    ["channel"] = "WinCC Unified Open Pipe (WriteTag)",
                    ["connected"] = before.Connected,
                    ["status"] = dryRun ? "Preview" : "Failed",
                    ["safety"] = OpenPipeSafety(!dryRun)
                };
                var meta = RuntimeMeta(false, dryRun, false);
                if (failure != null)
                {
                    data["error"] = failure;
                    data["requestLine"] = dryRun ? RuntimeChannelsLogic.BuildWriteTagRequest(writes, "<cookie>") : null;
                    return new ResponseJsonReport { Ok = false, Message = "Pre-write ReadTag failed, nothing written: " + failure, Data = data, Meta = meta };
                }
                var beforeTags = before.Response!.Tags.ToDictionary(t => t.Name, t => t, StringComparer.Ordinal);
                var items = new JsonArray();
                foreach (var w in writes)
                {
                    beforeTags.TryGetValue(w.Name, out var bt);
                    var o = new JsonObject { ["name"] = w.Name, ["requested"] = w.Value?.DeepClone(), ["before"] = bt?.Value, ["beforeQuality"] = bt?.Quality };
                    if (bt == null) o["beforeError"] = "not in ReadTag response";
                    else if (!bt.Ok) o["beforeError"] = bt.ErrorDescription;
                    items.Add(o);
                }
                data["items"] = items;
                if (dryRun)
                {
                    data["requestLine"] = RuntimeChannelsLogic.BuildWriteTagRequest(writes, "<cookie>");
                    bool allKnown = writes.All(w => beforeTags.TryGetValue(w.Name, out var t) && t.Ok);
                    // envelope: legacy-single-verdict
                    meta["success"] = allKnown;
                    return new ResponseJsonReport
                    {
                        Ok = allKnown,
                        Message = allKnown
                            ? $"Preview: {writes.Count} tag write(s) validated and current values read; nothing written. Re-run with dryRun=false and confirmWrite=true to write."
                            : "Preview: some tags could not be read (see items[].beforeError); fix the names before writing. Nothing written.",
                        Data = data,
                        Meta = meta
                    };
                }

                // Refuse to write tags the Runtime does not know: WriteTag would just fail per tag,
                // but a partial batch is harder to reason about than a clean refusal.
                var unknown = writes.Where(w => !beforeTags.TryGetValue(w.Name, out var t) || !t.Ok).Select(w => w.Name).ToList();
                if (unknown.Count > 0)
                {
                    data["error"] = "Not written: these tags could not be read before writing: " + string.Join(", ", unknown);
                    return new ResponseJsonReport { Ok = false, Message = data["error"]!.ToString(), Data = data, Meta = meta };
                }

                meta["mayHaveChanged"] = true;
                string writeCookie = RuntimeChannelsLogic.NewClientCookie("write");
                var write = UnifiedOpenPipeChannel
                    .Exchange(pipeName, RuntimeChannelsLogic.BuildWriteTagRequest(writes, writeCookie), writeCookie, "WriteTag", timeoutMs);
                failure = OpenPipeFailure(write, "WriteTag");
                data["elapsedMs"] = before.ElapsedMs + write.ElapsedMs;
                if (failure != null)
                {
                    data["error"] = failure;
                    return new ResponseJsonReport {
                        Ok = false, Message = "WriteTag failed (values may or may not have been applied): " + failure, Data = data, Meta = meta };
                }
                var writeTags = write.Response!.Tags.ToDictionary(t => t.Name, t => t, StringComparer.Ordinal);

                string afterCookie = RuntimeChannelsLogic.NewClientCookie("after");
                var after = UnifiedOpenPipeChannel
                    .Exchange(pipeName, RuntimeChannelsLogic.BuildReadTagRequest(names, afterCookie), afterCookie, "ReadTag", timeoutMs);
                string? afterFailure = OpenPipeFailure(after, "ReadTag");
                var afterTags = after.Response?.Tags.ToDictionary(t => t.Name, t => t, StringComparer.Ordinal) ?? new Dictionary<string,
                    OpenPipeTagResult>(StringComparer.Ordinal);
                data["elapsedMs"] = before.ElapsedMs + write.ElapsedMs + after.ElapsedMs;

                int failed = 0;
                foreach (var w in writes)
                {
                    var o = (JsonObject)items.First(n => n!["name"]!.ToString() == w.Name)!;
                    writeTags.TryGetValue(w.Name, out var wt);
                    afterTags.TryGetValue(w.Name, out var at);
                    o["writeAttempted"] = true;
                    o["writeAccepted"] = wt != null && wt.Ok;
                    o["after"] = at?.Value;
                    o["afterQuality"] = at?.Quality;
                    o["writeOutcomeKnown"] = wt != null && (!wt.Ok || at != null && at.Ok);
                    bool verified = wt != null && wt.Ok && at != null && at.Ok && RuntimeChannelsLogic.ValuesMatch(w.Value, JsonValue.Create(at.Value));
                    o["verified"] = verified;
                    if (wt == null) o["error"] = "no entry for this tag in the NotifyWriteTag response";
                    else if (!wt.Ok) o["error"] = "WriteTag: " + wt.ErrorDescription + " (ErrorCode " + wt.ErrorCode + ")";
                    else if (afterFailure != null) o["error"] = "Readback failed: " + afterFailure;
                    else if (at == null || !at.Ok) o["error"] = "Readback failed: " + (at?.ErrorDescription ?? "tag missing in response");
                    else if (!verified) o["error"] =
                        $"Readback '{at.Value}' differs from requested {w.Value?.ToJsonString()} (the PLC or a script may overwrite this tag).";
                    if (o["error"] != null) failed++;
                }
                bool ok = failed == 0;
                data["status"] = ok ? "WrittenAndVerified" : "Failed";
                meta["success"] = ok;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? $"Wrote and verified {writes.Count} runtime tag(s) via Open Pipe."
                        : $"{failed} of {writes.Count} tag write(s) failed or did not verify (see items[].error).",
                    Data = data,
                    Meta = meta
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"WriteUnifiedRuntimeTags failed: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetUnifiedRuntimeAlarms"),
                Description("[L2][Online-Monitoring][ONLINE] Read the currently ACTIVE alarms of WinCC Unified Runtime through WinCC Unified Open Pipe " +
                "(ReadAlarm on \\\\.\\pipe\\HmiRuntime). Returns the alarm objects as the Runtime reports them (Name, State, StateText, EventText, " +
                "RaiseTime, Priority, AlarmClassName, Tag, Value, ...). Optional filter uses the Runtime alarm filter syntax (e.g. \"AlarmClassName != " +
                "'Warning'\"); empty systemNames means all systems. Read-only, no acknowledgement, no subscription. Local only; user must be in the 'SIMATIC " +
                "HMI' group.")]
        public CallToolResult ReadUnifiedRuntimeAlarmsV4(
            [Description("systemNames: structured string array input.")] string[] systemNames = null!,
            [Description("filter: optional alarm filter expression (Runtime syntax), e.g. \"Priority >= 10\". Empty = no filter.")] string filter = "",
            [Description("languageId: Windows LCID for alarm texts, e.g. 1033 (en-US), 1031 (de-DE), 2052 (zh-CN).")] int languageId = 1033,
            [Description("pipeName: named pipe of the Runtime; default \\\\.\\pipe\\HmiRuntime.")] string pipeName = RuntimeChannelsLogic.DefaultOpenPipeName,
            [Description("timeoutMs: connect + response timeout in ms (500..120000).")] int timeoutMs = 5000,
            [Description("maxAlarms: cap on returned alarms (1..500); the total count is always reported.")] int maxAlarms = 200)
            => RuntimeToolContract.Run("GetUnifiedRuntimeAlarms", true, false, () =>
            {
                return ReadUnifiedRuntimeAlarms(RuntimeToolContract.Names(systemNames, "systemNames", true), filter, languageId, pipeName, timeoutMs,
                        maxAlarms);
            });

        public ResponseJsonReport ReadUnifiedRuntimeAlarms(string systemNamesJson = "[]", string filter = "", int languageId = 1033, string pipeName =
                RuntimeChannelsLogic.DefaultOpenPipeName, int timeoutMs = 5000, int maxAlarms = 200)
        {
            try
            {
                List<string> systems;
                try
                {
                    systems = string.IsNullOrWhiteSpace(systemNamesJson) || systemNamesJson.Trim() == "[]" ? new List<string>() : RuntimeChannelsLogic
                        .ParseNameList(systemNamesJson, nameof(systemNamesJson));
                    RuntimeChannelsLogic.ParsePipeName(pipeName);
                }
                catch (ArgumentException bad) { return RuntimeRefusal(bad.Message); }
                if (languageId <= 0) return RuntimeRefusal("languageId must be a positive Windows LCID, e.g. 1033.");
                int cap = Math.Min(Math.Max(maxAlarms, 1), 500);

                string cookie = RuntimeChannelsLogic.NewClientCookie("alarms");
                var ex = UnifiedOpenPipeChannel
                    .Exchange(pipeName, RuntimeChannelsLogic.BuildReadAlarmRequest(systems, filter ?? "", languageId, cookie), cookie, "ReadAlarm", timeoutMs);
                string? failure = OpenPipeFailure(ex, "ReadAlarm");
                var alarms = new JsonArray();
                int total = 0;
                if (ex.Response?.Params is JsonObject p && p["Alarms"] is JsonArray arr)
                {
                    total = arr.Count;
                    foreach (var a in arr.Take(cap)) alarms.Add(a?.DeepClone());
                }
                var data = new JsonObject
                {
                    ["pipeName"] = pipeName,
                    ["channel"] = "WinCC Unified Open Pipe (ReadAlarm, read-only)",
                    ["connected"] = ex.Connected,
                    ["elapsedMs"] = ex.ElapsedMs,
                    ["filter"] = filter ?? "",
                    ["languageId"] = languageId,
                    ["totalCount"] = total,
                    ["returnedCount"] = alarms.Count,
                    ["dataComplete"] = failure == null && alarms.Count == total,
                    ["alarms"] = alarms,
                    ["safety"] = OpenPipeSafety(false)
                };
                if (failure != null) data["error"] = failure;
                bool ok = failure == null;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = failure ?? $"{total} active alarm(s) reported by the Runtime"
                        + (alarms.Count < total ? $" ({alarms.Count} returned, raise maxAlarms for more)." : "."),
                    Data = data,
                    Meta = RuntimeMeta(ok)
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"GetUnifiedRuntimeAlarms failed: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "InvokeUnifiedOpenPipe"),
                Description("[L2][Online-Monitoring][ONLINE-WRITE] Send ONE raw WinCC Unified Open Pipe expert-syntax request (object with 'message', " +
                "message-specific 'params' and optional 'clientCookie'; protocol member casing is restored when sent) to the local Runtime and return the " +
                "matching response line, e.g. BrowseTags, BrowseConfiguredAlarms, BrowseAlarmClasses, ReadConfig, ReadTag, ReadAlarm, WriteTag, WriteConfig. " +
                "Defaults to PREVIEW (dryRun=true): validates the JSON and shows the exact line that would be sent, nothing is sent. Read-only messages " +
                "(Read*/Browse*) are sent with dryRun=false; any other message (WriteTag, WriteConfig, ...) additionally requires confirmWrite=true. " +
                "Subscribe*/Unsubscribe* are refused (streaming). No readback verification is performed here; prefer " +
                "GetUnifiedRuntimeTags/WriteUnifiedRuntimeTags for tags. Local only. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult UnifiedOpenPipeRequestV4(
            [Description("request: object containing message, params and optional clientCookie.")] OpenPipeRequest request,
            [Description("pipeName: named pipe of the Runtime; default \\\\.\\pipe\\HmiRuntime.")] string pipeName = RuntimeChannelsLogic.DefaultOpenPipeName,
            [Description("timeoutMs: connect + response timeout in ms (500..120000).")] int timeoutMs = 5000,
            [Description("confirmWrite: required (with dryRun=false) for any message that is not Read*/Browse*.")] bool confirmWrite = false,
            [Description("dryRun: true (default) validates and shows the request line without sending it.")] bool dryRun = true)
            => RuntimeToolContract.Run("InvokeUnifiedOpenPipe", dryRun || request != null && request.IsReadOnly, true, () =>
            {
                RuntimeToolContract.Confirm(dryRun || request != null && request.IsReadOnly, confirmWrite);
                return UnifiedOpenPipeRequest(RuntimeToolContract.Request(request!), pipeName, timeoutMs, confirmWrite, dryRun);
            });

        public ResponseJsonReport UnifiedOpenPipeRequest(string requestJson, string pipeName = RuntimeChannelsLogic.DefaultOpenPipeName, int timeoutMs = 5000,
                bool confirmWrite = false, bool dryRun = true)
        {
            try
            {
                string line, message, cookie; bool readOnly;
                try { (line, message, cookie, readOnly) = RuntimeChannelsLogic.PrepareRawRequest(requestJson); RuntimeChannelsLogic.ParsePipeName(pipeName); }
                catch (ArgumentException bad) { return RuntimeRefusal(bad.Message); }
                catch (NotSupportedException bad) { return RuntimeRefusal(bad.Message); }

                var data = new JsonObject
                {
                    ["pipeName"] = pipeName,
                    ["channel"] = "WinCC Unified Open Pipe (raw request)",
                    ["message"] = message,
                    ["readOnlyMessage"] = readOnly,
                    ["clientCookie"] = cookie,
                    ["requestLine"] = line,
                    ["safety"] = OpenPipeSafety(!readOnly)
                };
                if (dryRun)
                    return new ResponseJsonReport
                    {
                        Ok = true,
                        Message = $"Preview: '{message}' request validated; nothing sent. Re-run with dryRun=false"
                            + (readOnly ? "" : " and confirmWrite=true") + " to send it.",
                        Data = data,
                        Meta = RuntimeMeta(true, true, false)
                    };
                if (!readOnly && !confirmWrite)
                    return RuntimeRefusal($"Refused: '{message}' is not a read-only Open Pipe message; sending it requires confirmWrite=true together with dryRun=false. Nothing sent.",
                            data);

                var ex = UnifiedOpenPipeChannel.Exchange(pipeName, line, cookie, message, timeoutMs);
                data["connected"] = ex.Connected;
                data["elapsedMs"] = ex.ElapsedMs;
                data["responseLine"] = ex.ResponseLine;
                data["response"] = ex.Response?.Raw?.DeepClone();
                if (ex.SkippedLines.Count > 0) data["skippedLines"] = new JsonArray(ex.SkippedLines.Select(s => (JsonNode)s).ToArray());
                string? failure = OpenPipeFailure(ex, message);
                if (failure != null) data["error"] = failure;
                bool ok = failure == null;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = failure ?? $"'{message}' answered with '{ex.Response!.Message}' in {ex.ElapsedMs} ms.",
                    Data = data,
                    Meta = RuntimeMeta(ok, false, !readOnly)
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"InvokeUnifiedOpenPipe failed: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }
    }
}
