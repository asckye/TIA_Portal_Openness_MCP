using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static class Program
{
    private static Assembly Server = null!;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
    private static int Passed;
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static Dictionary<string, object> Parse(string value) => Json.Deserialize<Dictionary<string, object>>(value);
    private static string Request(object id) => Json.Serialize(new { jsonrpc = "2.0", id, method = "tools/call", @params = new { name = "GetState" } });
    private static string Reply(object id, object value) => Json.Serialize(new { jsonrpc = "2.0", id, result = value });
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static async Task<T> Bounded<T>(Task<T> task, int ms = 5000)
    { if (await Task.WhenAny(task, Task.Delay(ms)) != task) throw new Exception("Test operation hung"); return await task; }
    private static async Task Fault(Task task, Type type)
    {
        try { await Bounded(AsResult(task)); }
        catch (Exception ex) { if (type.IsInstanceOfType(ex)) return; throw; }
        throw new Exception("Expected " + type.Name);
    }
    private static async Task<bool> AsResult(Task task) { await task; return true; }

    private sealed class Fixture : IDisposable
    {
        public readonly object Router;
        public readonly Stream Requests;
        public readonly Stream Responses;
        public readonly StreamReader RequestReader;
        private readonly StreamWriter ResponseWriter;
        private readonly Type RouterType;
        public Fixture()
        {
            var streamType = Server.GetType("TiaMcpServer.McpBlockingStream", true)!;
            Requests = (Stream)Activator.CreateInstance(streamType, true)!;
            Responses = (Stream)Activator.CreateInstance(streamType, true)!;
            RouterType = Server.GetType("TiaMcpServer.McpHttpResponseRouter", true)!;
            Router = Activator.CreateInstance(RouterType, All, null, new object[] { Requests, Responses }, null)!;
            RequestReader = new StreamReader(Requests, new UTF8Encoding(false));
            ResponseWriter = new StreamWriter(Responses, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        }
        public Task<string?> Send(string body, int timeoutMs = 3000)
            => (Task<string?>)RouterType.GetMethod("SendAsync", All)!.Invoke(Router, new object[] { body, TimeSpan.FromMilliseconds(timeoutMs) })!;
        public Task<string?> Read() => Task.Run<string?>(() => RequestReader.ReadLine());
        public void Write(string text) => ResponseWriter.WriteLine(text);
        public void Complete() => Responses.GetType().GetMethod("CompleteWriting")!.Invoke(Responses, null);
        public Task Completion => (Task)RouterType.GetProperty("Completion", All)!.GetValue(Router)!;
        public void Dispose()
        {
            ((IDisposable)Router).Dispose();
            Requests.GetType().GetMethod("CompleteWriting")!.Invoke(Requests, null);
        }
    }

    private static async Task Test(string label, Func<Task> test)
    { await test(); Passed++; Console.WriteLine("PASS " + label); }

    private static async Task RouterTests()
    {
        await Test("removed launcher key is ignored and explicit HTTP key still works", () => {
            string? previous = Environment.GetEnvironmentVariable("TIA_MCP_HTTP_API_KEY");
            try {
                Environment.SetEnvironmentVariable("TIA_MCP_HTTP_API_KEY", "environment-test-key");
                var cli = Server.GetType("TiaMcpServer.CliOptions", true)!;
                var parse = cli.GetMethod("ParseArgs", All)!;
                var property = cli.GetProperty("HttpApiKey")!;
                var env = parse.Invoke(null, new object[] { new string[0] });
                Check(property.GetValue(env) == null, "Removed launcher environment still configures the server");
                var explicitKey = parse.Invoke(null, new object[] { new[] { "--http-api-key", "explicit-test-key" } });
                Check((string)property.GetValue(explicitKey)! == "explicit-test-key", "Explicit HTTP key was not loaded");
            }
            finally { Environment.SetEnvironmentVariable("TIA_MCP_HTTP_API_KEY", previous); }
            return Task.CompletedTask;
        });
        await Test("one-click shutdown control is absent from the compiled server", () => {
            Check(Server.GetType("TiaMcpServer.Runtime.ServerShutdown", false) == null,
                "Compiled server still includes one-click shutdown");
            Check(Server.GetType("TiaMcpServer.Runtime.ServerShutdownGate", false) == null,
                "Compiled server still includes the one-click shutdown call gate");
            return Task.CompletedTask;
        });
        await Test("obsolete and unknown commands exit without starting the MCP server", () => {
            foreach (string command in new[] { "stop", "STOP", "stop --transport http", "unknown-command" }) {
                var start = new System.Diagnostics.ProcessStartInfo(Server.Location, command) {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                using var child = System.Diagnostics.Process.Start(start)!;
                if (!child.WaitForExit(5000)) {
                    child.Kill();
                    throw new Exception("Invalid command started a long-running process: " + command);
                }
                Check(child.ExitCode == 2, "Invalid command did not return usage error: " + command);
                Check(child.StandardError.ReadToEnd().Contains("Unknown command"),
                    "Invalid command reached TIA initialization: " + command);
            }
            return Task.CompletedTask;
        });
        await Test("numeric and string client IDs round-trip", async () => {
            using var f = new Fixture();
            foreach (object id in new object[] { 7, "client-id", "画面请求", null! }) {
                var call = f.Send(Request(id)); var wire = Parse((await Bounded(f.Read()))!);
                Check(!Equals(wire["id"], id), "ID was not remapped");
                f.Write(Reply(wire["id"], "ok")); var result = Parse((await Bounded(call))!);
                Check(Equals(result["id"], id) && (string)result["result"] == "ok", "Round-trip mismatch");
            }
        });
        await Test("late result cannot steal retry using the same client ID", async () => {
            using var f = new Fixture();
            var first = f.Send(Request(1), 100); var old = Parse((await Bounded(f.Read()))!);
            await Fault(first, typeof(TimeoutException));
            var retry = f.Send(Request(1)); var fresh = Parse((await Bounded(f.Read()))!);
            Check(!Equals(old["id"], fresh["id"]), "Retry reused wire ID");
            f.Write(Reply(old["id"], "stale")); await Task.Delay(60);
            Check(!retry.IsCompleted, "Late result incorrectly completed retry");
            f.Write(Reply(fresh["id"], "fresh"));
            Check((string)Parse((await Bounded(retry))!)["result"] == "fresh", "Retry did not recover");
        });
        await Test("50 timeouts followed by late replies and 100 successful requests", async () => {
            using var f = new Fixture(); var stale = new List<object>();
            for (int i=0; i<50; i++) {
                var call = f.Send(Request(0), 25); stale.Add(Parse((await Bounded(f.Read()))!)["id"]);
                await Fault(call, typeof(TimeoutException));
            }
            foreach (var id in stale.AsEnumerable().Reverse()) f.Write(Reply(id, "stale"));
            for (int i=0; i<100; i++) {
                var call = f.Send(Request(0)); var wire = Parse((await Bounded(f.Read()))!);
                f.Write(Reply(wire["id"], i));
                Check((int)Parse((await Bounded(call))!)["result"] == i, "Response loss after timeout stress");
            }
        });
        await Test("simultaneous clients with identical IDs receive their own results", async () => {
            using var f = new Fixture();
            var one = f.Send(Request(1)); var two = f.Send(Request(1));
            var a = Parse((await Bounded(f.Read()))!); f.Write(Reply(a["id"], "a"));
            Check((string)Parse((await Bounded(one))!)["result"] == "a", "First client mismatch");
            var b = Parse((await Bounded(f.Read()))!); f.Write(Reply(b["id"], "b"));
            Check(!Equals(a["id"], b["id"]), "Clients share wire ID");
            Check((string)Parse((await Bounded(two))!)["result"] == "b", "Second client mismatch");
        });
        await Test("notifications do not wait for a reply", async () => {
            using var f = new Fixture();
            var sent = f.Send("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            Check(await Bounded(sent) == null, "Notification returned a reply");
            Check(!Parse((await Bounded(f.Read()))!).ContainsKey("id"), "Notification acquired an ID");
        });
        await Test("malformed lines, unknown IDs, and server notifications are skipped", async () => {
            using var f = new Fixture(); var call = f.Send(Request(2)); var wire = Parse((await Bounded(f.Read()))!);
            f.Write("malformed"); f.Write("[]"); f.Write(Reply("unknown", "wrong"));
            f.Write("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/message\",\"params\":{}}");
            f.Write(Reply(wire["id"], "right")); Check((string)Parse((await Bounded(call))!)["result"] == "right", "Skipped frame corrupted response");
        });
        await Test("fragmented Unicode response larger than reader buffer", async () => {
            using var f = new Fixture(); var call = f.Send(Request("屏幕")); var wire = Parse((await Bounded(f.Read()))!);
            string payload = string.Concat(Enumerable.Repeat("中文画面🟦", 4000));
            byte[] bytes = Encoding.UTF8.GetBytes(Reply(wire["id"], payload) + "\n");
            for (int p=0; p<bytes.Length; p+=7) f.Responses.Write(bytes, p, Math.Min(7, bytes.Length-p));
            Check((string)Parse((await Bounded(call))!)["result"] == payload, "UTF-8 fragmentation damaged content");
        });
        await Test("JSON-RPC error response preserves original ID and error", async () => {
            using var f = new Fixture(); var call = f.Send(Request("fail")); var wire = Parse((await Bounded(f.Read()))!);
            f.Write(Json.Serialize(new { jsonrpc="2.0", id=wire["id"], error=new { code=-32602, message="invalid argument" } }));
            var r=Parse((await Bounded(call))!); Check((string)r["id"] == "fail" && r.ContainsKey("error"), "Error reply changed");
        });
        await Test("stream closure fails pending and subsequent requests promptly", async () => {
            using var f = new Fixture(); var call=f.Send(Request(4)); await Bounded(f.Read()); f.Complete();
            await Fault(call, typeof(EndOfStreamException)); await Fault(f.Send(Request(5)), typeof(IOException));
            await Bounded(AsResult(f.Completion));
        });
        await Test("disposal unblocks the single reader and pending requests", async () => {
            using var f=new Fixture(); var call=f.Send(Request(1)); await Bounded(f.Read()); f.Dispose();
            await Fault(call, typeof(ObjectDisposedException)); await Bounded(AsResult(f.Completion));
        });
        await Test("queued request expires without reaching MCP", async () => {
            using var f = new Fixture();
            var first = f.Send(Request(1)); var wire = Parse((await Bounded(f.Read()))!);
            await Fault(f.Send(Request(2), 80), typeof(TimeoutException));
            f.Write(Reply(wire["id"], "first")); await Bounded(first);
            var next = f.Send(Request(3)); var fresh = Parse((await Bounded(f.Read()))!);
            f.Write(Reply(fresh["id"], "third")); await Bounded(next);
        });
        await Test("shutdown promptly releases all queued callers", async () => {
            using var f = new Fixture();
            var first = f.Send(Request(1)); await Bounded(f.Read());
            var queued = Enumerable.Range(0, 25).Select(i => f.Send(Request(i))).ToArray();
            f.Dispose();
            await Fault(first, typeof(ObjectDisposedException));
            foreach (var call in queued) await Fault(call, typeof(IOException));
            await Bounded(AsResult(f.Completion));
        });
        await Test("closed request stream terminates router and queued callers", async () => {
            using var f = new Fixture();
            f.Requests.GetType().GetMethod("CompleteWriting")!.Invoke(f.Requests, null);
            await Fault(f.Send(Request(1)), typeof(IOException));
            await Fault(f.Send(Request(2)), typeof(IOException));
            await Bounded(AsResult(f.Completion));
        });
    }

    private static async Task<string> Post(string url, string body, string? key, string accept="application/json")
    {
        var req=(HttpWebRequest)WebRequest.Create(url); req.Proxy=null; req.Method="POST";
        req.ContentType="application/json"; req.Accept=accept; req.Timeout=5000; req.ReadWriteTimeout=5000;
        if(key!=null) req.Headers["X-API-Key"]=key;
        var bytes=Encoding.UTF8.GetBytes(body); req.ContentLength=bytes.Length;
        using(var stream=await req.GetRequestStreamAsync()) await stream.WriteAsync(bytes,0,bytes.Length);
        using var response=(HttpWebResponse)await req.GetResponseAsync();
        using var reader=new StreamReader(response.GetResponseStream()); return await reader.ReadToEndAsync();
    }

    private static async Task HttpTest(bool baseline)
    {
        var tcp = new TcpListener(IPAddress.Loopback,0); tcp.Start(); int port=((IPEndPoint)tcp.LocalEndpoint).Port; tcp.Stop();
        string prefix="http://localhost:"+port+"/";
        var httpType=Server.GetType("TiaMcpServer.HttpMcpServer",true)!;
        httpType.GetField("ResponseTimeout",All)!.SetValue(null,TimeSpan.FromMilliseconds(180));
        var cliType=Server.GetType("TiaMcpServer.CliOptions",true)!; var options=Activator.CreateInstance(cliType,true)!;
        cliType.GetProperty("HttpPrefix")!.SetValue(options,prefix); cliType.GetProperty("HttpApiKey")!.SetValue(options,"local-test-secret");
        var streamType=Server.GetType("TiaMcpServer.McpBlockingStream",true)!;
        var requests=(Stream)Activator.CreateInstance(streamType,true)!; var responses=(Stream)Activator.CreateInstance(streamType,true)!;
        var logs=new List<string>(); Action<string> log=s=>{ lock(logs) logs.Add(s); if(s.StartsWith("HTTP handler error")) Console.WriteLine(s); };
        using var stop = new CancellationTokenSource();
        var run = httpType.GetMethod("Run",All)!;
        var serverTask=(Task)run.Invoke(null,run.GetParameters().Length == 5
            ? new object[]{options,requests,responses,log,stop.Token}
            : new object[]{options,requests,responses,log})!;
        if(serverTask.IsFaulted) await serverTask;
        using var requestReader=new StreamReader(requests,new UTF8Encoding(false));
        using var writer=new StreamWriter(responses,new UTF8Encoding(false)){AutoFlush=true,NewLine="\n"};
        Func<Task<string?>> read=()=>Task.Run<string?>(()=>requestReader.ReadLine());

        var late=Post(prefix+"mcp",Request(1),"local-test-secret"); var old=Parse((await Bounded(read()))!);
        try { await Bounded(late); throw new Exception("Expected HTTP 504"); }
        catch(WebException ex) { Check(((HttpWebResponse)ex.Response).StatusCode==HttpStatusCode.GatewayTimeout,"Wrong timeout status"); }
        var next=Post(prefix+"mcp",Request(1),"local-test-secret"); var fresh=Parse((await Bounded(read()))!);
        writer.WriteLine(Reply(fresh["id"],"fresh")); await Task.Delay(25); writer.WriteLine(Reply(old["id"],"stale"));
        try {
            var result=Parse(await Bounded(next));
            Check((string)result["result"]=="fresh","Late reply delivered to a new HTTP request");
        }
        catch(Exception ex) when(baseline) { Console.WriteLine("BASELINE REPRODUCED: " + ex.Message); return; }
        if(baseline) throw new Exception("Baseline unexpectedly passed the race regression");
        Passed++; Console.WriteLine("PASS real HTTP timeout, retry with reused ID, and late response");

        var sse=Post(prefix+"mcp",Request("sse"),"local-test-secret","application/json, text/event-stream");
        var wire=Parse((await Bounded(read()))!); writer.WriteLine(Reply(wire["id"],"sse-ok"));
        string frame=await Bounded(sse); Check(frame.StartsWith("event: message\n") && frame.Contains("\"id\":\"sse\""),"SSE framing mismatch");
        Passed++; Console.WriteLine("PASS real HTTP SSE response and restored ID");
        using(var unauthorized=new TcpClient()) {
            await unauthorized.ConnectAsync("localhost",port);
            var socket=unauthorized.GetStream();
            var unauthorizedBytes=Encoding.ASCII.GetBytes("GET /mcp HTTP/1.1\r\nHost: localhost:"+port+"\r\nX-API-Key: wrong\r\nConnection: close\r\n\r\n");
            await socket.WriteAsync(unauthorizedBytes,0,unauthorizedBytes.Length);
            using var unauthorizedReader=new StreamReader(socket);
            var status=await Bounded(unauthorizedReader.ReadLineAsync());
            Check(status!=null && status.Contains(" 401 "),"Wrong authentication response: "+status);
        }
        Passed++; Console.WriteLine("PASS real HTTP authentication remains required");
        foreach(var contentType in new[]{"application/json","application/json; charset=utf-8"})
        foreach(var escaped in new[]{false,true}) {
            var body="{\"jsonrpc\":\"2.0\",\"id\":\"unicode\",\"method\":\"echo\",\"params\":{\"text\":\""+(escaped?"\\u4e2d\\u6587":"中文")+"\"}}";
            var req=(HttpWebRequest)WebRequest.Create(prefix+"mcp");req.Proxy=null;req.Method="POST";req.ContentType=contentType;req.Headers["X-API-Key"]="local-test-secret";
            var bytes=Encoding.UTF8.GetBytes(body);req.ContentLength=bytes.Length;
            using(var upload=await req.GetRequestStreamAsync())await upload.WriteAsync(bytes,0,bytes.Length);
            var resultTask=req.GetResponseAsync();
            var forwarded=Parse((await Bounded(read()))!);
            Check((string)((Dictionary<string,object>)forwarded["params"])["text"]=="中文","UTF-8 and escapes differ");
            writer.WriteLine(Reply(forwarded["id"],"中文"));
            using var response=(HttpWebResponse)await Bounded(resultTask);
            Check(response.StatusCode==HttpStatusCode.OK,"UTF-8 returned non-200");
            Passed++;Console.WriteLine("PASS UTF-8/escape semantics, "+contentType+" escaped="+escaped);
        }
        try {await Bounded(Post(prefix+"mcp","{broken-json","local-test-secret"));throw new Exception("Expected 400");}
        catch(WebException ex){
            using var response=(HttpWebResponse)ex.Response;
            using var bodyReader=new StreamReader(response.GetResponseStream());var detail=Parse(bodyReader.ReadToEnd());
            Check(response.StatusCode==HttpStatusCode.BadRequest&&detail.ContainsKey("bytePosition")&&detail.ContainsKey("requestId"),"400 missing evidence fields");
        }
        lock(logs)Check(logs.Any(x=>x.Contains("sha256="))&&!logs.Any(x=>x.Contains("local-test-secret")||x.Contains("{broken-json")),"Diagnostics omitted hash or leaked request/key");
        Passed++;Console.WriteLine("PASS invalid JSON evidence without body or key logging");
        var readiness=Server.GetType("TiaMcpServer.McpHostReadiness",true)!;
        foreach(var phase in new[]{"Initializing","Ready","Failed"}) {
            readiness.GetMethod("Set",All)!.Invoke(null,new object?[]{phase,phase=="Failed"?"injected startup failure":null});
            var req=(HttpWebRequest)WebRequest.Create(prefix+"mcp/ready");req.Proxy=null;req.Headers["X-API-Key"]="local-test-secret";
            HttpWebResponse response;
            try {response=(HttpWebResponse)await req.GetResponseAsync();}catch(WebException ex){response=(HttpWebResponse)ex.Response;}
            using(response){using var bodyReader=new StreamReader(response.GetResponseStream());var state=Parse(bodyReader.ReadToEnd());
                Check((bool)state["mcpHostReady"]==(phase=="Ready")&&(string)state["tiaAvailability"]=="NotProbed","Readiness conflates HTTP/host/TIA");
                Check((int)response.StatusCode==(phase=="Ready"?200:503),"Wrong readiness status");}
            Passed++;Console.WriteLine("PASS authenticated readiness "+phase);
        }
        lock(logs) Check(!logs.Any(s=>s.StartsWith("HTTP handler error")),"HTTP handler threw: "+string.Join(";",logs));
        var pending = Post(prefix+"mcp",Request("stop"),"local-test-secret");
        await Bounded(read());
        using var incompleteUpload = new TcpClient();
        await incompleteUpload.ConnectAsync("localhost", port);
        var partialBody = Encoding.ASCII.GetBytes("POST /mcp HTTP/1.1\r\nHost: localhost:"+port+"\r\nX-API-Key: local-test-secret\r\nContent-Length: 100\r\n\r\n{");
        await incompleteUpload.GetStream().WriteAsync(partialBody, 0, partialBody.Length);
        await Task.Delay(50);
        stop.Cancel();
        await Bounded(AsResult(serverTask));
        try { await Bounded(pending); } catch (WebException) { }
        Passed++; Console.WriteLine("PASS real HTTP shutdown drains reader, waiting request and incomplete upload");
    }

    private static async Task<int> Main(string[] args)
    {
        try {
            string exe=Path.GetFullPath(args[0]); string dir=Path.GetDirectoryName(exe)!;
            AppDomain.CurrentDomain.AssemblyResolve+=(sender,e)=>{
                string dependency=Path.Combine(dir,new AssemblyName(e.Name).Name+".dll");
                return File.Exists(dependency)?Assembly.LoadFrom(dependency):null;
            };
            Server=Assembly.LoadFrom(exe);
            if(args.Skip(1).Contains("hmi-only")) {
                var refs=Server.GetReferencedAssemblies().Where(x=>x.Name!.StartsWith("Siemens.Engineering")).ToArray();
                int major = int.Parse(args[2]);
                Check(refs.Length>0 && refs.All(x=>x.Version!.Major==major),"Wrong TIA reference version");
                Check(System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).FileVersion==args[3],"Wrong fixed file version");
                TiaMcpServer.Siemens.HmiScreenTraversal.EngineType=Server.GetType("TiaMcpServer.Siemens.HmiScreenTraversal",true)!;
                int failures=0;
                TiaMcpServer.Tests.HmiScreenTraversalTests.Run((ok,name)=>{if(ok)Passed++;else{failures++;Console.Error.WriteLine("FAIL "+name);}});
                Check(failures==0 && Passed==18,"HMI traversal regression failed");
                Console.WriteLine("PASS actual V" + major + " EXE " + args[3] + ": 18 HMI traversal assertions, 0 failed");
                return 0;
            }
            bool baseline=args.Skip(1).Contains("baseline");
            if(!baseline) await RouterTests();
            await HttpTest(baseline);
            Console.WriteLine("COMPLETE: "+Passed+" passed; tested actual EXE "+System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).FileVersion);
            return 0;
        } catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
