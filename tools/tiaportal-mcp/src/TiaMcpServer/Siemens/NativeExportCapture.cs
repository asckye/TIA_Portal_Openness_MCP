using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    // Consume the native result before returning any rows to the paging iterator.
    // In net48 FileInfo can be a remoting proxy: retain path strings, never the
    // returned FileInfo, result or diagnostic collection across a page boundary.
    internal sealed class NativeExportCapture
    {
        internal readonly List<JsonObject> Rows = new List<JsonObject>();
        internal readonly List<string> Paths = new List<string>();
        internal readonly string OperationId = Guid.NewGuid().ToString("N");
        internal bool Script, ApiCallSuccess, NativeSuccess, InspectionComplete;
        internal bool ConnectionUnavailable, RemoteInspectionStopped, ExportAttempted, LibraryXml;
        internal string Method = "Export(DirectoryInfo, string)";
        internal string State = "Unknown", DiagnosticsStatus = "notRead", Phase = "prepare";
        internal string? Format, FailurePhase;
        private readonly string scope;
        private static readonly object LogGate = new object();
        private static readonly string DiagnosticLog = Path.Combine(Path.GetTempPath(), "TiaMcpServer.native-export.log");

        private NativeExportCapture(string scope, bool libraryVersion)
        { this.scope = scope; Script = !libraryVersion; }

        private void Begin(string phase)
        { Phase = phase; Log("begin"); }
        private void Log(string outcome, Exception? error = null)
        {
            // Persist even when the console closes; stdout remains MCP-only.
            // No script text or connection key is logged. Logging cannot fail a read.
            var text = string.Format("[native-export] utc={0:O} operation={1} phase={2} outcome={3}{4}",
                DateTime.UtcNow, OperationId, Phase, outcome, error == null ? "" : Environment.NewLine + error);
            try { Console.Error.WriteLine(text); } catch { }
            try { lock (LogGate) { File.AppendAllText(DiagnosticLog, text + Environment.NewLine); } } catch { }
        }

        internal static NativeExportCapture Read(object target, string scope, bool libraryVersion, DirectoryInfo directory)
        {
            var capture = new NativeExportCapture(scope, libraryVersion);
            capture.ReadCore(target, libraryVersion, directory);
            return capture;
        }

        private void ReadCore(object target, bool libraryVersion, DirectoryInfo directory)
        {
            object? result;
            try
            {
                Begin("plan");
                if (!libraryVersion)
                {
                    var method = target.GetType().GetMethod("Export", new[] { typeof(DirectoryInfo), typeof(string) })
                        ?? throw new NotSupportedException("Official Export(DirectoryInfo, string) is unavailable.");
                    Begin("invokeExport");
                    ExportAttempted = true;
                    result = method.Invoke(target, new object[] { directory, "Module" });
                }
                else
                {
                    var type = MigrationRead.Get(target, "TypeObject") ?? throw new NotSupportedException("The selected version has no TypeObject.");
                    Script = type.GetType().FullName == "Siemens.Engineering.HmiUnified.Library.ScriptModuleType";
                    var query = type.GetType().GetMethod("GetSupportedExportFormats", Type.EmptyTypes)
                        ?? throw new NotSupportedException("Official LibraryType.GetSupportedExportFormats() is unavailable; no format was guessed.");
                    var formats = ReadFormats(query.Invoke(type, Array.Empty<object>()));
                    Format = formats.FirstOrDefault();
                    LibraryXml = Format == null && !Script;
                    Method = LibraryXml ? "LibraryTypeVersion.Export(FileInfo, ExportOptions)"
                        : "LibraryTypeVersion.ExportAsDocuments(DirectoryInfo, string, string, LibraryExportOptions)";
                    Rows.Add(new JsonObject { ["path"] = scope, ["kind"] = "nativeExportPlan", ["status"] = "ok", ["operationId"] = OperationId,
                        ["type"] = type.GetType().FullName, ["versionType"] = target.GetType().FullName,
                        ["supportedFormats"] = new JsonArray(formats.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
                        ["selectedFormat"] = Format, ["formatEvidence"] = "LibraryType.GetSupportedExportFormats()",
                        ["method"] = Method, ["selectionReason"] = LibraryXml ? "No document format was advertised; use the separate official XML export action once. This does not establish internal-content support." : "Advertised document format",
                        ["libraryExportOptions"] = LibraryXml ? null : "None", ["exportOptions"] = LibraryXml ? "WithReadOnly" : null,
                        ["baseFileName"] = LibraryXml ? "Type.xml" : "Type", ["outputDirectory"] = directory.FullName,
                        ["timing"] = "Result inspection is completed or stopped before this plan is delivered; this row is not proof that invocation is pending." });
                    if (LibraryXml) { ReadLibraryXml(target, directory); return; }
                    if (Format == null) throw new NotSupportedException("The selected library type advertises no supported native export format. No type was edited or instantiated.");
                    var method = target.GetType().GetMethods().SingleOrDefault(m => m.Name == "ExportAsDocuments" && m.GetParameters().Length == 4
                        && m.GetParameters()[0].ParameterType == typeof(DirectoryInfo) && m.GetParameters()[1].ParameterType == typeof(string)
                        && m.GetParameters()[2].ParameterType == typeof(string) && m.GetParameters()[3].ParameterType.IsEnum)
                        ?? throw new NotSupportedException("Official ExportAsDocuments is unavailable for this version.");
                    Begin("invokeExport");
                    ExportAttempted = true;
                    result = method.Invoke(target, new object[] { directory, "Type", Format, Enum.Parse(method.GetParameters()[3].ParameterType, "None") });
                }
                ApiCallSuccess = true;
                Log("returned");
            }
            catch (Exception ex) { Fail(MigrationRead.Cause(ex) is NotSupportedException ? "Unsupported" : "NativeExportFailed", ex); return; }

            // The official V21 example first reads TransferResultState. Messages
            // are inspected only on a non-success result, not on the success path.
            if (libraryVersion)
            {
                try
                {
                    Begin("readNativeState");
                    if (result == null) throw new InvalidOperationException("Native export returned no ExportTransferResult.");
                    State = MigrationRead.Get(result, "TransferResultState")?.ToString() ?? "Unknown";
                    NativeSuccess = State == "Success";
                    Log(State);
                }
                catch (Exception ex) { Fail("NativeStatusReadFailed", ex); return; }
            }
            else { State = "Returned"; NativeSuccess = true; }

            try
            {
                Begin("snapshotExportedDocuments");
                ReadPaths(libraryVersion ? MigrationRead.Get(result!, "ExportedDocuments") : result);
                Log("snapshotted");
            }
            catch (Exception ex) { Fail("NativeFileListFailed", ex); return; }

            if (libraryVersion && !NativeSuccess)
            {
                try
                {
                    Begin("readMessages");
                    var messages = MigrationRead.Get(result!, "Messages") ?? throw new InvalidOperationException("Export diagnostics are unavailable.");
                    Begin("readMessageCount");
                    int count = MigrationRead.Count(messages);
                    if (count < 0) throw new InvalidOperationException("Export diagnostics returned a negative count.");
                    Rows.Add(new JsonObject { ["path"] = scope + "/ExportResult/Messages", ["kind"] = "collection", ["status"] = "ok", ["expectedCount"] = count });
                    for (int i = 0; i < Math.Min(count, 1000); i++)
                    {
                        Begin("readMessage[" + i + "]");
                        var message = MigrationRead.Get(MigrationRead.At(messages, i), "Message");
                        if (message is not string) throw new NotSupportedException("TransferResultMessage.Message is not a string; no arbitrary graph was traversed.");
                        Rows.Add(MigrationRead.Scalar(scope + "/ExportResult/Messages/[" + i + "]/Message", message));
                    }
                    if (count > 1000) { Begin("readMessagesLimit"); throw new NotSupportedException("Export diagnostics exceed 1000 messages; diagnostic evidence is incomplete."); }
                    DiagnosticsStatus = "read";
                    Log("snapshotted");
                }
                catch (Exception ex) { DiagnosticsStatus = "failed"; Fail(Phase == "readMessagesLimit" ? "NativeDiagnosticLimit" : "NativeDiagnosticsReadFailed", ex); return; }
            }
            else DiagnosticsStatus = libraryVersion ? "notRequiredOnSuccess" : "notApplicable";
            InspectionComplete = true;
            Begin("snapshotComplete"); Log("completed");
        }

        private void ReadLibraryXml(object target, DirectoryInfo directory)
        {
            // This is a different documented API, not a guessed ExportAsDocuments
            // format. Never fall back after an invocation/IPC/result failure.
            var method = target.GetType().GetMethods().SingleOrDefault(m => m.Name == "Export" && m.ReturnType == typeof(void)
                && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(FileInfo)
                && m.GetParameters()[1].ParameterType.IsEnum && Enum.IsDefined(m.GetParameters()[1].ParameterType, "WithReadOnly"))
                ?? throw new NotSupportedException("No document format or public Export(FileInfo, ExportOptions.WithReadOnly) action is available on the selected version. Internal content is unknown, not empty.");
            var file = new FileInfo(Path.Combine(directory.FullName, "Type.xml"));
            var options = Enum.Parse(method.GetParameters()[1].ParameterType, "WithReadOnly");
            Begin("invokeLibraryXmlExport");
            ExportAttempted = true;
            method.Invoke(target, new object[] { file, options });
            ApiCallSuccess = true; NativeSuccess = true; State = "Returned";
            // void Export has no TransferResultState or remote file manifest.
            // Inspect only the local directory; returning normally is not proof
            // that the XML contains faceplate objects, bindings or scripts.
            InspectionComplete = true; DiagnosticsStatus = "notApplicable";
            Begin("snapshotComplete"); Log("returned; local XML content requires verification");
        }

        private void ReadPaths(object? result)
        {
            if (result is not IEnumerable values) throw new NotSupportedException("Native exported document list is not enumerable.");
            foreach (var value in values)
            {
                if (Paths.Count >= 4096) throw new NotSupportedException("Native document list exceeds 4096; explicit gap, not a truncated success.");
                if (value is not FileInfo file) throw new NotSupportedException("ExportedDocuments contains a value other than FileInfo.");
                // FullName itself can perform IPC. Do it once, inside the guarded
                // inspection phase. All later FileInfo instances are locally built.
                Paths.Add(file.FullName);
            }
        }
        private static string[] ReadFormats(object? result)
        {
            if (result is not IEnumerable values) throw new NotSupportedException("Supported export formats are not enumerable.");
            var formats = new List<string>();
            foreach (var value in values)
            {
                if (formats.Count >= 32) throw new NotSupportedException("Supported format list exceeds 32; no unbounded format probing is performed.");
                if (value is not string format || string.IsNullOrWhiteSpace(format)) throw new NotSupportedException("The API returned an invalid export format identifier.");
                formats.Add(format);
            }
            return formats.ToArray();
        }
        private void Fail(string code, Exception error)
        {
            FailurePhase = Phase;
            RemoteInspectionStopped = true;
            for (var e = error; e != null; e = e.InnerException)
            {
                var name = e.GetType().Name;
                if (name == "RemotingException" || name == "EngineeringObjectDisposedException" || name == "ObjectDisposedException"
                    || name.IndexOf("NonRecoverable", StringComparison.Ordinal) >= 0) ConnectionUnavailable = true;
            }
            var row = MigrationRead.Failure(scope + "/ExportResult/" + Phase, code, error);
            row["operationId"] = OperationId; row["phase"] = Phase; row["apiCallSuccess"] = ApiCallSuccess;
            row["exportAttempted"] = ExportAttempted; row["method"] = Method;
            row["nativeState"] = State; row["connectionUnavailable"] = ConnectionUnavailable;
            row["remoteInspectionStopped"] = true;
            row["recovery"] = "No automatic export retry, reconnection, project close or Portal disposal was performed. Check the server log and TIA process before manually retrying.";
            var chain = new JsonArray(); int count = 0;
            for (var e = error; e != null && count++ < 8; e = e.InnerException)
                chain.Add(new JsonObject { ["type"] = e.GetType().FullName, ["hResult"] = "0x" + e.HResult.ToString("X8"),
                    ["message"] = Clip(e.Message, 4096), ["stackTrace"] = Clip(e.StackTrace, 8192),
                    ["textTruncated"] = e.Message.Length > 4096 || (e.StackTrace?.Length ?? 0) > 8192 });
            row["exceptions"] = chain; row["exceptionChainTruncated"] = count > 8;
            Rows.Add(row); Log("failed; further remote inspection stopped", error);
        }
        private static string? Clip(string? value, int max) => value != null && value.Length > max ? value.Substring(0, max) : value;

        internal JsonObject Status()
        {
            bool ok = ApiCallSuccess && NativeSuccess && InspectionComplete;
            return new JsonObject { ["path"] = scope, ["kind"] = "nativeExportStatus", ["status"] = ok ? "ok" : ExportAttempted ? "failed" : "notAttempted",
                ["operationId"] = OperationId, ["apiCallSuccess"] = ApiCallSuccess, ["nativeState"] = State,
                ["exportAttempted"] = ExportAttempted, ["method"] = Method,
                ["diagnosticLog"] = DiagnosticLog,
                ["nativeStateSuccess"] = NativeSuccess, ["resultInspectionComplete"] = InspectionComplete,
                ["diagnosticsStatus"] = DiagnosticsStatus, ["selectedFormat"] = Format, ["failurePhase"] = FailurePhase,
                ["connectionUnavailable"] = ConnectionUnavailable, ["remoteInspectionStopped"] = RemoteInspectionStopped,
                ["dataComplete"] = null, ["completenessScope"] = "native call and result inspection only; see nativeExportSummary for file and body completeness",
                ["reason"] = ok ? null : !ExportAttempted ? "Export was not invoked. Capability selection failed; internal objects and tags remain unknown, not empty."
                    : !ApiCallSuccess ? "Native call did not return successfully."
                    : !InspectionComplete ? "The native call returned, but result inspection failed at " + FailurePhase + ". nativeState is the previously observed raw value; it does not establish complete data."
                    : "Native call and result inspection returned, but the native state was " + State + ". Inspect diagnostics and the file summary." };
        }
    }
}
