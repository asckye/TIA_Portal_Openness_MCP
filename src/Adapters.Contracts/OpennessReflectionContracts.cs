using System;
using System.Collections.Generic;

namespace TiaMcp.Adapters.Contracts
{
    public static class OpennessReflectionPolicy
    {
        public static string? ReflectionRefusal(string? service, string? method)
        {
            const string type = "Siemens.Engineering.CrossReference.CrossReferenceService";
            var suffix = (service ?? "").Trim();
            bool matches = suffix.Length > 0 && (type.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                || suffix.IndexOf("CrossReference", StringComparison.OrdinalIgnoreCase) >= 0);
            return matches || string.Equals(method, "GetCrossReferences", StringComparison.OrdinalIgnoreCase)
                ? "Native PLC cross references cannot be accessed through reflection. Use the guarded GetPlcCrossReferences tool; its default-disabled policy and consistency checks also apply after explicit opt-in."
                : null;
        }
    }

    public sealed class OpennessReflectionRequest
    {
        public string Operation { get; set; } = "";
        public string ObjectKind { get; set; } = "";
        public string ObjectPath { get; set; } = "";
        public string SoftwarePath { get; set; } = "";
        public string PropertyPath { get; set; } = "";
        public string ServiceTypeSuffix { get; set; } = "";
        public string MethodName { get; set; } = "";
        public HardwareScalar[]? Arguments { get; set; }
        public int Limit { get; set; } = 200;
        public bool AllowWrite { get; set; }
    }

    public sealed class OpennessMember
    {
        public string? Name { get; set; }
        public string? Kind { get; set; }
        public string? Type { get; set; }
        public string? Signature { get; set; }
    }

    public class OpennessObservation
    {
        public string Message { get; set; } = "";
        public Dictionary<string, object?> Meta { get; set; } = new Dictionary<string, object?>();
        public string? ObjectKind { get; set; }
        public string? ObjectPath { get; set; }
        public string? TypeName { get; set; }
        public IEnumerable<OpennessMember>? Members { get; set; }
        public string? ValueType { get; set; }
        public object? Value { get; set; }
        public string? Collection { get; set; }
        public IEnumerable<string>? Items { get; set; }
    }

    public sealed class OpennessReflectionReply : IWorkerOperationReply
    {
        public OpennessObservation? Observation { get; set; }
        public PlcSoftwareFailure? Failure { get; set; }
        public bool MayHaveChanged { get; set; }
        public bool RequiresSessionReset { get; set; }
        public bool BlockReadsAfterUncertain => true;
    }
}
