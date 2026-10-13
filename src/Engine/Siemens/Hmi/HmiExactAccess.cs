using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    internal static class HmiExactAccess
    {
        private static string tokenGeneration = Guid.NewGuid().ToString("N");
        // Both worker restart and explicit project rebinding invalidate old preview hashes.
        internal static void InvalidateTokens()
        {
            tokenGeneration = Guid.NewGuid().ToString("N");
        }
        internal static object? Get(object value, string name)
            => Native(() => TiaMcp.Adapters.Native.Hmi.HmiAccessPrimitives.Get(value, name));
        internal static List<object> Items(object? value)
            => Native(() => TiaMcp.Adapters.Native.Hmi.HmiAccessPrimitives.Items(value));
        internal static object Named(object collection, string name)
            => Native(() => TiaMcp.Adapters.Native.Hmi.HmiAccessPrimitives.Named(collection, name));

        internal static List<(string Path, object Value)> Screens(object software)
            => Native(() => TiaMcp.Adapters.Native.Hmi.HmiAccessPrimitives.Screens(software));

        internal static object Screen(object software, string nameOrPath)
            => Native(() => TiaMcp.Adapters.Native.Hmi.HmiAccessPrimitives.Screen(software, nameOrPath));
        internal static object Group(object software,string path)
            => Native(() => TiaMcp.Adapters.Native.Hmi.HmiAccessPrimitives.Group(software, path));
        internal static object Event(object button,string type)
            => HmiEventAccess.Resolve(Get(button,"EventHandlers") ?? throw new PortalException(PortalErrorCode.NotFound,"EventHandlers unavailable."),type);
        internal static JsonObject EventDto(object handler)
        {
            var scriptRead = PropertyPathReader.Read(handler,"Script");
            if (!scriptRead.Success) throw new InvalidOperationException("Script: "+scriptRead.Status+" "+scriptRead.Error);
            var script = scriptRead.Value;
            var result = new JsonObject { ["eventType"] = Get(handler,"EventType")?.ToString(), ["scriptExists"] = script != null };
            foreach(var name in new[]{"ScriptCode","GlobalDefinitionAreaScriptCode","Async"})
            {
                var value = script == null ? null : PropertyPathReader.Read(script,name);
                if (value != null && !value.Success) throw new InvalidOperationException(name+": "+value.Status+" "+value.Error);
                if (value?.Value is bool flag) result[name] = flag;
                else if(value?.Value is string str) result[name] = str;
                else if(value?.Value == null) result[name] = null;
                else throw new InvalidOperationException("Unexpected event script property type: "+name);
            }
            return result;
        }
        internal static string Token(string scope, JsonNode data)
        {
            // Inspection timing is diagnostic metadata, not guarded engineering content.
            var content = data.DeepClone();
            if (content is JsonObject snapshot && snapshot["kind"]?.ToString() == "InspectionSnapshot")
                snapshot.Remove("elapsedMs");
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(tokenGeneration+"\n"+scope+"\n"+content.ToJsonString()))).Replace("-", "").ToLowerInvariant();
        }
        internal static void Delete(object value)
            => Native(() => { TiaMcp.Adapters.Native.Hmi.HmiAccessPrimitives.Delete(value); return true; });
        internal static void RequireToken(string? expected,string actual)
        {
            if (string.IsNullOrWhiteSpace(expected) || !string.Equals(expected,actual,StringComparison.Ordinal))
                throw new PortalException(PortalErrorCode.InvalidState,"Content changed or expectedToken missing; read/preview again.");
        }
        private static T Native<T>(Func<T> action)
        {
            try { return action(); }
            catch (TiaMcp.Adapters.Contracts.PlcSoftwareException error)
            { throw new PortalException((PortalErrorCode)Enum.Parse(typeof(PortalErrorCode), error.Code), error.Message, error.Candidates, error.InnerException); }
        }
    }
}
