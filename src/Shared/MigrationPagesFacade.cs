using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace TiaMcpServer.Siemens
{
    internal sealed class MigrationPages : IDisposable
    {
        internal readonly TiaMcp.Adapters.Native.Hmi.UnifiedPrimitiveBridge.Pages state;
        internal MigrationPages(Func<DateTime>? clock = null)
            => state = new TiaMcp.Adapters.Native.Hmi.UnifiedPrimitiveBridge.Pages(clock);
        internal JsonObject Read(object project, string scope, string cursor, int pageSize, int budgetMs,
            Func<IEnumerable<JsonObject>> factory, int? expected = null)
            => JsonSerializer.SerializeToNode(state.Read(project, scope, cursor, pageSize, budgetMs,
                () => factory().Select(row => JsonSerializer.Deserialize<Dictionary<string, object?>>(row.ToJsonString())!), expected))!.AsObject();
        internal bool Cancel(string cursor) => state.Cancel(cursor);
        public void Dispose() => state.Dispose();
    }
}
