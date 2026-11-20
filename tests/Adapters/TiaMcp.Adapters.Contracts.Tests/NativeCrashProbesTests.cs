using System.Collections;
using System.Text.Json;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class NativeCrashProbesTests
{
    private sealed class Definition(string name, List<string> calls)
    {
        public string Name { get { calls.Add("Name"); return name; } }
    }

    // Stand-in for the V17-V21 definition compositions: enumeration is the member that terminates TIA on V17-V20.
    private sealed class Composition(List<string> calls, params string[] names) : IEnumerable
    {
        public int Count { get { calls.Add("Count"); return names.Length; } }
        public Definition? Find(string name) { calls.Add("Find"); return names.Contains(name) ? new Definition(name, calls) : null; }
        public Definition this[int index] { get { calls.Add("Item"); return new Definition(names[index], calls); } }
        public IEnumerator GetEnumerator() => throw new InvalidOperationException("GetEnumerator must never be called.");
    }

    private sealed class Provider(Composition tags, Composition texts)
    {
        public Composition TagDefinitions => tags;
        public Composition TextDefinitions => texts;
    }

    private sealed class Throwing : IEnumerable
    {
        public int Count => throw new TimeoutException();
        public IEnumerator GetEnumerator() => throw new InvalidOperationException("GetEnumerator must never be called.");
    }

    private static (Dictionary<string, object?> Reply, List<string> Calls, List<string> Journal) Sivarc(string probe, string? enabled = "1",
        string release = "18", string name = "", string[]? tags = null)
    {
        var calls = new List<string>();
        var journal = new List<string>();
        var provider = new Provider(new Composition(calls, tags ?? new[] { "MCP_RC_Tag" }), new Composition(calls));
        var binding = NativeCrashProbes.ReflectionBinding(block => { calls.Add("GetService"); return provider; });
        var reply = NativeCrashProbes.Sivarc(enabled, release, probe, name, () => { calls.Add("Resolve"); return new object(); }, binding,
            step => { journal.Add(step); calls.Add("journal"); });
        return (reply, calls, journal);
    }

    [Theory]
    [InlineData(null, "18", "disabled")]
    [InlineData("true", "18", "disabled")]
    [InlineData("1", "14sp1", "unsupported")]
    [InlineData("1", "15.1", "unsupported")]
    [InlineData("1", "16", "unsupported")]
    public void Sivarc_probes_refuse_without_the_opt_in_or_before_V17_without_native_steps(string? enabled, string release, string status)
    {
        foreach (var probe in new[] { "sivarc-count", "sivarc-find", "sivarc-item" })
        {
            var (reply, calls, _) = Sivarc(probe, enabled, release, "MCP_RC_None");
            Assert.Equal(status, reply["status"]);
            Assert.Empty(calls);
        }
    }

    [Theory]
    [InlineData("17")]
    [InlineData("20")]
    [InlineData("21")]
    public void Count_reads_only_the_two_counts_and_journals_each_step_first(string release)
    {
        var (reply, calls, journal) = Sivarc("sivarc-count", release: release);
        Assert.Equal("ok", reply["status"]);
        Assert.Equal(1, reply["tagDefinitionCount"]);
        Assert.Equal(0, reply["textDefinitionCount"]);
        Assert.Equal(new[] { "journal", "Resolve", "journal", "GetService", "journal", "journal", "Count", "journal", "journal", "Count" }, calls);
        Assert.Equal(new[] { "PlcBlock.Resolve", "PlcBlock.GetService<SivarcDataProvider>", "SivarcDataProvider.TagDefinitions",
            "TagDefinitionComposition.Count", "SivarcDataProvider.TextDefinitions", "TextDefinitionComposition.Count" }, journal);
        Assert.Equal(journal, Assert.IsType<List<string>>(reply["steps"]));
    }

    [Fact]
    public void Find_reports_presence_on_both_compositions_without_counting_or_enumerating()
    {
        var (reply, calls, journal) = Sivarc("sivarc-find", name: "MCP_RC_Tag");
        Assert.Equal("ok", reply["status"]);
        Assert.Equal(true, reply["tagFound"]);
        Assert.Equal(false, reply["textFound"]);
        Assert.Equal(2, calls.Count(c => c == "Find"));
        Assert.DoesNotContain("Count", calls);
        Assert.Contains("TextDefinitionComposition.Find", journal);
        Assert.Equal("invalid-argument", Sivarc("sivarc-find", name: " ").Reply["status"]);
    }

    [Fact]
    public void Item_reads_names_by_index_up_to_the_cap_and_returns_only_test_object_names()
    {
        var names = Enumerable.Range(0, 25).Select(i => i % 2 == 0 ? "MCP_RC_T" + i : "private" + i).ToArray();
        var (reply, calls, journal) = Sivarc("sivarc-item", tags: names);
        Assert.Equal("ok", reply["status"]);
        Assert.Equal(25, reply["tagDefinitionCount"]);
        Assert.Equal(20, reply["tagItemsRead"]);
        Assert.Equal(0, reply["textItemsRead"]);
        Assert.Equal(20, calls.Count(c => c == "Item"));
        Assert.Equal(20, calls.Count(c => c == "Name"));
        Assert.Equal(Enumerable.Range(0, 20).Where(i => i % 2 == 0).Select(i => "MCP_RC_T" + i), Assert.IsType<List<string>>(reply["tagTestNames"]));
        Assert.Equal("TagDefinitionComposition.Item(19)", journal[journal.Count - 4]);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(reply));
    }

    [Fact]
    public void A_native_failure_returns_the_exception_type_and_the_step_that_was_entered()
    {
        var binding = NativeCrashProbes.ReflectionBinding(_ => new { TagDefinitions = new Throwing(), TextDefinitions = new Throwing() });
        var reply = NativeCrashProbes.Sivarc("1", "19", "sivarc-count", "", () => new object(), binding, _ => { });
        Assert.Equal("read-failed", reply["status"]);
        Assert.Equal(typeof(TimeoutException).FullName, reply["exceptionType"]);
        Assert.Equal("TagDefinitionComposition.Count", Assert.IsType<List<string>>(reply["steps"]).Last());
    }

    private static (Dictionary<string, object?> Reply, List<string> Calls) MasterCopy(string kind, string path, bool dryRun, string? enabled = "1",
        Func<object, object, object>? create = null)
    {
        var calls = new List<string>();
        var reply = NativeCrashProbes.MasterCopy(enabled, kind, path, dryRun, () => { calls.Add("source"); return "source"; },
            () => { calls.Add("target"); return "copies"; }, create ?? ((copies, source) => { calls.Add("Create:" + copies + ":" + source); return "copy"; }),
            step => calls.Add("journal:" + step));
        return (reply, calls);
    }

    [Theory]
    [InlineData("block", "Group/private")]
    [InlineData("tagTable", "private_MCP_RC_")]
    [InlineData("device", "MCP_RC_Device")]
    [InlineData("type", "MCP_RC_Udt")]
    public void Master_copy_refuses_other_source_kinds_and_names_without_the_test_prefix(string kind, string path)
    {
        var (reply, calls) = MasterCopy(kind, path, false);
        Assert.Equal("invalid-argument", reply["status"]);
        Assert.Empty(calls);
        Assert.Equal("disabled", MasterCopy("block", "MCP_RC_DB", false, enabled: null).Reply["status"]);
    }

    [Theory]
    [InlineData("block", "MCP_RC_Group/MCP_RC_DB")]
    [InlineData("blockGroup", "MCP_RC_Group")]
    [InlineData("tagTable", "MCP_RC_Tags")]
    [InlineData("plcType", "MCP_RC_Udt")]
    public void Master_copy_creates_directly_without_exclusive_access_and_previews_without_create(string kind, string path)
    {
        var (preview, previewCalls) = MasterCopy(kind, path, true);
        Assert.Equal("preview", preview["status"]);
        Assert.Equal(false, preview["created"]);
        Assert.Equal(new[] { "journal:ResolveMasterCopySource(" + kind + ")", "source", "journal:MasterCopyFolder.MasterCopies", "target" },
            previewCalls);
        var (reply, calls) = MasterCopy(kind, path, false);
        Assert.Equal("ok", reply["status"]);
        Assert.Equal(true, reply["created"]);
        Assert.Equal(previewCalls.Concat(new[] { "journal:MasterCopyComposition.Create", "Create:copies:source" }), calls);
        Assert.DoesNotContain(calls, call => call.Contains("Exclusive", StringComparison.Ordinal) || call.Contains("Access", StringComparison.Ordinal));
    }

    [Fact]
    public void A_failed_create_is_reported_with_its_exception_type()
    {
        var (reply, _) = MasterCopy("block", "MCP_RC_DB", false, create: (_, _) => throw new InvalidOperationException());
        Assert.Equal("create-failed", reply["status"]);
        Assert.Equal(false, reply["created"]);
        Assert.Equal(typeof(InvalidOperationException).FullName, reply["exceptionType"]);
    }

    [Theory]
    [InlineData("1", "14sp1", "unsupported")]
    [InlineData("1", "15.1", "unsupported")]
    [InlineData("", "16", "disabled")]
    public void Alarm_export_refuses_before_V16_or_without_the_opt_in(string enabled, string release, string status)
    {
        int calls = 0;
        var reply = NativeCrashProbes.AlarmExport(enabled, release, Path.Combine(Path.GetTempPath(), "x.xlsx"), Path.GetTempPath(), "MCP_RC_None", "en-US",
            () => calls++, _ => calls++, (_, _, _, _) => { calls++; return ""; }, _ => calls++);
        Assert.Equal(status, reply["status"]);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Alarm_export_uses_the_explicit_overload_and_reports_the_file_size()
    {
        string directory = Path.Combine(Path.GetTempPath(), "crash-probe-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new List<string>();
            string[]? exportedNames = null;
            object? exportedLanguages = null;
            var reply = NativeCrashProbes.AlarmExport("1", "16", Path.Combine(directory, "out.xlsx"), directory, "MCP_RC_None, MCP_RC_Other",
                "en-US,de-DE",
                () => "provider", cultures => cultures, (provider, file, names, languages) => {
                    Assert.Equal("provider", provider);
                    exportedNames = names;
                    exportedLanguages = languages;
                    File.WriteAllBytes(file.FullName, new byte[7]);
                    return "Success";
                }, journal.Add);
            Assert.Equal("ok", reply["status"]);
            Assert.Equal(7L, reply["fileBytes"]);
            Assert.Equal("Success", reply["exportState"]);
            Assert.Equal(new[] { "MCP_RC_None", "MCP_RC_Other" }, exportedNames);
            Assert.Equal(new[] { "en-US", "de-DE" }, exportedLanguages);
            Assert.Equal(new[] { "PlcSoftware.GetService<PlcAlarmTextListProvider>", "LanguageSettings.Languages.Find",
                "PlcAlarmTextListProvider.ExportToXlsx(FileInfo,names,languages)" }, journal);
            var failed = NativeCrashProbes.AlarmExport("1", "16", Path.Combine(directory, "failed.xlsx"), directory, "MCP_RC_None", "en-US",
                () => "provider", cultures => cultures, (_, _, _, _) => throw new InvalidOperationException(), _ => { });
            Assert.Equal("export-failed", failed["status"]);
            Assert.Null(failed["fileBytes"]);
            Assert.Equal("invalid-argument", NativeCrashProbes.AlarmExport("1", "16", "relative.xlsx", directory, "MCP_RC_None", "en-US",
                () => "provider", c => c, (_, _, _, _) => "", _ => { })["status"]);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("diagnostics.ExecuteProbeMasterCopy", null, true)]
    [InlineData("diagnostics.ReadProbeSivarc", "0", true)]
    [InlineData("diagnostics.ReadProbePortalInfo", "1", false)]
    [InlineData("library.CreateLibraryMasterCopy", null, false)]
    public void The_worker_answers_disabled_probes_before_any_other_check(string operation, string? enabled, bool disabled)
    {
        var reply = TiaMcp.PlcWorker.WorkerOperations.DisabledProbeReply(operation, enabled);
        Assert.Equal(disabled, reply != null);
        if (disabled) Assert.Equal("disabled", JsonDocument.Parse(reply!).RootElement.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("other", false)]
    [InlineData("root-sibling", false)]
    [InlineData("root/../escape", false)]
    [InlineData("none", false)]
    [InlineData("root/sub", true)]
    public void The_worker_writes_the_alarm_workbook_only_under_its_own_output_root(string target, bool allowed)
    {
        string baseDirectory = Path.Combine(Path.GetTempPath(), "crash-probe-root-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(baseDirectory, "root");
        string path = Path.Combine(baseDirectory, target.Replace('/', Path.DirectorySeparatorChar), "out.xlsx");
        try
        {
            int calls = 0;
            var reply = NativeCrashProbes.AlarmExport("1", "17", path, target == "none" ? null : root + Path.DirectorySeparatorChar, "MCP_RC_None",
                "en-US", () => { calls++; return "provider"; }, c => c, (_, _, _, _) => { calls++; return "Success"; }, _ => { });
            Assert.Equal(allowed ? "ok" : "outside-output-root", reply["status"]);
            Assert.Equal(allowed ? 2 : 0, calls);
            Assert.Equal(allowed, NativeCrashProbes.UnderRoot(path, root));
        }
        finally { if (Directory.Exists(baseDirectory)) Directory.Delete(baseDirectory, true); }
    }
}