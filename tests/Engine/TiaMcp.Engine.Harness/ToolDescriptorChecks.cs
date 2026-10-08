using System;
using System.Collections;
using System.Linq;
using System.Reflection;

internal static class ToolDescriptorChecks
{
    internal static void Run(Assembly engine, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        var catalogType = engine.GetType("TiaMcpServer.ModelContextProtocol.ToolCatalog", true)!;
        var catalog = catalogType.GetProperty("Engine", flags)!.GetValue(null)!;
        var all = (IDictionary)catalogType.GetProperty("All", flags)!.GetValue(catalog)!;
        var complete = (IDictionary)catalogType.GetProperty("IncludingUnavailable", flags)!.GetValue(catalog)!;
        var executionType = Assembly.Load("TiaMcp.Logic").GetType("TiaMcpServer.ModelContextProtocol.ToolExecution", true)!;
        var execution = (IDictionary)executionType.GetField("Table", flags)!.GetValue(null)!;
        check(complete.Count == 491 && execution.Count == complete.Count, "complete execution table matches the engine catalog");
        foreach (DictionaryEntry pair in complete)
        {
            string name = (string)pair.Key;
            var descriptor = pair.Value!;
            var type = descriptor.GetType();
            check(execution.Contains(name), name + " has an explicit execution entry");
            check((string)type.GetProperty("Execution")!.GetValue(descriptor)! == (string)execution[name]!, name + " retains reviewed ownership");
            check((string)type.GetProperty("Name")!.GetValue(descriptor)! == name, name + " retains its canonical name");
        }
        var server = engine.GetType("TiaMcpServer.ModelContextProtocol.McpServer", true)!;
        var tools = ((IEnumerable)server.GetMethod("GetAllTools", flags)!.Invoke(null, null)!).Cast<object>().ToArray();
        check(tools.Length == all.Count, "full profile comes from release descriptors");
        for (int i = 0; i < tools.Length; i++)
        {
            var protocol = tools[i].GetType().GetProperty("ProtocolTool")!.GetValue(tools[i])!;
            var name = (string)protocol.GetType().GetProperty("Name")!.GetValue(protocol)!;
            check(ReferenceEquals(protocol, all[name]!.GetType().GetProperty("Tool")!.GetValue(all[name])), name + " publishes its decorated SDK Tool");
        }
        var lite = ((IEnumerable)catalogType.GetProperty("Lite", flags)!.GetValue(catalog)!).Cast<object>().Count();
        check(lite == ((IEnumerable)server.GetMethod("GetLiteTools", flags)!.Invoke(null, null)!).Cast<object>().Count(), "lite profile comes from release descriptors");
    }
}
