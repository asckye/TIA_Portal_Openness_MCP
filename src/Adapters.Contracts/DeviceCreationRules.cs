using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.Adapters.Contracts;

// Pure decisions for device creation (decided 2026-10-19 for CreateDevice, CreateHardwareDevice, CreateHardwareCatalogDevice,
// CreateGsdDevice and the BuildProjectScaffold addDevice step): create only the requested type identifier, version and name with
// one CreateWithItem call. No firmware-version stepping, no alternative type from a list and no renaming retries. A refusal is
// typed (AdapterPreconditionException) and lists the hardware-catalog candidates so the caller can choose again. The native
// adapter supplies the observed catalog rows and device inventories; nothing here touches a Siemens object.
public static class DeviceCreationRules
{
    public const int CandidateListLimit = 12;

    public static bool IsFullTypeIdentifier(string value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.StartsWith("OrderNumber:", StringComparison.Ordinal) || text.StartsWith("GSD:", StringComparison.Ordinal);
    }

    // OrderNumber:<article>[/<version>] from the caller's article number and version, without rewriting either. TIA documents that
    // the firmware version may be omitted only for a module with no or a single installed version, so an empty version is passed on.
    public static string RequestedTypeIdentifier(string orderNumber, string version, string orderParameter, string versionParameter)
    {
        var order = (orderNumber ?? string.Empty).Trim();
        var requestedVersion = (version ?? string.Empty).Trim();
        if (order.Length == 0)
            throw new AdapterPreconditionException(orderParameter + " is required: the exact article number (with its spaces, e.g. '6ES7 513-1AM03-0AB0') or the full TypeIdentifier from the hardware catalog. No family default device is created.", orderParameter);
        if (IsFullTypeIdentifier(order))
        {
            if (requestedVersion.Length != 0)
                throw new AdapterPreconditionException("A full TypeIdentifier already contains its version; pass " + versionParameter + " empty.", versionParameter);
            return order;
        }
        return requestedVersion.Length == 0 ? "OrderNumber:" + order : "OrderNumber:" + order + "/" + requestedVersion;
    }

    // BuildProjectScaffold spec check for one device: null when the spec names exactly one type, otherwise the refusal text.
    public static string? SpecProblem(string mlfb, string version, string mlfbKey, string versionKey)
    {
        if (string.IsNullOrWhiteSpace(mlfb))
            return mlfbKey + " is required: the exact article number with " + versionKey + ", or the full TypeIdentifier from SearchHardwareCatalog. No family default device is created.";
        return IsFullTypeIdentifier(mlfb) && !string.IsNullOrWhiteSpace(version)
            ? "A full TypeIdentifier in " + mlfbKey + " already contains its version; leave " + versionKey + " empty." : null;
    }

    // Keyword tools create only when keyword equals one ranked candidate's TypeIdentifier (ordinal). Otherwise the ranked
    // identifiers are returned in a typed refusal; no candidate is tried.
    public static string ExactCandidate(IEnumerable<string?> rankedTypeIdentifiers, string keyword, string source, string note = "")
    {
        var requested = (keyword ?? string.Empty).Trim();
        var identifiers = (rankedTypeIdentifiers ?? Enumerable.Empty<string?>()).Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!).Distinct(StringComparer.Ordinal).ToList();
        var matches = identifiers.Where(id => string.Equals(id, requested, StringComparison.Ordinal)).ToList();
        if (matches.Count == 1) return matches[0];
        throw new AdapterPreconditionException(
            "Creation needs keyword to be one exact TypeIdentifier; '" + requested + "' is not. No device was created and no candidate was tried. "
            + (identifiers.Count == 0 ? "The " + source + " search found no candidate with a TypeIdentifier."
                : "Ranked " + source + " candidates: " + List(identifiers) + ". Pass one of them as keyword.") + note,
            "keyword", false);
    }

    // Catalog filter for a type identifier: the article number part between "OrderNumber:" and "/".
    public static string CatalogFilter(string typeIdentifier)
    {
        var text = (typeIdentifier ?? string.Empty).Trim();
        if (text.StartsWith("OrderNumber:", StringComparison.Ordinal)) text = text.Substring("OrderNumber:".Length);
        int slash = text.IndexOf('/');
        return (slash >= 0 ? text.Substring(0, slash) : text).Trim();
    }

    public static string CandidateText(string filter, IEnumerable<string?> catalogTypeIdentifiers)
    {
        var rows = (catalogTypeIdentifiers ?? Enumerable.Empty<string?>()).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!)
            .Distinct(StringComparer.Ordinal).ToList();
        if (rows.Count == 0) return " The hardware catalog has no entry for '" + filter + "'; search it with SearchHardwareCatalog.";
        return " Hardware-catalog candidates for '" + filter + "': " + List(rows) + ". Choose one exact TypeIdentifier and call again.";
    }

    public static string NoCatalogText(string typeIdentifier)
        => " This release has no HardwareCatalog API (V18+), so no candidates are listed; read the exact TypeIdentifier in TIA (Options > Settings > Hardware configuration > Display of the type identifier) for '" + typeIdentifier + "'.";

    // A TIA refusal is typed only when the device inventory read back unchanged; otherwise the outcome stays unknown.
    public static AdapterPreconditionException TiaRefusal(string typeIdentifier, string deviceName, string reason, string candidates, string parameter, Exception cause)
        => new AdapterPreconditionException("TIA refused CreateWithItem('" + typeIdentifier + "', '" + deviceName + "'): " + reason
            + " No device was created (device inventory unchanged); no other type, version or name was tried." + candidates, parameter, false, cause);

    public static bool SameInventory<T>(IReadOnlyCollection<T> before, IReadOnlyCollection<T> after)
        => before.Count == after.Count && before.All(after.Contains);

    public static void RequireFreeName(IEnumerable<string> occupiedNames, string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            throw new AdapterPreconditionException("deviceName is required; it names the device and its head item.", "deviceName");
        if (occupiedNames.Any(name => string.Equals(name, deviceName, StringComparison.OrdinalIgnoreCase)))
            throw new AdapterPreconditionException("A device or device head item named '" + deviceName + "' already exists (root, grouped or ungrouped devices); no create attempted. Choose a new deviceName.", "deviceName", false);
    }

    private static string List(IReadOnlyList<string> identifiers)
        => string.Join("; ", identifiers.Take(CandidateListLimit)) + (identifiers.Count > CandidateListLimit ? "; ..." : "");
}
