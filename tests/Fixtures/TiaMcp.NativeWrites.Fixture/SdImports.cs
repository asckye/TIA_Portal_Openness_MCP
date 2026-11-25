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
    internal class PlcBlock : IEngineeringObject
    {
        public Dictionary<string, object?> Attributes { get; } = new();
        public List<string> Setters { get; } = new();
        public int Number = 1;
        public bool AutoNumber = true, IsConsistent = true;
        public bool IsKnowHowProtected => false;
        public string ProgrammingLanguage = "LAD";
        public bool RefuseNumber, Unreadable, RefuseExport;
        public int Exports;
        public IEnumerable<SdAttributeInfo> GetAttributeInfos() => Attributes.Keys.Select(k => new SdAttributeInfo(k));
        public object? GetAttribute(string name) => Unreadable ? throw new InvalidOperationException("Cannot read " + name) : Attributes[name];
        public void Export(FileInfo destination, ExportOptions options)
        {
            if (options != ExportOptions.WithDefaults) throw new InvalidOperationException("Wrong recovery export options.");
            Exports++;
            if (RefuseExport) throw new IOException("Recovery export refused.");
            File.WriteAllText(destination.FullName, "<Document><SW.Blocks.OB ID=\"0\"><AttributeList>" +
                "<Name>Main</Name></AttributeList></SW.Blocks.OB></Document>");
        }
    }
    internal sealed class GlobalDB : PlcBlock { }
    internal sealed class OB : PlcBlock { public string SecondaryType = "ProgramCycle"; }
    internal sealed class PlcBlockGroup { public SdBlockComposition Blocks { get; } = new(); }
    internal sealed class SdBlockComposition : Dictionary<string, PlcBlock>
    {
        public PlcBlock? Find(string name) => TryGetValue(name, out var block) ? block : null;
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
    internal static class PlcBatchImportRunner
    {
        internal static string Directory = "";
        internal static string SingleImportRecoveryDirectory() => System.IO.Directory.CreateDirectory(Path.Combine(Directory, "backup")).FullName;
    }
}

namespace TiaMcp.Tests.NativeWrites
{
    internal static class SdImports
    {
        internal static object Run(string scenario, string directory)
        {
            PlcBatchImportRunner.Directory = directory;
            var group = new PlcBlockGroup();
            var main = new OB { Number = 1, IsConsistent = scenario != "sd-inconsistent", RefuseExport = scenario == "sd-backup-refusal" };
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
                using var files = new SdDeclarationFiles();
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
