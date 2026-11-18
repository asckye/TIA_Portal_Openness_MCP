using System;
using System.Collections.Generic;

namespace TiaMcp.Adapters.Contracts;

public static class PlcServiceLookup
{
    public static AdapterPreconditionException Missing(string service, string cpu, string? typeIdentifier, IReadOnlyList<string> searched,
        string? orderNumber = null, string? firmware = null)
    {
        const string prefix = "OrderNumber:";
        if (typeIdentifier?.StartsWith(prefix, StringComparison.Ordinal) == true)
        {
            int separator = typeIdentifier.LastIndexOf('/');
            if (string.IsNullOrWhiteSpace(orderNumber))
                orderNumber = separator < 0 ? typeIdentifier.Substring(prefix.Length) : typeIdentifier.Substring(prefix.Length, separator - prefix.Length);
            if (string.IsNullOrWhiteSpace(firmware) && separator >= 0) firmware = typeIdentifier.Substring(separator + 1);
        }
        string message = $"The official {service} API exists on this release. CPU '{cpu}' ({typeIdentifier ?? "type identifier unavailable"}, "
            + $"order number {orderNumber ?? "unavailable"}, firmware {firmware ?? "unavailable"}) did not provide the service. "
            + $"Searched {string.Join(", ", searched)}.";
        var refusal = new AdapterPreconditionException(message, "service-not-provided", innerException: null);
        refusal.Data["nativeResultEvidence"] = new Dictionary<string, object?> {
            ["service"] = service, ["searched"] = searched, ["typeIdentifier"] = typeIdentifier, ["orderNumber"] = orderNumber, ["firmware"] = firmware
        };
        return refusal;
    }
}
