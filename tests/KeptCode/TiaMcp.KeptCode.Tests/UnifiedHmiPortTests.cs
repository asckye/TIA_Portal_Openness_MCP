using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens.Services;
using Xunit;

namespace TiaMcp.Engine.Tests
{
    public sealed class UnifiedHmiPortTests
    {
        [Theory]
        [InlineData("ExpressionDynamization")]
        [InlineData("Expression")]
        [InlineData("TagParameterDynamization")]
        [InlineData("Siemens.Engineering.HmiUnified.UI.Dynamization.TagParameterDynamization")]
        public void Missing_dynamization_types_are_refused_on_V19(string type)
        {
            Assert.NotNull(UnifiedHmiCapabilities.Unsupported("19", "EnsureUnifiedHmiDynamization", "", "", type));
            Assert.Null(UnifiedHmiCapabilities.Unsupported("20", "EnsureUnifiedHmiDynamization", "", "", type));
            Assert.Null(UnifiedHmiCapabilities.Unsupported("21", "EnsureUnifiedHmiDynamization", "", "", type));
            Assert.Null(UnifiedHmiCapabilities.Unsupported("19", "EnsureUnifiedHmiDynamization", "", "", "TagDynamization"));
        }

        [Theory]
        [InlineData("HmiAlarmIndicator")]
        [InlineData("AlarmLineControl")]
        [InlineData("Controls.HmiProcessDiagnosisCriteriaAnalysisControl")]
        [InlineData("Siemens.Engineering.HmiUnified.UI.Controls.HmiProcessDiagnosisGraphOverviewControl")]
        [InlineData("HmiProcessDiagnosisOverviewControl")]
        [InlineData("HmiProcessDiagnosisPlcCodeViewerControl")]
        public void Missing_official_item_types_are_release_capability_refusals(string itemType)
        {
            foreach (var release in new[] { "19", "20" })
            {
                Assert.NotNull(UnifiedHmiCapabilities.Unsupported(release, "ManageUnifiedScreenItem", "create", itemType));
                Assert.NotNull(UnifiedHmiCapabilities.Unsupported(release, "EnsureUnifiedHmiScreenItem", "", itemType));
                Assert.NotNull(UnifiedHmiCapabilities.Unsupported(release, "DescribeUnifiedScreenItemType", "", itemType));
                Assert.NotNull(UnifiedHmiCapabilities.Unsupported(release, "ApplyUnifiedHmiScreenDesign", "", itemType));
                Assert.Null(UnifiedHmiCapabilities.Unsupported(release, "ManageUnifiedScreenItem", "read", itemType));
                Assert.Null(UnifiedHmiCapabilities.Unsupported(release, "ManageUnifiedScreenItem", "update", itemType));
                Assert.Null(UnifiedHmiCapabilities.Unsupported(release, "EnsureUnifiedHmiScreenItem", "", "Unknown"));
            }
            Assert.Null(UnifiedHmiCapabilities.Unsupported("21", "ManageUnifiedScreenItem", "create", itemType));
        }

        [Theory]
        [InlineData("read", false, true)]
        [InlineData("list", false, true)]
        [InlineData("create", false, false)]
        [InlineData("update", false, false)]
        [InlineData("delete", false, false)]
        [InlineData("update", true, true)]
        public void Worker_read_admission_preserves_the_original_preview_argument(string action, bool dryRun, bool readOnly)
        {
            var request = new UnifiedHmiRequest { Operation = "ManageUnifiedScreenItem", Action = action, DryRun = dryRun };
            Assert.Equal(readOnly, request.IsReadOnly);
            Assert.Equal(dryRun, request.DryRun);
        }
        [Theory]
        [InlineData("14sp1", 0)]
        [InlineData("15.1", 0)]
        [InlineData("16", 0)]
        [InlineData("17", 0)]
        [InlineData("18", 0)]
        [InlineData("19", 25)]
        [InlineData("20", 25)]
        [InlineData("21", 26)]
        public void Release_registration_matches_the_Unified_API(string release, int count)
        {
            var family = PortedFamilies.All.Single(f => f.Name == "F26");
            Assert.Equal(26, family.Tools.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal("hmi-unified", family.OperationPrefix);
            Assert.Equal(count, family.Tools.Count(tool => PortedFamilies.Available(release, tool)));
            Assert.All(family.Tools, tool => Assert.Equal("foundation", ToolExecution.Table[tool]));
        }

        [Theory]
        [InlineData("{}")]
        [InlineData("{\"Width\":120}")]
        [InlineData("{\"Font\":{\"Size\":18}}")]
        [InlineData("{\"Text\":{\"en-US\":\"Start\"}}")]
        [InlineData("{\"Text\":{\"zh-CN\":\"\"}}")]
        [InlineData("{\"Name\":\"renamed\"}")]
        [InlineData("{\"Owner\":null}")]
        [InlineData("{\"Width\":{\"nested\":1}}")]
        [InlineData("{\"Unknown\":1}")]
        [InlineData("{\"Font\":{\"ReadOnly\":1}}")]
        [InlineData("{\"Font\":{\"Missing\":1}}")]
        [InlineData("{\"Text\":\"plain string\"}")]
        [InlineData("{\"Text\":{\"en-US\":null}}")]
        [InlineData("{\"Text\":{\"\":\"Start\"}}")]
        [InlineData("{\"Text\":{\"CultureNameExceeds16\":\"Start\"}}")]
        public void Standalone_worker_property_admission_matches_the_original_domain_validator(string input)
        {
            var properties = V4Json.Deserialize<CompositeAttributeMap>(input);
            var expected = UnifiedScreenItemsTools.ValidateProperties(typeof(ScreenItemContractFixture), properties);
            var actual = TiaMcp.Shared.UnifiedHmiPropertyAdmission.Validate(typeof(ScreenItemContractFixture), input);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(V4Json.Serialize(expected)), JsonSerializer.SerializeToNode(actual)));
        }

        [Fact]
        public void Neutral_input_roundtrip_retains_number_tokens_and_nested_text()
        {
            var request = new UnifiedHmiRequest
            {
                Operation = "ApplyUnifiedHmiScreenDesign",
                HmiSoftwarePath = "HMI_1",
                DryRun = false,
                Inputs = new Dictionary<string, UnifiedHmiInput>
                {
                    ["input"] = new UnifiedHmiInput
                    {
                        Kind = "object",
                        Properties = new Dictionary<string, UnifiedHmiInput>
                        {
                            ["decimal"] = new UnifiedHmiInput { Kind = "number", Text = "1.0000000000000000001" },
                            ["text"] = new UnifiedHmiInput { Kind = "string", Text = "启动\nStop" }
                        }
                    }
                }
            };
            var actual = JsonSerializer.Deserialize<UnifiedHmiRequest>(JsonSerializer.Serialize(request))!;
            Assert.Equal(request.Operation, actual.Operation);
            Assert.Equal(request.DryRun, actual.DryRun);
            Assert.Equal("1.0000000000000000001", actual.Inputs["input"].Properties["decimal"].Text);
            Assert.Equal("启动\nStop", actual.Inputs["input"].Properties["text"].Text);
        }
    }
}
