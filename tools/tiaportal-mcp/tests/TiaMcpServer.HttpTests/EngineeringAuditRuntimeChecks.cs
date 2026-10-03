using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security;

// Exercises actual compiled guards and native result traversal with local failure injection, never a TIA process.
internal static class EngineeringAuditRuntimeChecks
{
    internal static void Run(Assembly server, Action<bool, string> check)
    {
        var portalType = EngineSurface.For(server);
        bool Refused(Action call)
        {
            try { call(); return false; }
            catch (TargetInvocationException ex) { return ex.InnerException?.Message.Contains("cannot be accessed through reflection") == true; }
        }
        foreach (string suffix in new[] { "CrossReferenceService", "Siemens.Engineering.CrossReference.CrossReferenceService", "Service" })
        {
            check(Refused(() => EngineSurface.InvokeUninitialized(portalType.Method("InvokeService"), new object?[] { "Block", "does-not-exist", suffix, "GetCrossReferences", null, "", true })), "actual InvokeService rejects " + suffix + " before resolving an invalid native target");
            check(Refused(() => EngineSurface.InvokeUninitialized(portalType.Method("DescribeService"), new object?[] { "Block", "does-not-exist", suffix, "", 200 })), "actual DescribeService rejects " + suffix + " before native acquisition");
        }
        check(Refused(() => EngineSurface.InvokeUninitialized(portalType.Method("InvokeObject"), new object?[] { "Block", "does-not-exist", "GetCrossReferences", null, "", true })), "actual InvokeObject cannot bypass cross-reference policy with allowWrite");
        var query = portalType.Method("GetCrossReferences", new[] { typeof(string), typeof(string), typeof(string), typeof(string),
            typeof(string).MakeByRefType(), typeof(bool).MakeByRefType(), typeof(string), typeof(string) });
        string? previous = Environment.GetEnvironmentVariable("TIA_MCP_ENABLE_NATIVE_PLC_CROSS_REFERENCES");
        try
        {
            Environment.SetEnvironmentVariable("TIA_MCP_ENABLE_NATIVE_PLC_CROSS_REFERENCES", null);
            object?[] args = { "invalid", "invalid", "Tag", "AllObjects", null, false, "", "unit" };
            check(EngineSurface.InvokeUninitialized(query, args) == null && args[4]!.ToString()!.Contains("No native query was made") && (bool)args[5]! == false,
                "disabled tag query refuses before inspecting a project and never reports zero references");
        }
        finally { Environment.SetEnvironmentVariable("TIA_MCP_ENABLE_NATIVE_PLC_CROSS_REFERENCES", previous); }

        var api = portalType.Method("BuildCrossReferenceEntry", BindingFlags.NonPublic | BindingFlags.Static)!.GetParameters()[0].ParameterType.Assembly;
        var read = server.GetType("TiaMcpServer.Siemens.CrossReferenceTreeReader", true)!.GetMethod("Read", BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(typeof(object), typeof(object), typeof(object), typeof(object));
        Func<object, System.Collections.Generic.IEnumerable<object>> noRows = _ => Array.Empty<object>();
        Func<object, object, object, object> entry = (s, r, l) => new object();
        var empty = (IList)read.Invoke(null, new object[] { Array.Empty<object>(), noRows, noRows, noRows, entry })!;
        check(empty.Count == 0, "complete native-adapter traversal can report zero");
        bool incomplete = false; int collected = 0;
        Func<object, System.Collections.Generic.IEnumerable<object>> oneReference = _ => new object[] { new object() };
        Func<object, System.Collections.Generic.IEnumerable<object>> failedChildren = _ => throw new InvalidOperationException("Injected child-read failure");
        Func<object, object, object, object> countedEntry = (s, r, l) => { collected++; return new object(); };
        try { read.Invoke(null, new object[] { new object[] { new object() }, oneReference, noRows, failedChildren, countedEntry }); }
        catch (TargetInvocationException ex) { incomplete = ex.GetBaseException().Message.Contains("Injected child-read failure"); }
        check(incomplete && collected == 1, "actual native-adapter walker throws after collecting a row instead of returning successful partial results");

        var core = api.GetType("Siemens.Engineering.NonRecoverableException")?.Assembly;
        if (core == null) core = Assembly.Load("Siemens.Engineering.Base");
        var fatal = (Exception)FormatterServices.GetUninitializedObject(core.GetType("Siemens.Engineering.NonRecoverableException", true)!);
        var recoverable = portalType.Method("RecoverableAuditError", BindingFlags.NonPublic | BindingFlags.Static)!;
        check(!(bool)recoverable.Invoke(null, new object[] { new TargetInvocationException(fatal) })!, "wrapped native NonRecoverableException is never swallowed by the audit reader");

        var mcp = EngineSurface.For(server);
        foreach (var name in new[] { "ReadPlcBlockScopes", "ManagePlcBlockDocuments", "ReadOpennessCompatibility", "InspectSimaticSdCompatibility", "ReadNativeInvocationLog",
            "ManageSinumerikArchive", "ImportSinumerikAlarmTexts", "ManageSinumerikSafetyMode", "InitializeSimotionScripting", "ExportScadaData" })
            check(mcp.Tool(name) != null && mcp.Tool(name)!.GetCustomAttributesData().Any(a => a.AttributeType.Name == "McpServerToolAttribute"), "new tool attributed in actual EXE: " + name);

        bool v20 = api.GetName().Version!.Major == 20;
        if (v20)
        {
            Type T(string n) => api.GetType(n, true)!;
            const string archiveName = "Siemens.Engineering.HW.Utilities.SinumerikArchiveProvider";
            var archive = T(archiveName); var device = T("Siemens.Engineering.HW.DeviceItem"); var file = typeof(FileInfo); var secure = typeof(SecureString);
            var mode = T("Siemens.Engineering.MC.Sinumerik.SinumerikArchivationMode");
            check(archive.GetMethod("Archive", new[] { device, file, mode, typeof(string), typeof(string), secure }) != null, "V20 SINUMERIK archive password/comment/author signature");
            check(archive.GetMethod("CreateFAddressAssignmentArchive", new[] { device, device, file, typeof(string), typeof(string), secure }) != null, "V20 F-address archive signature");
            check(archive.GetMethod("Retrieve", new[] { file, secure })!.ReturnType.Name == "Device", "V20 SINUMERIK retrieval returns Device");
            check(T("Siemens.Engineering.MC.Sinumerik.AlarmTextImporter").GetMethod("ImportAlarmTexts")!.GetParameters()[0].ParameterType == typeof(System.Collections.Generic.IEnumerable<FileInfo>), "V20 alarm importer accepts typed file sequence");
            check(T("Siemens.Engineering.MC.Sinumerik.SafetyModeProvider").GetMethod("SetSafetyMode") != null, "V20 safety mode setter exists");
            check(T("Siemens.Engineering.Simotion.SimotionProvider").GetMethod("Initialize")!.ReturnType == typeof(string), "V20 SIMOTION initialization returns a string, never executable code in this tool");
            check(T("Siemens.Engineering.SCADAExporter.ScadaExportProvider").GetMethod("Export", new[] { file }) != null, "V20 SCADA export signature");
        }
        else
        {
            var result = EngineSurface.InvokeUninitialized(portalType.Method("InitializeSimotionScripting"), new object[] { true })!;
            check(result.GetType().GetProperty("Meta")!.GetValue(result)!.ToString()!.Contains("absent from the supplied V21 SDK"), "V21 option refusal precedes project resolution");
        }
    }
}
