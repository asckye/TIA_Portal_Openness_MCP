using System;
using System.Collections.Generic;
using System.Reflection;

internal static class SoftwareLookupRuntimeTests
{
    public sealed class Node
    {
        public string? Alias;
        public object? Container;
        public string SoftwareName = "+S1-K1";
        public List<object> Children = new List<object>();
        public object BlockGroup => throw new Exception("Unexpected block access");
    }
    internal static void Run(Assembly server, Action<bool, string> check)
    {
        var lookup = server.GetType("TiaMcpServer.Siemens.SoftwareContainerLookup", true)!
            .GetMethod("FindUnique", BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(object));
        var cpu = new Node(); cpu.Container = cpu;
        var device = new Node { Alias = "Station", Children = new List<object> { cpu } };
        var group = new Node { Children = new List<object> { new Node { Children = new List<object> { device } } } };
        object? Resolve(string name, object[] roots, int limit = 100)
            => lookup.Invoke(null, new object[] { roots,
                new Func<object, IEnumerable<object>>(n => ((Node)n).Children),
                new Func<object, string?>(n => ((Node)n).Alias),
                new Func<object, object?>(n => ((Node)n).Container),
                new Func<object, string?>(n => ((Node)n).SoftwareName), name, limit });
        check(ReferenceEquals(Resolve("+S1-K1", new object[] { group }), cpu), "actual EXE resolves IEC software name below nested device groups");
        check(ReferenceEquals(Resolve("Station", new object[] { group }), cpu), "actual EXE resolves device alias to software descendant");
        check(Resolve(".*", new object[] { group }) == null, "actual EXE does not interpret software name as regex");
        check(Resolve("K1", new object[] { group }) == null, "actual EXE does not guess a lone PLC from substring");
        var other = new Node(); other.Container = other;
        bool ambiguous = false;
        try { Resolve("+S1-K1", new object[] { group, other }); }
        catch (TargetInvocationException ex) { ambiguous = ex.InnerException?.GetType().GetProperty("Code")?.GetValue(ex.InnerException)?.ToString() == "InvalidParams"; }
        check(ambiguous, "actual EXE rejects duplicate software names");
        bool limited = false;
        try { Resolve("+S1-K1", new object[] { group }, 1); }
        catch (TargetInvocationException ex) { limited = ex.InnerException?.GetType().GetProperty("Code")?.GetValue(ex.InnerException)?.ToString() == "OpennessError"; }
        check(limited, "actual EXE reports incomplete bounded scan as error");
        var portal = server.GetType("TiaMcpServer.Siemens.Portal", true)!;
        var listing = portal.GetMethod("ResolvePlcForListing", BindingFlags.Instance | BindingFlags.NonPublic)!;
        // Required receives a compiler-generated delegate. Check the delegate target's IL too.
        var targets = new List<MethodInfo>(portal.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic));
        foreach (var nested in portal.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
            targets.AddRange(nested.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic));
        int token = portal.GetMethod("GetPlcSoftware", new[] { typeof(string) })!.MetadataToken;
        bool shared = false;
        foreach (var method in targets)
        {
            if (!method.Name.Contains("ResolvePlcForListing")) continue;
            var il = method.GetMethodBody()?.GetILAsByteArray();
            if (il == null) continue;
            for (int i = 0; i + 4 < il.Length; i++)
                if ((il[i] == 0x28 || il[i] == 0x6f) && BitConverter.ToInt32(il, i + 1) == token) shared = true;
        }
        check(shared, "EXE listing calls shared GetPlcSoftware resolver, including enumeration fallback");
        var match = server.GetType("TiaMcpServer.Siemens.Guard", true)!.GetMethod("MatchPlcName")!;
        check((string)match.Invoke(null, new object[] { new[] { "+S1-K1", "PLC_2" }, "+S1-K1" })! == "+S1-K1", "EXE enumeration fallback matches IEC name literally before single-PLC fallback");
        check((string)match.Invoke(null, new object[] { new[] { "+S1-K1" }, "ET 200SP station_1" })! == "+S1-K1", "EXE shared single-PLC fallback resolves station alias");
        var rt = server.GetType("TiaMcpServer.Siemens.PlcListingRead", true)!;
        var reader = Activator.CreateInstance(rt, true)!;
        var optional = rt.GetMethod("Optional", BindingFlags.Instance | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(string));
        var required = rt.GetMethod("Required", BindingFlags.Instance | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(string));
        var metadata = rt.GetMethod("Metadata", BindingFlags.Instance | BindingFlags.NonPublic)!;
        check((string)required.Invoke(reader, new object[] { "+S1-K1/Name", new Func<string>(() => "+S1-K1") })! == "+S1-K1", "EXE listing retains exact PLC name");
        check(optional.Invoke(reader, new object?[] { "F_FB/Namespace", new Func<string>(() => throw new NotSupportedException("unavailable")), null }) == null, "EXE optional property failure yields null");
        check((string)optional.Invoke(reader, new object?[] { "Healthy/Name", new Func<string>(() => "Healthy"), null })! == "Healthy", "EXE continues after optional failure");
        var json = metadata.Invoke(reader, new object[] { "user blocks" })!.ToString();
        var values = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
        check((bool)values["apiCallSuccess"] && !(bool)values["dataComplete"] && (int)values["failureCount"] == 1, "EXE differentiates call success from completeness");
        check(json.Contains("F_FB/Namespace") && json.Contains("NotSupportedException"), "EXE includes failing attribute evidence");
        bool ipcStopped = false;
        try { optional.Invoke(reader, new object?[] { "F_FB/Namespace", new Func<string>(() => throw new System.Runtime.Remoting.RemotingException("IPC lost")), null }); }
        catch (TargetInvocationException ex) { ipcStopped = ex.InnerException!.Message.Contains("connection unavailable"); }
        check(ipcStopped, "EXE aborts on IPC loss");
        bool rootFailure = false;
        try { required.Invoke(reader, new object[] { "+S1-K1/BlockGroup", new Func<string>(() => throw new InvalidOperationException("root denied")) }); }
        catch (TargetInvocationException ex) { rootFailure = ex.InnerException!.Message.Contains("+S1-K1/BlockGroup"); }
        check(rootFailure, "EXE preserves BlockGroup failure stage");
        bool typeRootFailure = false;
        try { required.Invoke(reader, new object[] { "+S1-K1/TypeGroup", new Func<string>(() => throw new InvalidOperationException("type root denied")) }); }
        catch (TargetInvocationException ex) { typeRootFailure = ex.InnerException!.Message.Contains("+S1-K1/TypeGroup"); }
        check(typeRootFailure, "EXE preserves TypeGroup failure stage");
        check((int)new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(metadata.Invoke(reader, new object[] { "user blocks" })!.ToString())["failureCount"] == 1, "fatal failures are not downgraded to optional warnings");
    }
}
