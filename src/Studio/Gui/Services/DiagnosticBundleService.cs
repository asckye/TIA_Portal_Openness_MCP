using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TiaMcp.Versioning;
using TiaOpenness.Core.Environment;
using TiaOpenness.Gui.Common;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services.Stubs;
using TiaOpenness.Shared;

namespace TiaOpenness.Gui.Services;

public sealed record DiagnosticInput(string Category, string Path, string Entry, bool Tail = false, string? Content = null);
public sealed record DiagnosticManifestItem(string Category, string Entry, string Source, string State,
    bool Truncated, IReadOnlyDictionary<string, int> Redactions);

public class DiagnosticBundleService : ObservableObject, IDiagnosticBundleService
{
    private readonly Func<EnvironmentCheckContext> _context;
    private readonly Func<IReadOnlyList<EnvironmentFinding>> _checks;
    private readonly Func<EnvironmentCheckContext, IReadOnlyList<DiagnosticInput>> _inputs;
    private readonly Action<string> _open;
    private int _running;
    public Task Completion { get; private set; } = Task.CompletedTask;
    public DiagnosticProgress Progress { get; private set; } = new(DiagnosticState.Idle, LocalizedText.Empty);

    public DiagnosticBundleService() : this(EnvironmentServiceContext.Current, new EnvironmentCheckService().Capture, Inputs,
        folder => Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true })) { }
    public DiagnosticBundleService(Func<EnvironmentCheckContext> context, Func<IReadOnlyList<EnvironmentFinding>> checks,
        Func<EnvironmentCheckContext, IReadOnlyList<DiagnosticInput>> inputs, Action<string> open)
    { _context = context; _checks = checks; _inputs = inputs; _open = open; }

    public string Export()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return "";
        Update(new(DiagnosticState.Running, LocalizedText.Key("Env.BundleChecks"), 5));
        Completion = Task.Run(() =>
        {
            string? temporary = null;
            try
            {
                var context = _context();
                var findings = _checks();
                var inputs = _inputs(context);
                var redactor = new DiagnosticRedactor();
                var payloads = new List<(DiagnosticInput Input, string Text, bool Truncated)>();
                var manifest = new List<DiagnosticManifestItem>();
                // Read configurations first so their secret values can also be removed from free-form logs.
                foreach (var input in inputs.OrderBy(i => i.Tail))
                {
                    if (input.Content == null && !File.Exists(input.Path))
                    { manifest.Add(Item(input, "missing", false, new Dictionary<string, int>())); continue; }
                    try
                    {
                        if (input.Content == null && (File.GetAttributes(input.Path) & FileAttributes.ReparsePoint) != 0)
                        { manifest.Add(Item(input, "skipped-reparse-point", false, new Dictionary<string, int>())); continue; }
                        var content = Read(input);
                        if (!input.Tail && content.Truncated)
                        { manifest.Add(Item(input, "skipped-oversized-config", true, new Dictionary<string, int>())); continue; }
                        // Collect configuration secrets without retaining their raw bytes in the archive.
                        if (!input.Tail) redactor.Redact(content.Text, Format(input));
                        payloads.Add((input, content.Text, content.Truncated));
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    { manifest.Add(Item(input, "unreadable", false, new Dictionary<string, int>())); }
                }
                Update(new(DiagnosticState.Running, LocalizedText.Key("Env.BundleRedact"), 40));
                var reports = Path.Combine(context.DataRoot, "reports");
                Directory.CreateDirectory(reports);
                string final = Path.Combine(reports, "diagnostics-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8] + ".zip");
                temporary = final + ".partial";
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    var checkDocument = JsonSerializer.Serialize(new
                    {
                        capturedAtUtc = DateTimeOffset.UtcNow,
                        userFallback = context.UserFallback, dataRoot = context.DataRoot,
                        configDirectory = context.ConfigDirectory, logsDirectory = context.LogsDirectory,
                        checks = findings.Select(f => new
                        {
                            id = f.Id, status = f.Status.ToString(), evidence = f.Evidence,
                            en = Text(f, false), zh = Text(f, true),
                        }),
                    }, JsonOptions);
                    var checks = redactor.Redact(checkDocument, "json");
                    Add(zip, "environment/checks.json", checks.Text);
                    manifest.Add(new("environment", "environment/checks.json", "local doctor", "included", false, checks.Rules));
                    for (int index = 0; index < payloads.Count; index++)
                    {
                        var payload = payloads[index];
                        var redacted = redactor.Redact(payload.Text, Format(payload.Input));
                        Add(zip, payload.Input.Entry, redacted.Text);
                        manifest.Add(Item(payload.Input, "included", payload.Truncated, redacted.Rules));
                        Update(new(DiagnosticState.Running, LocalizedText.Key("Env.BundleWrite"), 50 + 40 * (index + 1) / Math.Max(1, payloads.Count)));
                    }
                    var manifestText = JsonSerializer.Serialize(new
                    {
                        format = "tia-workbench-diagnostics-v1", createdAtUtc = DateTimeOffset.UtcNow,
                        limits = new { logTailBytes = 262144, logTailLines = 200, configBytes = 2097152 },
                        redaction = new[] { "secret-field", "xml-secret", "auth-scheme", "url-userinfo", "query-secret", "text-secret", "private-key", "known-secret", "unparseable-hidden" },
                        items = manifest.Select(item => item with
                        {
                            Source = redactor.Redact(item.Source).Text,
                            Entry = redactor.Redact(item.Entry).Text,
                        }),
                    }, JsonOptions);
                    Add(zip, "manifest.json", manifestText);
                }
                File.Move(temporary, final); temporary = null;
                Update(new(DiagnosticState.Done, LocalizedText.Key("Env.BundleDone"), 100, final));
            }
            catch (Exception ex)
            { Update(new(DiagnosticState.Idle, LocalizedText.Key("Env.BundleFailed", new DiagnosticRedactor().Redact(ex.Message).Text))); }
            finally
            {
                if (temporary != null)
                    try { File.Delete(temporary); }
                    catch (IOException ex) { Trace.TraceWarning("Diagnostic partial cleanup failed: {0}", ex.Message); }
                    catch (UnauthorizedAccessException ex) { Trace.TraceWarning("Diagnostic partial cleanup denied: {0}", ex.Message); }
                Interlocked.Exchange(ref _running, 0);
            }
        });
        return "";
    }

    public LocalizedText OpenFolder()
    {
        if (Progress.State == DiagnosticState.Done) _open(Path.GetDirectoryName(Progress.Path)!);
        return LocalizedText.Empty;
    }

    private void Update(DiagnosticProgress progress) { Progress = progress; Raise(nameof(Progress)); }
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static object Text(EnvironmentFinding finding, bool zh)
    {
        var table = zh ? Strings.Chinese : Strings.English;
        return new { result = table[EnvironmentCheckCatalogue.ResultKey(finding)], fix = table[EnvironmentCheckCatalogue.FixKey(finding.Id, finding.Result == "NewLogon")] };
    }
    private static DiagnosticManifestItem Item(DiagnosticInput input, string state, bool truncated, IReadOnlyDictionary<string, int> rules)
        => new(input.Category, input.Entry, input.Path, state, truncated, rules);
    private static string Format(DiagnosticInput input) => input.Tail ? "text" : Path.GetExtension(input.Path).ToLowerInvariant() switch { ".json" => "json", ".config" => "xml", _ => "text" };
    private static void Add(ZipArchive zip, string entry, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(entry, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    internal static (string Text, bool Truncated) Read(DiagnosticInput input)
    {
        int limit = input.Tail ? 262144 : 2097152;
        string? content = input.Content;
        bool clipped = content != null && content.Length > limit;
        if (clipped) content = content![^limit..];
        using Stream stream = content == null
            ? new FileStream(input.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)
            : new MemoryStream(Encoding.UTF8.GetBytes(content));
        long start = input.Tail ? Math.Max(0, stream.Length - limit) : 0;
        stream.Position = start;
        var buffer = new byte[(int)Math.Min(stream.Length - start, limit + 1L)];
        int count = 0, read;
        while (count < buffer.Length && (read = stream.Read(buffer, count, buffer.Length - count)) > 0) count += read;
        bool truncated = clipped || start > 0 || count > limit;
        string text = Encoding.UTF8.GetString(buffer, 0, Math.Min(count, limit));
        if (input.Tail)
        {
            if (clipped || start > 0) text = text.Contains('\n') ? text[(text.IndexOf('\n') + 1)..] : "";
            var lines = text.Split('\n');
            if (lines.Length > 200) { truncated = true; text = string.Join("\n", lines.Skip(lines.Length - 200)); }
        }
        return (text, truncated);
    }

    public static IReadOnlyList<DiagnosticInput> Inputs(EnvironmentCheckContext context)
    {
        var inputs = new List<DiagnosticInput>();
        inputs.Add(new("windows-event", "local Windows event/process/dump inventory", "windows/tia-exit-evidence.json", false,
            TiaExitEvidenceCollector.Capture()));
        inputs.Add(TiaExitEvidenceCollector.NativeExportLogInput(DataLocations.Current.SystemTempDirectory));
        foreach (string file in Directory.Exists(context.ConfigDirectory) ? Directory.GetFiles(context.ConfigDirectory).OrderBy(p => p).Take(128) : [])
            if (Path.GetExtension(file).ToLowerInvariant() is ".json" or ".settings" or ".config")
                inputs.Add(new("config", file, "config/" + Path.GetFileName(file)));
        inputs.Add(new("config", DataLocations.Current.UiFilePath, "config/ui.settings"));
        var logRoots = context.LogReadRoots ?? new[] { context.LogsDirectory };
        for (int index = 0; index < logRoots.Length; index++)
            AddLogs(inputs, Path.Combine(logRoots[index], "studio"), "workbench-log", "logs/studio" + (logRoots.Length == 1 ? "" : "/root-" + (index + 1)));
        // The existing operation log is held by the open Workbench, rather than persisted to a file.
        var activity = System.Windows.Application.Current?.Dispatcher.Invoke(() => System.Windows.Application.Current.Windows
            .OfType<MainWindow>().Select(window => ((ViewModels.MainViewModel)window.DataContext).Activity.Log).ToArray()) ?? [];
        for (int index = 0; index < activity.Length; index++)
            inputs.Add(new("workbench-log", "current Workbench activity " + (index + 1), "logs/workbench/activity-" + (index + 1) + ".log", true, activity[index]));
        if (activity.Length == 0) inputs.Add(new("workbench-log", "current Workbench activity", "logs/workbench/activity.log", true));
        string diagnostics = context.DiagnosticsDirectory ?? DataLocations.Current.DiagnosticsDirectory;
        var journals = Directory.Exists(diagnostics) ? Directory.GetFiles(diagnostics, "calls-*.jsonl*")
            .OrderByDescending(File.GetLastWriteTimeUtc).Take(64).ToArray() : [];
        foreach (string journal in journals)
            inputs.Add(new("host-log", journal, "logs/hosts/journal/" + Path.GetFileName(journal), true));
        if (journals.Length == 0) inputs.Add(new("host-log", Path.Combine(diagnostics, "calls-*.jsonl"), "logs/hosts/journal", true));
        foreach (var release in TiaVersionCatalog.Runnable)
        {
            for (int index = 0; index < logRoots.Length; index++)
                AddLogs(inputs, Path.Combine(logRoots[index], release.Key), "host-log", "logs/hosts/" + release.Key + (logRoots.Length == 1 ? "" : "/root-" + (index + 1)));
            if (context.BundleRoot != null)
            {
                string host = EnvironmentBundleFiles.Host(context.BundleRoot, release.Key);
                string config = Path.ChangeExtension(host, ".runtimeconfig.json");
                inputs.Add(new("host-config", config, "config/hosts/" + release.Key + "/" + Path.GetFileName(config)));
                string workerConfig = BundleLayout.WorkerPath(context.BundleRoot, release.Key) + ".config";
                inputs.Add(new("host-config", workerConfig, "config/workers/" + release.Key + "/" + Path.GetFileName(workerConfig)));
            }
        }
        var auditRoots = context.AuditReadRoots ?? new[] { context.AuditDirectory ?? DataLocations.Current.AuditDirectory };
        for (int index = 0; index < auditRoots.Length; index++)
            foreach (var file in Directory.Exists(auditRoots[index]) ? Directory.GetFiles(auditRoots[index], "audit-*.jsonl").OrderByDescending(File.GetLastWriteTimeUtc).Take(64) : [])
                inputs.Add(new("host-log", file, "logs/audit/chain-" + (index + 1) + "/" + Path.GetFileName(file), true));
        return inputs;
    }

    internal static void AddLogs(List<DiagnosticInput> inputs, string directory, string kind, string archive)
    {
        var files = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.log").OrderByDescending(File.GetLastWriteTimeUtc).Take(64).ToArray() : [];
        foreach (var file in files) inputs.Add(new(kind, file, archive + "/" + Path.GetFileName(file), true));
    }
}
