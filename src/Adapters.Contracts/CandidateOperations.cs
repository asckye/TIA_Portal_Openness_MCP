using System;
using System.Linq;
using System.Reflection;

namespace TiaMcp.Adapters.Contracts.Candidates
{
    public sealed class DeviceCandidateCall
    {
        public string Action { get; set; } = "identity";
        public string TypeIdentifier { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public DeviceCreateCheck? Check { get; set; }
    }
    public sealed class DeviceCandidateReply : TiaMcp.Adapters.Contracts.IWorkerOperationReply
    {
        bool TiaMcp.Adapters.Contracts.IWorkerOperationReply.MayHaveChanged => Attempt?.Issued == true;
        bool TiaMcp.Adapters.Contracts.IWorkerOperationReply.BlockReadsAfterUncertain => true;
        public CandidateIdentity? Identity { get; set; }
        public DeviceCatalogEntry[]? Catalog { get; set; }
        public DeviceInventoryItem[]? Inventory { get; set; }
        public string RootId { get; set; } = "";
        public DeviceCreateAttempt? Attempt { get; set; }
        public CandidateFault? Fault { get; set; }
        public bool RequiresSessionReset { get; set; }
    }
    public sealed class ImportCandidateCall
    {
        public string Action { get; set; } = "identity";
        public string Tool { get; set; } = "";
        public PlcImportRequest Request { get; set; } = new PlcImportRequest();
        public PlcImportObject? Target { get; set; }
        public PlcImportInput? Input { get; set; }
        public PlcImportCheck? Check { get; set; }
    }
    public sealed class ImportCandidateReply : TiaMcp.Adapters.Contracts.IWorkerOperationReply
    {
        bool TiaMcp.Adapters.Contracts.IWorkerOperationReply.MayHaveChanged => Attempt?.Issued == true;
        bool TiaMcp.Adapters.Contracts.IWorkerOperationReply.BlockReadsAfterUncertain => true;
        public CandidateIdentity? Identity { get; set; }
        public PlcImportInput[]? Inputs { get; set; }
        public PlcImportObject[]? Inventory { get; set; }
        public string GroupIdentity { get; set; } = "";
        public bool OverwriteSupported { get; set; }
        public PlcImportAttempt? Attempt { get; set; }
        public CandidateFault? Fault { get; set; }
        public bool RequiresSessionReset { get; set; }
    }
    public static class CandidatePolicy
    {
        // All released P6-DEVICE/P6-IMPORT states are current. This native side
        // admits only the explicit isolated test-build marker, never an env var.
        public static bool Enabled(Assembly worker, string release, string family)
        {
            var setting = worker.GetCustomAttributes<AssemblyMetadataAttribute>().SingleOrDefault(a => a.Key == "TiaMcpTestPolicy")?.Value;
            if (setting == null) return false;
            var tokens = setting.Split(',');
            var known = new[] { "DEVICE", "IMPORT", "EXPORT", "SESSION", "CLOSE", "SOURCE", "COMPILE", "FALLBACK" }.Select(f => "P6-" + f + ":safe-v4").ToArray();
            if (tokens.Any(t => !known.Contains(t, StringComparer.Ordinal)) || tokens.Distinct(StringComparer.Ordinal).Count() != tokens.Length)
                throw new InvalidOperationException("Invalid test policy build metadata.");
            return (family != "P6-DEVICE" || release == "19") && tokens.Contains(family + ":safe-v4", StringComparer.Ordinal);
        }
    }
}
