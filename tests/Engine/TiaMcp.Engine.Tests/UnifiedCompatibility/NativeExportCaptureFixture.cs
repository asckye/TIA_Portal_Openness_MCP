using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
namespace TiaMcpServer.Siemens
{
    internal sealed class NativeExportCapture
    {
        private readonly object state;
        private NativeExportCapture(object state) => this.state = state;
        internal static NativeExportCapture Read(object target, string scope, bool libraryVersion, DirectoryInfo directory)
            => new NativeExportCapture(UnifiedPrimitiveCompatibility.Call<object>("NativeExportCapture", "Read", target, scope, libraryVersion, directory));
        internal JsonObject Status() => UnifiedPrimitiveCompatibility.CallState<JsonObject>(state, "NativeExportCapture", "Status");
        internal List<JsonObject> Rows => UnifiedPrimitiveCompatibility.Field<List<JsonObject>>(state, "NativeExportCapture", "Rows");
        internal List<string> Paths => UnifiedPrimitiveCompatibility.Field<List<string>>(state, "NativeExportCapture", "Paths");
        internal string OperationId => UnifiedPrimitiveCompatibility.Field<string>(state, "NativeExportCapture", "OperationId");
        internal bool Script => UnifiedPrimitiveCompatibility.Field<bool>(state, "NativeExportCapture", "Script");
        internal bool ApiCallSuccess => UnifiedPrimitiveCompatibility.Field<bool>(state, "NativeExportCapture", "ApiCallSuccess");
        internal bool NativeSuccess => UnifiedPrimitiveCompatibility.Field<bool>(state, "NativeExportCapture", "NativeSuccess");
        internal bool InspectionComplete => UnifiedPrimitiveCompatibility.Field<bool>(state, "NativeExportCapture", "InspectionComplete");
        internal bool ConnectionUnavailable => UnifiedPrimitiveCompatibility.Field<bool>(state, "NativeExportCapture", "ConnectionUnavailable");
        internal bool RemoteInspectionStopped => UnifiedPrimitiveCompatibility.Field<bool>(state, "NativeExportCapture", "RemoteInspectionStopped");
        internal bool ExportAttempted => UnifiedPrimitiveCompatibility.Field<bool>(state, "NativeExportCapture", "ExportAttempted");
        internal bool LibraryXml => UnifiedPrimitiveCompatibility.Field<bool>(state, "NativeExportCapture", "LibraryXml");
        internal string Method => UnifiedPrimitiveCompatibility.Field<string>(state, "NativeExportCapture", "Method");
        internal string State => UnifiedPrimitiveCompatibility.Field<string>(state, "NativeExportCapture", "State");
        internal string DiagnosticsStatus => UnifiedPrimitiveCompatibility.Field<string>(state, "NativeExportCapture", "DiagnosticsStatus");
        internal string Phase => UnifiedPrimitiveCompatibility.Field<string>(state, "NativeExportCapture", "Phase");
        internal string? Format => UnifiedPrimitiveCompatibility.Field<string?>(state, "NativeExportCapture", "Format");
        internal string? FailurePhase => UnifiedPrimitiveCompatibility.Field<string?>(state, "NativeExportCapture", "FailurePhase");
    }
}
