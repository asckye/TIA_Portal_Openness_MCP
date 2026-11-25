using System.Xml.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW.Blocks;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Plc;

namespace Siemens.Engineering
{
    internal enum ExportOptions { WithDefaults }
    internal interface IEngineeringObject
    {
        public IEnumerable<SdAttributeInfo> GetAttributeInfos();
        public object? GetAttribute(string name);
    }
    internal sealed class SdAttributeInfo(string name) { public string Name => name; }
}

namespace Siemens.Engineering.SW.Blocks
{
    internal partial class PlcBlock : IEngineeringObject
    {
        public Dictionary<string, object?> Attributes { get; } = new();
        public List<string> Setters { get; } = new();
        public int Number = 1;
        public bool AutoNumber = true, IsConsistent = true;
        public bool IsKnowHowProtected => false;
        public string ProgrammingLanguage = "LAD";
        public bool RefuseNumber, Unreadable, RefuseExport;
        public int Exports;
        public Action? AfterExport;
        public IEnumerable<SdAttributeInfo> GetAttributeInfos() => Attributes.Keys.Select(k => new SdAttributeInfo(k));
        public object? GetAttribute(string name) => Unreadable ? throw new InvalidOperationException("Cannot read " + name) : Attributes[name];
        public void Export(FileInfo destination, ExportOptions options)
        {
            if (options != ExportOptions.WithDefaults) throw new InvalidOperationException("Wrong recovery export options.");
            Exports++;
            if (RefuseExport) throw new IOException("Recovery export refused.");
            new XDocument(new XElement("Document", new XElement("SW.Blocks." + GetType().Name, new XAttribute("ID", "0"),
                new XElement("AttributeList", new XElement("Name", Name), new XElement("Number", Number))))).Save(destination.FullName);
            AfterExport?.Invoke();
        }
    }
    internal sealed class GlobalDB : PlcBlock { }
    internal sealed class OB : PlcBlock { public string SecondaryType = "ProgramCycle"; }
    internal sealed class PlcBlockGroup
    {
        public PlcBlockGroup? Parent { get; set; }
        public List<PlcBlockGroup> Groups { get; } = new();
        public SdBlockComposition Blocks { get; } = new();
    }
    internal sealed partial class SdBlockComposition : IEnumerable<PlcBlock>
    {
        private readonly Dictionary<string, PlcBlock> byName = new(StringComparer.OrdinalIgnoreCase);
        public void Add(string name, PlcBlock block) => byName.Add(name, block);
        public PlcBlock? Find(string name) => byName.TryGetValue(name, out var block) ? block : null;
        public IEnumerator<PlcBlock> GetEnumerator() => byName.Values.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

namespace TiaMcp.Adapters.Native.Plc
{
    internal static class PlcDocumentPrimitives
    {
        public static void SetAutoNumber(PlcBlock block, bool value)
        {
            block.Setters.Add("AutoNumber=" + value);
            block.AutoNumber = value;
        }
        public static void SetNumber(PlcBlock block, int value)
        {
            block.Setters.Add("Number=" + value);
            if (block.RefuseNumber) throw new InvalidOperationException("Number setter refused.");
            block.Number = value;
        }
    }
    internal static class PlcBatchImportRecovery
    {
        internal static string Root = "";
        internal static string Directory() =>
            System.IO.Directory.CreateDirectory(Path.Combine(Root, Guid.NewGuid().ToString("N"))).FullName;
    }
}

namespace TiaMcp.Tests.NativeWrites
{
    internal static class SdImports
    {
        private static object BatchRefresh(string directory, PlcBlockGroup group)
        {
            var files = new SdDeclarationFiles();
            var selected = new[] { "A", "B" }.Select(name => {
                group.Blocks.Add(name, new GlobalDB { Name = name, Number = 42, ProgrammingLanguage = "DB" });
                var file = new FileInfo(Path.Combine(directory, name + ".s7dcl"));
                File.WriteAllText(file.FullName, $"DATA_BLOCK \"{name}\"\nVAR\n    Value : Int;\nEND_VAR\nEND_DATA_BLOCK");
                return file;
            }).ToArray();
            var plans = PlcSdBlockSnapshot.PrepareBatch(selected, files, group, true, _ => false);
            int admitted = plans["B"].Existing!.Number;
            foreach (var file in selected)
            {
                string name = Path.GetFileNameWithoutExtension(file.Name);
                var plan = plans[name];
                plan.RefreshForImport();
                files.RequireUnchanged(file);
                // The first import renumbers its neighbour. The second import renumbers its own target.
                if (name == "A") group.Blocks.Find("B")!.Number = 88;
                else group.Blocks.Find("B")!.Number = 1;
                if (!plan.Verify(() => group.Blocks.Find(name))) throw new InvalidOperationException("Batch verification failed.");
            }
            var second = group.Blocks.Find("B")!;
            return new { admitted, second.Number, second.Exports, second.Setters, evidence = plans["B"].Evidence };
        }

