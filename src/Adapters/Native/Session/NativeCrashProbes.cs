using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace TiaMcp.Adapters;

// Hidden crash probes. Every native step is journaled before it runs; replies carry counts, statuses,
// exception type names and step names only. SiVArc compositions are read by Count, Find and Item, never enumerated.
internal static class NativeCrashProbes
{
    internal const string TestObjectPrefix = "MCP_RC_";
    internal const int ItemCap = 20;
    internal static readonly string[] MasterCopySourceKinds = { "block", "blockGroup", "tagTable", "plcType" };

    internal sealed class SivarcBinding
    {
        internal Func<object, object> Provider = _ => throw new NotSupportedException();
        internal Func<object, object> TagDefinitions = _ => throw new NotSupportedException();
        internal Func<object, object> TextDefinitions = _ => throw new NotSupportedException();
        internal Func<object, int> Count = _ => throw new NotSupportedException();
        internal Func<object, string, object?> Find = (_, _) => throw new NotSupportedException();
        internal Func<object, int, object> Item = (_, _) => throw new NotSupportedException();
        internal Func<object, string> Name = _ => throw new NotSupportedException();
    }

    // V17-V19 reach the compositions by reflection, as the existing SiVArc block-definition code does.
    internal static SivarcBinding ReflectionBinding(Func<object, object> provider) => new SivarcBinding {
        Provider = provider, TagDefinitions = p => Property(p, "TagDefinitions"), TextDefinitions = p => Property(p, "TextDefinitions"),
        Count = c => (int)Property(c, "Count"),
        Find = (c, name) => Invoke(() => (c.GetType().GetMethod("Find", new[] { typeof(string) })
            ?? throw new NotSupportedException(c.GetType().FullName + ".Find(string) is unavailable.")).Invoke(c, new object[] { name })),
        Item = (c, index) => Invoke(() => (c.GetType().GetProperty("Item", new[] { typeof(int) })
            ?? throw new NotSupportedException(c.GetType().FullName + ".Item(int) is unavailable.")).GetValue(c, new object[] { index }))
            ?? throw new InvalidDataException("Composition item is null."),
        Name = item => Convert.ToString(Property(item, "Name"), System.Globalization.CultureInfo.InvariantCulture) ?? ""
    };

    private static object Property(object target, string name) => Invoke(() => (target.GetType().GetProperty(name, Type.EmptyTypes)
        ?? throw new NotSupportedException(target.GetType().FullName + "." + name + " is unavailable.")).GetValue(target))
        ?? throw new InvalidDataException(target.GetType().FullName + "." + name + " returned null.");

