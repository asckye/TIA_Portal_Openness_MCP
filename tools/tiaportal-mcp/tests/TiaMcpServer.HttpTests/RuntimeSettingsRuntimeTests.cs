using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;

internal static class RuntimeSettingsRuntimeTests
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    public enum Resolution { SR_800X480, SR_1920X1080 }
    public sealed class Settings
    {
        private string start = "Old";
        public int Writes;
        public bool Ignore;
        public Exception? Error;
        public string StartScreen { get => start; set { Writes++; if (Error != null) throw Error; if (!Ignore) start = value; } }
        public Resolution ScreenResolution { get; set; }
        public object Parent => throw new Exception("Parent read");
    }
    public sealed class Screen { public string Name { get; set; } = "New"; }
    public sealed class Hmi { public Settings RuntimeSettings { get; } = new Settings(); public List<Screen> Screens { get; } = new List<Screen> { new Screen() }; public List<object> ScreenGroups { get; } = new List<object>(); }
    internal static void Run(Assembly server, Action<bool, string> check)
    {
        var helper = server.GetType("TiaMcpServer.Siemens.UnifiedRuntimeSettingsAccess", true)!;
        var update = helper.GetMethod("Update", All)!; var read = helper.GetMethod("Read", All)!;
        var changes = helper.GetMethod("Changes", All)!;
        var metaType = update.GetParameters()[6].ParameterType; var traceType = update.GetParameters()[7].ParameterType;
        var json = new JavaScriptSerializer(); var hmi = new Hmi();
        Dictionary<string, object> Parse(object value) => (Dictionary<string, object>)json.DeserializeObject(value.ToString());
        Dictionary<string, object> Update(string data, bool dry = true, string token = "")
        {
            var meta = Activator.CreateInstance(metaType, new object?[] { null })!;
            update.Invoke(null, new object[] { hmi, "P", "HMI", changes.Invoke(null, new object[] { data })!, dry, token, meta, Activator.CreateInstance(traceType, true)! });
            return Parse(meta);
        }
        bool Reject(string data) { try { Update(data); return false; } catch (TargetInvocationException) { return true; } }
        var mcp = server.GetType("TiaMcpServer.ModelContextProtocol.McpServer", true)!;
        check(new[] { "ReadUnifiedRuntimeSettings", "UpdateUnifiedRuntimeSettings" }.All(n => mcp.GetMethod(n, All)?.CustomAttributes.Any(a => a.AttributeType.Name == "McpServerToolAttribute") == true)
            && (bool)mcp.GetMethod("UpdateUnifiedRuntimeSettings", All)!.GetParameters().Single(p => p.Name == "dryRun").DefaultValue,
            "actual EXE registers runtime setting tools with preview default");
        var readMeta = Activator.CreateInstance(metaType, new object?[] { null })!;
        read.Invoke(null, new object[] { hmi, new[] { "StartScreen", "ScreenResolution" }, readMeta, Activator.CreateInstance(traceType, true)! });
        check((bool)Parse(readMeta)["dataComplete"] && hmi.RuntimeSettings.Writes == 0, "actual EXE reads startup setting without writing");
        const string data = "{\"StartScreen\":\"/New\",\"ScreenResolution\":\"SR_1920X1080\"}";
        var preview = Update(data);
        check((string)preview["status"] == "Preview" && hmi.RuntimeSettings.Writes == 0 && (string)((Dictionary<string, object>)preview["proposed"])["StartScreen"] == "New", "actual EXE validates target path and previews API name");
        var applied = Update(data, false, (string)preview["token"]);
        check((bool)applied["verificationSuccess"] && hmi.RuntimeSettings.StartScreen == "New" && hmi.RuntimeSettings.Writes == 1, "actual EXE writes and verifies startup settings");
        check(Reject("{\"StartScreen\":\"/Missing\"}") && hmi.RuntimeSettings.Writes == 1, "actual EXE rejects absent target before writing");
        check(Reject("{\"ScreenResolution\":1}") && hmi.RuntimeSettings.Writes == 1, "actual EXE rejects numeric enum guesses");
        hmi.RuntimeSettings.StartScreen = "Old"; var mismatchPreview = Update(data); hmi.RuntimeSettings.Ignore = true;
        var mismatch = Update(data, false, (string)mismatchPreview["token"]);
        check(!(bool)mismatch["operationSuccess"] && (string)mismatch["status"] == "ReadbackMismatch", "actual EXE does not report ineffective setter as success");
        hmi.RuntimeSettings.Ignore = false; var failurePreview = Update(data); hmi.RuntimeSettings.Error = new ObjectDisposedException("settings");
        bool fatal = false;
        try { Update(data, false, (string)failurePreview["token"]); }
        catch (TargetInvocationException ex) { fatal = ex.ToString().Contains("ObjectDisposedException"); }
        check(fatal, "actual EXE propagates fatal write failure without retry");
        var reference = server.GetReferencedAssemblies().SingleOrDefault(a => a.Name == "Siemens.Engineering.WinCCUnified");
        if (reference == null) { Console.WriteLine("V20 Unified API capability not established by these adapter checks."); return; }
        var type = Assembly.Load(reference).GetType("Siemens.Engineering.HmiUnified.RuntimeSettings.HmiRuntimeSetting", true)!;
        check(type.GetProperty("StartScreen")?.PropertyType == typeof(string) && type.GetProperty("StartScreen")?.SetMethod?.IsPublic == true
            && type.GetProperty("ScreenResolution")?.PropertyType.IsEnum == true && type.GetProperty("ScreenResolution")?.SetMethod?.IsPublic == true,
            "referenced V21 DLL exposes public StartScreen and ScreenResolution setters");
    }
}
