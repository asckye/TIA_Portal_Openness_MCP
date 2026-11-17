using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TiaMcp.Adapters.Contracts;

public sealed class OnlineTargetList
{
    public bool IsValid { get; }
    public IReadOnlyList<string> Entries { get; }
    public static OnlineTargetList Invalid { get; } = new OnlineTargetList(false, Array.Empty<string>());

    internal OnlineTargetList(bool isValid, string[] entries)
    { IsValid = isValid; Entries = Array.AsReadOnly(entries); }

    internal bool Allows(uint address) => IsValid && Entries.Any(entry => {
        var parts = entry.Split('/');
        OnlineTargetPolicy.TryAddress(parts[0], out uint network);
        int prefix = parts.Length == 1 ? 32 : int.Parse(parts[1], CultureInfo.InvariantCulture);
        uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        return (address & mask) == (network & mask);
    });
}

// Shared by host admission and settings; no Siemens objects or network access.
public static class OnlineTargetPolicy
{
    public static OnlineTargetList Parse(string text)
    {
        var entries = new List<string>();
        foreach (string raw in text.Split('\n'))
        {
            int comment = raw.IndexOf('#');
            string line = (comment < 0 ? raw : raw.Substring(0, comment)).Trim();
            if (line.Length == 0) continue;
            var pair = line.Split('=');
            if (pair.Length != 2 || pair[0].Trim() != "target") return OnlineTargetList.Invalid;
            string entry = pair[1].Trim();
            var parts = entry.Split('/');
            if (parts.Length > 2 || !TryAddress(parts[0], out _)) return OnlineTargetList.Invalid;
            if (parts.Length == 2 && !Decimal(parts[1], 32, out _)) return OnlineTargetList.Invalid;
            entries.Add(entry);
        }
        return new OnlineTargetList(true, entries.ToArray());
    }

    public static (string Parameter, string? Condition, string Message)? Refusal(OnlineRequest request, OnlineTargetList? list)
    {
        string? parameter = AddressParameter(request.Operation);
        if (parameter == null) return null;
        string text = parameter == "ipAddress" ? request.IpAddress : request.TargetIpAddress;
        if (!TryAddress(text, out uint address))
            return (parameter, null, "An explicit IPv4 address is required.");
        if (list == null) return null;
        if (!list.IsValid)
            return (parameter, "online-target-list-invalid", "The online-target list is unavailable or invalid; edit it in the Workbench.");
        return list.Allows(address) ? null : (parameter, "online-target-not-allowed",
            "The address '" + text + "' is outside the online-target list; edit the list in the Workbench.");
    }

    public static string? AddressParameter(string operation) => operation switch {
        "ConnectOnlinePlc" => "ipAddress",
        "DownloadPlc" or "UploadDeviceParameters" or "GetPlcBlockFingerprints" or "UploadStationFromPlc" => "targetIpAddress",
        _ => null };

    internal static bool TryAddress(string text, out uint address)
    {
        address = 0;
        if (text == null) return false;
        var parts = text.Split('.');
        if (parts.Length != 4) return false;
        foreach (string part in parts)
        {
            if (part.Length > 3 || !Decimal(part, 255, out int octet)) return false;
            address = (address << 8) | (uint)octet;
        }
        return true;
    }

    private static bool Decimal(string text, int maximum, out int value)
    {
        value = 0;
        return text.Length > 0 && text.All(c => c >= '0' && c <= '9')
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value <= maximum;
    }
}
