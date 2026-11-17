using System.Text;
using TiaMcp.Adapters.Contracts;
using TiaOpenness.Shared;
using Xunit;

public sealed class OnlineTargetSettingsTests
{
    [Fact]
    public void Missing_file_is_unconfigured_and_invalid_or_unreadable_files_fail_closed()
    {
        using var fixture = new SettingsFixture();
        string path = Path.Combine(fixture.Root, "config", "online-targets.settings");
        Assert.Null(OnlineTargetSettings.Load(path));
        File.WriteAllText(path, "target=192.168.1.10\n");
        Assert.True(OnlineTargetSettings.Load(path)!.IsValid);
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.False(OnlineTargetSettings.Load(path)!.IsValid);
        File.WriteAllText(path, "target=192.168.1.10\ntarget=host.invalid\n");
        Assert.False(OnlineTargetSettings.Load(path)!.IsValid);
        Assert.False(OnlineTargetSettings.Load(fixture.Root)!.IsValid);
    }

    [Fact]
    public void Atomic_save_round_trips_and_audits_changes_without_addresses()
    {
        using var fixture = new SettingsFixture();
        string path = Path.Combine(fixture.Root, "online-targets.settings");
        string logs = Path.Combine(fixture.Root, "audit");
        var audit = new AuditLog(logs);
        var list = OnlineTargetPolicy.Parse("# allowed\ntarget=192.168.1.10\ntarget=10.20.30.0/24\n");
        OnlineTargetSettings.Save(path, list, audit);
        Assert.Equal(list.Entries, OnlineTargetSettings.Load(path)!.Entries);
        Assert.Equal("target=192.168.1.10\ntarget=10.20.30.0/24\n", File.ReadAllText(path));
        Assert.False(File.ReadAllBytes(path).Take(3).SequenceEqual(Encoding.UTF8.GetPreamble()));
        OnlineTargetSettings.Save(path, list, audit);
        Assert.Single(audit.Read());
        OnlineTargetSettings.Save(path, OnlineTargetPolicy.Parse(""), audit);
        var empty = OnlineTargetSettings.Load(path)!;
        Assert.True(empty.IsValid);
        Assert.Empty(empty.Entries);
        Assert.Equal("online-target-not-allowed", OnlineTargetPolicy.Refusal(
            new OnlineRequest { Operation = "ConnectOnlinePlc", IpAddress = "192.168.1.10" }, empty)!.Value.Condition);
        Assert.Equal(2, audit.Read().Count);
        Assert.All(audit.Read(), row => Assert.Equal("online-targets-change", row.Event));
        Assert.True(audit.Verify().Passed);
        string records = string.Concat(Directory.GetFiles(logs, "*.jsonl").Select(File.ReadAllText));
        Assert.DoesNotContain("192.168.1.10", records);
        Assert.DoesNotContain("10.20.30", records);
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp"));
        Assert.Throws<ArgumentException>(() => OnlineTargetSettings.Save(path, OnlineTargetList.Invalid, audit));
        Assert.Equal("", File.ReadAllText(path));
    }

    private sealed class SettingsFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(AppContext.BaseDirectory, "online-target-tests", Guid.NewGuid().ToString("N"));
        internal SettingsFixture() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, true);
    }
}

internal sealed class OnlineTargetSettingsScope : IDisposable
{
    private readonly string path = OnlineTargetSettings.SettingsPath;
    private readonly byte[]? previous;
    internal OnlineTargetSettingsScope(string? text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        previous = File.Exists(path) ? File.ReadAllBytes(path) : null;
        if (text == null) File.Delete(path); else File.WriteAllText(path, text);
    }
    public void Dispose()
    {
        if (previous == null) File.Delete(path); else File.WriteAllBytes(path, previous);
    }
}
