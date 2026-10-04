using System;

namespace TiaMcpServer.ModelContextProtocol
{
    internal static partial class InvocationJournal
    {
        internal static Func<JsonLineObject?>? BindingSnapshot;
        private static string? ReadBinding() => BindingSnapshot?.Invoke()?.ToString();
        internal static JsonLineObject Health() => HealthRow();
        internal static void Write(string id, string name, string phase, string? objectType = null, string? objectPath = null, JsonLineObject? details = null)
            => WriteRow(id, name, phase, objectType, objectPath, () => details);
    }
}
