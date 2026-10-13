using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.Adapters.Native.Hmi
{
    // Typed compatibility for the three remaining engine consumers. No member-name lookup.
    public static class UnifiedPrimitiveBridge
    {
        public static Exception Cause(Exception error) => MigrationRead.Cause(error);
        public static object ResolveScripts(Func<string, object> resolveHmi, string path, string softwarePath, bool collection)
            => UnifiedScriptAccess.Resolve(resolveHmi, path, softwarePath, collection);

        public sealed class Pages : IDisposable
        {
            private readonly MigrationPages _pages;
            public Pages(Func<DateTime>? clock = null) => _pages = new MigrationPages(clock);
            public Dictionary<string, object?> Read(object project, string scope, string cursor, int limit, int budgetMs,
                Func<IEnumerable<Dictionary<string, object?>>> factory, int? expected = null)
                => _pages.Read(project, scope, cursor, limit, budgetMs,
                    () => factory().Select(row => new HmiObject(row)), expected);
            public bool Cancel(string cursor) => _pages.Cancel(cursor);
            public void Dispose() => _pages.Dispose();
        }
    }
}
