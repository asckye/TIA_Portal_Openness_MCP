using System;
using System.Text.Json.Nodes;
using System.Threading;

namespace TiaMcpServer.Siemens
{
    public static class DownloadTargetPolicy
    {
        private static readonly AsyncLocal<Func<JsonObject?>?> Instances = new AsyncLocal<Func<JsonObject?>?>();
        private static readonly AsyncLocal<Func<string, JsonObject>?> Network = new AsyncLocal<Func<string, JsonObject>?>();

        public static IDisposable UseSources(Func<JsonObject?> instances, Func<string, JsonObject>? network = null)
        {
            var previousInstances = Instances.Value;
            var previousNetwork = Network.Value;
            Instances.Value = instances;
            if (network != null) Network.Value = network;
            return new Scope(() => { Instances.Value = previousInstances; Network.Value = previousNetwork; });
        }

        public static string OnlineTargetKind(string? targetIpAddress)
        {
            if (string.IsNullOrWhiteSpace(targetIpAddress)) return "physical";
            JsonObject? instances = null;
            try { instances = Instances.Value?.Invoke(); }
            catch (Exception) /* swallow(env-probe): unavailable simulator evidence never grants the simulator exemption. */ { }
            return Classify(targetIpAddress!, instances, (Network.Value ?? LocalNetworkPolicy.Inspect)(targetIpAddress!));
        }

        public static string Classify(string targetIpAddress, JsonObject? listing, JsonObject network)
        {
            var instances = listing?["Data"]?["instances"] ?? listing?["data"]?["instances"] ?? listing?["instances"];
            if (instances is JsonArray rows)
                foreach (var row in rows)
                    if (row is JsonObject instance && instance["stateError"] == null
                        && string.Equals((string?)instance["operatingState"], "Run", StringComparison.OrdinalIgnoreCase)
                        && string.Equals((string?)instance["controllerIP"], targetIpAddress, StringComparison.Ordinal)) return "plcsim";
            return network["simulationOnlySubnet"] is JsonValue value && value.TryGetValue<bool>(out var onlySimulator) && onlySimulator
                ? "plcsim" : "physical";
        }

        private sealed class Scope(Action restore) : IDisposable
        {
            public void Dispose() => restore();
        }
    }
}
