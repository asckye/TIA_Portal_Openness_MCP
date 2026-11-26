using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Plc;

namespace Siemens.Engineering.SW
{
    internal enum ImportDocumentOptions { None, Override }
    internal enum DocumentResultState { Success }
    internal sealed class SdResult
    {
        public DocumentResultState State => DocumentResultState.Success;
        public string[] Messages => Array.Empty<string>();
        public PlcBlock[] ImportedPlcBlocks { get; set; } = Array.Empty<PlcBlock>();
    }
}

namespace Siemens.Engineering.SW.Blocks
{
    internal sealed class FC : PlcBlock { }

    internal partial class PlcBlock
    {
        public string Name { get; set; } = "";
        public PlcBlockGroup? Parent { get; set; }
        public SdResult ExportAsDocuments(DirectoryInfo directory, string name) => throw new NotSupportedException();
    }

    internal sealed partial class SdBlockComposition
    {
        public int Imports { get; private set; }
        public Action<PlcBlock>? OnImport;
        public SdResult ImportFromDocuments(DirectoryInfo directory, string name, ImportDocumentOptions option)
        {
            Imports++;
            // Openness must be able to open both inputs exclusively at this call boundary.
            using var declaration = File.Open(Path.Combine(directory.FullName, name + ".s7dcl"), FileMode.Open, FileAccess.Read, FileShare.None);
            string resource = Path.Combine(directory.FullName, name + ".s7res");
            using var translations = File.Exists(resource) ? File.Open(resource, FileMode.Open, FileAccess.Read, FileShare.None) : null;
            var block = Find(name);
            if (block == null)
            {
                block = new GlobalDB { Name = name, ProgrammingLanguage = "DB" };
                Add(name, block);
            }
            OnImport?.Invoke(block);
            return new SdResult { ImportedPlcBlocks = new[] { block } };
        }
    }
}

namespace TiaMcpServer.ModelContextProtocol
{
    internal static class InvocationJournal
    {
        internal static T Native<T>(string name, Func<T> call) => call();
    }
}

namespace TiaMcp.Adapters.Native.Plc
{
    internal sealed partial class ImportSession
    {
        internal PlcBlockGroup Root { get; } = new();
        internal IDisposable? AcquireHmiEditAccess() => null;
        internal ImportSession ExactPlcForEngineering(string path, bool writing) => this;
        internal object? OptionalUnit(ImportSession plc, string name, string kind) => null;
        internal PlcBlockGroup BlockRootOf(ImportSession plc, object? unit) => Root;
    }

    internal sealed partial class PlcB5Adapter
    {
        private readonly SdFixtureEngine engine = new();
        private sealed class SdFixtureEngine
        {
            internal Func<string, object>? PlcB5DocumentPreflight { get; set; } = SdBlockDeclaration.Scan;
        }
        private static void ValidateUnitKind(string kind) { if (kind != "unit") throw new NotSupportedException(); }
        private static IEnumerable<(string Name, string Kind, object? Unit)> PlcScopes(ImportSession plc) => throw new NotSupportedException();
        private static IEnumerable<(string Path, object Value)> ScopedObjects(PlcBlockGroup group, string collection) =>
            group.Blocks.Select(block => (block.Name, (object)block));
        private static void Page(object[] items, int offset, int limit, Dictionary<string, object?> meta) => throw new NotSupportedException();
        private static PlcB5Array Names(PlcBlock[] blocks) => new(blocks.Select(block => (object)block.Name).ToArray());
        private static PlcB5Object DocumentExportRow(SdResult result) => throw new NotSupportedException();
        private static PlcB5Array DocumentMessages(string[] messages) => new(messages);
        private static PlcB5Object DocumentImportRow(SdResult result, PlcB5Array names) => new() { ["imported"] = names, ["messages"] = result.Messages };
        private static bool ExactNamesPresent(string[] imported, IEnumerable<string> actual) => imported.Length > 0 && imported.All(actual.Contains);
        private static PlcB5Reply RunTool(string tool, Func<Dictionary<string, object?>, string> action)
        {
            var meta = LegacyResponseMetadata.Step(TiaMcpServer.ModelContextProtocol.ResponseClock.Now, tool);
            try
            {
                string message = action(meta);
                LegacyResponseMetadata.Complete(meta);
                return new PlcB5Reply { Meta = meta, Message = message };
            }
            catch (AdapterPreconditionException error)
            {
                meta["condition"] = error.Condition;
                meta["parameter"] = error.ParamName;
                return new PlcB5Reply { Meta = meta, Message = error.Message };
            }
        }
    }
}

