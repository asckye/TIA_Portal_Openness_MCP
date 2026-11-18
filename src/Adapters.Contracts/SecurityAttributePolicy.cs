using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.Adapters.Contracts;

public static class SecurityAttributePolicy
{
    // S7-1500 CPU DeviceItem Boolean, documented in sdk/TIA_V15.1_PublicAPI/V15.1/HW Parameter description/Openness_hardware_parameter_description.pdf.
    public static IReadOnlyList<string> PutGetNames { get; } = Array.AsReadOnly(new[] { "ProtectionEnablePutGetCommunication" });
    public static IReadOnlyList<string> CertificateNames { get; } = Array.AsReadOnly(Array.Empty<string>());
    public static IReadOnlyList<string> SyslogNames { get; } = Array.AsReadOnly(Array.Empty<string>());
    public const string Unverified =
        "No documented or VM-verified dynamic attribute name is approved for this operation; VM confirmation of owner, exact name, type and write semantics " +
        "is required.";

    public static int SelectExact(IEnumerable<string> candidates, IEnumerable<string> approvedNames)
    {
        var approved = new HashSet<string>(approvedNames, StringComparer.Ordinal);
        if (approved.Count == 0) throw new NotSupportedException(Unverified);
        var matches = candidates.Select((name, index) => (name, index)).Where(row => approved.Contains(row.name)).Take(2).ToArray();
        if (matches.Length > 1)
            throw new InvalidOperationException("More than one approved dynamic attribute matches; an exact owner is required. Nothing changed.");
        if (matches.Length == 0) throw new NotSupportedException("No approved dynamic attribute is exposed by this device. Nothing changed.");
        return matches[0].index;
    }

    public static void RequireBooleanWrite(string accessMode, object? current)
    {
        if (accessMode != "ReadWrite" || current is not bool)
            throw new NotSupportedException("The approved PUT/GET attribute must have AccessMode ReadWrite and a Boolean value. Nothing changed.");
    }

    public static void RequireApproved(string name, IEnumerable<string> approvedNames)
    {
        if (!approvedNames.Contains(name, StringComparer.Ordinal)) throw new NotSupportedException(Unverified);
    }
}
