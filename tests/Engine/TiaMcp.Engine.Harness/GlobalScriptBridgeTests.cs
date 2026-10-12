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
        var portal = EngineSurface.For(server);
        var reflection = portal.Adapter!.GetType("TiaMcp.Adapters.Reflection.OpennessReflection", true)!;
        var sessionType = portal.Adapter.GetType("TiaMcp.Adapters.Reflection.ReflectionSession", true)!;
        var session = Activator.CreateInstance(sessionType, All, null, new object[] {
            (Func<string, string, string, bool, object?>)((_, __, ___, ____) => throw new InvalidOperationException("This conversion fixture must not resolve a native target.")),
            (Func<string?>)(() => null) }, null)!;
        var native = Activator.CreateInstance(reflection, All, null, new[] { session }, null)!;
        var invoke = reflection.GetMethod("InvokeOnInstance", All)!;
        var scalar = invoke.GetParameters()[4].ParameterType.GetElementType()!;
        object Strings(string value)
        {
            var argument = Activator.CreateInstance(scalar)!;
            scalar.GetProperty("Kind")!.SetValue(argument, "string");
            scalar.GetProperty("Text")!.SetValue(argument, value);
            var array = Array.CreateInstance(scalar, 1); array.SetValue(argument, 0); return array;
        }
        object? Get(object value, string property) => value.GetType().GetProperty(property)!.GetValue(value);
        var target = new PathTarget();
        var result = invoke.Invoke(native, new object[] { target, "HmiScripts", "/Scripts", "Import", Strings(@"C:\NativeScripts"), true })!;
        check((string?)Get(result, "Message") == "OK" && (string?)Get(result, "Value") == Path.GetFullPath(@"C:\NativeScripts") && target.Calls == 1,
            "actual EXE converts JSON directory string to DirectoryInfo");
        result = invoke.Invoke(native, new object[] { target, "HmiScriptModule", "Navigation", "Export", Strings(@"C:\NativeScripts\Module.hmi.js"), true })!;
        check((string?)Get(result, "Message") == "OK" && (string?)Get(result, "Value") == Path.GetFullPath(@"C:\NativeScripts\Module.hmi.js") && target.Calls == 2,
            "actual EXE converts JSON file string to FileInfo");
        result = invoke.Invoke(native, new object[] { target, "HmiScripts", "/Scripts", "Import", Strings(@"C:\NativeScripts"), false })!;
        check((string?)Get(result, "Message") != "OK" && target.Calls == 2, "actual EXE retains allowWrite gate for native Import");
        result = invoke.Invoke(native, new object[] { target, "Force", "/Scripts", "Import", Strings(@"C:\NativeScripts"), true })!;
        check((string?)Get(result, "Message") != "OK" && target.Calls == 2, "actual EXE retains hard force deny even with allowWrite");

        var access = server.GetType("TiaMcpServer.Siemens.UnifiedScriptAccess", true)!;
        var resolve = access.GetMethod("Resolve", All)!;
        var hmi = new Hmi(); string selected = "";
        Func<string, object> resolver = path => { selected = path; return hmi; };
        var module = resolve.Invoke(null, new object[] { resolver, "/Scripts/Navigation", "HMI_RT_2", false });
        check(module is Module && selected == "HMI_RT_2", "actual EXE resolves exact module from indexed Scripts composition");
        var scripts = resolve.Invoke(null, new object[] { resolver, "HMI_RT_2:Scripts", "", true });
        check(ReferenceEquals(scripts, hmi.Scripts), "actual EXE resolves Scripts composition for native import");

        var tool = EngineSurface.For(server).Tool("SetUnifiedGlobalScript")!;
        check(tool.CustomAttributes.Any(a => a.AttributeType.Name == "McpServerToolAttribute")
            && (bool)tool.GetParameters().Single(p => p.Name == "dryRun").DefaultValue
            && tool.GetParameters().Single(p => p.Name == "previewToken").DefaultValue?.ToString() == "",
            "actual EXE registers SetUnifiedGlobalScript with safe preview defaults");
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
