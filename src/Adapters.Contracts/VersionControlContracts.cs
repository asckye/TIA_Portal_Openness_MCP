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
    public const string Reconnect = "Compare the file, back it up, move or delete it, and connect again.";
    public const string RefreshSeparator = " | refresh refused: ";
    public static string StoredEqualSummary(int count) => count == 0 ? "" :
        $" {count} object(s) reported Equal from the status TIA stored. TIA refused the refresh. Check the workspace files if an object was edited.";

    public static string DisconnectedSummary(IReadOnlyCollection<string> names) => names.Count == 0 ? "" :
        " " + names.Count + " disconnected mapping(s) skipped: " + string.Join(", ", names) + ". " + Reconnect;

    public static string NotVerifiedSummary(IReadOnlyCollection<string> names) => names.Count == 0 ? "" :
        " Not verified: " + string.Join(", ", names) + ". Export the block, type or tag table separately and compare it with the workspace file. "
        + "After a backup, remove the stale workspace file and synchronize again.";

    public string Message { get; set; } = "";
    public IEnumerable<string>? Items { get; set; } = System.Array.Empty<string>();
    public Dictionary<string, object?> Meta { get; set; } = new Dictionary<string, object?>();
    public bool OutcomeUnknown { get; set; }
    public bool MayHaveChanged => Meta.TryGetValue("mayHaveChanged", out var changed)
        && string
            .Equals(System.Convert.ToString(changed, System.Globalization.CultureInfo.InvariantCulture), "true", System.StringComparison.OrdinalIgnoreCase);
}
