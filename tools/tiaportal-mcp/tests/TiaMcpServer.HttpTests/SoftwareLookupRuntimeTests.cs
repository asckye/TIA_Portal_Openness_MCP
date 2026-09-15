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
    public sealed class BlockGroup
    {
        public string Name = "Program blocks";
        public List<BlockGroup> Groups = new List<BlockGroup>();
        public List<string> Blocks = new List<string>();
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
        var findBlock = server.GetType("TiaMcpServer.Siemens.PlcBlockLookup", true)!.GetMethod("Find", BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(typeof(BlockGroup), typeof(string));
        var root = new BlockGroup();
        root.Groups.Add(new BlockGroup { Name = "03_OPMode", Blocks = new List<string> { "OPMODE01_FC" } });
        var selectBlock = portal.GetMethod("ResolveSingleByName", BindingFlags.NonPublic | BindingFlags.Instance)!.MakeGenericMethod(typeof(string));
        var uninitialized = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(portal);
        // Avoid constructing/connecting a Portal; initialize the pure name selector's only field.
        portal.GetField("_regexChars", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(uninitialized,
            new[] { '.', '^', '$', '*', '+', '?', '(', '[', '{', '\\', '|' });
        string? FindBlock(string path) => (string?)findBlock.Invoke(null, new object[] { root, path,
            new Func<BlockGroup,string>(g => g.Name), new Func<BlockGroup,IEnumerable<BlockGroup>>(g => g.Groups),
            new Func<BlockGroup,IEnumerable<string>>(g => g.Blocks),
            new Func<IEnumerable<string>,string,string?>((items, name) => (string?)selectBlock.Invoke(uninitialized, new object[] {items, name, new Func<string,string>(x => x), "block"})) });
        foreach (var path in new[] { "03_OPMode/OPMODE01_FC", "Program blocks/03_OPMode/OPMODE01_FC", "程序块/03_OPMode/OPMODE01_FC", @"03_OPMode\OPMODE01_FC", "OPMODE01_FC" })
            check(FindBlock(path) == "OPMODE01_FC", "EXE resolves grouped block: " + path);
        check(FindBlock("Wrong/OPMODE01_FC") == null, "EXE does not discard incorrect qualified group");
        check(FindBlock("Program blocks/OPMODE01_FC") == null, "EXE explicit root does not silently search descendants");
        root.Groups.Add(new BlockGroup { Name = "Other", Blocks = new List<string> { "OPMODE01_FC", "FB_Motor.V2" } });
        bool duplicateBlock = false;
        try { FindBlock("OPMODE01_FC"); } catch (TargetInvocationException ex) { duplicateBlock = ex.ToString().Contains("Ambiguous block"); }
        check(duplicateBlock, "EXE rejects ambiguous bare block names");
        check(FindBlock("03_OPMode/OPMODE01_FC") == "OPMODE01_FC", "EXE qualified block remains unique with duplicate elsewhere");
        check(FindBlock("FB_Motor.V2") == "FB_Motor.V2", "EXE block dot retains literal name priority");
        var getBlockIl = portal.GetMethod("GetBlock")!.GetMethodBody()!.GetILAsByteArray()!;
        int rootToken = portal.GetMethod("GetBlockRootGroup")!.MetadataToken;
        bool sharedRoot = false;
        for (int i=0; i+4<getBlockIl.Length; i++) if ((getBlockIl[i]==0x28 || getBlockIl[i]==0x6f) && BitConverter.ToInt32(getBlockIl,i+1)==rootToken) sharedRoot=true;
        check(sharedRoot, "EXE single-block entry uses same root resolver as listings");
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
