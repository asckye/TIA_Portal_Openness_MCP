using System;
using System.Collections.Generic;
namespace TiaMcp.Adapters.Contracts
{
    public sealed class PlcWatchRow { public string Name { get; set; } = ""; public string Address { get; set; } = ""; public string Format { get; set; } = ""; }
    public sealed class PlcTagAddress { public string Name { get; set; } = ""; public string Address { get; set; } = ""; public string DataType { get; set; } = ""; }
    public sealed class PlcApiMember { public string? Kind { get; set; } public string? Name { get; set; } public string? Type { get; set; } public string? Signature { get; set; } }
    public sealed class PlcWatchReport
    {
        public bool? Ok { get; set; }
        public string? Message { get; set; }
        public Dictionary<string, object?>? Data { get; set; }
        public string[]? Warnings { get; set; }
        public Dictionary<string, object?>? Meta { get; set; }
        public PlcWatchRow[] Rows { get; set; } = Array.Empty<PlcWatchRow>();
    }
}