        internal static object Run(string scenario, string directory)
        {
            PlcBatchImportRecovery.Root = directory;
            var group = new PlcBlockGroup();
            if (scenario == "sd-batch-refresh") return BatchRefresh(directory, group);
            var main = new OB { Name = "Main", Number = 1, IsConsistent = scenario != "sd-inconsistent", RefuseExport = scenario == "sd-backup-refusal" };
            main.Attributes.Add("IsIECCheckEnabled", false);
            group.Blocks.Add("Main", main);
            bool create = scenario == "sd-new";
            PlcBlock after = create ? new GlobalDB { Number = 1, ProgrammingLanguage = "DB" }
                : new OB { Number = 123, RefuseNumber = scenario == "sd-setter" };
            after.Attributes.Add("IsIECCheckEnabled", false);
            var evidence = new Dictionary<string, object?>();
            int imports = 0, postReads = 0;
            try
            {
                var files = new SdDeclarationFiles();
                var declarations = new List<FileInfo>();
                void Add(string name, string kind = "ORGANIZATION_BLOCK")
                {
                    var file = new FileInfo(Path.Combine(directory, name + ".s7dcl"));
                    File.WriteAllText(file.FullName, kind + " \"" + name + "\"\nEND_" + kind + "\n");
                    declarations.Add(file);
                }
                if (create) Add("NewDB", "DATA_BLOCK");
                else Add("Main");
                if (scenario == "sd-batch")
                {
                    Add("MCP_RC_OBStart");
                    group.Blocks.Add("MCP_RC_OBStart", new OB { Number = 151, SecondaryType = "Startup" });
                }
                if (scenario == "sd-before-unreadable") main.Unreadable = true;
                if (scenario == "sd-preview")
                {
                    main.SecondaryType = "Startup";
                    var previews = PlcSdBlockSnapshot.PrepareBatch(declarations.ToArray(), files, group, true, _ => false, dryRun: true);
                    return new { evidence = previews["Main"].Evidence, imports, postReads, exports = main.Exports };
                }
                var plans = PlcSdBlockSnapshot.PrepareBatch(declarations.ToArray(), files, group, !create, _ => false);
                foreach (var pair in plans)
                {
                    foreach (var field in pair.Value.Evidence) evidence[field.Key] = field.Value;
                    imports++;
                    if (scenario == "sd-after-unreadable") after.Unreadable = true;
                    bool preserved = pair.Value.Verify(() => { postReads++; return after; });
                    foreach (var field in pair.Value.Evidence) evidence[field.Key] = field.Value;
                    evidence["writeReturned"] = true;
                    evidence["operationSuccess"] = preserved;
                    if (!preserved) evidence["status"] = SdBlockOverridePolicy.Failure;
                }
            }
            catch (Exception error)
            {
                evidence["error"] = error.Message;
                evidence["condition"] = (error as AdapterPreconditionException)?.Condition;
                evidence["operationSuccess"] = false;
            }
            return new { evidence, imports, postReads, exports = main.Exports, after.Setters, after.Number, after.AutoNumber };
        }
    }
}
