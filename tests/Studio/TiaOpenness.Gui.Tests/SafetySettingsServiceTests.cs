using System;
using System.IO;
using System.Linq;
using TiaOpenness.Gui.Services;
using TiaOpenness.Shared;
using Xunit;

namespace TiaOpenness.Gui.Tests;

public sealed class SafetySettingsServiceTests
{
    [Fact]
    public void Online_targets_round_trip_validate_and_audit_without_addresses()
    {
        using var fixture = new Fixture();
        Assert.Null(fixture.Service.LoadTargets());
        fixture.Service.SaveTargets(new[] { "192.0.2.10", "198.51.100.0/24" });
        var loaded = fixture.Service.LoadTargets();
        Assert.True(loaded!.IsValid);
        Assert.Equal(new[] { "192.0.2.10", "198.51.100.0/24" }, loaded.Entries);
        var reopened = new SafetySettingsService(fixture.TargetsPath, fixture.Audit);
        Assert.Equal(loaded.Entries, reopened.LoadTargets()!.Entries);
        reopened.SaveTargets(new[] { "198.51.100.0/24" });
        Assert.Equal("198.51.100.0/24", Assert.Single(reopened.LoadTargets()!.Entries));
        reopened.SaveTargets(Array.Empty<string>());
        Assert.True(reopened.LoadTargets()!.IsValid);
        Assert.Empty(reopened.LoadTargets()!.Entries);
        var events = fixture.Audit.Read();
        Assert.Equal(3, events.Count);
        Assert.All(events, row => Assert.Equal("online-targets-change", row.Event));
        string audit = string.Join("\n", events.Select(AuditLog.Canonical));
        Assert.DoesNotContain("192.0.2.10", audit);
        Assert.DoesNotContain("198.51.100", audit);
        Assert.True(fixture.Audit.Verify().Passed);
    }

    [Theory]
    [InlineData("192.168.1")] [InlineData("host.invalid")] [InlineData("192.0.2.10/33")]
    [InlineData("192.0.2.010")] [InlineData("192.0.2.10\ntarget=198.51.100.1")]
    [InlineData("192.0.2.10 #ignored")] [InlineData("")] [InlineData(" 192.0.2.10")]
    public void Invalid_entries_do_not_write_or_audit(string entry)
    {
        using var fixture = new Fixture();
        Assert.Throws<ArgumentException>(() => fixture.Service.SaveTargets(new[] { entry }));
        Assert.Null(fixture.Service.LoadTargets());
        Assert.Empty(fixture.Audit.Read());
    }

    [Fact]
    public void Plan_lifetime_is_persisted_without_changing_the_approval_switch_or_timeout()
    {
        using var fixture = new Fixture();
        string path = Path.Combine(fixture.Root, "approval.settings");
        new ApprovalSettings(false, 60).Save(path, fixture.Audit);
        using var service = new ApprovalService(path, "tia-safe4-settings-" + Guid.NewGuid().ToString("N"), fixture.Audit);
        int notifications = 0;
        service.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(service.OnlinePlanLifetimeSeconds)) notifications++; };
        service.OnlinePlanLifetimeSeconds = 1800;
        var saved = ApprovalSettings.Load(path);
        Assert.Equal(1800, saved.OnlinePlanLifetimeSeconds);
        Assert.False(saved.Enabled);
        Assert.Equal(60, saved.TimeoutSeconds);
        Assert.Equal(1, notifications);
        Assert.Throws<ArgumentOutOfRangeException>(() => service.OnlinePlanLifetimeSeconds = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => service.OnlinePlanLifetimeSeconds = 86401);
        Assert.Equal(1800, ApprovalSettings.Load(path).OnlinePlanLifetimeSeconds);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Invalid_or_unreadable_target_files_require_explicit_replacement(bool unreadable)
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.TargetsPath, "invalid original entries");
        using var held = unreadable ? new FileStream(fixture.TargetsPath, FileMode.Open, FileAccess.Read, FileShare.None) : null;
        Assert.False(fixture.Service.LoadTargets()!.IsValid);
        Assert.Equal(unreadable ? "unreadable" : "invalid", fixture.Service.TargetsProblem);
        Assert.True(fixture.Service.TargetsReadOnly);
        Assert.Throws<InvalidOperationException>(() => fixture.Service.SaveTargets(new[] { "192.0.2.10" }));
        Assert.Empty(fixture.Audit.Read());
        fixture.Service.ConfirmTargetReplacement();
        Assert.False(fixture.Service.TargetsReadOnly);
    }

    [Theory]
    [InlineData("content")] [InlineData("timestamp")] [InlineData("created")]
    public void Concurrent_target_changes_are_refused_without_overwriting_or_auditing(string change)
    {
        using var fixture = new Fixture();
        if (change != "created") File.WriteAllText(fixture.TargetsPath, "target=192.0.2.10\n");
        fixture.Service.LoadTargets();
        DateTime loadedTime = File.GetLastWriteTimeUtc(fixture.TargetsPath);
        if (change == "timestamp") File.SetLastWriteTimeUtc(fixture.TargetsPath, DateTime.UtcNow.AddMinutes(-2));
        else File.WriteAllText(fixture.TargetsPath, "target=198.51.100.10\n");
        if (change == "content") File.SetLastWriteTimeUtc(fixture.TargetsPath, loadedTime);
        string expected = File.ReadAllText(fixture.TargetsPath);
        Assert.Throws<OnlineTargetSettingsChangedException>(() => fixture.Service.SaveTargets(new[] { "203.0.113.10" }));
        Assert.Throws<OnlineTargetSettingsChangedException>(fixture.Service.RemoveTargetList);
        Assert.Equal(expected, File.ReadAllText(fixture.TargetsPath));
        Assert.Empty(fixture.Audit.Read());
    }

    [Fact]
    public void Removing_the_list_respects_concurrent_changes_and_keeps_their_audit()
    {
        using var fixture = new Fixture();
        fixture.Service.SaveTargets([]);
        var other = new SafetySettingsService(fixture.TargetsPath, fixture.Audit);
        other.SaveTargets(["192.0.2.5"]);
        Assert.Throws<OnlineTargetSettingsChangedException>(fixture.Service.RemoveTargetList);
        Assert.Equal("192.0.2.5", Assert.Single(other.LoadTargets()!.Entries));
        Assert.Equal(2, fixture.Audit.Read().Count);
    }

    internal sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.GetFullPath(Path.Combine("bin-build", "TIA-SAFE-4", Guid.NewGuid().ToString("N")));
        internal string TargetsPath => Path.Combine(Root, "online-targets.settings");
        internal AuditLog Audit { get; }
        internal SafetySettingsService Service { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            Audit = new AuditLog(Path.Combine(Root, "audit"));
            Service = new SafetySettingsService(TargetsPath, Audit);
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
