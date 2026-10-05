using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using ModelContextProtocol.Protocol;
using System.Reflection;

internal static class UnifiedHmiDomainShapeChecks
{
    internal static void Run(Assembly server, Action<bool, string> check)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var surface = EngineSurface.For(server);
        var portal = server.GetType("TiaMcpServer.Siemens.Portal", true)!;
        var contract = server.GetType("TiaMcpServer.Siemens.IEngineeringSession", true)!;
        var facade = server.GetType("TiaMcpServer.ModelContextProtocol.McpServer", true)!;
        var provider = (IServiceProvider)server.GetType("TiaMcpServer.EngineServices", true)!.GetProperty("Provider", all)!.GetValue(null)!;
        var session = provider.GetService(contract);
        foreach (var domain in new[] {
            (Name: "UnifiedHmi", Count: 22), (Name: "UnifiedObjectServices", Count: 12),
            (Name: "UnifiedUiModel", Count: 7), (Name: "UnifiedScreenItems", Count: 2),
            (Name: "UnifiedEngineering", Count: 3), (Name: "UnifiedExchange", Count: 3),
            (Name: "UnifiedEvents", Count: 1), (Name: "UnifiedHmiGroups", Count: 1)
        })
        {
            var service = server.GetType("TiaMcpServer.Siemens.Services." + domain.Name + "Service", true)!;
            var tools = server.GetType("TiaMcpServer.ModelContextProtocol." + domain.Name + "Tools", true)!;
            var target = provider.GetService(service);
            var toolTarget = provider.GetService(tools);
            check(target != null && toolTarget != null && ReferenceEquals(target, provider.GetService(service))
                && ReferenceEquals(toolTarget, provider.GetService(tools)), domain.Name + " singleton registration");
            check(ReferenceEquals(service.GetField("_session", all)!.GetValue(target), session)
                && ReferenceEquals(tools.GetField("_service", all)!.GetValue(toolTarget), target),
                domain.Name + " tools share the kernel through their service");
            foreach (var type in new[] { service, tools })
                check(type.IsSealed && !type.GetInterfaces().Any(item => item.Name == "IDisposable" || item.Name == "IAsyncDisposable"),
                    type.Name + " is sealed and non-disposable");
            var methods = tools.GetMethods(all).Where(method => method.GetCustomAttribute<McpServerToolAttribute>() != null).ToArray();
            check(methods.Length == domain.Count, domain.Name + " complete tool ownership");
            foreach (var tool in methods)
            {
                var name = tool.GetCustomAttribute<McpServerToolAttribute>()!.Name!;
                check(!tool.IsStatic && surface.Tool(name) == tool && ReferenceEquals(surface.Target(tool), toolTarget),
                    name + " resolves to the instance tool singleton");
                bool inspection = new[] { "UnifiedObjectServices", "UnifiedEngineering", "UnifiedEvents" }.Contains(domain.Name);
                bool migrated = inspection || new[] { "UnifiedHmi", "UnifiedHmiGroups", "UnifiedScreenItems", "UnifiedUiModel" }.Contains(domain.Name);
                if (migrated)
                {
                    check(tool.ReturnType == typeof(CallToolResult), name + " returns the V4 envelope");
                    check(!tool.GetParameters().Any(p => p.Name!.EndsWith("Json", StringComparison.Ordinal)), name + " exposes typed arguments");
                }
                var implementation = LegacyNames.TryGetValue(name, out var legacy) ? legacy : name;
                if (inspection)
                {
                    check(tool.Name.EndsWith("V4", StringComparison.Ordinal), name + " exposes its V4 boundary");
                    implementation = tool.Name.Substring(0, tool.Name.Length - 2);
                    var original = tools.GetMethod(implementation, all)!;
                    check(original.GetCustomAttribute<McpServerToolAttribute>() == null,
                        name + " retains an unregistered implementation without an alias");
                    check(CallsService(tool, original, tools, new HashSet<MethodBase>()),
                        name + " V4 boundary calls its original implementation");
                }
                var method = service.GetMethod(implementation, all);
                if (method != null)
                {
                    check(portal.GetMethod(implementation, all) == null && surface.Method(implementation) == method,
                        name + " implementation belongs to its service");
                    var il = tool.GetMethodBody()!.GetILAsByteArray()!;
                    bool calls = domain.Name == "UnifiedExchange"
                        ? DomainShapeChecks.HmiCallsService(tool, method, tools, new HashSet<MethodBase>())
                        : migrated ? CallsService(tool, method, tools, new HashSet<MethodBase>())
                        : Enumerable.Range(0, Math.Max(0, il.Length - 4)).Any(index =>
                            (il[index] == 0x28 || il[index] == 0x6f) && BitConverter.ToInt32(il, index + 1) == method.MetadataToken);
                    EngineSurface.CheckIl(check, calls,
                        name + " tool calls the migrated service", tool, method);
                }
                var forwarder = facade.GetMethod(name, all);
                check(forwarder == null && ReferenceEquals(surface.Target(tool), toolTarget),
                    name + " resolves directly without a static CLI facade");
            }
        }
        foreach (var name in new[] { "FindExistingByName", "TryGetEngineeringAttribute", "SummarizeHmiObjectReadback",
            "CoerceReflectionValue", "ExactUnifiedRoot", "UnifiedCollection", "FillUnifiedHmiPartnerNetworkInfo",
            "TryGetHmiTagRoot", "TryGetHmiTagTablesCollection", "TryFindHmiTagTable" })
            check(surface.Method(name, all).DeclaringType == portal && contract.GetMethod(name) != null,
                name + " remains on the kernel through IEngineeringSession");
        check(surface.Field("UnifiedAssembly", all).DeclaringType == portal
            && ReferenceEquals(contract.GetProperty("UnifiedAssembly")!.GetValue(session), surface.Field("UnifiedAssembly", all).GetValue(null)),
            "Unified assembly initialization remains on the kernel for the loaded-assembly API inventory");
    }

    private static readonly Dictionary<string, string> LegacyNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["SetUnifiedHmiRuntimeState"] = "EnsureStartStopUnifiedHmi",
        ["GetUnifiedHmiTexts"] = "ReadUnifiedHmiTexts",
        ["ApplyUnifiedHmiScreenDesign"] = "ApplyUnifiedHmiScreenDesignJson",
        ["BuildUnifiedHmiThemeDesign"] = "BuildUnifiedHmiThemeDesignJson",
        ["BuildUnifiedHmiLayoutDesign"] = "BuildUnifiedHmiLayoutDesignJson",
        ["GetUnifiedObjectEvents"] = "ReadUnifiedObjectEvents",
        ["GetUnifiedAlarmCommon"] = "ReadUnifiedAlarmCommon",
        ["GetUnifiedAuditSettings"] = "ReadUnifiedAuditSettings"
    };
    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!).ToDictionary(op => op.Value);

    // Follow only this tool's adapters and captured delegates. Do not execute any
    // code or accept a service call belonging to an unrelated tool method.
    private static bool CallsService(MethodBase caller, MethodInfo service, Type owner, HashSet<MethodBase> seen)
    {
        if (!seen.Add(caller)) return false;
        var il = caller.GetMethodBody()?.GetILAsByteArray() ?? Array.Empty<byte>();
        for (int i = 0; i < il.Length;)
        {
            short code = il[i++] == 0xfe ? (short)(0xfe00 | il[i++]) : (short)il[i - 1];
            var op = OpCodesByValue[code];
            if (op.OperandType == OperandType.InlineMethod)
            {
                var target = caller.Module.ResolveMethod(BitConverter.ToInt32(il, i));
                if (target == service) return true;
                var declaring = target!.DeclaringType;
                while (declaring != null && declaring != owner) declaring = declaring.DeclaringType;
                if (declaring == owner && CallsService(target, service, owner, seen)) return true;
            }
            i += op.OperandType == OperandType.InlineNone ? 0
                : op.OperandType == OperandType.ShortInlineBrTarget || op.OperandType == OperandType.ShortInlineI || op.OperandType == OperandType.ShortInlineVar ? 1
                : op.OperandType == OperandType.InlineVar ? 2
                : op.OperandType == OperandType.InlineI8 || op.OperandType == OperandType.InlineR ? 8
                : op.OperandType == OperandType.InlineSwitch ? 4 + 4 * BitConverter.ToInt32(il, i) : 4;
        }
        return false;
    }
}
