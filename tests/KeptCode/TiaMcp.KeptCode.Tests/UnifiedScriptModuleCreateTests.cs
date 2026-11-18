using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Engineering;
using TiaMcp.Adapters.Native.Hmi;
using TiaMcpServer.Siemens.Services;
using Xunit;

namespace TiaMcp.Engine.Tests;

public sealed class UnifiedScriptModuleCreateTests : IDisposable, IUnifiedCodecs
{
    private const string Code = "export function Status() {\n  return '状态';\n}\n";
    private readonly IUnifiedCodecs? previousCodecs = UnifiedCodecs.Current;
    private readonly Func<string, Dictionary<string, object?>, string>? previousToken = HmiExactAccess.ContentToken;
    private readonly List<string> workDirectories = new();
    private readonly Software software = new();

    public UnifiedScriptModuleCreateTests()
    {
        UnifiedCodecs.Current = this;
        HmiExactAccess.ContentToken = (scope, value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope + JsonSerializer.Serialize(value))));
    }

    public Func<string, string, UnifiedScriptPair>? ReadUnifiedScriptPair => UnifiedScriptFileCodec.ReadPair;
    public Func<string, string, string, UnifiedScriptPair>? ComposeUnifiedScriptPair { get; private set; } = UnifiedScriptFileCodec.ComposePair;
    public Action<string, string>? RequireUnifiedPlainFile => UnifiedScriptFileCodec.RequirePlainFile;
    public Action<string>? ValidateUnifiedScript => UnifiedScriptFileCodec.ValidateScript;
    public Func<string, string, string, IEnumerable<Dictionary<string, object?>>>? DecodeUnifiedDocument => null;
    public Func<string, string, Dictionary<string, object?>>? AnalyzeUnifiedScript => null;

    private HmiObject Create(bool preview, string token = "", string code = Code, HmiObject? evidence = null)
    {
        var meta = evidence ?? new HmiObject();
        try
        {
            EngineeringWriteJournal.Run(meta, () => UnifiedGlobalScriptEdit.Create(() => software,
                "Fixture", "HMI", "Status", code, preview, token, meta));
            return meta;
        }
        finally
        {
            if (meta["candidateDirectoryRelativeToTemp"] is string relative)
                workDirectories.Add(Path.GetDirectoryName(Path.Combine(TiaOpenness.Shared.DataLocations.Current.TempDirectory, relative))!);
        }
    }

    [Theory]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public void Create_execution_refuses_before_the_importing_worker_is_called(string release)
    {
        using var scope = TiaMcpServer.ModelContextProtocol.HardwareContract.UseRelease(release);
        var preview = Create(true);
        string token = (string)preview["token"]!;
        int dispatches = 0;
        var service = new GlobalScriptEditService((_, _) => {
            dispatches++;
            return JsonSerializer.SerializeToNode(new HmiReply { Value = new HmiMessage { Meta = Create(false, token) } });
        }, () => "C:/Fixture.ap" + release);
        var error = Assert.Throws<TiaMcpServer.Siemens.PortalException>(() =>
            service.UpdateUnifiedGlobalScript("HMI", "Fixture", "Status", Code, false, token, "create"));
        Assert.Equal(TiaMcpServer.Siemens.PortalErrorCode.NotSupportedOnVersion, error.Code);
        Assert.Contains("Create the module in TIA", error.Message);
        Assert.Equal(0, dispatches);
        Assert.Empty(software.Scripts.Effects);
        Assert.Null(software.Scripts.ImportDirectory);
    }

    private void AssertCandidateEvidence(HmiObject result, JsonNode reply)
    {
        string candidate = Assert.IsType<string>(software.Scripts.ImportDirectory);
        Assert.Equal("<path>", result["candidateDirectory"]);
        foreach (var evidence in new[] { reply["data"]!["evidence"]!, reply["error"]!["details"]!["evidence"]! })
        {
            Assert.Equal("<path>", (string?)evidence["candidateDirectory"]);
            string relative = (string)evidence["candidateDirectoryRelativeToTemp"]!;
            Assert.False(Path.IsPathRooted(relative));
            Assert.Equal(candidate, Path.Combine(TiaOpenness.Shared.DataLocations.Current.TempDirectory, relative));
        }
        Assert.True(File.Exists(Path.Combine(candidate, "Status.hmi.yml")));
        Assert.True(File.Exists(Path.Combine(candidate, "Status.hmi.js")));
    }

    [Fact]
    public void True_import_without_a_module_is_a_known_failure_with_candidate_evidence()
    {
        for (int i = 0; i < 5; i++) software.Scripts.Modules.Add(new Module(software.Scripts, "Existing" + i, Code));
        var preview = Create(true);
        software.Scripts.NoInsertion = true;
        var result = Create(false, (string)preview["token"]!);
        Assert.Equal("ImportDidNotCreateModule", result["status"]);
        Assert.Equal(true, result["importReturned"]);
        Assert.Equal(false, result["moduleExistsAfterImport"]);
        Assert.Equal(5, result["scriptCountAfterImport"]);
        Assert.Equal(false, result["operationSuccess"]);
        Assert.Equal(false, result["verificationSuccess"]);
        Assert.Equal(false, result["mayHaveChanged"]);
        Assert.NotEqual(true, result["writeOutcomeUnknown"]);
        Assert.Contains("requested module does not exist", Assert.IsType<string>(result["nativeMessage"]));
        Assert.NotNull(result["candidateDirectory"]);
        Assert.NotNull(result["after"]);
        Assert.Equal(new[] { "Import" }, software.Scripts.Effects);
        Assert.Equal(5, software.Scripts.Modules.Count);
        Assert.DoesNotContain(software.Scripts.Modules, module => module.Name == "Status");
        var reply = TiaMcpServer.ModelContextProtocol.UnifiedHmiContract.Map("SetUnifiedGlobalScript",
            new { Meta = result, Message = "Import did not create the module." }, writes: true, current: true).StructuredContent!;
        Assert.False((bool?)reply["ok"]);
        Assert.Equal("failed", (string?)reply["meta"]!["outcome"]);
        Assert.NotEqual("OUTCOME_UNKNOWN", (string?)reply["error"]!["code"]);
        Assert.Contains("requested script module was not created", (string?)reply["error"]!["message"]);
        Assert.Contains("candidate JS/YAML pair", (string?)reply["error"]!["message"]);
        var evidence = reply["error"]!["details"]!["evidence"]!;
        AssertCandidateEvidence(result, reply);
        Assert.Equal(5, (int?)evidence["scriptCountAfterImport"]);
        Assert.False((bool?)evidence["moduleExistsAfterImport"]);
    }

    [Theory]
    [InlineData("Other", false)]
    [InlineData("STATUS", true)]
    public void Changed_count_or_case_variant_after_import_keeps_the_write_unknown(string importedName, bool replaceExisting)
    {
        software.Scripts.Modules.Add(new Module(software.Scripts, "Existing", Code));
        var preview = Create(true);
        software.Scripts.AfterImport = () => {
            if (replaceExisting) software.Scripts.Modules.RemoveAt(0);
            software.Scripts.Modules.Add(new Module(software.Scripts, importedName, Code));
        };
        var result = Create(false, (string)preview["token"]!);
        Assert.Equal("ImportUnverified", result["status"]);
        Assert.Equal(true, result["mayHaveChanged"]);
        AssertManualCleanup(result);
        Assert.Equal(new[] { "Import" }, software.Scripts.Effects);
        var reply = TiaMcpServer.ModelContextProtocol.UnifiedHmiContract.Map("SetUnifiedGlobalScript",
            new { Meta = result, Message = "Unverified import." }, writes: true, current: true).StructuredContent!;
        Assert.Equal("unknown", (string?)reply["meta"]!["outcome"]);
        Assert.Equal("OUTCOME_UNKNOWN", (string?)reply["error"]!["code"]);
        var evidence = reply["error"]!["details"]!["evidence"]!;
        AssertCandidateEvidence(result, reply);
        Assert.Equal(replaceExisting ? 1 : 2, (int?)evidence["scriptCountAfterImport"]);
        Assert.Equal(importedName == "STATUS", (bool?)evidence["moduleExistsAfterImport"]);
    }

    [Fact]
    public void Create_refuses_a_manifest_and_script_with_different_stems_before_import()
    {
        var preview = Create(true);
        ComposeUnifiedScriptPair = (directory, name, code) => {
            var pair = UnifiedScriptFileCodec.ComposePair(directory, name, code);
            File.Move(Path.Combine(directory, pair.YamlName), Path.Combine(directory, "Different.hmi.yml"));
            return UnifiedScriptFileCodec.ReadPair(directory, name);
        };
        var error = Assert.Throws<NotSupportedException>(() => Create(false, (string)preview["token"]!));
        Assert.Contains("share a base name", error.Message);
        Assert.Empty(software.Scripts.Effects);
    }

    [Fact]
    public void Preview_then_create_imports_once_and_exports_exact_text_without_a_before_export()
    {
        var preview = Create(true);
        Assert.Equal("Preview", preview["status"]);
        Assert.Equal(Code, ((HmiObject)preview["after"]!)["scriptCode"]);
        Assert.Equal("No Openness delete exists for script modules; remove manually in TIA if needed.", preview["irreversible"]);
        Assert.Empty(software.Scripts.Effects);
        Assert.Empty(software.Scripts.Modules);
        var result = Create(false, (string)preview["token"]!);
        Assert.Equal("Verified", result["status"]);
        Assert.Equal(true, result["operationSuccess"]);
        Assert.Equal(true, result["verificationSuccess"]);
        Assert.Equal(new[] { "Import", "Export" }, software.Scripts.Effects);
        Assert.Equal(Code, Assert.Single(software.Scripts.Modules).Text);
        Assert.Null(result["before"]);
    }

    [Theory]
    [InlineData("Status")]
    [InlineData("status")]
    public void Existing_name_or_case_variant_refuses_preview_and_apply(string name)
    {
        var preview = Create(true);
        software.Scripts.Modules.Add(new Module(software.Scripts, name, Code));
        Assert.Contains(name, Assert.Throws<ArgumentException>(() => Create(true)).Message);
        Assert.Contains(name, Assert.Throws<ArgumentException>(() => Create(false, (string)preview["token"]!)).Message);
        Assert.Empty(software.Scripts.Effects);
    }

    [Theory]
    [InlineData("")]
    [InlineData("stale-token")]
    public void Missing_or_stale_token_never_imports(string token)
    {
        Assert.Throws<PlcSoftwareException>(() => Create(false, token));
        Assert.Empty(software.Scripts.Effects);
    }

    [Fact]
    public void Changed_source_invalidates_native_token()
    {
        var preview = Create(true);
        Assert.Throws<PlcSoftwareException>(() => Create(false, (string)preview["token"]!, "export const changed = true;"));
        Assert.Empty(software.Scripts.Effects);
    }

    [Theory]
    [InlineData(false, false, "ImportRejected")]
    [InlineData(true, true, "ReadbackMismatch")]
    public void Rejected_import_or_mismatched_readback_reports_failure_and_manual_cleanup(bool accepted, bool mismatch, string status)
    {
        var preview = Create(true);
        software.Scripts.Accept = accepted; software.Scripts.Mismatch = mismatch;
        var result = Create(false, (string)preview["token"]!);
        Assert.Equal(status, result["status"]);
        Assert.Equal(false, result["operationSuccess"]);
        Assert.Equal(false, result["verificationSuccess"]);
        Assert.Equal(true, result["mayHaveChanged"]);
        Assert.Equal(1, software.Scripts.Effects.Count(e => e == "Import"));
        Assert.NotNull(result["cleanupRequired"]);
        AssertManualCleanup(result);
    }

    private static void AssertManualCleanup(HmiObject result)
    {
        Assert.IsType<string>(result["rollbackOutcome"]);
        Assert.NotEqual("restored", result["rollbackOutcome"]);
        Assert.Equal(false, result["rollbackCompleted"]);
        var cleanup = Assert.Single(Assert.IsAssignableFrom<IEnumerable<object?>>(result["cleanupRequired"]));
        var task = Assert.IsAssignableFrom<IDictionary<string, object?>>(cleanup);
        Assert.Equal("HMI/Scripts/Status: no Openness delete; remove manually in TIA if needed", task["target"]);
        Assert.Contains("remove manually in TIA", Assert.IsType<string>(task["target"]));
        Assert.Equal("NotSupportedException", task["reason"]);
    }

    [Fact]
    public void True_import_with_failed_readback_export_retains_the_module_and_manual_cleanup()
    {
        var preview = Create(true);
        software.Scripts.FailExport = true;
        var result = Create(false, (string)preview["token"]!);
        Assert.Equal(true, result["importReturned"]);
        Assert.Equal("NativeExportFailed", result["status"]);
        Assert.Equal(false, result["operationSuccess"]);
        Assert.Equal(false, result["verificationSuccess"]);
        Assert.Equal(true, result["mayHaveChanged"]);
        Assert.Equal(new[] { "Import", "Export" }, software.Scripts.Effects);
        Assert.Single(software.Scripts.Modules);
        AssertManualCleanup(result);
    }

    [Fact]
    public void Exception_after_true_import_does_not_claim_that_the_new_module_was_restored()
    {
        var preview = Create(true);
        software.Scripts.MalformedExport = true;
        var result = new HmiObject();
        Assert.Throws<NotSupportedException>(() => Create(false, (string)preview["token"]!, evidence: result));
        Assert.Equal(true, result["importReturned"]);
        Assert.Equal(false, result["operationSuccess"]);
        Assert.Equal(true, result["mayHaveChanged"]);
        Assert.Equal(new[] { "Import", "Export" }, software.Scripts.Effects);
        Assert.Single(software.Scripts.Modules);
        AssertManualCleanup(result);
    }

    [Fact]
    public void Native_import_exception_after_insertion_preserves_the_manual_delete_obligation()
    {
        var preview = Create(true);
        software.Scripts.ThrowAfterImport = true;
        var result = new HmiObject();
        var error = Assert.Throws<TargetInvocationException>(() => Create(false, (string)preview["token"]!, evidence: result));
        Assert.IsType<IOException>(error.InnerException);
        Assert.Equal(false, result["operationSuccess"]);
        Assert.Equal(true, result["mayHaveChanged"]);
        Assert.Equal(new[] { "Import" }, software.Scripts.Effects);
        Assert.Single(software.Scripts.Modules);
        AssertManualCleanup(result);
    }

    public sealed class Software
    {
        public Scripts Scripts { get; } = new();
    }
    public sealed class Scripts
    {
        internal readonly List<Module> Modules = new();
        internal readonly List<string> Effects = new();
        internal bool Accept = true, Mismatch;
        internal bool FailExport, MalformedExport, ThrowAfterImport, NoInsertion;
        internal Action? AfterImport;
        internal string? ImportDirectory;
        public int Count => Modules.Count;
        public Module this[int index] => Modules[index];
        public bool Import(DirectoryInfo directory, string name)
        {
            Effects.Add("Import");
            ImportDirectory = directory.FullName;
            var pair = UnifiedScriptFileCodec.ReadPair(directory.FullName, "Status");
            Assert.Equal("Status", name);
            Assert.Equal(pair.YamlName, name + ".hmi.yml");
            Assert.Equal(pair.JsName, name + ".hmi.js");
            if (NoInsertion) return true;
            if (AfterImport != null) { AfterImport(); return true; }
            Modules.Add(new Module(this, "Status", Mismatch ? pair.Text + "// changed\n" : pair.Text));
            if (ThrowAfterImport) throw new IOException("Synthetic import failure after module insertion.");
            return Accept;
        }
    }
    public sealed class Module(Scripts owner, string name, string text)
    {
        public string Name => name;
        internal string Text => text;
        public IEnumerable<FileInfo> Export(DirectoryInfo directory, string fileName)
        {
            Assert.Equal("Module", fileName);
            owner.Effects.Add("Export");
            if (owner.FailExport) throw new IOException("Synthetic readback export failure after a successful import.");
            var pair = UnifiedScriptFileCodec.ComposePair(directory.FullName, name, text);
            if (owner.MalformedExport) File.WriteAllText(Path.Combine(directory.FullName, pair.YamlName), "ScriptModules: {}\n");
            return new[] { new FileInfo(Path.Combine(directory.FullName, pair.YamlName)), new FileInfo(Path.Combine(directory.FullName, pair.JsName)) };
        }
    }

    public void Dispose()
    {
        UnifiedCodecs.Current = previousCodecs; HmiExactAccess.ContentToken = previousToken;
        foreach (var directory in workDirectories)
        {
            Assert.StartsWith(Path.GetFullPath(TiaOpenness.Shared.DataLocations.Current.TempDirectory), Path.GetFullPath(directory),
                StringComparison.OrdinalIgnoreCase);
            Directory.Delete(directory, true);
        }
    }
}
