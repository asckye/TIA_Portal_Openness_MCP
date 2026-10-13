using System;
using System.Collections.Generic;

namespace TiaMcp.Adapters.Contracts
{
#if TIA_PLCSIM_COMPONENT
    internal static class LegacyResponseMetadata
#else
    public static class LegacyResponseMetadata
#endif
    {
        public static Dictionary<string, object?> Basic(DateTime? timestamp, bool success, string? rejection = null)
        {
            var meta = new Dictionary<string, object?>();
            if (timestamp.HasValue) meta["timestamp"] = timestamp.Value;
            meta["success"] = success;
            if (rejection != null) meta["v4Rejection"] = rejection;
            return meta;
        }

        public static Dictionary<string, object?> Step(DateTime timestamp, string tool)
        {
            var meta = new Dictionary<string, object?> { ["timestamp"] = timestamp, ["tool"] = tool };
            foreach (var pair in Basic(null, false)) meta[pair.Key] = pair.Value;
            return meta;
        }

        public static void Complete(Dictionary<string, object?> meta)
            => Complete(meta, !meta.TryGetValue("operationSuccess", out var verdict) || !Equals(verdict, false));

        public static void Complete(Dictionary<string, object?> meta, bool success)
        {
            foreach (var pair in Basic(null, success)) meta[pair.Key] = pair.Value;
            meta["operationSuccess"] = meta["success"];
        }
    }
}
