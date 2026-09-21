using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Runtime
{
    // RUNTIME channel to S7-PLCSIM Advanced through its official .NET API
    // (Siemens.Simatic.Simulation.Runtime, namespace of Siemens.Simatic.Simulation.Runtime.Api.x64.dll,
    // installed with PLCSIM Advanced under "%ProgramFiles(x86)%\Common Files\Siemens\PLCSIMADV\API\<version>").
    // The assembly is NOT redistributable and is not referenced at compile time: it is located at run time
    // (explicit apiPath > PLCSIMADV_API_PATH > newest API folder) and used through reflection, the same
    // late-binding approach as Openness. Without PLCSIM Advanced every call fails with a clear
    // "API not found" message. Nothing here touches a TIA project.
    //
    // API members used (PLCSIM Advanced V4..V7):
    //   SimulationRuntimeManager: Version, RegisteredInstanceInfo, RegisterInstance(name|ECPUType,name), CreateInterface(name)
    //   IInstance: Name, ID, OperatingState, OperatingMode, CPUType, CommunicationInterface, StoragePath,
    //              PowerOn([timeout]), PowerOff([timeout]), Run([timeout]), Stop([timeout]), MemoryReset([timeout]),
    //              UnregisterInstance(), UpdateTagList([ETagListDetails, bool]), TagInfos, Read(name), Write(name, SDataValue),
    //              RunToNextSyncPoint(), Dispose()
    //   SDataValue: Type (EPrimitiveDataType) + one member per primitive type; STagInfo: Name, Area, DataType, PrimitiveDataType, Size, Offset, Bit
    public sealed class PlcSimApi
    {
        public Assembly Assembly = null!;
        public Type Manager = null!;
        public Type DataValue = null!;
        public Type PrimitiveDataType = null!;
        public string Path = "";
        public string Source = "";
        public string Version = "";
        public List<string> Probed = new List<string>();
    }

    public sealed class PlcSimTag
    {
        public string Name = "";
        public string Area = "";
        public string DataType = "";
        public string PrimitiveType = "";
        public long Offset;
        public int Bit;
        public long Size;
    }

    public static class PlcSimAdvancedChannel
    {
        private static readonly object Gate = new object();
        private static PlcSimApi? _api;
        // 2.7.41: one IInstance interface per instance name, kept open across tool calls. 2.7.38-2.7.40 created and disposed an
        // interface per call and rebuilt the tag list every time; on the maintainer's machine the engine died after 15-30 consecutive
        // reads / writes (exit 0xE0434352 from the PLCSIM Advanced API's own thread). Interfaces are re-created only when the cached one
        // no longer answers, and every API call is serialized on Gate (the API is not documented as thread-safe).
        private sealed class CachedInterface { public object Instance = null!; public bool TagListLoaded; public DateTime Opened; public int Uses; }
        private static readonly Dictionary<string, CachedInterface> Interfaces = new Dictionary<string, CachedInterface>(StringComparer.OrdinalIgnoreCase);
        public static object Acquire(PlcSimApi api, string name)
        {
            lock (Gate)
            {
                if (Interfaces.TryGetValue(name, out var cached))
                {
                    try { _ = OperatingState(cached.Instance); cached.Uses++; return cached.Instance; }
                    catch (Exception) { Forget(name); }
                }
                var created = OpenInterface(api, name);
                Interfaces[name] = new CachedInterface { Instance = created, Opened = DateTime.Now, Uses = 1 };
                return created;
            }
        }
        // Loads the tag list once per cached interface; force=true after a download / register or when a tag is not found.
        public static void EnsureTagList(PlcSimApi api, string name, object instance, bool force = false)
        {
            lock (Gate)
            {
                Interfaces.TryGetValue(name, out var cached);
                if (!force && cached != null && cached.TagListLoaded && ReferenceEquals(cached.Instance, instance)) return;
                UpdateTagList(api, instance);
                if (cached != null && ReferenceEquals(cached.Instance, instance)) cached.TagListLoaded = true;
            }
        }
        public static void Forget(string name)
        {
            lock (Gate)
            {
                if (!Interfaces.TryGetValue(name, out var cached)) return;
                Interfaces.Remove(name);
                Dispose(cached.Instance);
            }
        }
        public static JsonObject CacheState(string name)
        {
            lock (Gate)
            {
                return Interfaces.TryGetValue(name, out var cached)
                    ? new JsonObject { ["cachedInterface"] = true, ["openedAt"] = cached.Opened, ["uses"] = cached.Uses, ["tagListLoaded"] = cached.TagListLoaded }
                    : new JsonObject { ["cachedInterface"] = false };
            }
        }
        // Read / Write with one tag-list refresh when PLCSIM reports the tag as unknown (new download since the list was loaded).
        public static (string type, object? value) ReadWithRefresh(PlcSimApi api, string name, object instance, string tagName)
        {
            try { return Read(api, instance, tagName); }
            catch (InvalidOperationException ex) when (LooksLikeUnknownTag(ex)) { EnsureTagList(api, name, instance, true); return Read(api, instance, tagName); }
        }
        public static string WriteWithRefresh(PlcSimApi api, string name, object instance, string tagName, JsonNode? value)
        {
            try { return Write(api, instance, tagName, value); }
            catch (InvalidOperationException ex) when (LooksLikeUnknownTag(ex)) { EnsureTagList(api, name, instance, true); return Write(api, instance, tagName, value); }
        }
        private static bool LooksLikeUnknownTag(Exception ex)
        {
            var m = ex.Message;
            return m.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("NotFound", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("does not exist", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("DoesNotExist", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.IgnoreCase;

        // ------------------------------------------------------------------ loading

        public static List<string> ProbePaths(string? explicitPath)
        {
            var roots = new List<KeyValuePair<string, IEnumerable<string>>>();
            foreach (var pf in new[] { Environment.GetEnvironmentVariable("ProgramFiles(x86)"), Environment.GetEnvironmentVariable("ProgramFiles") })
            {
                if (string.IsNullOrWhiteSpace(pf)) continue;
                var root = System.IO.Path.Combine(pf, "Common Files", "Siemens", "PLCSIMADV", "API");
                IEnumerable<string> folders;
                try { folders = Directory.Exists(root) ? Directory.GetDirectories(root) : Array.Empty<string>(); }
                catch (Exception) { folders = Array.Empty<string>(); }
                roots.Add(new KeyValuePair<string, IEnumerable<string>>(root, folders));
            }
            return PlcSimAdvancedLogic.CandidateApiPaths(explicitPath, Environment.GetEnvironmentVariable(PlcSimAdvancedLogic.ApiEnvironmentVariable), roots);
        }

        public static PlcSimApi Load(string? explicitPath)
        {
            lock (Gate)
            {
                if (_api != null && (string.IsNullOrWhiteSpace(explicitPath) || string.Equals(_api.Path, explicitPath, StringComparison.OrdinalIgnoreCase))) return _api;
                var probed = ProbePaths(explicitPath);
                var hit = probed.FirstOrDefault(File.Exists);
                Assembly? asm = null;
                string source;
                if (hit != null)
                {
                    asm = Assembly.LoadFrom(hit);
                    var fromArgument = PlcSimAdvancedLogic.CandidateApiPaths(explicitPath, null, Array.Empty<KeyValuePair<string, IEnumerable<string>>>());
                    var fromEnvironment = PlcSimAdvancedLogic.CandidateApiPaths(null, Environment.GetEnvironmentVariable(PlcSimAdvancedLogic.ApiEnvironmentVariable), Array.Empty<KeyValuePair<string, IEnumerable<string>>>());
                    source = fromArgument.Contains(hit, StringComparer.OrdinalIgnoreCase) ? "apiPath"
                        : fromEnvironment.Contains(hit, StringComparer.OrdinalIgnoreCase) ? PlcSimAdvancedLogic.ApiEnvironmentVariable
                        : "installed API folder";
                }
                else
                {
                    try { asm = Assembly.Load("Siemens.Simatic.Simulation.Runtime.Api.x64"); source = "GAC"; hit = asm.Location; }
                    catch (Exception) { source = ""; }
                }
                if (asm == null)
                    throw new InvalidOperationException("PLCSIM Advanced API (" + PlcSimAdvancedLogic.ApiFileName + ") not found. Install S7-PLCSIM Advanced on this machine, or set apiPath / " + PlcSimAdvancedLogic.ApiEnvironmentVariable + ". Probed: " + string.Join("; ", probed));
                var manager = asm.GetType("Siemens.Simatic.Simulation.Runtime.SimulationRuntimeManager", false)
                              ?? throw new InvalidOperationException("Assembly at " + hit + " has no SimulationRuntimeManager type; is it the PLCSIM Advanced API?");
                var api = new PlcSimApi
                {
                    Assembly = asm, Manager = manager, Path = hit ?? "", Source = source, Probed = probed,
                    DataValue = asm.GetType("Siemens.Simatic.Simulation.Runtime.SDataValue", false) ?? throw new InvalidOperationException("SDataValue type missing in PLCSIM Advanced API."),
                    PrimitiveDataType = asm.GetType("Siemens.Simatic.Simulation.Runtime.EPrimitiveDataType", false) ?? throw new InvalidOperationException("EPrimitiveDataType missing in PLCSIM Advanced API.")
                };
                try { api.Version = Convert.ToString(GetMember(manager, null, "Version")) ?? asm.GetName().Version?.ToString() ?? ""; }
                catch (Exception) { api.Version = asm.GetName().Version?.ToString() ?? ""; }
                _api = api;
                return api;
            }
        }

        public static JsonObject Describe(PlcSimApi api)
        {
            var o = new JsonObject
            {
                ["apiPath"] = api.Path, ["apiSource"] = api.Source, ["apiVersion"] = api.Version, ["assemblyVersion"] = api.Assembly.GetName().Version?.ToString()
            };
            try { var mode = NetworkMode(api); if (mode != null) o["networkMode"] = mode; }
            catch (Exception ex) { o["networkModeError"] = ex.Message; }
            return o;
        }

        // 2.7.51: SimulationRuntimeManager.NetworkMode (ENetworkMode) is the global interface choice of PLCSIM Advanced 6+; null when the
        // installed API predates it. The setter is refused by the API while an instance is running (InstanceAlreadyRunning).
        public static string? NetworkMode(PlcSimApi api)
        {
            var prop = api.Manager.GetProperty("NetworkMode", Any);
            if (prop == null || !prop.CanRead) return null;
            try { lock (Gate) return Convert.ToString(prop.GetValue(null)); }
            catch (TargetInvocationException tie) when (tie.InnerException != null) { throw tie.InnerException; }
        }

        public static string SetNetworkMode(PlcSimApi api, string networkMode)
        {
            var prop = api.Manager.GetProperty("NetworkMode", Any);
            if (prop == null || !prop.CanWrite || prop.SetMethod == null) throw NotSupported("SimulationRuntimeManager.NetworkMode setter");
            var enumType = api.Assembly.GetType("Siemens.Simatic.Simulation.Runtime.ENetworkMode", false) ?? throw NotSupported("ENetworkMode");
            object value;
            try { value = Enum.Parse(enumType, networkMode.Trim(), true); }
            catch (ArgumentException) { throw new ArgumentException("networkMode '" + networkMode + "' unknown; valid: " + string.Join(", ", Enum.GetNames(enumType))); }
            try { lock (Gate) prop.SetValue(null, value); }
            catch (TargetInvocationException tie) when (tie.InnerException != null) { throw tie.InnerException; }
            return NetworkMode(api) ?? "";
        }

        // Static members of SimulationRuntimeManager whose name contains the filter (the manager-side counterpart of DescribeMembers).
        public static string DescribeManagerMembers(PlcSimApi api, string contains)
        {
            var seen = new List<string>();
            foreach (var p in api.Manager.GetProperties(Any))
                if (p.Name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0) seen.Add(api.Manager.Name + "." + p.Name + " {get" + (p.CanWrite ? ";set" : "") + "}");
            foreach (var m in api.Manager.GetMethods(Any))
                if (m.Name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0 && !m.IsSpecialName) seen.Add(api.Manager.Name + "." + m.Name + "(" + string.Join(",", m.GetParameters().Select(x => x.ParameterType.Name)) + ")");
            return seen.Count == 0 ? "none" : string.Join("; ", seen.Distinct().Take(40));
        }

        // ------------------------------------------------------------------ instances

        public static List<(string name, int id)> RegisteredInstances(PlcSimApi api)
        {
            var list = new List<(string, int)>();
            var infos = GetMember(api.Manager, null, "RegisteredInstanceInfo") as Array;
            if (infos == null) return list;
            foreach (var info in infos)
            {
                if (info == null) continue;
                var name = Convert.ToString(GetMember(info.GetType(), info, "Name")) ?? "";
                var id = Convert.ToInt32(GetMember(info.GetType(), info, "ID") ?? 0);
                list.Add((name, id));
            }
            return list;
        }

        public static object OpenInterface(PlcSimApi api, string name)
        {
            var m = api.Manager.GetMethod("CreateInterface", new[] { typeof(string) }) ?? throw NotSupported("SimulationRuntimeManager.CreateInterface(string)");
            return Invoke(m, null, name) ?? throw new InvalidOperationException("CreateInterface returned null for '" + name + "'.");
        }

        public static object Register(PlcSimApi api, string name, string? cpuType)
        {
            if (string.IsNullOrWhiteSpace(cpuType))
            {
                var m = api.Manager.GetMethod("RegisterInstance", new[] { typeof(string) }) ?? throw NotSupported("SimulationRuntimeManager.RegisterInstance(string)");
                return Invoke(m, null, name) ?? throw new InvalidOperationException("RegisterInstance returned null.");
            }
            var enumType = api.Assembly.GetType("Siemens.Simatic.Simulation.Runtime.ECPUType", false) ?? throw NotSupported("ECPUType");
            object cpu;
            try { cpu = Enum.Parse(enumType, cpuType!.Trim(), true); }
            catch (ArgumentException) { throw new ArgumentException("cpuType '" + cpuType + "' unknown; valid: " + string.Join(", ", Enum.GetNames(enumType))); }
            var typed = api.Manager.GetMethod("RegisterInstance", new[] { enumType, typeof(string) }) ?? throw NotSupported("SimulationRuntimeManager.RegisterInstance(ECPUType,string)");
            return Invoke(typed, null, cpu, name) ?? throw new InvalidOperationException("RegisterInstance returned null.");
        }

        public static JsonObject InstanceState(object instance)
        {
            var t = instance.GetType();
            var o = new JsonObject();
            foreach (var prop in new[] { "Name", "ID", "OperatingState", "OperatingMode", "CPUType", "CommunicationInterface", "StoragePath", "ControllerName", "ControllerShortDesignation", "ControllerIP" })
            {
                try
                {
                    var v = GetMember(t, instance, prop);
                    if (v == null) continue;
                    if (v is Array arr) o[Camel(prop)] = new JsonArray(arr.Cast<object?>().Select(x => (JsonNode?)Convert.ToString(x)).ToArray());
                    else if (v is int i) o[Camel(prop)] = i;
                    else o[Camel(prop)] = Convert.ToString(v);
                }
                catch (Exception) { /* optional members differ between API versions */ }
            }
            return o;
        }

        public static string OperatingState(object instance) => Convert.ToString(GetMember(instance.GetType(), instance, "OperatingState")) ?? "";

        public static void Lifecycle(object instance, string action, int timeoutMs)
        {
            var method = action switch
            {
                "powerOn" => "PowerOn", "powerOff" => "PowerOff", "run" => "Run", "stop" => "Stop", "memoryReset" => "MemoryReset", "unregister" => "UnregisterInstance",
                _ => throw new ArgumentException("Unsupported lifecycle action '" + action + "'.")
            };
            var t = instance.GetType();
            var withTimeout = t.GetMethod(method, new[] { typeof(uint) });
            if (withTimeout != null && method != "UnregisterInstance") { Invoke(withTimeout, instance, (uint)Math.Max(1, timeoutMs)); return; }
            var plain = t.GetMethod(method, Type.EmptyTypes) ?? throw NotSupported("IInstance." + method + "()");
            Invoke(plain, instance);
        }

        // 2.7.48: ECommunicationInterface (None / Softbus / TCPIP). TCPIP makes the instance reachable through the "Siemens PLCSIM
        // Virtual Ethernet Adapter" PG/PC interface at its IP suite (API default 192.168.0.1/24 on X1), which is what a TIA download
        // needs; must be set before PowerOn.
        // 2.7.51 (real machine, PLCSIM Advanced 8.0): the per-instance property is read-only on the class AND on IInstance, and no
        // Set...() method exists (members seen: CommunicationInterface {get} only); the manual says "To set the network mode, refer to
        // the NetworkMode section" - the choice is the global SimulationRuntimeManager.NetworkMode. So: instance setter when the API
        // still has one (PLCSIM Advanced <= 5), otherwise the manager network mode (mapped by PlcSimAdvancedLogic.NetworkModeFor).
        public static string SetCommunicationInterface(PlcSimApi api, object instance, string communicationInterface, JsonObject? detail = null)
        {
            var enumType = api.Assembly.GetType("Siemens.Simatic.Simulation.Runtime.ECommunicationInterface", false) ?? throw NotSupported("ECommunicationInterface");
            object? value = null;
            try { value = Enum.Parse(enumType, communicationInterface.Trim(), true); }
            catch (ArgumentException) { /* not an ECommunicationInterface name - may still be an ENetworkMode name below */ }
            if (value != null && (WritableProperty(instance, "CommunicationInterface") != null || FindSetter(instance, "CommunicationInterface", enumType) != null))
            {
                SetThroughPropertyOrMethod(instance, "CommunicationInterface", value, enumType);
                if (detail != null) detail["route"] = "IInstance.CommunicationInterface";
                return Convert.ToString(GetMember(instance.GetType(), instance, "CommunicationInterface")) ?? "";
            }
            var before = NetworkMode(api);
            if (before == null)
                throw NotSupported("IInstance.CommunicationInterface setter and SimulationRuntimeManager.NetworkMode (members seen: " + DescribeMembers(instance, "CommunicationInterface") + "; manager: " + DescribeManagerMembers(api, "NetworkMode") + ")");
            var wanted = PlcSimAdvancedLogic.NetworkModeFor(communicationInterface, before);
            if (detail != null) { detail["route"] = "SimulationRuntimeManager.NetworkMode"; detail["networkModeBefore"] = before; detail["networkModeRequested"] = wanted; }
            var after = wanted.Equals(before, StringComparison.OrdinalIgnoreCase) ? before : SetNetworkMode(api, wanted);
            if (detail != null) detail["networkModeAfter"] = after;
            return Convert.ToString(GetMember(instance.GetType(), instance, "CommunicationInterface")) ?? "";
        }

        // Returns the EOperatingMode name actually applied (2.7.54: the first candidate the installed API defines).
        public static string SetOperatingMode(PlcSimApi api, object instance, string mode)
        {
            var enumType = api.Assembly.GetType("Siemens.Simatic.Simulation.Runtime.EOperatingMode", false) ?? throw NotSupported("EOperatingMode");
            var candidates = PlcSimAdvancedLogic.OperatingModeCandidates(mode);
            var name = candidates.FirstOrDefault(c => Enum.GetNames(enumType).Any(n => n.Equals(c, StringComparison.OrdinalIgnoreCase)))
                ?? throw new ArgumentException("operating mode '" + mode + "' is not defined by this PLCSIM Advanced API (tried " + string.Join(" / ", candidates) + "); valid: " + string.Join(", ", Enum.GetNames(enumType)));
            var value = Enum.Parse(enumType, name, true);
            SetThroughPropertyOrMethod(instance, "OperatingMode", value, enumType);
            return Convert.ToString(GetMember(instance.GetType(), instance, "OperatingMode")) ?? name;
        }

        // 2.7.50 (real machine, PLCSIM Advanced 8.0): neither the instance class nor any interface it implements carries a writable
        // CommunicationInterface / OperatingMode property. Try the property, then Set<Name>(value) / set_<Name>(value) on the class and
        // its interfaces; when nothing fits, the refusal lists every member whose name contains <Name>. (2.7.51: for CommunicationInterface
        // the answer turned out to be the manager-level NetworkMode, see SetCommunicationInterface; OperatingMode still goes through here.)
        private static void SetThroughPropertyOrMethod(object instance, string name, object value, Type valueType)
        {
            var prop = WritableProperty(instance, name);
            if (prop != null) { lock (Gate) prop.SetValue(instance, value); return; }
            var method = FindSetter(instance, name, valueType);
            if (method != null) { Invoke(method, instance, value); return; }
            throw NotSupported("IInstance." + name + " setter (members seen: " + DescribeMembers(instance, name) + ")");
        }

        private static MethodInfo? FindSetter(object instance, string name, Type valueType)
        {
            var candidates = new[] { "Set" + name, "set_" + name };
            foreach (var type in new[] { instance.GetType() }.Concat(instance.GetType().GetInterfaces()))
                foreach (var method in type.GetMethods(Any))
                {
                    if (!candidates.Contains(method.Name, StringComparer.OrdinalIgnoreCase)) continue;
                    var ps = method.GetParameters();
                    if (ps.Length == 1 && (ps[0].ParameterType == valueType || ps[0].ParameterType.IsAssignableFrom(valueType))) return method;
                }
            return null;
        }

        public static string DescribeMembers(object instance, string contains)
        {
            var seen = new List<string>();
            foreach (var type in new[] { instance.GetType() }.Concat(instance.GetType().GetInterfaces()))
            {
                foreach (var p in type.GetProperties(Any))
                    if (p.Name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0) seen.Add(type.Name + "." + p.Name + " {get" + (p.CanWrite ? ";set" : "") + "}");
                foreach (var m in type.GetMethods(Any))
                    if (m.Name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0 && !m.IsSpecialName) seen.Add(type.Name + "." + m.Name + "(" + string.Join(",", m.GetParameters().Select(x => x.ParameterType.Name)) + ")");
            }
            return seen.Count == 0 ? "none" : string.Join("; ", seen.Distinct().Take(40));
        }

        // 2.7.49 (real machine): the runtime's instance class exposes CommunicationInterface / OperatingMode as read-only public
        // properties and implements the IInstance setters explicitly, so PropertyInfo.SetValue on the concrete type threw
        // "Property set method not found" (localized) - register with communicationInterface failed AFTER RegisterInstance had
        // already created the instance. Look for a setter on the concrete type first, then on every interface the instance implements.
        private static PropertyInfo? WritableProperty(object instance, string name)
        {
            var direct = instance.GetType().GetProperty(name, Any);
            if (direct != null && direct.CanWrite && direct.SetMethod != null) return direct;
            foreach (var itf in instance.GetType().GetInterfaces())
            {
                var p = itf.GetProperty(name, Any);
                if (p != null && p.CanWrite && p.SetMethod != null) return p;
            }
            return null;
        }

        public static void RunToNextSyncPoint(object instance)
        {
            var m = instance.GetType().GetMethod("RunToNextSyncPoint", Type.EmptyTypes) ?? throw NotSupported("IInstance.RunToNextSyncPoint()");
            Invoke(m, instance);
        }

        // 2.7.55 (real machine + manual "SingleStep operating modes"): RunToNextSyncPoint() only cancels the freeze state and returns; the
        // instance then runs until the next synchronization point and reports OperatingState Freeze again. Five back-to-back calls advanced
        // ONE cycle (303 -> 304) because the later calls hit an instance that was still running. Each stepped cycle therefore waits for
        // Freeze before the trigger and again after it; the wait times are returned so a scenario can show them.
        public static (int steps, long waitedMs, string finalState, string? failure) StepCycles(object instance, int count, int waitMs, int pollMs)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var state = OperatingState(instance);
            if (state != "Freeze" && !WaitForState(instance, "Freeze", waitMs, pollMs, out state))
                return (0, watch.ElapsedMilliseconds, state, "instance did not reach the first sync point (Freeze) within " + waitMs + " ms; state " + state);
            for (var i = 0; i < count; i++)
            {
                RunToNextSyncPoint(instance);
                if (!WaitForState(instance, "Freeze", waitMs, pollMs, out state))
                    return (i, watch.ElapsedMilliseconds, state, "sync point " + (i + 1) + " of " + count + " not reached within " + waitMs + " ms; state " + state);
            }
            return (count, watch.ElapsedMilliseconds, state, null);
        }

        private static bool WaitForState(object instance, string wanted, int waitMs, int pollMs, out string last)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            last = OperatingState(instance);
            while (last != wanted)
            {
                if (watch.ElapsedMilliseconds > waitMs) return false;
                System.Threading.Thread.Sleep(Math.Max(1, pollMs));
                last = OperatingState(instance);
            }
            return true;
        }

        public static void Dispose(object? instance)
        {
            if (instance == null) return;
            try { (instance as IDisposable)?.Dispose(); } catch (Exception) { /* interface release is best effort */ }
        }

        // ------------------------------------------------------------------ tags

        public static void UpdateTagList(PlcSimApi api, object instance)
        {
            var t = instance.GetType();
            var plain = t.GetMethod("UpdateTagList", Type.EmptyTypes);
            if (plain != null) { Invoke(plain, instance); return; }
            var detailsType = api.Assembly.GetType("Siemens.Simatic.Simulation.Runtime.ETagListDetails", false) ?? throw NotSupported("IInstance.UpdateTagList()");
            var two = t.GetMethod("UpdateTagList", new[] { detailsType, typeof(bool) }) ?? throw NotSupported("IInstance.UpdateTagList(ETagListDetails,bool)");
            var all = Enum.GetNames(detailsType).FirstOrDefault(n => n.IndexOf("IOMCTDB", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("All", StringComparison.OrdinalIgnoreCase) >= 0)
                      ?? Enum.GetNames(detailsType).Last();
            Invoke(two, instance, Enum.Parse(detailsType, all), false);
        }

        public static List<PlcSimTag> Tags(object instance)
        {
            var infos = GetMember(instance.GetType(), instance, "TagInfos") as Array ?? Array.Empty<object>();
            var list = new List<PlcSimTag>();
            foreach (var info in infos)
            {
                if (info == null) continue;
                var it = info.GetType();
                list.Add(new PlcSimTag
                {
                    Name = Convert.ToString(GetMember(it, info, "Name")) ?? "",
                    Area = Convert.ToString(GetMember(it, info, "Area")) ?? "",
                    DataType = Convert.ToString(GetMember(it, info, "DataType")) ?? "",
                    PrimitiveType = Convert.ToString(GetMember(it, info, "PrimitiveDataType")) ?? "",
                    Offset = ToLong(GetMember(it, info, "Offset")),
                    Bit = (int)ToLong(GetMember(it, info, "Bit")),
                    Size = ToLong(GetMember(it, info, "Size"))
                });
            }
            return list;
        }

        public static (string type, object? value) Read(PlcSimApi api, object instance, string tagName)
        {
            var m = instance.GetType().GetMethod("Read", new[] { typeof(string) }) ?? throw NotSupported("IInstance.Read(string)");
            var dv = Invoke(m, instance, tagName) ?? throw new InvalidOperationException("Read returned null for '" + tagName + "'.");
            var type = Convert.ToString(GetMember(api.DataValue, dv, "Type")) ?? "";
            if (!PlcSimAdvancedLogic.PrimitiveTypes.Contains(type)) return (type, null);
            return (type, GetMember(api.DataValue, dv, type));
        }

        public static string Write(PlcSimApi api, object instance, string tagName, JsonNode? value)
        {
            var (type, _) = Read(api, instance, tagName);
            if (!PlcSimAdvancedLogic.PrimitiveTypes.Contains(type)) throw new ArgumentException("Tag '" + tagName + "' has type " + type + "; only primitive elements can be written (address the element, e.g. \"DB\".arr[1]).");
            var converted = PlcSimAdvancedLogic.ConvertValue(value, type);
            var boxed = Activator.CreateInstance(api.DataValue) ?? throw NotSupported("SDataValue()");
            SetMember(api.DataValue, boxed, "Type", Enum.Parse(api.PrimitiveDataType, type));
            SetMember(api.DataValue, boxed, type, converted);
            var m = instance.GetType().GetMethod("Write", new[] { typeof(string), api.DataValue }) ?? throw NotSupported("IInstance.Write(string,SDataValue)");
            Invoke(m, instance, tagName, boxed);
            return type;
        }

        // ------------------------------------------------------------------ reflection helpers

        private static object? GetMember(Type type, object? target, string name)
        {
            var prop = type.GetProperty(name, Any);
            if (prop != null) { lock (Gate) return prop.GetValue(target); }
            var field = type.GetField(name, Any);
            if (field != null) { lock (Gate) return field.GetValue(target); }
            throw NotSupported(type.Name + "." + name);
        }

        private static void SetMember(Type type, object boxed, string name, object value)
        {
            var prop = type.GetProperty(name, Any);
            if (prop != null) { prop.SetValue(boxed, Coerce(value, prop.PropertyType)); return; }
            var field = type.GetField(name, Any);
            if (field != null) { field.SetValue(boxed, Coerce(value, field.FieldType)); return; }
            throw NotSupported(type.Name + "." + name);
        }

        private static object Coerce(object value, Type target)
        {
            if (target.IsInstanceOfType(value)) return value;
            if (target.IsEnum) return Enum.ToObject(target, value);
            if (target == typeof(char) && value is sbyte sb) return (char)sb;
            if (target == typeof(sbyte) && value is char c) return (sbyte)c;
            return Convert.ChangeType(value, target, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static object? Invoke(MethodInfo m, object? target, params object?[] args)
        {
            try { lock (Gate) return m.Invoke(target, args); }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                var inner = tie.InnerException;
                throw new InvalidOperationException("PLCSIM Advanced " + m.Name + " failed: " + inner.GetType().Name + ": " + inner.Message, inner);
            }
        }

        private static long ToLong(object? v) => v == null ? 0 : Convert.ToInt64(v);

        private static string Camel(string name) => name.Length == 0 ? name : (name == "ID" ? "id" : char.ToLowerInvariant(name[0]) + name.Substring(1));

        private static NotSupportedException NotSupported(string member) => new NotSupportedException("PLCSIM Advanced API member not available in the installed version: " + member);
    }
}