namespace TiaMcp.Tests.NativeWrites
{
    internal static class SdScopedImport
    {
        private static void ChangeBytes(string path, string oldValue, string newValue)
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            File.WriteAllText(path, File.ReadAllText(path).Replace(oldValue, newValue));
            File.SetLastWriteTimeUtc(path, stamp);
        }

        internal static object Run(string scenario, string directory)
        {
            PlcBatchImportRecovery.Root = directory;
            var session = new ImportSession(null);
            var group = session.Root;
            bool startup = scenario == "sd-scoped-startup";
            bool lad = scenario == "sd-scoped-lad";
            string name = startup ? "MCP_RC_OBStart" : lad ? "MCP_RC_FC" : "Target";
            string code = Path.Combine(directory, name + ".s7dcl");
            string resource = Path.ChangeExtension(code, ".s7res");
            bool hasNumber = scenario is "sd-scoped-new-number" or "sd-scoped-number" or "sd-scoped-wrong-number" or "sd-scoped-unrequested-auto";
            string header = hasNumber ? "{ S7_BlockNumber := \"150\" }\n" : "";
            bool instance = scenario.StartsWith("sd-scoped-instance", StringComparison.Ordinal);
            string body = instance ? "\"MotorFB\"" : "VAR\n    Type : Int;\nEND_VAR";
            string source = startup ? $"ORGANIZATION_BLOCK \"{name}\"\nEND_ORGANIZATION_BLOCK\n"
                : lad ? $"FUNCTION \"{name}\" : Void\nEND_FUNCTION\n"
                : header + $"DATA_BLOCK \"{name}\"\n{body}\nEND_DATA_BLOCK\n";
            File.WriteAllText(code, source);
            const string translations = "MultiLingualTexts:\n  - id: MLC_Value\n    en-US: Value\n";
            if (scenario != "sd-scoped-add-resource") File.WriteAllText(resource, translations);

            PlcBlock target = startup ? new OB { Number = 151, SecondaryType = "Startup" }
                : lad ? new FC { Number = 9812, ProgrammingLanguage = "LAD" }
                : new GlobalDB { Number = 42, ProgrammingLanguage = "DB",
                    AutoNumber = scenario is not ("sd-scoped-auto-false" or "sd-scoped-unrequested-auto") };
            target.Name = name;
            target.Parent = group;
            if (scenario != "sd-scoped-new-number") group.Blocks.Add(name, target);
            switch (scenario)
            {
                case "sd-scoped-lad":
                    target.Attributes["IsConsistent"] = true;
                    target.Attributes["LibraryConformanceStatus"] = "该对象为库兼容。";
                    group.Blocks.OnImport = block => {
                        block.Attributes["IsConsistent"] = false;
                        block.Attributes["LibraryConformanceStatus"] = "无";
                    };
                    break;
                case "sd-scoped-change-code": target.AfterExport = () => ChangeBytes(code, "Int", "int"); break;
                case "sd-scoped-change-resource": target.AfterExport = () => ChangeBytes(resource, "Value", "VALUE"); break;
                case "sd-scoped-add-resource": target.AfterExport = () => File.WriteAllText(resource, translations); break;
                case "sd-scoped-remove-resource": target.AfterExport = () => File.Delete(resource); break;
                case "sd-scoped-optional":
                    target.Attributes["VendorValue"] = new object();
                    group.Blocks.OnImport = block => block.Attributes["VendorValue"] = new object();
                    break;
                case "sd-scoped-required": target.Attributes["MemoryReserve"] = new object(); break;
                case "sd-scoped-auto-true": case "sd-scoped-auto-false":
                    group.Blocks.OnImport = block => block.AutoNumber = !block.AutoNumber;
                    break;
                case "sd-scoped-number": case "sd-scoped-unrequested-auto":
                    group.Blocks.OnImport = block => { block.Number = 150; block.AutoNumber = !block.AutoNumber; };
                    break;
            }
            string option = scenario is "sd-scoped-new-number" or "sd-scoped-instance-none" ? "None" : "Override";
            var reply = new PlcB5Adapter(session).ManagePlcBlockDocuments("PLC", "import", name,
                directoryPath: directory, importOption: option, dryRun: false);
            return new { reply, calls = group.Blocks.Imports, target.Exports, target.Setters };
        }
    }
}
