using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Remoting;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Web.Script.Serialization;

internal static class HmiSnapshotRemotingTests
{
    public class RemoteItem : MarshalByRefObject
    {
        public string A => "retained";
        public string B => "unavailable";
        public string Z => "must not be called";
    }
    private sealed class ItemProxy : RealProxy
    {
        internal readonly List<string> Calls = new List<string>();
        internal ItemProxy() : base(typeof(RemoteItem)) { }
        public override IMessage Invoke(IMessage message)
        {
            var call = (IMethodCallMessage)message; Calls.Add(call.MethodName);
            if (call.MethodName == "get_B") return new ReturnMessage(new RemotingException("Injected IPC failure"), call);
            try { return new ReturnMessage(call.MethodBase.Invoke(new RemoteItem(), call.Args), null, 0, call.LogicalCallContext, call); }
            catch (Exception ex) { return new ReturnMessage(ex, call); }
        }
    }
    public sealed class Sequence : IEnumerable, IEnumerator, IDisposable
    {
        public object Current { get; set; } = null!;
        public int Moves, Disposals;
        public IEnumerator GetEnumerator() => this;
        public bool MoveNext() => ++Moves == 1;
        public void Reset() => throw new NotSupportedException();
        public void Dispose() { Disposals++; }
    }
    internal static void Run(Assembly server, Action<bool,string> check)
    {
        var proxy = new ItemProxy();
        var sequence = new Sequence { Current = proxy.GetTransparentProxy() };
        var capture = server.GetType("TiaMcpServer.Siemens.HmiSnapshot", true)!.GetMethod("Capture", BindingFlags.Static | BindingFlags.NonPublic)!;
        var value = capture.Invoke(null, new object?[] { sequence, 6, 2000, 65536, null });
        var json = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 }.Deserialize<Dictionary<string,object>>(value!.ToString());
        if ((bool)json["apiCallSuccess"]) Console.Error.WriteLine("Unexpected snapshot: " + value + "; proxy calls=" + string.Join(",", proxy.Calls));
        check(!(bool)json["apiCallSuccess"] && !(bool)json["dataComplete"] && (bool)json["connectionUnavailable"], "actual net48 snapshot returns failure for a real remoting exception");
        check(proxy.Calls.Contains("get_A") && proxy.Calls.Contains("get_B") && !proxy.Calls.Contains("get_Z"), "actual EXE stops proxy property reads at first IPC failure");
        check(sequence.Moves == 1 && sequence.Disposals == 0, "actual EXE avoids enumerator calls after nested IPC failure");
        check(value.ToString().Contains("retained") && (string)json["failurePath"] == "$/0/B", "actual EXE retains partial data and exact failing path");
    }
}
