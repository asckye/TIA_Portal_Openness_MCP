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
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        Assert.Empty(Directory.GetFileSystemEntries(fixture.Root));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "target=192.168.1.10\n");
        Assert.True(OnlineTargetSettings.Load(path)!.IsValid);
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.False(OnlineTargetSettings.Load(path)!.IsValid);
        File.WriteAllText(path, "target=192.168.1.10\ntarget=host.invalid\n");
        Assert.False(OnlineTargetSettings.Load(path)!.IsValid);
        Assert.False(OnlineTargetSettings.Load(fixture.Root)!.IsValid);
    }

    [Fact]
    public void Open_reader_allows_the_file_to_be_renamed_while_it_reads_the_original_contents()
    {
        using var fixture = new SettingsFixture();
        string path = Path.Combine(fixture.Root, "online-targets.settings");
        const string original = "target=192.168.1.10\n";
        File.WriteAllText(path, original);
        using var reader = new StreamReader(OnlineTargetSettings.OpenRead(path));
        File.Move(path, path + ".previous");
        File.WriteAllText(path, "target=192.168.1.11\n");
        Assert.Equal(original, reader.ReadToEnd());
        Assert.Equal(new[] { "192.168.1.11" }, OnlineTargetSettings.Load(path)!.Entries);
    }

    [Fact]
    public void Saving_replaces_the_list_while_an_existing_reader_finishes_its_snapshot()
    {
        using var fixture = new SettingsFixture();
        string path = Path.Combine(fixture.Root, "online-targets.settings");
        var audit = new AuditLog(Path.Combine(fixture.Root, "audit"));
        OnlineTargetSettings.Save(path, OnlineTargetPolicy.Parse("target=192.168.1.10"), audit);
        using var reader = new StreamReader(OnlineTargetSettings.OpenRead(path));
        OnlineTargetSettings.Save(path, OnlineTargetPolicy.Parse("target=192.168.1.11"), audit);
        Assert.Equal("target=192.168.1.10\n", reader.ReadToEnd());
        Assert.Equal(new[] { "192.168.1.11" }, OnlineTargetSettings.Load(path)!.Entries);
    }

    [Fact]
    public void Save_replaces_settings_with_the_current_reader_and_reproduces_the_original_Windows_sharing_violation()
    {
        using var fixture = new SettingsFixture();
        string path = Path.Combine(fixture.Root, "online-targets.settings");
        var audit = new AuditLog(Path.Combine(fixture.Root, "audit"));
        var replacement = OnlineTargetPolicy.Parse("target=192.168.1.11");
        OnlineTargetSettings.Save(path, OnlineTargetPolicy.Parse("target=192.168.1.10"), audit);
        if (OperatingSystem.IsWindows())
        {
            using var originalReader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var failure = Assert.Throws<IOException>(() => OnlineTargetSettings.Save(path, replacement, audit));
            Assert.Equal(32, failure.HResult & 0xffff);
        }
        using var reader = new StreamReader(OnlineTargetSettings.OpenRead(path));
        OnlineTargetSettings.Save(path, replacement, audit);
        Assert.Equal("target=192.168.1.10\n", reader.ReadToEnd());
        Assert.Equal(replacement.Entries, OnlineTargetSettings.Load(path)!.Entries);
    }

    [Fact]
    public void Existing_parent_missing_file_is_read_without_creating_a_lock()
    {
        using var fixture = new SettingsFixture();
        string path = Path.Combine(fixture.Root, "online-targets.settings");
        Assert.Null(OnlineTargetSettings.Load(path));
        Assert.Empty(Directory.GetFileSystemEntries(fixture.Root));
        Assert.Throws<ArgumentException>(() => OnlineTargetSettings.Load("\0"));
    }

    [Fact]
    public void Audit_append_failure_is_reported_after_the_list_write()
    {
        using var fixture = new SettingsFixture();
        string path = Path.Combine(fixture.Root, "online-targets.settings");
        string auditPath = Path.Combine(fixture.Root, "audit-is-a-file");
        File.WriteAllText(auditPath, "not a directory");
        Assert.Throws<IOException>(() => OnlineTargetSettings.Save(path,
            OnlineTargetPolicy.Parse("target=192.168.1.10"), new AuditLog(auditPath)));
        Assert.Equal("target=192.168.1.10\n", File.ReadAllText(path));
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
