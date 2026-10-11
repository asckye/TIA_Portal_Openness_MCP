using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace TiaMcpServer.Siemens
{
    internal static class HmiTagDeletion
    {
        internal static string[] Validate(string path, string name, bool dryRun, bool confirm)
            => TiaMcp.Adapters.Native.Hmi.HmiAccessPrimitives.ValidateTagDeletion(path, name, dryRun, confirm);
        internal static string Execute(JsonObject meta, bool dryRun, Func<object?> find, Action<object> delete)
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, object?>>(meta.ToJsonString())!;
            try { return TiaMcp.Adapters.Native.Hmi.HmiAccessPrimitives.DeleteTag(values, dryRun, find, delete); }
            catch (TiaMcp.Adapters.Contracts.PlcSoftwareException error)
            { throw new PortalException((PortalErrorCode)Enum.Parse(typeof(PortalErrorCode), error.Code), error.Message); }
            finally { foreach (var pair in values) meta[pair.Key] = JsonSerializer.SerializeToNode(pair.Value); }
        }
    }
}
