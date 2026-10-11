using System;
using System.Collections.Generic;

namespace TiaMcp.Adapters.Contracts;

public sealed class TeamcenterRequest
{
    public string Operation { get; set; } = "";
    public string? ValidationError { get; set; }
    public TeamcenterPlan Plan { get; set; } = new TeamcenterPlan();
    public string Action { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public string Group { get; set; } = "";
    public string Role { get; set; } = "";
    public string HostUrl { get; set; } = "";
    public string Instance { get; set; } = "";
    public string LoginUrl { get; set; } = "";
    public string ApplicationId { get; set; } = "";
    public bool DryRun { get; set; } = true;
    public string ItemId { get; set; } = "";
    public string RevisionId { get; set; } = "";
    public string DatasetType { get; set; } = "";
    public string DatasetName { get; set; } = "";
    public string ItemType { get; set; } = "";
    public string TiaObjectName { get; set; } = "";
    public string ItemName { get; set; } = "";
    public string LocalCacheOption { get; set; } = "";
    public string Target { get; set; } = "project";
    public string LibraryName { get; set; } = "";
    public bool ConfirmSave { get; set; } = false;
}

public sealed class TeamcenterPlan
{
    public string Action { get; set; } = "";
    public bool Writes { get; set; }
    public string Target { get; set; } = "project";
    public string DatasetType { get; set; } = "";
    public string ItemType { get; set; } = "";
    public string LocalCacheOption { get; set; } = "";
    public TeamcenterItemDetails? ItemDetails { get; set; }
    public TeamcenterRevisionDetails? RevisionDetails { get; set; }
    public Dictionary<string, string> CustomAttributes { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public bool NeedsAttributes => Action == "readCustomAttributes" || Action.StartsWith("saveAsNew", StringComparison.Ordinal);
}

public sealed class TeamcenterItemDetails
{
    public string ItemId { get; set; } = "";
    public string ItemName { get; set; } = "";
    public string RevisionId { get; set; } = "";
    public string TeamcenterItemType { get; set; } = "";
    public string Comment { get; set; } = "";
    public string TeamcenterFolder { get; set; } = "";
    public string[] TeamcenterProject { get; set; } = Array.Empty<string>();
}

public sealed class TeamcenterRevisionDetails
{
    public string RevisionId { get; set; } = "";
    public string Comment { get; set; } = "";
}
