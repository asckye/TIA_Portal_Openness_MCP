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
        var generation = new[] { "ListStandardPackages", "ManageStandardPackage", "ValidateStandardPackage", "DescribeStandardPackage", "ManageMachineDescription" };
        var host = EngineSurface.HostTools(engine).Concat(generation).ToArray();
        CheckCoverage(complete.Keys.Cast<string>().ToArray(), host, execution, check);
        foreach (var name in host) check(execution.Contains(name), name + " host declaration has an explicit execution entry");
        check(complete.Keys.Cast<string>().Intersect(host, StringComparer.Ordinal).Count() == 0,
            "complete execution table includes the host declarations excluded from the engine catalog");
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

    internal static void CheckCoverage(string[] complete, string[] host, IDictionary execution, Action<bool, string> check)
    {
        var expected = complete.Concat(host).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        check(expected.Distinct(StringComparer.Ordinal).Count() == expected.Length
            && execution.Count == expected.Length
            && execution.Keys.Cast<string>().OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(expected),
            "execution table equals the complete engine catalog plus host declarations, with no missing or extra entries");
    }
}
