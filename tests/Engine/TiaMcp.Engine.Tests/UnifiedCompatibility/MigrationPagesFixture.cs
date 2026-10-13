using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
namespace TiaMcpServer.Siemens
{
    internal sealed class MigrationPages : IDisposable
    {
        internal readonly object state;
        internal MigrationPages(Func<DateTime>? clock = null) => state = UnifiedPrimitiveCompatibility.Create("MigrationPages", clock);
        internal JsonObject Read(object project, string scope, string cursor, int pageSize, int budgetMs, Func<IEnumerable<JsonObject>> factory, int? expected = null)
            => UnifiedPrimitiveCompatibility.CallState<JsonObject>(state, "MigrationPages", "Read", project, scope, cursor, pageSize, budgetMs, factory, expected);
        internal bool Cancel(string cursor) => UnifiedPrimitiveCompatibility.CallState<bool>(state, "MigrationPages", "Cancel", cursor);
        public void Dispose() => UnifiedPrimitiveCompatibility.CallState(state, "MigrationPages", "Dispose");
    }
}
