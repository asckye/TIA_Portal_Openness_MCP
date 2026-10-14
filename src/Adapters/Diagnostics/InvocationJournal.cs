using System;

namespace TiaMcp.Adapters.Diagnostics
{
    // Delegates cross assembly boundaries without exposing either journal's internal types.
    public static class AdapterJournal
    {
        public static void ConfigureCorrelation(Func<string> correlation)
            => TiaMcpServer.ModelContextProtocol.InvocationJournal.ConfigureCorrelation(correlation);

        public static RequestScope BeginRequest(string id, string tool, string release)
            => new RequestScope(id, tool, release);

        // Adapter replies are internal DTOs, not MCP envelopes. Keep the paired
        // dispatch projection free of payloads; native rows retain their own evidence.
        public sealed class RequestScope : IDisposable
        {
            private readonly string id, tool, release;
            private readonly IDisposable correlation;
            private bool completed;
            internal RequestScope(string id, string tool, string release)
            {
                this.id = id; this.tool = tool; this.release = release;
                correlation = TiaMcpServer.ModelContextProtocol.InvocationJournal.UseCorrelation(id);
                Write("BEFORE");
            }
            public void Complete() { if (!completed) { completed = true; Write("RETURNED"); } }
            private void Write(string phase)
                => TiaMcpServer.ModelContextProtocol.InvocationJournal.Write(id, tool, phase, details:
                    new TiaMcpServer.ModelContextProtocol.InvocationJournal.JsonLineObject().Number("callProjection", 1)
                        .String("host", "worker").String("releaseKey", release).String("requestId", id));
            public void Dispose()
            {
                if (!completed) Write("INTERRUPTED");
                correlation.Dispose();
            }
        }

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
