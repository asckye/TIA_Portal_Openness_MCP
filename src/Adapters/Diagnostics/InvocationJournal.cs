using System;

namespace TiaMcp.Adapters.Diagnostics
{
    // Delegates cross assembly boundaries without exposing either journal's internal types.
    public static class AdapterJournal
    {
        public static void ConfigureOutput(Action<string> write, Func<string> correlation)
        {
            if (write == null) throw new ArgumentNullException(nameof(write));
            if (correlation == null) throw new ArgumentNullException(nameof(correlation));
            TiaMcpServer.ModelContextProtocol.InvocationJournal.ConfigureOutput(write, correlation);
        }
    }
}

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
