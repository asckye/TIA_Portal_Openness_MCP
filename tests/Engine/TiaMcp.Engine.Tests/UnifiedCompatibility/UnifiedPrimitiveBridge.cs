using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters.Native.Hmi
{
    // Compatibility callers select only these application primitives. Siemens
    // member selection stays in the primitive; this is not a worker operation.
    internal static class UnifiedPrimitiveBridge
    {
        private static readonly IReadOnlyDictionary<string, Type> Types = new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            ["UnifiedUiModelLogic"] = typeof(TiaMcpServer.Siemens.UnifiedUiModelLogic), ["UnifiedMultilingualText"] = typeof(TiaMcpServer.Siemens.UnifiedMultilingualText),
            ["MigrationRead"] = typeof(MigrationRead), ["MigrationPages"] = typeof(MigrationPages),
            ["UnifiedNativeRead"] = typeof(UnifiedNativeRead), ["UnifiedTagDefinitions"] = typeof(UnifiedTagDefinitions),
            ["UnifiedScriptAccess"] = typeof(UnifiedScriptAccess), ["UnifiedGlobalScriptEdit"] = typeof(UnifiedGlobalScriptEdit),
            ["UnifiedGraphicSelection"] = typeof(UnifiedGraphicSelection), ["UnifiedRuntimeSettingsAccess"] = typeof(UnifiedRuntimeSettingsAccess),
            ["UnifiedExchangeLogic"] = typeof(UnifiedExchangeLogic), ["UnifiedEventOperations"] = typeof(UnifiedEventOperations),
            ["NativeExportCapture"] = typeof(NativeExportCapture), ["HmiExactAccess"] = typeof(HmiExactAccess)
        };
        public static Type Type(string helper) => Types.TryGetValue(helper, out var type) ? type : throw new ArgumentException("Unknown HMI primitive.");
                public static void Configure(Func<Dictionary<string, object?>, string> render, Func<string, UnifiedValue> decode, IUnifiedCodecs codecs, int major)
        {
            HmiValues.Renderer = render; UnifiedRequestValues.Decoder = decode; UnifiedCodecs.Current = codecs; UnifiedRequestValues.Major = major;
            HmiExactAccess.ContentToken = (scope, content) => TiaMcpServer.Siemens.HmiExactAccess.Token(scope, System.Text.Json.JsonSerializer.SerializeToNode(content)!);
            UnifiedRequestValues.Inputs ??= new Dictionary<string, object?>(StringComparer.Ordinal);
        }
        public static object? Invoke(object? target, MethodBase method, object?[] arguments)
        {
            try { return method is ConstructorInfo constructor ? constructor.Invoke(arguments) : ((MethodInfo)method).Invoke(target, arguments); }
            catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw(); throw; }
        }
        public static object? Field(object? target, string helper, string name)
            => (target?.GetType() ?? Type(helper)).GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)!.GetValue(target);
    }
}
