using System;
using System.Collections.Generic;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Reflection;

namespace TiaMcp.Adapters.Native.Hmi
{
    internal static partial class HmiExactAccess
    {
        [ThreadStatic] internal static Func<string, Dictionary<string, object?>, string>? ContentToken;

        internal static object Event(object button, string type)
            => HmiEventAccess.Resolve(Get(button, "EventHandlers")
                ?? throw new PlcSoftwareException("NotFound", "EventHandlers unavailable."), type);

        internal static HmiObject EventDto(object handler)
        {
            var scriptRead = PropertyPathReader.Read(handler, "Script");
            if (!scriptRead.Success)
                throw new InvalidOperationException("Script: " + scriptRead.Status + " " + scriptRead.Error);
            object? script = scriptRead.Value;
            var result = new HmiObject
            {
                ["eventType"] = Get(handler, "EventType")?.ToString(),
                ["scriptExists"] = script != null
            };
            foreach (string name in new[] { "ScriptCode", "GlobalDefinitionAreaScriptCode", "Async" })
            {
                var value = script == null ? null : PropertyPathReader.Read(script, name);
                if (value != null && !value.Success)
                    throw new InvalidOperationException(name + ": " + value.Status + " " + value.Error);
                if (value?.Value == null || value.Value is bool || value.Value is string) result[name] = value?.Value;
                else throw new InvalidOperationException("Unexpected event script property type: " + name);
            }
            return result;
        }

        internal static string Token(string scope, Dictionary<string, object?> data)
        {
            var content = (HmiObject)data.DeepClone();
            if (content["kind"]?.ToString() == "InspectionSnapshot") content.Remove("elapsedMs");
            return (ContentToken ?? throw new InvalidOperationException("HMI content token provider unavailable."))(scope, content);
        }

        internal static void RequireToken(string? expected, string actual)
        {
            if (string.IsNullOrWhiteSpace(expected) || !string.Equals(expected, actual, StringComparison.Ordinal))
                throw new PlcSoftwareException("InvalidState", "Content changed or expectedToken missing; read/preview again.");
        }
    }
}