    private static object? Invoke(Func<object?> call)
    {
        try { return call(); }
        catch (TargetInvocationException error) when (error.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    private sealed class Probe
    {
        internal readonly List<string> Steps = new List<string>();
        internal readonly Dictionary<string, object?> Reply;
        private readonly Action<string> journal;
        internal Probe(Action<string> journal)
        {
            this.journal = journal;
            Reply = new Dictionary<string, object?> { ["status"] = "disabled", ["steps"] = Steps };
        }
        internal T Step<T>(string member, Func<T> call)
        {
            journal(member);
            Steps.Add(member);
            return call();
        }
        internal Dictionary<string, object?> Fail(string status, Exception error)
        {
            Reply["status"] = status;
            Reply["exceptionType"] = error.GetBaseException().GetType().FullName;
            return Reply;
        }
        internal Dictionary<string, object?> Status(string status) { Reply["status"] = status; return Reply; }
    }

    internal static Dictionary<string, object?> Sivarc(string? enabled, string release, string probe, string name, Func<object> block,
        SivarcBinding binding, Action<string> journal)
    {
        var run = new Probe(journal);
        if (enabled != "1") return run.Reply;
        if (release is "14sp1" or "15.1" or "16") return run.Status("unsupported");
        if (probe is not ("sivarc-count" or "sivarc-find" or "sivarc-item") || probe == "sivarc-find" && string.IsNullOrWhiteSpace(name))
            return run.Status("invalid-argument");
        try
        {
            var owner = run.Step("PlcBlock.Resolve", block);
            var provider = run.Step("PlcBlock.GetService<SivarcDataProvider>", () => binding.Provider(owner));
            foreach (var (kind, composition) in new[] { ("Tag", binding.TagDefinitions), ("Text", binding.TextDefinitions) })
            {
                string prefix = kind.ToLowerInvariant();
                var definitions = run.Step("SivarcDataProvider." + kind + "Definitions", () => composition(provider));
                string type = kind + "DefinitionComposition";
                if (probe == "sivarc-find")
                {
                    run.Reply[prefix + "Found"] = run.Step(type + ".Find", () => binding.Find(definitions, name)) != null;
                    continue;
                }
                int count = run.Step(type + ".Count", () => binding.Count(definitions));
                run.Reply[prefix + "DefinitionCount"] = count;
                if (probe != "sivarc-item") continue;
                var names = new List<string>();
                int read = 0;
                for (int index = 0; index < Math.Min(count, ItemCap); index++)
                {
                    var item = run.Step(type + ".Item(" + index + ")", () => binding.Item(definitions, index));
                    var itemName = run.Step(kind + "Definition.Name", () => binding.Name(item));
                    read++;
                    if (itemName.StartsWith(TestObjectPrefix, StringComparison.Ordinal)) names.Add(itemName);
                }
                run.Reply[prefix + "ItemsRead"] = read;
                run.Reply[prefix + "TestNames"] = names;
            }
            return run.Status("ok");
        }
        catch (Exception error) { return run.Fail("read-failed", error); }
    }

    internal static string? MasterCopyProblem(string sourceKind, string sourcePath)
    {
        if (!MasterCopySourceKinds.Contains(sourceKind)) return "sourceKind must be block, blockGroup, tagTable or plcType.";
        string leaf = sourcePath.Replace('\\', '/').TrimEnd('/').Split('/').Last();
        return leaf.StartsWith(TestObjectPrefix, StringComparison.Ordinal) ? null : "The source name must start with " + TestObjectPrefix + ".";
    }

    // The source is resolved by exact path and copied without acquiring ExclusiveAccess.
    internal static Dictionary<string, object?> MasterCopy(string? enabled, string sourceKind, string sourcePath, bool dryRun,
        Func<object> source, Func<object> target, Func<object, object, object> create, Action<string> journal)
    {
        var run = new Probe(journal);
        if (enabled != "1") return run.Reply;
        if (MasterCopyProblem(sourceKind, sourcePath) != null) return run.Status("invalid-argument");
        object copySource, copies;
        try
        {
            copySource = run.Step("ResolveMasterCopySource(" + sourceKind + ")", source);
            copies = run.Step("MasterCopyFolder.MasterCopies", target);
        }
        catch (Exception error) { return run.Fail("read-failed", error); }
        run.Reply["created"] = false;
        if (dryRun) return run.Status("preview");
        try { run.Step("MasterCopyComposition.Create", () => create(copies, copySource)); }
        catch (Exception error) { return run.Fail("create-failed", error); }
        run.Reply["created"] = true;
        return run.Status("ok");
    }

    internal static string[] List(string value) => value.Split(',').Select(part => part.Trim()).Where(part => part.Length != 0).ToArray();

    internal static bool UnderRoot(string path, string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathRooted(path)) return false;
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    // The explicit (FileInfo, names, languages) overload; the workbook stays under the session output root.

    internal static Dictionary<string, object?> AlarmExport(string? enabled, string release, string exportPath, string? outputRoot, string textListNames,
        string cultures, Func<object> provider, Func<string[], object> languages, Func<object, FileInfo, string[], object, string> export,
        Action<string> journal)
    {
        var run = new Probe(journal);
        if (enabled != "1") return run.Reply;
        if (release is "14sp1" or "15.1") return run.Status("unsupported");
        string[] names = List(textListNames), selected = List(cultures);
        if (names.Length == 0 || selected.Length == 0 || !Path.IsPathRooted(exportPath)
            || !string.Equals(Path.GetExtension(exportPath), ".xlsx", StringComparison.OrdinalIgnoreCase)) return run.Status("invalid-argument");
        // The worker checks its own output root; the host-built path is not trusted on its own.
        if (!UnderRoot(exportPath, outputRoot)) return run.Status("outside-output-root");
        try
        {
            var service = run.Step("PlcSoftware.GetService<PlcAlarmTextListProvider>", provider);
            var languageSet = run.Step("LanguageSettings.Languages.Find", () => languages(selected));
            Directory.CreateDirectory(Path.GetDirectoryName(exportPath)!);
            var file = new FileInfo(exportPath);
            run.Reply["exportState"] = run.Step("PlcAlarmTextListProvider.ExportToXlsx(FileInfo,names,languages)",
                () => export(service, file, names, languageSet));
            file.Refresh();
            run.Reply["fileBytes"] = file.Exists ? file.Length : (long?)null;
            return run.Status("ok");
        }
        catch (Exception error)
        {
            run.Reply["fileBytes"] = File.Exists(exportPath) ? new FileInfo(exportPath).Length : (long?)null;
            return run.Fail("export-failed", error);
        }
    }
}
