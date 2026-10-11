using System.Collections.Generic;

namespace TiaMcp.Adapters.Contracts;

public sealed class VersionControlRequest
{
    public string Operation { get; set; } = "";
    public string WorkspaceName { get; set; } = "";
    public string FolderPath { get; set; } = "";
    public string Direction { get; set; } = "ProjectToWorkspace";
    public string DeviceFilter { get; set; } = "";
    public bool ChangedOnly { get; set; } = true;
    public bool WalkTrace { get; set; }
    public int MaxObjects { get; set; } = 3000;
    public bool DryRun { get; set; } = true;
}

public sealed class VersionControlResult : IWorkerOperationReply
{
    public string Message { get; set; } = "";
    public IEnumerable<string>? Items { get; set; } = System.Array.Empty<string>();
    public Dictionary<string, object?> Meta { get; set; } = new Dictionary<string, object?>();
    public bool RequiresSessionReset { get; set; }
    public bool MayHaveChanged => Meta.TryGetValue("mayHaveChanged", out var changed)
        && string.Equals(System.Convert.ToString(changed, System.Globalization.CultureInfo.InvariantCulture), "true", System.StringComparison.OrdinalIgnoreCase);
    public bool BlockReadsAfterUncertain => true;
}
