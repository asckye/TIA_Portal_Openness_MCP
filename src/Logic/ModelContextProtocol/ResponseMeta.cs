using System;
using System.Text.Json.Nodes;

namespace TiaMcpServer.ModelContextProtocol
{
    // Keep the original key order and JsonValue CLR types. Callers still own messages,
    // exception formatting and native work; take the stamp at the original call position.
    internal static class ResponseMeta
    {
        internal static JsonObject Stamp() => new JsonObject { ["timestamp"] = ResponseClock.Now };

        internal static JsonObject Basic(bool success) => new JsonObject
        {
            ["timestamp"] = ResponseClock.Now,
            ["success"] = success
        };

        internal static JsonObject Basic(bool success, params (string Key, JsonNode? Value)[] after)
        {
            var meta = Basic(success);
            Append(meta, after);
            return meta;
        }

        // Inline callers evaluate the clock before success and the remaining values.
        internal static JsonObject Basic(DateTime timestamp, bool success, params (string Key, JsonNode? Value)[] after)
        {
            var meta = new JsonObject { ["timestamp"] = timestamp, ["success"] = success };
            Append(meta, after);
            return meta;
        }

        internal static JsonObject Unstamped(bool success, params (string Key, JsonNode? Value)[] after)
        {
            var meta = new JsonObject { ["success"] = success };
            Append(meta, after);
            return meta;
        }

        internal static JsonObject StampThen(params (string Key, JsonNode? Value)[] after)
        {
            var meta = Stamp();
            Append(meta, after);
            return meta;
        }

        internal static JsonObject Step(string tool, params (string Key, JsonNode? Value)[] after)
        {
            var meta = new JsonObject
            {
                ["timestamp"] = ResponseClock.Now,
                ["tool"] = tool,
                ["success"] = false
            };
            Append(meta, after);
            return meta;
        }

        // HMI keeps the action's business verdict; offline executors pass true explicitly.
        internal static void Complete(JsonObject meta) => Complete(meta, meta["operationSuccess"]?.GetValue<bool>() ?? true);

        internal static void Complete(JsonObject meta, bool success)
        {
            meta["success"] = success;
            meta["operationSuccess"] = meta["success"]?.DeepClone();
        }

        // Matches the contiguous failure fields in RunOfflineAnalysisTool. Leave success,
        // error and status alone: their assignments differ between executor families.
        internal static void Failed(JsonObject meta)
        {
            meta["operationSuccess"] = false;
            meta["apiCallSuccess"] = false;
            meta["dataComplete"] = false;
        }

        internal static JsonObject LegacyBatch() => new JsonObject
        {
            ["success"] = false,
            ["timestamp"] = ResponseClock.UtcNow
        };

        internal static JsonObject Bridge(bool bridgeSuccess, JsonObject? operationMeta = null)
        {
            bool? operationSuccess = null;
            if (bridgeSuccess && operationMeta?["success"] is JsonValue value && value.TryGetValue<bool>(out var success))
                operationSuccess = success;
            return new JsonObject
            {
                ["timestamp"] = ResponseClock.Now,
                ["bridgeSuccess"] = bridgeSuccess,
                ["operationSuccess"] = operationSuccess,
                ["success"] = bridgeSuccess ? operationSuccess : false,
                ["operationStatus"] = !bridgeSuccess ? "notCompleted" : operationSuccess == true ? "succeeded" : operationSuccess == false ? "failed" : "unknown"
            };
        }

        internal static JsonObject RoundTripStamp() => new JsonObject { ["timestamp"] = ResponseClock.Now.ToString("O") };

        private static void Append(JsonObject meta, (string Key, JsonNode? Value)[] after)
        {
            // Indexer assignment preserves overwrite position and takes ownership just as
            // an initializer does. Do not clone, enumerate external objects or stringify values.
            foreach (var item in after) meta[item.Key] = item.Value;
        }
    }
}
