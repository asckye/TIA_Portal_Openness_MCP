using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;

internal static class GraphicSelectionRuntimeTests
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    public sealed class Hmi { public List<Screen> Screens { get; } = new List<Screen>(); }
    public sealed class Screen { public string Name => "Main"; public List<object> ScreenItems { get; } = new List<object>(); public object Parent => throw new Exception("Recursive Parent read"); }
    public class Item
    {
        public string Name { get; set; } = "";
        public virtual int Left { get; set; }
        public int Top { get; set; }
        public uint Width => 86;
        public uint Height => 42;
        public Screen Parent { get; set; } = new Screen();
        public string UnrelatedScript => throw new Exception("Deep traversal");
    }
    public sealed class Broken : Item { public override int Left { get => throw new ObjectDisposedException("SoftwareContainer"); set { } } }
    internal static void Run(Assembly server, Action<bool, string> check)
    {
        var helper = server.GetType("TiaMcpServer.Siemens.UnifiedGraphicSelection", true)!;
        var capture = helper.GetMethod("Capture", All)!;
        var compare = helper.GetMethod("Compare", All)!;
        var json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
        Dictionary<string, object> Parse(object value) => (Dictionary<string, object>)json.DeserializeObject(value.ToString());
        var names = new[] { "fgxTop", "txtTop", "gfxBotom", "txtBotom" };
        var hmi = new Hmi(); var screen = new Screen(); hmi.Screens.Add(screen);
        foreach (var name in names) screen.ScreenItems.Add(new Item { Name = name, Left = name.StartsWith("txt") ? -22 : 4, Parent = screen });
        List<Dictionary<string, object>> Read(string[] selected)
        {
            var trace = Activator.CreateInstance(capture.GetParameters()[3].ParameterType, true)!;
            return ((IEnumerable)capture.Invoke(null, new object[] { hmi, "/Main", selected, trace })!).Cast<object>().Select(Parse).ToList();
        }
        string Page(List<Dictionary<string, object>> records, string id) => json.Serialize(new[] { new Dictionary<string, object?> {
            ["scope"] = new { tool = "ReadUnifiedGraphicSelection", expectedProject = "Project_A", softwarePath = "HMI", screenPath = "/Main", itemNames = names },
            ["collectionId"] = id, ["pageIndex"] = 0, ["apiCallSuccess"] = true, ["traversalComplete"] = true, ["truncated"] = false,
            ["nextCursor"] = null, ["records"] = records } });
        var before = Read(names);
        check(before.Count == 5 && before.Take(4).All(r => (bool)r["geometryComplete"]), "actual EXE reads four exact objects without recursive getters");
        var bounds = (Dictionary<string, object>)before.Last()["rawCoordinateEnvelope"];
        check(Convert.ToInt32(bounds["left"]) == -22 && Convert.ToInt32(bounds["width"]) == 112, "actual EXE retains negative raw geometry");
        check((string)before.Last()["status"] == "unsupported" && before.Last()["nativeGroupBounds"] == null, "actual EXE does not invent native graphical group bounds");
        ((Item)screen.ScreenItems[0]).Left += 7; ((Item)screen.ScreenItems[1]).Top += 3;
        var after = Read(names);
        var result = Parse(compare.Invoke(null, new object[] { Page(before, "a"), Page(after, "b") })!);
        check((int)result["changedObjectCount"] == 2 && !(bool)result["causalityEstablished"], "actual EXE compares sibling coordinate changes without claiming causality");
        var damaged = Read(names); damaged.RemoveAt(0);
        bool rejected = false;
        try { compare.Invoke(null, new object[] { Page(before, "a"), Page(damaged, "b") }); }
        catch (TargetInvocationException ex) when (ex.InnerException is ArgumentException) { rejected = true; }
        check(rejected, "actual EXE refuses incomplete object samples");
        var near = Read(new[] { "gfxTop" });
        check(!near[0].ContainsKey("type") && !(bool)near[0]["geometryComplete"], "actual EXE rejects gfx/fgx near match");
        screen.ScreenItems.Add(new Broken { Name = "Broken" });
        bool fatal = false;
        try { Read(new[] { "Broken", "fgxTop" }); }
        catch (TargetInvocationException ex) when (ex.InnerException is ObjectDisposedException) { fatal = true; }
        check(fatal, "actual EXE propagates fatal handle exception instead of continuing selection");
        var mcp = server.GetType("TiaMcpServer.ModelContextProtocol.McpServer", true)!;
        check(new[] { "ReadUnifiedGraphicSelection", "CompareUnifiedGraphicSelections" }.All(n => mcp.GetMethod(n, All)?.CustomAttributes.Any(a => a.AttributeType.Name == "McpServerToolAttribute") == true), "actual EXE registers both graphical-selection tools");
    }
}
