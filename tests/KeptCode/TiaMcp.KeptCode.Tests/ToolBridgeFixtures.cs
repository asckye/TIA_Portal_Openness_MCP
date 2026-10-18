using TiaMcpServer;
// Response boundary stand-ins. The catalog and bridge use the real MCP SDK attributes.
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using TiaMcpServer.ModelContextProtocol;
using TiaMcp.Logic.V4;
using Xunit;

namespace TiaMcp.Engine.Tests
{
    public sealed class ToolCatalogTests
    {

        [Fact]
        public void BridgeReceivesLiteProfileAndAllowlist()
        {
            ToolBridgeFixture.Configure(lite: true);
            try
            {
                Assert.Contains(McpServer.FindTools("ProbeResult").Items!, line => line.Contains("[already listed - call it directly]"));
                Assert.Contains(McpServer.FindTools("InstanceProbe").Items!, line => line.Contains("[call via CallTool]"));
            }
            finally { ToolBridgeFixture.Configure(); }
        }
    }
}
