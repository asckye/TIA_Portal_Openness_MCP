using System;

namespace TiaMcp.Adapters.Contracts
{
    // Scope evidence extends live replies without changing the frozen legacy DTO wire fixtures.
    internal sealed class PlcScopedMutationResult : TiaMcp.Adapters.PlcMutationResult
    {
        public string UnitName { get; internal set; } = "";
        public string[] SwImportOptions { get; internal set; } = Array.Empty<string>();
        public string[] ImportOptions { get; internal set; } = Array.Empty<string>();
    }
    internal sealed class PlcScopedBatchImportResult : TiaMcp.Adapters.PlcBatchImportResult
    {
        public string UnitName { get; internal set; } = "";
        public string[] SwImportOptions { get; internal set; } = Array.Empty<string>();
        public string[] ImportOptions { get; internal set; } = Array.Empty<string>();
    }
    internal sealed class PlcScopedBatchExportResult : TiaMcp.Adapters.PlcBatchExportResult
    {
        public string UnitName { get; internal set; } = "";
    }
}
