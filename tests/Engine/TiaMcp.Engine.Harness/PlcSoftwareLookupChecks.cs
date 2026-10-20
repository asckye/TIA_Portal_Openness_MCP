using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;

internal static class PlcSoftwareLookupChecks
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    internal static void Run(Assembly adapter, Action<bool, string> check)
    {
        var candidate = adapter.GetType("TiaMcp.Adapters.PlcReadCandidate`1", true)!.MakeGenericType(typeof(object));
        var policy = adapter.GetType("TiaMcp.Adapters.PlcReadPathPolicy", true)!;
        var resolve = policy.GetMethod("Resolve", All)!.MakeGenericMethod(typeof(object));
        var select = policy.GetMethod("Select", All)!.MakeGenericMethod(typeof(object));
        object Value(string exact, string device, string host, string[] groups, object value)
        {
            var item = Activator.CreateInstance(candidate, true)!;
            foreach (var field in new Dictionary<string, object> { ["ExactPath"] = exact, ["Device"] = device, ["Host"] = host, ["Groups"] = groups, ["Value"] = value })
                candidate.GetField(field.Key, All)!.SetValue(item, field.Value);
            return item;
        }
        Array Inventory(params object[] values) { var items = Array.CreateInstance(candidate, values.Length); for (int i = 0; i < values.Length; i++) items.SetValue(values[i], i); return items; }
        bool Refused(Array values, string path)
        {
            try { resolve.Invoke(null, new object[] { values, path }); return false; }
            catch (TargetInvocationException error) { return error.InnerException?.GetType().Name == "AdapterPreconditionException"; }
        }
        // Exercise the literal, case, group, ambiguity and wrong-target cases from
        // SoftwareLookupRuntimeTests against the adapter's actual selection policy.
        foreach (var host in new[] { "+S1-K1", "PLC_1", "控制器.一", "FB_Motor.V2" })
        foreach (var groups in new[] { Array.Empty<string>(), new[] { "Line" }, new[] { "Line", "Area" } })
        {
            var identity = new object(); string exact = (groups.Length == 0 ? "" : "device-groups/" + string.Join("/", groups) + "/") + "devices/Station/CPU/" + host;
            var first = Value(exact, "Station", host, groups, identity);
            var items = Inventory(first);
            // Device/host aliases ignore case; a grouped device takes its case-sensitive group prefix (PlcReadPathPolicy,
            // as PathAndIdentityTests pins for the Foundation releases).
            foreach (string path in new[] { exact, host, host.ToLowerInvariant(), "Station", "station",
                string.Join("/", groups.Concat(new[] { host })), string.Join("/", groups.Concat(new[] { "Station", host })),
                string.Join("/", groups.Concat(new[] { "station", host.ToLowerInvariant() })) }.Distinct())
            {
                check(ReferenceEquals(resolve.Invoke(null, new object[] { items, path }), identity), "Adapter resolves exact software alias: " + path);
                check(ReferenceEquals(candidate.GetField("Value", All)!.GetValue(select.Invoke(null, new object[] { items, path })), identity), "Adapter selection retains software identity: " + path);
            }
            foreach (string path in new[] { "WrongPLC", "PLC", ".*", "Prefix_" + host + "_suffix", "Wrong/" + host, exact + "/garbage", "", " ", "Line/../" + host, "Line//" + host })
                check(Refused(items, path), "Adapter refuses an inexact or unsafe software path: " + path);
            if (groups.Length != 0)
                check(Refused(items, string.Join("/", groups.Select(group => group.ToLowerInvariant()).Concat(new[] { host }))), "Adapter group segments are case-sensitive");
            var second = Value("devices/Other/CPU/" + host, "Other", host, Array.Empty<string>(), new object());
            check(Refused(Inventory(first, second), host), "Adapter refuses duplicate software names");
            check(ReferenceEquals(resolve.Invoke(null, new object[] { Inventory(first, second), exact }), identity), "Adapter exact address disambiguates duplicate software names");
            check(Refused(Inventory(), host), "Adapter empty project never guesses a software target");
        }
        // The optional-read implementation remains in Logic. Reuse its original
        // nine assertion bodies without including any deleted Portal/cache code.
        var type = PlcHarnessChecks.Logic.GetType("TiaMcpServer.Siemens.PlcListingRead", true)!;
        var reader = Activator.CreateInstance(type, true)!;
        var optional = type.GetMethod("Optional", All)!.MakeGenericMethod(typeof(string));
        var required = type.GetMethod("Required", All)!.MakeGenericMethod(typeof(string));
        var metadata = type.GetMethod("Metadata", All)!;
        check((string)required.Invoke(reader, new object[] { "+S1-K1/Name", new Func<string>(() => "+S1-K1") })! == "+S1-K1", "Listing retains exact PLC name");
        check(optional.Invoke(reader, new object?[] { "F_FB/Namespace", new Func<string>(() => throw new NotSupportedException("unavailable")), null }) == null, "Optional property failure yields null");
        check((string)optional.Invoke(reader, new object?[] { "Healthy/Name", new Func<string>(() => "Healthy"), null })! == "Healthy", "Listing continues after optional failure");
        JsonObject Meta() => JsonNode.Parse(metadata.Invoke(reader, new object[] { "user blocks" })!.ToString())!.AsObject();
        var meta = Meta();
        check((bool)meta["apiCallSuccess"]! && !(bool)meta["dataComplete"]! && (int)meta["failureCount"]! == 1, "Call success differs from completeness");
        check(meta.ToJsonString().Contains("F_FB/Namespace") && meta.ToJsonString().Contains("NotSupportedException"), "Listing includes failing attribute evidence");
        bool Throws(Action action, string message) { try { action(); return false; } catch (TargetInvocationException error) { return error.InnerException!.Message.Contains(message); } }
        check(Throws(() => optional.Invoke(reader, new object?[] { "F_FB/Namespace", new Func<string>(() => throw new System.Runtime.Remoting.RemotingException("IPC lost")), null }), "connection unavailable"), "Listing aborts on IPC loss");
        check(Throws(() => required.Invoke(reader, new object[] { "+S1-K1/BlockGroup", new Func<string>(() => throw new InvalidOperationException("root denied")) }), "+S1-K1/BlockGroup"), "Listing preserves block root failure stage");
        check(Throws(() => required.Invoke(reader, new object[] { "+S1-K1/TypeGroup", new Func<string>(() => throw new InvalidOperationException("type root denied")) }), "+S1-K1/TypeGroup"), "Listing preserves type root failure stage");
        check((int)Meta()["failureCount"]! == 1, "Fatal failures are not downgraded to optional warnings");
    }
}
