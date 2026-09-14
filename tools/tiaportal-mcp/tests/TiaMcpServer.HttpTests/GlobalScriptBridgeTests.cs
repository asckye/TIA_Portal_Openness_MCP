using System;
using System.IO;
using System.Linq;
using System.Reflection;

internal static class GlobalScriptBridgeTests
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    public sealed class PathTarget
    {
        public int Calls;
        public string Import(DirectoryInfo directory) { Calls++; return directory.FullName; }
        public string Export(FileInfo file) { Calls++; return file.FullName; }
    }
    public sealed class Hmi
    {
        public Modules Scripts { get; } = new Modules();
    }
    public sealed class Modules
    {
        public int Count => 1;
        public Module this[int index] => index == 0 ? new Module() : throw new IndexOutOfRangeException();
    }
    public sealed class Module { public string Name => "Navigation"; }

    internal static void Run(Assembly server, Action<bool, string> check)
    {
        var portal = server.GetType("TiaMcpServer.Siemens.Portal", true)!;
        var invoke = portal.GetMethod("InvokeOnInstance", All)!;
        var jsonAssembly = invoke.GetParameters()[4].ParameterType.Assembly;
        var jsonNode = jsonAssembly.GetType("System.Text.Json.Nodes.JsonNode", true)!;
        var parse = jsonNode.GetMethods(All).Single(m => m.Name == "Parse" && m.GetParameters()[0].ParameterType == typeof(string));
        object Json(string value) => parse.Invoke(null, new object?[] { value, null, Activator.CreateInstance(parse.GetParameters()[2].ParameterType) })!;
        object? Get(object value, string property) => value.GetType().GetProperty(property)!.GetValue(value);
        var target = new PathTarget();
        var result = invoke.Invoke(null, new object[] { target, "HmiScripts", "/Scripts", "Import", Json("[\"C:\\\\NativeScripts\"]"), true })!;
        check((string?)Get(result, "Message") == "OK" && (string?)Get(result, "Value") == Path.GetFullPath(@"C:\NativeScripts") && target.Calls == 1,
            "actual EXE converts JSON directory string to DirectoryInfo");
        result = invoke.Invoke(null, new object[] { target, "HmiScriptModule", "Navigation", "Export", Json("[\"C:\\\\NativeScripts\\\\Module.hmi.js\"]"), true })!;
        check((string?)Get(result, "Message") == "OK" && (string?)Get(result, "Value") == Path.GetFullPath(@"C:\NativeScripts\Module.hmi.js") && target.Calls == 2,
            "actual EXE converts JSON file string to FileInfo");
        result = invoke.Invoke(null, new object[] { target, "HmiScripts", "/Scripts", "Import", Json("[\"C:\\\\NativeScripts\"]"), false })!;
        check((string?)Get(result, "Message") != "OK" && target.Calls == 2, "actual EXE retains allowWrite gate for native Import");
        result = invoke.Invoke(null, new object[] { target, "Force", "/Scripts", "Import", Json("[\"C:\\\\NativeScripts\"]"), true })!;
        check((string?)Get(result, "Message") != "OK" && target.Calls == 2, "actual EXE retains hard force deny even with allowWrite");

        var access = server.GetType("TiaMcpServer.Siemens.UnifiedScriptAccess", true)!;
        var resolve = access.GetMethod("Resolve", All)!;
        var hmi = new Hmi(); string selected = "";
        Func<string, object> resolver = path => { selected = path; return hmi; };
        var module = resolve.Invoke(null, new object[] { resolver, "/Scripts/Navigation", "HMI_RT_2", false });
        check(module is Module && selected == "HMI_RT_2", "actual EXE resolves exact module from indexed Scripts composition");
        var scripts = resolve.Invoke(null, new object[] { resolver, "HMI_RT_2:Scripts", "", true });
        check(ReferenceEquals(scripts, hmi.Scripts), "actual EXE resolves Scripts composition for native import");

        var tool = server.GetType("TiaMcpServer.ModelContextProtocol.McpServer", true)!.GetMethod("UpdateUnifiedGlobalScript", All)!;
        check(tool.CustomAttributes.Any(a => a.AttributeType.Name == "McpServerToolAttribute")
            && (bool)tool.GetParameters().Single(p => p.Name == "dryRun").DefaultValue
            && tool.GetParameters().Single(p => p.Name == "expectedToken").DefaultValue?.ToString() == "",
            "actual EXE registers UpdateUnifiedGlobalScript with safe preview defaults");
        // Inspect the actual referenced Siemens DLL, never start/attach TIA.
        var unifiedReference = server.GetReferencedAssemblies().SingleOrDefault(a => a.Name == "Siemens.Engineering.WinCCUnified");
        if (unifiedReference == null)
        {
            Console.WriteLine("CAPABILITY NOT VERIFIED: this build does not reference WinCCUnified; native global-script support on this TIA version is not established by the seven bridge checks.");
            return;
        }
        var unified = Assembly.Load(unifiedReference);
        var composition = unified.GetType("Siemens.Engineering.HmiUnified.Scripts.HmiScriptModuleComposition", true)!;
        var officialModule = unified.GetType("Siemens.Engineering.HmiUnified.Scripts.HmiScriptModule", true)!;
        check(composition.GetMethod("Import", new[] { typeof(DirectoryInfo) })?.ReturnType == typeof(bool)
            && officialModule.GetMethod("Export", new[] { typeof(DirectoryInfo), typeof(string) }) != null,
            "referenced Siemens API exposes the exact native Import/Export signatures used by editor");
    }
}
