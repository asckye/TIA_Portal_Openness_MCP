using System;
using System.Text.Json;

namespace TiaMcp.Logic.V4.Inputs;

internal static class CompatibilityActionConfirmation
{
    internal static (bool Reviewed, bool Delete, Error? Error) Check(string tool, JsonElement arguments, bool executing)
    {
        if (tool is not ("ManagePlcExternalSources" or "ManagePlcTagDefinition")) return default;
        string? Text(string field) => arguments.TryGetProperty(field, out var value) ? value.GetString() : null;
        bool? Flag(string field) => arguments.TryGetProperty(field, out var value) ? value.GetBoolean() : null;
        string? action = Text("action");
        bool external = tool == "ManagePlcExternalSources";
        bool reviewed = external
            ? action is "createFromFile" or "createFromMasterCopy" or "generateBlocks" or "createGroup" or "renameGroup" or "deleteGroup" or "delete"
            : action is "create" or "update";
        if (!reviewed) return default;

        bool delete = external && action is ("delete" or "deleteGroup");
        bool rootDelete = delete && action == "delete" && string.IsNullOrEmpty(Text("unitName")) && string.IsNullOrEmpty(Text("groupPath"));
        bool confirmedDelete = Flag("confirmDelete") == true;
        bool? confirmed = Flag("confirm");
        Error? error = null;
        if (executing && delete && !confirmedDelete) error = Invalid("confirmDelete", "Deletion requires confirmDelete=true.");
        else if (rootDelete && confirmed.HasValue && confirmed.Value != confirmedDelete)
            error = Invalid("confirm", "confirm and confirmDelete must agree for deletion.");
        else if (executing && !rootDelete && confirmed != true) error = Invalid("confirm", "Apply requires confirm=true.");
        return (true, delete, error);
    }

    private static Error Invalid(string field, string message) => new(message, new InvalidArgumentDetails(field, Array.Empty<string>()));
}
