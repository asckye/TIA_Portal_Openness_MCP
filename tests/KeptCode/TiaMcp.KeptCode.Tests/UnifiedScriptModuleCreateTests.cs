using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    public Func<string, string, string, UnifiedScriptPair>? ComposeUnifiedScriptPair => UnifiedScriptFileCodec.ComposePair;
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
            if (meta["candidateDirectory"] is string candidate) workDirectories.Add(Path.GetDirectoryName(candidate)!);
        }
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
        internal bool FailExport, MalformedExport, ThrowAfterImport;
        public int Count => Modules.Count;
        public Module this[int index] => Modules[index];
        public bool Import(DirectoryInfo directory)
        {
            Effects.Add("Import");
            var pair = UnifiedScriptFileCodec.ReadPair(directory.FullName, "Status");
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
