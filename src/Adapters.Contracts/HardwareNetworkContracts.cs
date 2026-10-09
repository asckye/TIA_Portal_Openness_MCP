using System.Collections.Generic;

namespace TiaMcp.Adapters.Contracts
{
    public sealed class HardwareNetworkAttribute
    {
        public string? Name { get; set; }
        public string? Value { get; set; }
        public string? DataType { get; set; }
        public bool? IsWritable { get; set; }
        public string? WriteProbeError { get; set; }
    }
    // The facade wraps lines explicitly so its refusal survives wire serialization.
    public sealed class HardwareProbeLines : List<string>
    {
        public string? FailureCode { get; set; }
    }
    public sealed class HardwareProbeReply
    {
        public string[] Lines { get; set; } = System.Array.Empty<string>();
        public string? FailureCode { get; set; }
    }
}
