using System;
using System.Collections.Generic;
namespace TiaMcp.Adapters.Contracts
{
    public sealed class PlcWatchRow { public string Name { get; set; } = ""; public string Address { get; set; } = ""; public string Format { get; set; } = "";
    }
    public sealed class PlcTagAddress {
        public string Name { get; set; } = ""; public string Address { get; set; } = ""; public string DataType { get; set; } = ""; }
    public sealed class PlcApiMember {
        public string? Kind { get; set; } public string? Name { get; set; } public string? Type { get; set; } public string? Signature { get; set; } }
    public sealed class PlcWatchReport
    {
        public bool? Ok { get; set; }
        public string? Message { get; set; }
        public Dictionary<string, object?>? Data { get; set; }
        public string[]? Warnings { get; set; }
        public Dictionary<string, object?>? Meta { get; set; }
        internal static PlcWatchReport? EntryAccessLimit(string release, Dictionary<string, object?> data, bool monitoring)
        {
            if (OpennessApi.WatchForceEntries.Includes(release)) return null;
            string message = $"Watch table entries cannot be read on release {release}. Typed entry access requires V17 or later.";
            return new PlcWatchReport { Ok = monitoring, Message = message, Data = data, Meta = new Dictionary<string, object?> {
                ["dataComplete"] = false, ["mayHaveChanged"] = false,
                ["status"] = monitoring ? "Partial" : "NotSupportedOnVersion", ["nativeMessage"] = message } };
        }

        public PlcWatchRow[] Rows { get; set; } = Array.Empty<PlcWatchRow>();
    }
}
