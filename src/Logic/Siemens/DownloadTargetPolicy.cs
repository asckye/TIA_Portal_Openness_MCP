using System;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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

        public static string OnlineTargetKind(string? targetIpAddress, string? pgPcInterface = null)
        {
            if (string.IsNullOrWhiteSpace(targetIpAddress)) return "physical";
            JsonObject? instances = null;
            try { instances = Instances.Value?.Invoke(); }
            catch (Exception) /* swallow(env-probe): unavailable simulator evidence never grants the simulator exemption. */ { }
            return Classify(targetIpAddress!, pgPcInterface, instances, NetworkSnapshot(targetIpAddress!));
        }

        public static JsonObject NetworkSnapshot(string target) => (Network.Value ?? LocalNetworkPolicy.Inspect)(target).DeepClone().AsObject();

        public static string Classify(string targetIpAddress, string? pgPcInterface, JsonObject? listing, JsonObject network)
        {
            if (!SimulatorInterface(pgPcInterface) || !True(network["noNonPlcsimSubnetOverlap"])) return "physical";
            var data = listing?["Data"] as JsonObject ?? listing?["data"] as JsonObject ?? listing;
            if (data?["instances"] is JsonArray rows)
                foreach (var row in rows)
                    if (RunningAt(row, targetIpAddress)) return "plcsim";
            return True(network["simulationOnlySubnet"]) ? "plcsim" : "physical";
        }

        private static bool SimulatorInterface(string? name)
            => string.IsNullOrWhiteSpace(name) || Regex.IsMatch(name!.Trim(),
                @"^(?:(?:Siemens )?PLCSIM Virtual Ethernet Adapter|PLCSIM|Softbus)(?:\s*(?:\(#\d+\)|#\d+))?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static bool True(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

        private static bool RunningAt(JsonNode? row, string target)
        {
            if (row is not JsonObject instance || instance["stateError"] != null
                || instance["operatingState"] is not JsonValue state || !state.TryGetValue<string>(out var operatingState)
                || !string.Equals(operatingState, "Run", StringComparison.OrdinalIgnoreCase)
                || instance["controllerIP"] is not JsonArray addresses) return false;
            bool matches = false;
            foreach (var address in addresses)
            {
                if (address is not JsonValue value || !value.TryGetValue<string>(out var ip) || !IPAddress.TryParse(ip, out _)) return false;
                matches |= string.Equals(ip, target, StringComparison.Ordinal);
            }
            return matches;
        }

        private sealed class Scope(Action restore) : IDisposable
        {
            public void Dispose() => restore();
        }
    }
}
