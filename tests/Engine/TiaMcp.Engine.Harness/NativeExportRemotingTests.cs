using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Web.Script.Serialization;

// Executes the actual net48 server implementation. The only remote endpoint is
// a local RealProxy test double: no TIA process or engineering project is used.
internal static class NativeExportRemotingTests
{
    public enum Options { None }
    public sealed class TypeObject { public string[] GetSupportedExportFormats() => new[] { "NativeTest" }; }
    public sealed class Result
    {
        public string TransferResultState => "Success";
        public IEnumerable<FileInfo> ExportedDocuments { get; set; } = Array.Empty<FileInfo>();
        public object Messages => throw new Exception("Success path must not touch Messages");
    }
    public sealed class Version
    {
        public TypeObject TypeObject => new TypeObject();
        internal FileProxy? Proxy;
        public bool FailInitially;
        public int Calls;
        public Result ExportAsDocuments(DirectoryInfo directory, string name, string format, Options options)
        {
            Calls++;
            var file = new FileInfo(Path.Combine(directory.FullName, "native.xml"));
            File.WriteAllText(file.FullName, "<Faceplate><Object Binding='Interface.Value'/></Faceplate>");
            Proxy = new FileProxy(file) { Unavailable = FailInitially };
            return new Result { ExportedDocuments = new[] { (FileInfo)Proxy.GetTransparentProxy() } };
        }
    }
    public enum XmlOptions { WithReadOnly }
    public sealed class NoFormats { public string[] GetSupportedExportFormats() => Array.Empty<string>(); }
    public sealed class XmlVersion
    {
        public NoFormats TypeObject => new NoFormats();
        public int Calls;
        public void Export(FileInfo file, XmlOptions options)
        {
            Calls++;
            if (options != XmlOptions.WithReadOnly) throw new Exception("Unexpected XML options");
            File.WriteAllText(file.FullName, "<Document><LibraryTypeVersion VersionNumber='1.0.0'/></Document>");
        }
    }
    internal sealed class FileProxy : RealProxy
    {
        private readonly FileInfo local;
        internal bool Unavailable;
        internal int Calls;
        internal FileProxy(FileInfo file) : base(typeof(FileInfo)) { local = file; }
        public override IMessage Invoke(IMessage message)
        {
            var call = (IMethodCallMessage)message; Calls++;
            if (Unavailable) return new ReturnMessage(new RemotingException("Injected unavailable IPC endpoint"), call);
            try { return new ReturnMessage(call.MethodBase.Invoke(local, call.Args), null, 0, call.LogicalCallContext, call); }
            catch (Exception ex) { return new ReturnMessage(ex, call); }
        }
    }
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
    internal static void Run(Assembly server, Action<bool, string> check)
    {
        var method = server.GetType("TiaMcpServer.Siemens.UnifiedNativeRead", true)!.GetMethod("Export", BindingFlags.NonPublic | BindingFlags.Static)!;
        IEnumerator Start(object version) => ((IEnumerable)method.Invoke(null, new object[] { version, "/Type/Versions/1.0.0", true })!).GetEnumerator();
        Dictionary<string, object> Row(object value) => Json.Deserialize<Dictionary<string, object>>(value.ToString());
        var version = new Version(); var iterator = Start(version);
        var rows = new List<Dictionary<string, object>>();
        try
        {
            check(iterator.MoveNext(), "export returns an initial row"); rows.Add(Row(iterator.Current));
            check(version.Proxy != null && RemotingServices.IsTransparentProxy(version.Proxy.GetTransparentProxy()) && version.Proxy.Calls == 1, "native manifest path is captured exactly once through a real net48 FileInfo proxy");
            version.Proxy!.Unavailable = true;
            while (iterator.MoveNext()) rows.Add(Row(iterator.Current));
            check(version.Proxy.Calls == 1 && version.Calls == 1, "no proxy access or re-export after the first page boundary");
            check(rows.Any(r => (string)r["kind"] == "nativeFile") && (bool)rows.Single(r => (string)r["kind"] == "nativeExportSummary")["dataComplete"], "local native file and binding evidence complete after remote endpoint becomes unavailable");
        }
        finally { (iterator as IDisposable)?.Dispose(); }
        var unavailable = new Version { FailInitially = true }; iterator = Start(unavailable); rows.Clear();
        try { while (iterator.MoveNext()) rows.Add(Row(iterator.Current)); }
        finally { (iterator as IDisposable)?.Dispose(); }
        var failure = rows.Single(r => r.ContainsKey("code") && (string)r["code"] == "NativeFileListFailed");
        check((string)failure["phase"] == "snapshotExportedDocuments" && (bool)failure["connectionUnavailable"], "manifest IPC failure is attributed to path capture and marked unavailable");
        check(unavailable.Proxy!.Calls == 1 && rows.Any(r => (string)r["kind"] == "nativeFile") && !(bool)rows.Single(r => (string)r["kind"] == "nativeExportSummary")["dataComplete"], "IPC failure is never re-accessed in catch; disk evidence is retained with an explicit incomplete summary");
        var xml = new XmlVersion(); iterator = Start(xml); rows.Clear();
        try { while (iterator.MoveNext()) rows.Add(Row(iterator.Current)); }
        finally { (iterator as IDisposable)?.Dispose(); }
        var summary = rows.Single(r => (string)r["kind"] == "nativeExportSummary");
        check(xml.Calls == 1 && rows.Any(r => (string)r["kind"] == "nativeFile") && (bool)summary["nativeFilesComplete"], "actual net48 executable invokes the separate XML action when document formats are empty");
        check(!(bool)summary["dataComplete"] && summary["internalObjectCount"] == null && rows.Any(r => r.ContainsKey("code") && (string)r["code"] == "LibraryXmlContentUnverified"), "actual executable never accepts metadata-only XML as complete faceplate bindings");
    }
}
