using System;
using System.Collections.Generic;

namespace TiaMcp.Adapters.Contracts
{
    public sealed class IoAddressInfo
    {
        public string IoType { get; set; } = "";
        public int StartAddress { get; set; }
        public int Length { get; set; }
        public override string ToString() => $"{IoType} start={StartAddress} length={Length}";
    }

    public sealed class HardwareAddressRow
    {
        public int StartAddress { get; set; }
        public int Length { get; set; }
        public string IoType { get; set; } = "";
        public string[][] Controllers { get; set; } = Array.Empty<string[]>();
        public Dictionary<string, object?> Attributes { get; set; } = new Dictionary<string, object?>();
        // The optional evidence bag uses the released names and insertion order.
        public Dictionary<string, object?> ToEvidence() => new Dictionary<string, object?> {
            ["startAddress"] = StartAddress, ["length"] = Length, ["ioType"] = IoType,
            ["controllers"] = Controllers, ["attributes"] = Attributes
        };
    }

    public sealed class HardwareAddressingException : Exception
    {
        public string Status { get; }
        public HardwareAddressingException(string status, string message) : base(message) { Status = status; }
    }

    public sealed class IoAddressWriteReply : IWorkerOperationReply
    {
        public bool Ok { get; set; }
        public string Message { get; set; } = "";
        public IoAddressInfo? Before { get; set; }
        public IoAddressInfo? After { get; set; }
        public bool RequiresSessionReset { get; set; }
        public bool MayHaveChanged => Before != null;
        public bool BlockReadsAfterUncertain => true;
    }

    // Scalar values are explicit at the worker boundary. Native code never parses JSON.
    public sealed class HardwareScalar
    {
        public string Kind { get; set; } = "null";
        public string Text { get; set; } = "";
    }

    public sealed class HardwareAddressUpdate
    {
        public string[] DevicePath { get; set; } = Array.Empty<string>();
        public string[] ItemPath { get; set; } = Array.Empty<string>();
        public string IoType { get; set; } = "";
        public int StartAddress { get; set; }
        public Dictionary<string, HardwareScalar> Properties { get; set; } = new Dictionary<string, HardwareScalar>(StringComparer.Ordinal);
        public Dictionary<string, HardwareScalar> Attributes { get; set; } = new Dictionary<string, HardwareScalar>(StringComparer.Ordinal);
        public string SoftwarePath { get; set; } = "";
        public string ProcessImageObName { get; set; } = "";
        public bool DryRun { get; set; } = true;
    }

    public sealed class HardwareAddressingReply : IWorkerOperationReply
    {
        private bool mayHaveChanged;
        public string Message { get; set; } = "";
        // The evidence bag preserves the released step wrapper's optional fields.
        // Values are managed scalars, arrays and dictionaries; never native handles.
        public Dictionary<string, object?> Meta { get; set; } = new Dictionary<string, object?>(StringComparer.Ordinal);
        public bool RequiresSessionReset { get; set; }
        public bool MayHaveChanged
        {
            get => mayHaveChanged || Meta.TryGetValue("mayHaveChanged", out var value) && Equals(value, true);
            set => mayHaveChanged = value;
        }
        public bool BlockReadsAfterUncertain => true;
    }

    public sealed class HardwareIpNode
    {
        public string NodeName { get; set; } = "";
        public string Address { get; set; } = "";
        public string NodeType { get; set; } = "";
        public string ConnectedSubnet { get; set; } = "";
        public string InterfacePath { get; set; } = "";
        public bool IsIndustrialEthernet { get; set; }
    }

    public sealed class HardwareIpReply
    {
        public bool Found { get; set; }
        public string? Device { get; set; }
        public string? IpAddress { get; set; }
        public string? Message { get; set; }
        public HardwareIpNode[] Nodes { get; set; } = Array.Empty<HardwareIpNode>();
    }
}
