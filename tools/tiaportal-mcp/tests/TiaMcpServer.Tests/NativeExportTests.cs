using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    internal static class NativeExportTests
    {
        public class LibraryType
        {
            public string[] Formats = new[] { "DomainSpecificNative" };
            public IEnumerable<string> GetSupportedExportFormats() => Formats;
        }
        public enum ExportOptions { None, OnlyLibraryVersionInfoFile }
        public sealed class DiagnosticMessage { public string MessageText = ""; public string Message => MessageText; }
        public sealed class Result
        {
            public string TransferResultState { get; set; } = "Success";
            public IEnumerable<FileInfo> ExportedDocuments { get; set; } = Array.Empty<FileInfo>();
            public List<DiagnosticMessage> Messages { get; } = new List<DiagnosticMessage>();
        }
        public sealed class Version
        {
            public object TypeObject { get; set; } = new LibraryType();
            public int Calls;
            public string? Format, Output;
            public Func<DirectoryInfo, Result>? Behavior;
            public Result ExportAsDocuments(DirectoryInfo directory, string name, string format, ExportOptions options)
            {
                if (name != "Type" || options != ExportOptions.None) throw new Exception("Unexpected export options");
                Calls++; Format = format; Output = directory.FullName;
                return Behavior?.Invoke(directory) ?? new Result { ExportedDocuments = LazyFiles(directory) };
            }
        }
        private static IEnumerable<FileInfo> LazyFiles(DirectoryInfo directory)
        {
            var nested = directory.CreateSubdirectory("nested");
            var xml = new FileInfo(Path.Combine(nested.FullName, "Type.xml"));
            File.WriteAllText(xml.FullName, "<Faceplate><Interface><Speed Type='Int'/></Interface><Object Binding='Interface.Speed'/></Faceplate>");
            yield return xml;
        }
        internal static void Run(Action<bool, string> check)
        {
            List<JsonObject> Read(Version version) => UnifiedNativeRead.Export(version, "/Selected/Versions/5.0.0", true).ToList();
            JsonObject Summary(List<JsonObject> rows) => rows.Single(r => r["kind"]?.ToString() == "nativeExportSummary");
            var version = new Version(); var rows = Read(version);
            check(version.Calls == 1 && version.Format == "DomainSpecificNative" && rows.Single(r => r["kind"]?.ToString() == "nativeExportPlan")["selectedFormat"]!.ToString() == version.Format, "native export uses exactly the format advertised by the selected type");
            check(rows.Count(r => r["kind"]?.ToString() == "nativeFile") == 1 && rows.Any(r => r["kind"]?.ToString() == "nativeValue" && r["value"]?.ToString() == "Interface.Speed") && Summary(rows)["dataComplete"]!.GetValue<bool>(), "lazy returned file list is consumed before recursive scan; nested native bindings are read once");
            check(version.Output != null && !Directory.Exists(version.Output), "native export temporary directory is removed after collection completes");
            var warning = new Version { Behavior = d => { var r = new Result { TransferResultState = "Warning" }; r.Messages.Add(new DiagnosticMessage { MessageText = "Selected faceplate version cannot be exported: domain reason" }); return r; } };
            var warnings = Read(warning);
            check(warnings.Any(r => r["value"]?.ToString() == "Selected faceplate version cannot be exported: domain reason") && warnings.Any(r => r["code"]?.ToString() == "NativeExportEmpty") && Summary(warnings)["apiCallSuccess"]!.GetValue<bool>() && !Summary(warnings)["dataComplete"]!.GetValue<bool>(), "Warning and empty export preserve Siemens diagnostics and never claim complete data");
            var partial = Read(new Version { Behavior = d => new Result { TransferResultState = "Warning", ExportedDocuments = LazyFiles(d) } });
            check(partial.Any(r => r["kind"]?.ToString() == "nativeFile") && !Summary(partial)["dataComplete"]!.GetValue<bool>(), "warning with files retains usable evidence without promoting it to complete export");
            var empty = Read(new Version { Behavior = _ => new Result() });
            check(Summary(empty)["apiCallSuccess"]!.GetValue<bool>() && !Summary(empty)["dataComplete"]!.GetValue<bool>(), "Success state with zero native files is incomplete");
            var unsupported = new Version { TypeObject = new LibraryType { Formats = Array.Empty<string>() } }; var noFormat = Read(unsupported);
            check(unsupported.Calls == 0 && noFormat.Any(r => r["code"]?.ToString() == "Unsupported") && !Summary(noFormat)["apiCallSuccess"]!.GetValue<bool>(), "no advertised format returns explicit unsupported without guessing or mutating a type");
            var script = new Version { TypeObject = new global::Siemens.Engineering.HmiUnified.Library.ScriptModuleType(), Behavior = d => new Result { ExportedDocuments = ScriptFiles(d) } };
            var scripts = Read(script);
            check(script.Format == "ScriptNative" && scripts.Any(r => r["bodyReadSuccess"]?.GetValue<bool>() == true) && Summary(scripts)["dataComplete"]!.GetValue<bool>(), "library ScriptModuleType chooses its supported native format and returns actual JS body");
            var noBody = Read(new Version { TypeObject = new global::Siemens.Engineering.HmiUnified.Library.ScriptModuleType() });
            check(noBody.Any(r => r["code"]?.ToString() == "ScriptBodyNotExported") && !Summary(noBody)["dataComplete"]!.GetValue<bool>(), "library script without JS cannot pass body acceptance");
            var missing = Read(new Version { Behavior = d => new Result { ExportedDocuments = new[] { new FileInfo(Path.Combine(d.FullName, "missing.xml")) } } });
            check(missing.Any(r => r["code"]?.ToString() == "NativeFileLocationRejected") && !Summary(missing)["dataComplete"]!.GetValue<bool>(), "manifest entry for a missing file is an explicit gap");
            var outside = Read(new Version { Behavior = d => new Result { ExportedDocuments = new[] { new FileInfo(Path.Combine(d.Parent!.FullName, "not-exported.xml")) } } });
            check(outside.Any(r => r["code"]?.ToString() == "NativeFileLocationRejected") && !outside.Any(r => r["kind"]?.ToString() == "nativeFile"), "reported paths outside this export are diagnosed without reading arbitrary files");
            var threw = Read(new Version { Behavior = _ => throw new InvalidOperationException("domain exception") });
            check(threw.Any(r => r["reason"]?.ToString().Contains("domain exception") == true) && !Summary(threw)["apiCallSuccess"]!.GetValue<bool>(), "native invocation exception retains its original cause and failed call status");
            var manyMessages = Read(new Version { Behavior = d => { var r = new Result { ExportedDocuments = LazyFiles(d) }; for (int i = 0; i < 1001; i++) r.Messages.Add(new DiagnosticMessage { MessageText = "message" }); return r; } });
            check(manyMessages.Any(r => r["code"]?.ToString() == "NativeDiagnosticLimit") && !Summary(manyMessages)["dataComplete"]!.GetValue<bool>(), "diagnostic capacity is bounded and never silently reports complete");
        }
        private static IEnumerable<FileInfo> ScriptFiles(DirectoryInfo directory)
        {
            var js = new FileInfo(Path.Combine(directory.FullName, "Module.js")); File.WriteAllText(js.FullName, "export function helper(name) { return Tags(name).Read(); }");
            yield return js;
            var yaml = new FileInfo(Path.Combine(directory.FullName, "Module.yml")); File.WriteAllText(yaml.FullName, "ScriptModule:\n  Name: helper\n");
            yield return yaml;
        }
    }
}

// Offline double with the official CLR type name; no Siemens assembly or TIA
// process is loaded by these regression tests.
namespace Siemens.Engineering.HmiUnified.Library
{
    internal sealed class ScriptModuleType : TiaMcpServer.Tests.NativeExportTests.LibraryType
    { public ScriptModuleType() { Formats = new[] { "ScriptNative" }; } }
}
