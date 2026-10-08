using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;

internal static class SoftwareLookupRuntimeTests
{
    public sealed class Node
    {
        public string? Alias;
        public bool IsGroup;
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
        var portal = EngineSurface.For(server);
        var listing = portal.Method("ResolvePlcForListing", BindingFlags.Instance | BindingFlags.NonPublic)!;
        // Required receives a compiler-generated delegate. Check the delegate target's IL too.
        var targets = EngineSurface.MethodFamily(listing);
        var resolver = portal.Method("GetPlcSoftware", new[] { typeof(string) });
        int token = resolver.MetadataToken;
        bool shared = false;
        foreach (var method in targets)
        {
            if (!method.Name.Contains("ResolvePlcForListing")) continue;
            var il = method.GetMethodBody()?.GetILAsByteArray();
            if (il == null) continue;
            for (int i = 0; i + 4 < il.Length; i++)
                if ((il[i] == 0x28 || il[i] == 0x6f) && BitConverter.ToInt32(il, i + 1) == token) shared = true;
        }
        EngineSurface.CheckIl(check, shared, "EXE listing calls shared GetPlcSoftware resolver, including enumeration fallback", listing, resolver);
        var match = Program.FindServerType(server, "TiaMcpServer.Siemens.Guard").GetMethod("MatchPlcName")!;
        check((string)match.Invoke(null, new object[] { new[] { "+S1-K1", "PLC_2" }, "+S1-K1" })! == "+S1-K1", "EXE enumeration fallback matches IEC name literally");
        check(match.Invoke(null, new object[] { new[] { "+S1-K1" }, "ET 200SP station_1" }) == null, "EXE Guard no longer guesses a station alias from a sole software name");
        StrictPlcChecks(server, check);
        var findBlock = server.GetType("TiaMcpServer.Siemens.PlcBlockLookup", true)!.GetMethod("Find", BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(typeof(BlockGroup), typeof(string));
        var root = new BlockGroup();
        root.Groups.Add(new BlockGroup { Name = "03_OPMode", Blocks = new List<string> { "OPMODE01_FC" } });
        var selectBlock = portal.Method("ResolveSingleByName", BindingFlags.NonPublic | BindingFlags.Instance)!.MakeGenericMethod(typeof(string));
        var uninitialized = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(selectBlock.DeclaringType!);
        // Avoid constructing/connecting a Portal; initialize the pure name selector's only field.
        portal.Field("_regexChars", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(uninitialized,
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
        var getBlock = portal.Method("GetBlock");
        var getBlockIl = getBlock.GetMethodBody()!.GetILAsByteArray()!;
        var rootResolver = portal.Method("GetBlockRootGroup");
        int rootToken = rootResolver.MetadataToken;
        bool sharedRoot = false;
        for (int i=0; i+4<getBlockIl.Length; i++) if ((getBlockIl[i]==0x28 || getBlockIl[i]==0x6f) && BitConverter.ToInt32(getBlockIl,i+1)==rootToken) sharedRoot=true;
        EngineSurface.CheckIl(check, sharedRoot, "EXE single-block entry uses same root resolver as listings", getBlock, rootResolver);
        var exactMatch = server.GetType("TiaMcpServer.Siemens.ExactSoftwareMatch", true)!.GetMethod("Select", BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(typeof(Node));
        var target = new Node { SoftwareName = "+S1-K1" };
        object? Select(IEnumerable<Node> nodes, string name) => exactMatch.Invoke(null, new object[] { nodes, name, new Func<Node,string>(n => n.SoftwareName) });
        check(ReferenceEquals(Select(new[] {target}, "+S1-K1"), target), "EXE strict container fallback resolves literal IEC software name");
        check(Select(new[] {target}, "WrongPLC") == null, "EXE write resolver refuses single-PLC guessing");
        check(Select(new[] {target}, "S1") == null && Select(new[] {target}, ".*") == null, "EXE write resolver rejects substring and regex guesses");
        bool duplicateSoftware = false;
        try { Select(new[] {target, new Node { SoftwareName = "+S1-K1" }}, "+S1-K1"); }
        catch (TargetInvocationException ex) { duplicateSoftware = ex.ToString().Contains("Ambiguous software"); }
        check(duplicateSoftware, "EXE write resolver rejects duplicate software names before selection");
        IEnumerable<Node> BrokenScan() { yield return target; throw new InvalidOperationException("scan interrupted"); }
        bool scanStopped = false;
        try { Select(BrokenScan(), "+S1-K1"); } catch (TargetInvocationException ex) { scanStopped = ex.ToString().Contains("scan interrupted"); }
        check(scanStopped, "EXE write resolver never returns early before uniqueness scan completes");
        var bareResolver = portal.Method("ResolveSoftwareLookup", BindingFlags.Instance | BindingFlags.NonPublic);
        var bareIl = bareResolver.GetMethodBody()!.GetILAsByteArray()!;
        var enumeration = portal.Method("EnumerateSoftwareContainersForExactLookup", BindingFlags.Instance | BindingFlags.NonPublic);
        int enumerationToken = enumeration.MetadataToken;
        bool wiredFallback = false;
        for (int i=0;i+4<bareIl.Length;i++) if ((bareIl[i]==0x28 || bareIl[i]==0x6f) && BitConverter.ToInt32(bareIl,i+1)==enumerationToken) wiredFallback=true;
        EngineSurface.CheckIl(check, wiredFallback, "EXE shared container resolver includes typed group/device enumeration fallback", bareResolver, enumeration);
        var deletion=server.GetType("TiaMcpServer.Siemens.EmptyPlcGroupDeletion",true)!;
        var parse=deletion.GetMethod("Parse",BindingFlags.Static|BindingFlags.NonPublic)!;
        int invalid=0;
        foreach(var bad in new[]{"", "/", "Program blocks", "程序块", ".", "A/../B"})
            try { parse.Invoke(null,new object[]{bad}); } catch(TargetInvocationException) { invalid++; }
        check(invalid==6,"EXE group deletion refuses root and invalid paths");
        var execute=deletion.GetMethod("Execute",BindingFlags.Static|BindingFlags.NonPublic)!.MakeGenericMethod(typeof(BlockGroup));
        var empty=new BlockGroup(); bool exists=true; int deletes=0; string online="Offline";
        object ExecuteDelete(bool preview,Func<BlockGroup,int>? count=null,bool remove=true) => execute.Invoke(null,new object[]{"ZZ_MCP_TEST",preview,
            new Func<string[],BlockGroup?>(_=>exists?empty:null),count??new Func<BlockGroup,int>(g=>g.Blocks.Count),
            new Func<BlockGroup,int>(g=>g.Groups.Count),new Func<string>(()=>online),
            new Action<BlockGroup>(_=>{deletes++;if(remove)exists=false;})})!;
        bool Refused(Action run) { try {run();return false;}catch(TargetInvocationException){return true;} }
        ExecuteDelete(true); check(deletes==0 && exists,"EXE group deletion preview never invokes Delete");
        empty.Blocks.Add("FC1"); check(Refused(()=>ExecuteDelete(false)) && deletes==0,"EXE refuses groups containing blocks"); empty.Blocks.Clear();
        empty.Groups.Add(new BlockGroup()); check(Refused(()=>ExecuteDelete(false)) && deletes==0,"EXE refuses groups containing subgroups"); empty.Groups.Clear();
        online="Online";check(Refused(()=>ExecuteDelete(false)) && deletes==0,"EXE refuses online group deletion");
        online="Unknown";check(Refused(()=>ExecuteDelete(false)) && deletes==0,"EXE unknown online state is not treated as offline");
        online="Offline";var deletionResult=ExecuteDelete(false).ToString();check(deletes==1 && !exists && deletionResult.Contains("true"),"EXE empty offline deletion verifies target absence");
        exists=true;check(Refused(()=>ExecuteDelete(false,null,false)) && deletes==2,"EXE does not report success if deleted group remains");
        int reads=0;check(Refused(()=>ExecuteDelete(false,_=>++reads==1?0:1)) && deletes==2,"EXE rechecks emptiness immediately before deletion");
        var deleteTool=EngineSurface.For(server).Tool("DeleteEmptyPlcBlockGroup")!;
        check((bool)deleteTool.GetParameters()[2].DefaultValue!,"EXE deletion tool defaults to dryRun=true");
        var rt = Program.FindServerType(server, "TiaMcpServer.Siemens.PlcListingRead");
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
    private static void StrictPlcChecks(Assembly server, Action<bool, string> check)
    {
        var portal = EngineSurface.For(server);
        var kernel = portal.Method("ResolvePlc", BindingFlags.Instance | BindingFlags.NonPublic);
        bool Calls(MethodInfo source, MethodInfo target, int? access = null)
        {
            var il = source.GetMethodBody()!.GetILAsByteArray()!;
            for (int i = 0; i + 4 < il.Length; i++)
                if ((il[i] == 0x28 || il[i] == 0x6f) && BitConverter.ToInt32(il, i + 1) == target.MetadataToken
                    && (access == null || (i > 0 && il[i - 1] == 0x16 + access))) return true;
            return false;
        }
        foreach (var name in new[] { "GetPlcSoftware", "ResolvePlcSoftwareFuzzy", "ExactPlcForEngineering" })
        {
            var entry = portal.Method(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            EngineSurface.CheckIl(check, Calls(entry, kernel), "EXE PLC entry uses session kernel: " + name, entry, kernel);
        }
        var objectResolver = portal.Method("ResolveObject", BindingFlags.Instance | BindingFlags.NonPublic);
        EngineSurface.CheckIl(check, Calls(objectResolver, kernel), "EXE reflection software resolution uses the PLC kernel", objectResolver, kernel);
        var objectIl = objectResolver.GetMethodBody()!.GetILAsByteArray()!;
        bool excludesPlcFallback = false;
        for (int i = 0; i + 4 < objectIl.Length; i++)
        {
            if (objectIl[i] != 0x75) continue; // isinst
            try { if (objectResolver.Module.ResolveType(BitConverter.ToInt32(objectIl, i + 1)).FullName == "Siemens.Engineering.SW.PlcSoftware") excludesPlcFallback = true; }
            catch (ArgumentException) { }
        }
        EngineSurface.CheckIl(check, excludesPlcFallback, "EXE HMI reflection fallback cannot return a PLC rejected by strict resolution", objectResolver);
        var accessType = server.GetType("TiaMcpServer.Siemens.PlcAccess", true)!;
        var session = server.GetType("TiaMcpServer.Siemens.IEngineeringSession", true)!;
        check(session.GetMethod("ResolvePlc")!.GetParameters()[1].ParameterType == accessType,
            "EXE session exposes PLC resolution with explicit access intent");
        var sessionResolver = session.GetMethod("ResolvePlc")!;
        var sessionMap = kernel.DeclaringType!.GetInterfaceMap(session);
        var sessionForwarder = sessionMap.TargetMethods[Array.IndexOf(sessionMap.InterfaceMethods, sessionResolver)];
        EngineSurface.CheckIl(check, Calls(sessionForwarder, kernel),
            "EXE session PLC resolver forwards to the same kernel", sessionForwarder, kernel);
        foreach (var name in new[] { "ImportPlcTagTable", "ImportAlarmClasses", "SetOpcUaInterfaceEnabled", "DownloadToPlc", "GoOnline" })
        {
            var writer = name == "GoOnline"
                ? portal.Method(name, new[] { typeof(string), typeof(string), typeof(string), typeof(string), typeof(string), typeof(string), typeof(string), typeof(bool) })
                : portal.Method(name);
            var resolver = writer.DeclaringType == kernel.DeclaringType ? kernel : sessionResolver;
            EngineSurface.CheckIl(check, Calls(writer, resolver, 1), "EXE write path records Write through same PLC kernel: " + name, writer, resolver);
        }
        EngineSurface.CheckIl(check, Calls(portal.Method("GetPlcSoftware"), kernel, 0), "EXE read path records Read through same PLC kernel", kernel);
        var strict = server.GetType("TiaMcpServer.Siemens.SoftwareContainerLookup", true)!
            .GetMethod("FindPlc", BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(object));
        var cpu = new Node { Alias = "CPU_1", SoftwareName = "PLC_1" }; cpu.Container = cpu;
        var station = new Node { Alias = "ET 200SP station_1", Children = new List<object> { cpu } };
        var group = new Node { Alias = "Line", IsGroup = true, Children = new List<object> { station } };
        string paths = "";
        object? Resolve(string name, object[]? roots = null, int limit = 100, Func<object, IEnumerable<object>>? children = null)
            => strict.Invoke(null, new object[] { roots ?? new object[] { group },
                children ?? new Func<object, IEnumerable<object>>(n => ((Node)n).Children),
                new Func<object, string?>(n => ((Node)n).Alias),
                new Func<object, object?>(n => ((Node)n).Container),
                new Func<object, string?>(n => ((Node)n).SoftwareName),
                new Func<object, bool>(n => ((Node)n).IsGroup), name, new Action<string>(value => paths = value), limit });
        foreach (var name in new[] { "WrongPLC", "PLC", "Prefix_PLC_1_suffix", "Line", "Wrong/PLC_1", "Line/CPU_1/garbage", ".*" })
            check(Resolve(name) == null && paths == " Available PLC paths: Line/ET 200SP station_1/CPU_1/PLC_1",
                "EXE read/write policy rejects unknown PLC with available paths: " + name);
        foreach (var name in new[] { "PLC_1", " plc_1 ", "CPU_1", "ET 200SP station_1", " line/et 200sp station_1 ", "Line/CPU_1", "Line/ET 200SP station_1/CPU_1", "Line/PLC_1", "" })
            check(ReferenceEquals(Resolve(name), cpu), "EXE read/write policy resolves exact or structural PLC alias: " + name);
        var other = new Node { Alias = "CPU_2", SoftwareName = "PLC_2" }; other.Container = other;
        var station2 = new Node { Alias = station.Alias, Children = new List<object> { other } };
        var both = new object[] { group, station2 };
        check(Resolve("", both) == null && paths.Contains("PLC_1") && paths.Contains("PLC_2"), "EXE read/write empty name retains multiple-PLC failure");
        bool ambiguous = false;
        try { Resolve(station.Alias!, both); }
        catch (TargetInvocationException ex) { ambiguous = ex.InnerException?.GetType().GetProperty("Code")?.GetValue(ex.InnerException)?.ToString() == "InvalidParams"
            && ex.InnerException.Message == "Ambiguous PLC software name 'ET 200SP station_1': ET 200SP station_1/CPU_2/PLC_2, Line/ET 200SP station_1/CPU_1/PLC_1. Use a group-qualified device/software path."; }
        check(ambiguous, "EXE read/write duplicate station alias is ambiguous and lists both candidates verbatim");
        check(ReferenceEquals(Resolve("Line/ET 200SP station_1", both), cpu), "EXE read/write group qualification disambiguates duplicate station alias");
        other.SoftwareName = "PLC_1";
        ambiguous = false;
        try { Resolve("PLC_1", both); } catch (TargetInvocationException ex) { ambiguous = ex.InnerException!.Message.Contains("Ambiguous PLC software name"); }
        check(ambiguous, "EXE read/write duplicate software names are not collapsed by name");
        check(Resolve("", Array.Empty<object>()) == null && paths == "", "EXE empty project has no sole PLC");
        bool incomplete = false;
        try { Resolve("PLC_1", null, 1); } catch (TargetInvocationException ex) { incomplete = ex.InnerException?.GetType().GetProperty("Code")?.GetValue(ex.InnerException)?.ToString() == "OpennessError"; }
        check(incomplete, "EXE strict PLC policy reports incomplete bounded scan as error");
        bool interrupted = false;
        try { Resolve("PLC_1", null, 100, n => ReferenceEquals(n, cpu) ? throw new InvalidOperationException("scan interrupted") : ((Node)n).Children); }
        catch (TargetInvocationException ex) { interrupted = ex.InnerException!.Message.Contains("scan interrupted"); }
        check(interrupted, "EXE strict PLC policy cannot return early before the scan completes");
        Resolve("WrongPLC");
        var instance = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(kernel.DeclaringType!);
        portal.Field("_plcLookupPathsSuffix", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, paths);
        check((string)portal.Method("AvailablePlcPathsSuffix").Invoke(instance, null)! == paths,
            "EXE missing PLC diagnostics use the completed strict scan's available paths");
        var guard = Program.FindServerType(server, "TiaMcpServer.Siemens.Guard").GetMethod("MatchPlcName")!;
        foreach (var name in new[] { "WrongPLC", "PLC", "Prefix_PLC_1_suffix" })
            check(guard.Invoke(null, new object[] { new[] { "PLC_1" }, name }) == null, "EXE enumeration fallback also refuses fuzzy token: " + name);
        check((string)guard.Invoke(null, new object[] { new[] { "PLC_1" }, " plc_1 " })! == "PLC_1", "EXE enumeration fallback retains case and whitespace normalization");
        check((string)guard.Invoke(null, new object[] { new[] { "PLC_1" }, "" })! == "PLC_1", "EXE enumeration fallback retains empty sole-PLC resolution");
        check(guard.Invoke(null, new object[] { new[] { "PLC_1", "PLC_2" }, "" }) == null, "EXE enumeration fallback rejects empty multiple-PLC resolution");
        var cached = portal.Method("ResolveCachedPlc", BindingFlags.Instance | BindingFlags.NonPublic);
        EngineSurface.CheckIl(check, Calls(kernel, cached), "EXE PLC kernel uses the verified resolution cache", kernel, cached);
        typeof(SoftwareLookupRuntimeTests).GetMethod(nameof(CachedPlcChecks), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(cached.GetParameters()[1].ParameterType.GetGenericArguments()[1], cached.ReturnType)
            .Invoke(null, new object[] { server, check });
    }

    private static void CachedPlcChecks<TContainer, TSoftware>(Assembly server, Action<bool, string> check)
        where TContainer : class where TSoftware : class
    {
        var portal = EngineSurface.For(server);
        var cached = portal.Method("ResolveCachedPlc", BindingFlags.Instance | BindingFlags.NonPublic);
        var strict = server.GetType("TiaMcpServer.Siemens.SoftwareContainerLookup", true)!
            .GetMethod("FindPlc", BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(TContainer));
        // Uninitialized SDK objects are identity tokens only: the delegates never access native properties.
        object Uninitialized(Type type) => System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
        var instance = Uninitialized(cached.DeclaringType!);
        var cache = new Dictionary<string, TContainer>(StringComparer.OrdinalIgnoreCase);
        var generic = new Dictionary<string, TContainer>(StringComparer.OrdinalIgnoreCase);
        portal.Field("_plcResolutionCache").SetValue(instance, cache);
        portal.Field("_softwareContainerCache").SetValue(instance, generic);
        var first = (TContainer)Uninitialized(typeof(TContainer));
        var second = (TContainer)Uninitialized(typeof(TContainer));
        var plc = (TSoftware)Uninitialized(typeof(TSoftware));
        bool firstIsPlc = true, secondIsPlc = true;
        var cpu = new Node { Alias = "CPU_1", SoftwareName = "PLC_1", Container = first };
        var roots = new List<object> { new Node { Alias = "Station", Children = new List<object> { cpu } } };
        int scans = 0, reads = 0, limit = 100;
        Func<string, TContainer?> lookup = name =>
        {
            scans++;
            return (TContainer?)strict.Invoke(null, new object[] { roots,
                new Func<object, IEnumerable<object>>(n => ((Node)n).Children),
                new Func<object, string?>(n => ((Node)n).Alias),
                new Func<object, TContainer?>(n => (TContainer?)((Node)n).Container),
                new Func<TContainer, string?>(_ => "PLC_1"),
                new Func<object, bool>(n => ((Node)n).IsGroup), name, new Action<string>(_ => { }), limit });
        };
        object? Resolve(string path) => cached.Invoke(instance, new object[] { path, lookup,
            new Func<TContainer, TSoftware?>(container => { reads++; return (ReferenceEquals(container, first) ? firstIsPlc : secondIsPlc) ? plc : null; }) });
        bool Refused(string path, string message)
        {
            try { Resolve(path); return false; }
            catch (TargetInvocationException ex) { return ex.ToString().Contains(message); }
        }

        generic["PLC_1"] = second;
        check(ReferenceEquals(Resolve("PLC_1"), plc) && scans == 1 && ReferenceEquals(cache["PLC_1"], first),
            "EXE strict PLC cache ignores an unverified generic cache entry and scans once");
        check(ReferenceEquals(Resolve("PLC_1"), plc) && scans == 1 && reads == 2,
            "EXE second PLC resolution validates Software without a second tree scan");
        check(ReferenceEquals(Resolve(" plc_1 "), plc) && scans == 1 && cache.Count == 1,
            "EXE PLC cache shares the trimmed case-insensitive path");
        check(ReferenceEquals(generic[" plc_1 "], first),
            "EXE normalized PLC cache hit publishes the verified CPU for service lookup");
        check(ReferenceEquals(Resolve(""), plc) && ReferenceEquals(Resolve("  "), plc) && scans == 2 && cache.ContainsKey(""),
            "EXE empty sole-PLC resolution is cached under the empty key");

        portal.Method("InvalidateHmiSoftwareCache", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(instance, null);
        check(cache.Count == 0 && generic.Count == 0, "EXE HMI invalidation clears both software caches");
        check(ReferenceEquals(Resolve("PLC_1"), plc) && scans == 3, "EXE PLC resolution rescans after HMI invalidation");
        var project = portal.Field("_softwareCacheProject");
        project.SetValue(instance, Uninitialized(project.FieldType.Assembly.GetType("Siemens.Engineering.Project", true)!));
        portal.Method("GetSoftwareContainer", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(instance, new object[] { "PLC_1" });
        check(cache.Count == 0 && generic.Count == 0 && project.GetValue(instance) == null,
            "EXE project change to no project clears both software caches");
        check(ReferenceEquals(Resolve("PLC_1"), plc) && scans == 4, "EXE PLC resolution rescans after project cache invalidation");

        firstIsPlc = false;
        cpu.Container = second;
        check(ReferenceEquals(Resolve("PLC_1"), plc) && scans == 5 && ReferenceEquals(cache["PLC_1"], second),
            "EXE cached container that is no longer PLC forces strict lookup and replacement");
        int before = scans;
        check(Resolve("WrongPLC") == null && Resolve("WrongPLC") == null && scans == before + 2 && !cache.ContainsKey("WrongPLC"),
            "EXE missing PLC resolutions are never cached");
        cache.Clear();
        secondIsPlc = false;
        before = scans;
        check(Resolve("PLC_1") == null && Resolve("PLC_1") == null && scans == before + 2 && cache.Count == 0,
            "EXE a lookup result without PLC Software is never cached");
        secondIsPlc = true;
        roots.Add(new Node { Alias = "CPU_2", SoftwareName = "PLC_1", Container = first });
        before = scans;
        check(Refused("PLC_1", "Ambiguous PLC software name") && Refused("PLC_1", "Ambiguous PLC software name")
            && scans == before + 2 && cache.Count == 0, "EXE ambiguous PLC resolutions are never cached");
        roots.RemoveAt(1);
        limit = 1;
        before = scans;
        check(Refused("PLC_1", "uniqueness was not established") && Refused("PLC_1", "uniqueness was not established")
            && scans == before + 2 && cache.Count == 0, "EXE incomplete PLC scans are never cached");
        limit = 100;
        check(ReferenceEquals(Resolve("PLC_1"), plc) && scans == before + 3 && cache.Count == 1,
            "EXE successful PLC lookup after failed scans can populate the cache");
    }
}
