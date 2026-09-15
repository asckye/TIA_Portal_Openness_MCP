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
            private string state = "Success";
            private IEnumerable<FileInfo> files = Array.Empty<FileInfo>();
            public Exception? StateError, FilesError, MessagesError;
            public int StateReads, FileReads, MessageReads;
            public string TransferResultState { get { StateReads++; if (StateError != null) throw StateError; return state; } set => state = value; }
            public IEnumerable<FileInfo> ExportedDocuments { get { FileReads++; if (FilesError != null) throw FilesError; return files; } set => files = value; }
            public readonly List<DiagnosticMessage> MessageItems = new List<DiagnosticMessage>();
            public object? MessageCollection;
            public object Messages { get { MessageReads++; if (MessagesError != null) throw MessagesError; return MessageCollection ?? MessageItems; } }
        }
        public sealed class BrokenMessageCount { public int Count => throw new InvalidOperationException("Unexpected exception - no exception message available."); }
        public class Version
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
        public enum XmlExportOptions { None, WithReadOnly }
        public sealed class XmlVersion : Version
        {
            public int XmlCalls;
            public Exception? Error;
            public bool Empty;
            public XmlVersion() { TypeObject = new LibraryType { Formats = Array.Empty<string>() }; }
            public void Export(FileInfo file, XmlExportOptions options)
            {
                XmlCalls++; Output = file.DirectoryName;
                if (options != XmlExportOptions.WithReadOnly) throw new Exception("Readonly metadata must be included");
                if (Error != null) throw Error;
                if (!Empty) File.WriteAllText(file.FullName, "<Document><LibraryTypeVersion><AttributeList><VersionNumber>5.0.0</VersionNumber></AttributeList></LibraryTypeVersion></Document>");
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
            var warning = new Version { Behavior = d => { var r = new Result { TransferResultState = "Warning" }; r.MessageItems.Add(new DiagnosticMessage { MessageText = "Selected faceplate version cannot be exported: domain reason" }); return r; } };
            var warnings = Read(warning);
            check(warnings.Any(r => r["value"]?.ToString() == "Selected faceplate version cannot be exported: domain reason") && warnings.Any(r => r["code"]?.ToString() == "NativeExportEmpty") && Summary(warnings)["apiCallSuccess"]!.GetValue<bool>() && !Summary(warnings)["dataComplete"]!.GetValue<bool>(), "Warning and empty export preserve Siemens diagnostics and never claim complete data");
            var partial = Read(new Version { Behavior = d => new Result { TransferResultState = "Warning", ExportedDocuments = LazyFiles(d) } });
            check(partial.Any(r => r["kind"]?.ToString() == "nativeFile") && !Summary(partial)["dataComplete"]!.GetValue<bool>(), "warning with files retains usable evidence without promoting it to complete export");
            var empty = Read(new Version { Behavior = _ => new Result() });
            check(Summary(empty)["apiCallSuccess"]!.GetValue<bool>() && !Summary(empty)["dataComplete"]!.GetValue<bool>(), "Success state with zero native files is incomplete");
            var unsupported = new Version { TypeObject = new LibraryType { Formats = Array.Empty<string>() } }; var noFormat = Read(unsupported);
            check(unsupported.Calls == 0 && noFormat.Any(r => r["code"]?.ToString() == "Unsupported") && !Summary(noFormat)["apiCallSuccess"]!.GetValue<bool>(), "no advertised format returns explicit unsupported without guessing or mutating a type");
            check(!Summary(noFormat)["exportAttempted"]!.GetValue<bool>() && !noFormat.Any(r => r["code"]?.ToString() == "NativeExportEmpty")
                && noFormat.Single(r => r["kind"]?.ToString() == "nativeExportStatus")["status"]!.ToString() == "notAttempted", "unavailable export is not attempted, not a zero-content result");
            var xmlVersion = new XmlVersion(); var xmlRows = Read(xmlVersion); var xmlSummary = Summary(xmlRows);
            check(xmlVersion.XmlCalls == 1 && xmlVersion.Calls == 0 && xmlRows.Any(r => r["kind"]?.ToString() == "nativeFile"), "empty document formats select the separate official XML action once");
            check(xmlSummary["exportAttempted"]!.GetValue<bool>() && xmlSummary["nativeFilesComplete"]!.GetValue<bool>()
                && !xmlSummary["dataComplete"]!.GetValue<bool>() && xmlSummary["internalObjectCount"] == null
                && xmlRows.Any(r => r["code"]?.ToString() == "LibraryXmlContentUnverified"), "metadata-only XML retains evidence without fabricating internal objects or complete bindings");
            check(xmlRows.Any(r => r["kind"]?.ToString() == "nativeValue" && r["value"]?.ToString() == "5.0.0")
                && !Directory.Exists(xmlVersion.Output), "XML fallback parses returned evidence and cleans only its own directory");
            var xmlEmpty = new XmlVersion { Empty = true }; var xmlEmptyRows = Read(xmlEmpty);
            check(xmlEmptyRows.Any(r => r["code"]?.ToString() == "NativeExportEmpty") && !Summary(xmlEmptyRows)["nativeFilesComplete"]!.GetValue<bool>(), "void XML action returning without a file is an explicit export gap");
            var xmlFailed = new XmlVersion { Error = new ObjectDisposedException("version") }; var xmlFailedRows = Read(xmlFailed);
            check(xmlFailed.XmlCalls == 1 && xmlFailed.Calls == 0 && Summary(xmlFailedRows)["connectionUnavailable"]!.GetValue<bool>()
                && Summary(xmlFailedRows)["failurePhase"]!.ToString() == "invokeLibraryXmlExport", "XML IPC failure stops after one exact-version attempt and preserves its phase");
            var documentsFailed = new XmlVersion { TypeObject = new LibraryType(), Behavior = _ => throw new InvalidOperationException("document failure") }; Read(documentsFailed);
            check(documentsFailed.Calls == 1 && documentsFailed.XmlCalls == 0, "a failed advertised document call never triggers a second XML export");
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
            var manyMessages = Read(new Version { Behavior = d => { var r = new Result { TransferResultState = "Warning", ExportedDocuments = LazyFiles(d) }; for (int i = 0; i < 1001; i++) r.MessageItems.Add(new DiagnosticMessage { MessageText = "message" }); return r; } });
            check(manyMessages.Any(r => r["code"]?.ToString() == "NativeDiagnosticLimit") && !Summary(manyMessages)["dataComplete"]!.GetValue<bool>(), "diagnostic capacity is bounded and never silently reports complete");
            var successResult = new Result { MessagesError = new InvalidOperationException("Success diagnostics must not be accessed") };
            var successRows = Read(new Version { Behavior = d => { successResult.ExportedDocuments = LazyFiles(d); return successResult; } });
            check(successResult.MessageReads == 0 && Summary(successRows)["dataComplete"]!.GetValue<bool>() && successRows.Single(r => r["kind"]?.ToString() == "nativeExportStatus")["diagnosticsStatus"]!.ToString() == "notRequiredOnSuccess", "native Success never accesses the Messages getter, following the official success path");
            var brokenResult = new Result { FilesError = new ObjectDisposedException("native result") };
            var brokenRows = Read(new Version { Behavior = d => { LazyFiles(d).ToList(); return brokenResult; } });
            var status = brokenRows.Single(r => r["kind"]?.ToString() == "nativeExportStatus");
            check(status["nativeState"]!.ToString() == "Success" && status["nativeStateSuccess"]!.GetValue<bool>() && !status["resultInspectionComplete"]!.GetValue<bool>() && status["failurePhase"]!.ToString() == "snapshotExportedDocuments" && status["reason"]!.ToString().Contains("result inspection failed"), "raw native Success and result inspection failure have separate explicit meanings");
            check(brokenResult.MessageReads == 0 && brokenRows.Any(r => r["kind"]?.ToString() == "nativeFile") && !Summary(brokenRows)["dataComplete"]!.GetValue<bool>() && !brokenRows.Any(r => r["code"]?.ToString() == "NativeExportFailed"), "disposed result stops remote inspection but retains local files without relabeling the native invocation");
            var brokenState = new Result { StateError = new ObjectDisposedException("native state") };
            var stateRows = Read(new Version { Behavior = _ => brokenState });
            check(brokenState.FileReads == 0 && brokenState.MessageReads == 0 && stateRows.Any(r => r["code"]?.ToString() == "NativeStatusReadFailed"), "failed native state read stops all further result access");
            var countRows = Read(new Version { Behavior = d => new Result { TransferResultState = "Warning", ExportedDocuments = LazyFiles(d), MessageCollection = new BrokenMessageCount() } });
            var failure = countRows.Single(r => r["code"]?.ToString() == "NativeDiagnosticsReadFailed");
            check(failure["phase"]!.ToString() == "readMessageCount" && failure["exceptions"]!.AsArray().Count >= 1 && failure["exceptions"]!.AsArray().Any(e => e!["type"]!.ToString() == "System.InvalidOperationException" && e["stackTrace"]!.ToString().Contains("BrokenMessageCount")) && countRows.Any(r => r["kind"]?.ToString() == "nativeExportSummary"), "message count exception keeps phase, original type and stack and still reaches inventory and summary");
            ContinuationTests(check);
        }
        private static void ContinuationTests(Action<bool, string> check)
        {
            var root = new global::Siemens.Engineering.HmiUnified.HmiSoftware();
            var portal = new Portal { FixtureRoot = root };
            var first = portal.ReadUnifiedGlobalScript("HMI_1", "Project_A", "Navigation", pageSize: 2).Meta!;
            check(first["records"]!.AsArray().Any(r => r!["kind"]!.ToString() == "nativeExportStatus"), "native result is captured before export file pagination starts");
            string cursor = first["nextCursor"]!.ToString();
            root.Disposed = true;
            check(portal.ReadUnifiedGlobalScript("HMI_1", "Project_A", "Navigation", first["pageCursor"]!.ToString(), 2).Meta!.ToJsonString() == first.ToJsonString(), "last page replays without accessing a disposed project Name");
            var mismatch = portal.ReadUnifiedGlobalScript("HMI_1", "Other_Project", "Navigation", cursor).Meta!;
            check(!mismatch["apiCallSuccess"]!.GetValue<bool>(), "skipping remote preflight never bypasses cursor project and argument validation");
            JsonObject last; int bodies = 0;
            do { last = portal.ReadUnifiedGlobalScript("HMI_1", "Project_A", "Navigation", cursor, 2).Meta!; bodies += last["records"]!.AsArray().Count(r => r!["kind"]?.ToString() == "nativeFile"); cursor = last["nextCursor"]?.ToString() ?? ""; } while (cursor != "");
            check(bodies == 2 && last["dataComplete"]!.GetValue<bool>(), "captured export finishes from local files after project handle disposal without another Openness access");
            check(!portal.ReadUnifiedGlobalScript("HMI_1", "Project_A", "Navigation").Meta!["apiCallSuccess"]!.GetValue<bool>(), "new collection still validates the live project handle");
            portal.FixtureRoot = new global::Siemens.Engineering.HmiUnified.HmiSoftware();
            check(!portal.ReadUnifiedGlobalScript("HMI_1", "Project_A", "Navigation", last["pageCursor"]!.ToString()).Meta!["apiCallSuccess"]!.GetValue<bool>(), "rebind to same named project still invalidates the old collection");
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

namespace Siemens.Engineering.HmiUnified
{
    internal sealed class HmiSoftware
    {
        public object RuntimeSettings { get; set; } = new TiaMcpServer.Tests.RuntimeSettingsTests.Settings();
        public List<TiaMcpServer.Tests.RuntimeSettingsTests.Group> ScreenGroups { get; } = new List<TiaMcpServer.Tests.RuntimeSettingsTests.Group>();
        public List<TiaMcpServer.Tests.GraphicSelectionTests.Screen> Screens { get; } = new List<TiaMcpServer.Tests.GraphicSelectionTests.Screen>();
        public bool Disposed;
        public string Name => Disposed ? throw new ObjectDisposedException("project") : "Project_A";
        public List<TiaMcpServer.Tests.MigrationReadTests.Module> Scripts { get; } = new List<TiaMcpServer.Tests.MigrationReadTests.Module> { new TiaMcpServer.Tests.MigrationReadTests.Module() };
    }
}

// Offline double with the official CLR type name; no Siemens assembly or TIA
// process is loaded by these regression tests.
namespace Siemens.Engineering.HmiUnified.Library
{
    internal sealed class ScriptModuleType : TiaMcpServer.Tests.NativeExportTests.LibraryType
    { public ScriptModuleType() { Formats = new[] { "ScriptNative" }; } }
}
