using System.Net.NetworkInformation;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;

namespace TiaMcp.Engine.Tests.Shared;

public static class DownloadTargetCases
{
    public const string Target = "192.0.2.10";

    public static JsonObject Listing(params string[] addresses) => new() { ["Ok"] = true, ["Data"] = new JsonObject {
        ["instances"] = new JsonArray(new JsonObject { ["name"] = "fixture", ["id"] = 1, ["operatingState"] = "Run",
            ["controllerIP"] = new JsonArray(addresses.Select(ip => (JsonNode?)JsonValue.Create(ip)).ToArray()) }) },
        ["Meta"] = new JsonObject { ["dataComplete"] = true } };

    public static JsonObject Network(OperationalStatus? physical = null, string simulatorMask = "", bool complete = true)
    {
        var addresses = new List<LocalNetworkPolicy.Address>();
        if (physical.HasValue) addresses.Add(new LocalNetworkPolicy.Address { Name = "Ethernet", Description = "Intel(R) Ethernet",
            Type = NetworkInterfaceType.Ethernet, Status = physical.Value, Ip = "192.0.2.20", Mask = "255.255.255.0" });
        if (simulatorMask.Length != 0) addresses.Add(new LocalNetworkPolicy.Address { Name = "PLCSIM",
            Description = "Siemens PLCSIM Virtual Ethernet Adapter", Type = NetworkInterfaceType.Ethernet, Status = OperationalStatus.Up,
            Ip = Target, Mask = simulatorMask });
        return LocalNetworkPolicy.Report(Target, addresses, complete);
    }

    public static IEnumerable<object[]> Cases()
    {
        object[] Row(string name, string adapter, JsonObject listing, JsonObject network, string expected)
            => new object[] { name, adapter, listing.ToJsonString(), network.ToJsonString(), expected };
        foreach (string adapter in new[] { "", "PLCSIM", "plcsim", "Softbus", "Siemens PLCSIM Virtual Ethernet Adapter",
            "PLCSIM Virtual Ethernet Adapter", "PLCSIM #1", "PLCSIM (#1)" })
            yield return Row("running instance / " + adapter, adapter, Listing("198.51.100.1", Target), Network(), "plcsim");
        foreach (string adapter in new[] { "Intel(R) Ethernet", "Realtek", "automatic", "PLCSIM-compatible Ethernet" })
            yield return Row("explicit physical / " + adapter, adapter, Listing(Target), Network(simulatorMask: "255.255.255.0"), "physical");
        foreach (OperationalStatus status in Enum.GetValues(typeof(OperationalStatus)))
        foreach (string adapter in new[] { "", "PLCSIM" })
        foreach (bool running in new[] { false, true })
            yield return Row("collision / " + status + " / " + adapter + " / " + running, adapter,
                Listing(running ? Target : "198.51.100.1"), Network(status, "255.255.255.0"), "physical");
        foreach (var mask in new[] { ("0.0.0.0", "physical"), ("254.0.0.0", "physical"), ("255.0.0.0", "plcsim"),
            ("255.255.255.0", "plcsim"), ("255.255.255.255", "plcsim"), ("255.0.255.0", "physical") })
            yield return Row("simulator mask / " + mask.Item1, "PLCSIM", Listing(), Network(simulatorMask: mask.Item1), mask.Item2);
        yield return Row("incomplete inventory", "PLCSIM", Listing(Target), Network(complete: false), "physical");
        yield return Row("unmatched instance", "PLCSIM", Listing("198.51.100.1"), Network(), "physical");
        yield return Row("untrusted network shape", "PLCSIM", Listing(Target), new JsonObject(), "physical");
        foreach (string row in new[] { "null", "42", "[]", "{}",
            "{\"operatingState\":7,\"controllerIP\":[\"192.0.2.10\"]}",
            "{\"operatingState\":\"Stop\",\"controllerIP\":[\"192.0.2.10\"]}",
            "{\"operatingState\":\"Run\",\"controllerIP\":\"192.0.2.10\"}",
            "{\"operatingState\":\"Run\",\"controllerIP\":{}}",
            "{\"operatingState\":\"Run\",\"controllerIP\":[\"192.0.2.10\",7]}",
            "{\"operatingState\":\"Run\",\"controllerIP\":[\"192.0.2.10\",\"not-an-ip\"]}",
            "{\"operatingState\":\"Run\",\"controllerIP\":[null,\"192.0.2.10\"]}",
            "{\"operatingState\":\"Run\",\"controllerIP\":[\"192.0.2.10\"],\"stateError\":\"unavailable\"}" })
        {
            var listing = Listing(Target);
            listing["Data"]!["instances"] = new JsonArray(JsonNode.Parse(row));
            yield return Row("malformed or stopped row / " + row, "PLCSIM", listing, Network(), "physical");
        }
        var malformed = Listing(Target);
        malformed["Data"] = new JsonArray();
        yield return Row("malformed data", "PLCSIM", malformed, Network(), "physical");
        var mixed = Listing(Target);
        mixed["Data"]!["instances"]!.AsArray().Insert(0, JsonValue.Create(42));
        yield return Row("valid instance after malformed row", "PLCSIM", mixed, Network(), "plcsim");
    }
}
