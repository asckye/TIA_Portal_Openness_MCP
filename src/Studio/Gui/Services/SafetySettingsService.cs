using System;
using System.Collections.Generic;
using System.Linq;
using TiaMcp.Adapters.Contracts;
using TiaOpenness.Shared;

namespace TiaOpenness.Gui.Services;

internal sealed class SafetySettingsService
{
    private readonly string targetsPath;
    private readonly SecretVault vault;
    private readonly AuditLog? audit;
    private string? revision;
    private bool replacementConfirmed;
    internal string TargetsPath => targetsPath;
    internal string? TargetsProblem { get; private set; }
    internal bool TargetsReadOnly => TargetsProblem != null && !replacementConfirmed;

    internal SafetySettingsService(string? targetsPath = null, string? vaultPath = null, AuditLog? audit = null)
    {
        this.targetsPath = targetsPath ?? OnlineTargetSettings.SettingsPath;
        vault = new SecretVault(vaultPath);
        this.audit = audit;
    }

    internal OnlineTargetList? LoadTargets()
    {
        var snapshot = OnlineTargetSettings.ReadSnapshot(targetsPath);
        revision = snapshot.Revision;
        TargetsProblem = snapshot.Problem;
        replacementConfirmed = false;
        return snapshot.List;
    }

    internal void ConfirmTargetReplacement() => replacementConfirmed = true;

    internal static string ValidateTarget(string entry)
    {
        var list = OnlineTargetPolicy.Parse("target=" + entry);
        if (!list.IsValid || list.Entries.Count != 1 || list.Entries[0] != entry)
            throw new ArgumentException("Expected one IPv4 address or CIDR subnet.", nameof(entry));
        return entry;
    }

    internal void SaveTargets(IEnumerable<string> entries)
    {
        var list = OnlineTargetPolicy.Parse(string.Join("\n", entries.Select(entry => "target=" + ValidateTarget(entry))));
        if (revision == null) LoadTargets();
        if (TargetsReadOnly) throw new InvalidOperationException("Confirm replacement of the invalid or unreadable file before editing.");
        revision = OnlineTargetSettings.SaveChecked(targetsPath, list, revision!, audit);
        TargetsProblem = null;
        replacementConfirmed = false;
    }

    internal string[] SecretNames() => vault.Names(validateProtected: false);
    internal bool HasSecret(string name) => SecretNames().Contains(name, StringComparer.Ordinal);
    internal void SetSecret(string name, string value, bool replaceConfirmed = false)
    {
        if (string.IsNullOrEmpty(value)) throw new ArgumentException("Secret values must not be empty.", nameof(value));
        if (!replaceConfirmed && HasSecret(name)) throw new InvalidOperationException("Confirm replacement of the existing secret.");
        vault.Set(name, value, audit);
    }
    internal bool RemoveSecret(string name) => vault.Remove(name, audit);
}
