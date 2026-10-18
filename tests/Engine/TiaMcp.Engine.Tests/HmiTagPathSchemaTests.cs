using System;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

namespace TiaMcp.Engine.Tests
{
    public sealed class HmiTagPathSchemaTests
    {



        [Theory]
        [InlineData("Table")]
        [InlineData("/")]
        [InlineData("/Group//Table")]
        [InlineData("/Group/Table/")]
        [InlineData("/./Table")]
        [InlineData("/Group/..")]
        [InlineData("/ ")]
        [InlineData("/\u0085")]
        [InlineData("/Ta*ble")]
        [InlineData("/Ta?ble")]
        [InlineData("/Group\\Table")]
        [InlineData("/Table\n")]
        [InlineData("/\u007f")]
        public void Malformed_paths_keep_the_existing_typed_refusal(string path)
        {
            Assert.DoesNotMatch(ToolMetadata.Parameter("DeleteHmiTag", "tagTablePath")!.Pattern!, path);
            var session = new FakeHmiToolSession { FixtureRoot = new object() };
            var result = HmiExchangeContract.Run("DeleteHmiTag", false, false, () => session.RunHmiStepTool("DeleteHmiTag", meta => {
                TiaMcp.Adapters.Native.Hmi.HmiTagDeletion.Validate(path, "Tag", true, false);
                return "Unexpectedly accepted malformed path.";
            }));
            Assert.Equal("PRECONDITION_FAILED", (string?)result.StructuredContent?["error"]?["code"]);
            Assert.Equal("rejected-before-operation", (string?)result.StructuredContent?["meta"]?["outcome"]);
            Assert.Equal(0, session.FixtureResolveCalls);
        }

    }
}
