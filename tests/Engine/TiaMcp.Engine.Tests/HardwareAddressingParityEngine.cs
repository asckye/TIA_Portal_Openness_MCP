using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcp.Engine.Tests;

public static class HardwareAddressingParityEngine
{
    public static CallToolResult Map(string tool, string evidence, bool write)
        => HardwareContract.Map(tool, new ResponseMessage { Message = "fixture", Meta = JsonNode.Parse(evidence)!.AsObject() }, write, false);
}
