using System;
using System.Collections.Generic;

namespace TiaMcp.Adapters.Contracts
{
    public static class LegacyResponseMetadata
    {
        public static Dictionary<string, object?> Basic(DateTime? timestamp, bool success, string? rejection = null)
        {
            var meta = new Dictionary<string, object?>();
            if (timestamp.HasValue) meta["timestamp"] = timestamp.Value;
            meta["success"] = success;
            if (rejection != null) meta["v4Rejection"] = rejection;
            return meta;
        }
    }
}
