using System.Collections.Generic;

namespace TiaMcp.Adapters.Contracts
{
    public sealed class HardwareAmlExportReply
    {
        public string DeviceName { get; set; } = "";
        public string FilePath { get; set; } = "";
        public bool Success { get; set; }
        public string State { get; set; } = "Unknown";
        public int ErrorCount { get; set; }
        public int WarningCount { get; set; }
        public List<string> Messages { get; set; } = new List<string>();
        public bool? NativeBooleanResult { get; set; }
    }
}
