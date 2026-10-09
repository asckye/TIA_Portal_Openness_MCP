using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.Generation;
using Xunit;

namespace TiaMcp.Generation.Planning.Tests
{
    public sealed class PlanningTests
    {
        public static IEnumerable<object[]> CulturesAndReleases => PlanningFixture.Releases.SelectMany(r => new[] { "en-US", "fr-FR", "tr-TR", "zh-CN" }.Select(c => new object[] { r, c }));

        [Theory]
        [MemberData(nameof(CulturesAndReleases))]
        public void GoldenPlansAndArtifactsAreByteIdentical(string release, string culture)
        {
            var previous = CultureInfo.CurrentCulture; var previousUi = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
                var first = new PlanningFixture(release).Build(); var second = new PlanningFixture(release).Build();
                var golden = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", "plan." + release + ".json"));
                Assert.Equal(golden, Encoding.UTF8.GetBytes(first.CanonicalPlan));
                Assert.Equal(first.CanonicalPlan, second.CanonicalPlan); Assert.Equal(first.Plan.PlanHash, GenerationDocuments.PlanHash(first.CanonicalPlan));
                Assert.Equal(0, first.Plan.SelfCheck.Errors); Assert.Empty(first.Plan.Conflicts); Assert.Empty(first.Plan.Unavailable);
                foreach (var artifact in first.Artifacts) Assert.Equal(artifact.Sha256, CanonicalJson.HashBytes(Encoding.UTF8.GetBytes(artifact.Content)));
                var call = first.Artifacts.Single(a => a.Content.StartsWith("FUNCTION \"FC_U01_Calls\"", StringComparison.Ordinal));
                Assert.Equal(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", "FC_U01_Calls.scl")), Encoding.UTF8.GetBytes(call.Content));
            }
            finally { CultureInfo.CurrentCulture = previous; CultureInfo.CurrentUICulture = previousUi; }
        }

        [Theory]
        [InlineData("14sp1")]
        [InlineData("17")]
        [InlineData("19")]
        [InlineData("21")]
        public void MatchingReadbackHasZeroCreateSteps(string release)
        {
            var fixture = new PlanningFixture(release); var first = fixture.Build(); fixture.Observed = PlanningFixture.Copy(first.Expected);
            var result = fixture.Build(); Assert.Empty(result.Plan.Steps); Assert.Empty(result.Plan.Conflicts); Assert.Empty(result.Plan.Unavailable);
            Assert.All(result.Plan.Skipped, s => Assert.Equal("exists-identical", s.Reason)); Assert.Equal(result.Plan.Skipped.Count, Count(first.Expected));
        }

        [Fact]
        public void ExplicitAddressesAreReservedBeforeAutoAndRepeatedCallsDoNotConsumeAllocation()
        {
            var result = new PlanningFixture().Build();
            Assert.Equal("%I0.1", result.Expected.Tags.Single(t => t.Name == "M01_FEEDBACK").Address);
            Assert.Equal("%I0.0", result.Expected.Tags.Single(t => t.Name == "M02_FEEDBACK").Address);
            Assert.Equal("%Q0.0", result.Expected.Tags.Single(t => t.Name == "M01_RUN").Address);
        }

        [Theory]
        [InlineData("address")]
        [InlineData("datatype")]
        [InlineData("table")]
        [InlineData("code")]
        [InlineData("interface")]
        [InlineData("group")]
        [InlineData("firmware")]
        public void ChangedObjectsConflictAndNeverGenerateOverwriteSteps(string change)
        {
            var fixture = new PlanningFixture(); fixture.Observed = PlanningFixture.Copy(fixture.Build().Expected);
            switch (change)
            {
                case "address": fixture.Observed.Tags[0].Address = "%I4.0"; break;
                case "datatype": fixture.Observed.Tags[0].DataType = "Byte"; break;
                case "table": fixture.Observed.Tags[0].Table = "Changed"; break;
                case "code": fixture.Observed.Blocks[0].CodeFingerprint = "sha256:" + new string('f', 64); break;
                case "interface": fixture.Observed.Blocks[0].InterfaceFingerprint = null; break;
                case "group": fixture.Observed.Blocks[0].Group = "Moved"; break;
                default: fixture.Observed.Devices[0].Firmware = "V2"; break;
            }
            var result = fixture.Build(); Assert.NotEmpty(result.Plan.Conflicts);
            Assert.DoesNotContain(result.Plan.Steps, s => s.Op == "update" || result.Plan.Conflicts.Any(c => c.Key == s.Key));
        }

        [Fact]
        public void PartialFailureReplanningReusesAlreadyImportedExternalSources()
        {
            var fixture = new PlanningFixture(); var first = fixture.Build();
            fixture.Observed.ExternalSources = PlanningFixture.Copy(first.Expected).ExternalSources;
            var resumed = fixture.Build(); Assert.DoesNotContain(resumed.Plan.Steps, s => s.Tool == "ImportPlcExternalSource");
            Assert.Contains(resumed.Plan.Steps, s => s.Tool == "GenerateBlocksFromExternalSource");
        }

        [Fact]
        public void ConflictingPrerequisiteBlocksDependentCallPhase()
        {
            var fixture = new PlanningFixture(); var first = fixture.Build();
            fixture.Observed.Tags.Add(new ProjectTag { Station = "PLC1", Name = "M01_FEEDBACK", Table = "Tags_U01", Address = "%I9.0", DataType = "Bool" });
            var result = fixture.Build(); Assert.Contains(result.Plan.Unavailable, u => u.Phase == "plcProgram" && u.Reason.Contains("conflicting", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Plan.Steps, s => s.Phase == "plcProgram");
            Assert.Contains(result.Plan.Conflicts, c => c.Key.Contains("M01_FEEDBACK", StringComparison.Ordinal));
        }

        [Fact]
        public void UnrelatedObservedTagOccupyingGeneratedAddressConflicts()
        {
            var fixture = new PlanningFixture(); fixture.Observed.Tags.Add(new ProjectTag { Station = "PLC1", Name = "Manual", Table = "Manual", DataType = "Word", Address = "%IW0" });
            var result = fixture.Build(); Assert.Contains(result.Plan.Conflicts, c => c.Detail.Contains("occupied", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("collision")]
        [InlineData("range")]
        [InlineData("direction")]
        [InlineData("width")]
        [InlineData("invalid")]
        [InlineData("exhausted")]
        public void BadIoAssignmentsAreRejected(string scenario)
        {
            var fixture = new PlanningFixture();
            fixture.Machine.Devices[0].Io["feedback"] = scenario switch { "collision" => "%I0.0", "range" => "%I99.0", "direction" => "%Q1.0", "width" => "%IW1", "invalid" => "%I1.8", _ => "auto" };
            if (scenario == "exhausted") { fixture.IoRanges[0]!["end"] = "%I0.0"; }
            Assert.Throws<GenerationValidationException>(() => fixture.Build());
        }

        [Fact]
        public void AnalogWordAndDigitalBitRangesCannotOverlap()
        {
            var fixture = new PlanningFixture(); fixture.IoRanges[2]!["start"] = "%IW0";
            Assert.Contains(Assert.Throws<GenerationValidationException>(() => fixture.Build()).Errors, e => e.Rule == "collision");
        }

        [Fact]
        public void AnalogAllocationUsesTypeWidths()
        {
            var fixture = new PlanningFixture(); fixture.Signals.Add(JsonNode.Parse("{\"role\":\"speed\",\"dir\":\"AI\",\"type\":\"Real\"}"));
            var result = fixture.Build(); Assert.Equal("%ID32", result.Expected.Tags.Single(t => t.Name == "M01_SPEED").Address); Assert.Equal("%ID36", result.Expected.Tags.Single(t => t.Name == "M02_SPEED").Address);
        }

        [Theory]
        [InlineData("duplicate")]
        [InlineData("subnet")]
        [InlineData("broadcast")]
        [InlineData("exhausted")]
        [InlineData("invalid")]
        public void BadIpAllocationIsRejected(string scenario)
        {
            var fixture = new PlanningFixture(); fixture.Machine.Stations[0].Ip = scenario switch { "subnet" => "192.168.2.10", "broadcast" => "192.168.1.255", "invalid" => "192.168.01.10", _ => "192.168.1.10" };
            if (scenario == "duplicate" || scenario == "exhausted") fixture.Machine.Stations.Add(new MachineDescriptionStationsItem { Id = "PLC2", Role = "plc.main", Article = "synthetic.article", Ip = scenario == "duplicate" ? "192.168.1.10" : "auto" });
            if (scenario == "exhausted") fixture.Documents["hardware.json"]["allocation"]!["ip"]!["end"] = "192.168.1.10";
            Assert.Throws<GenerationValidationException>(() => fixture.Build());
        }

        [Fact]
        public void IpAllocationIsStableAndDoesNotPretendEnsureSubnetSetsAnAddress()
        {
            var fixture = new PlanningFixture(); fixture.Machine.Stations[0].Ip = "auto";
            var result = fixture.Build(); Assert.Equal("192.168.1.10", Assert.Single(result.Expected.Networks).Ip);
            Assert.Contains(result.Plan.Unavailable, u => u.Phase == "network" && u.Reason.Contains("attribute", StringComparison.Ordinal)); Assert.DoesNotContain(result.Plan.Steps, s => s.Phase == "network");
        }

        [Fact]
        public void NumberAllocationMemoizesOwnersAndChecksRangeAndCollision()
        {
            var allocator = new GenerationAllocator(new HardwarePart(), new StructurePart { NumberRanges = new List<StructurePartNumberRangesItem> { new StructurePartNumberRangesItem { Id = "db", Kind = "DB", Start = 100, End = 101 } } });
            Assert.Equal(100, allocator.Number("db", "a")); Assert.Equal(100, allocator.Number("db", "a"));
            Assert.Throws<GenerationValidationException>(() => allocator.Number("db", "outside", 99));
            Assert.Throws<GenerationValidationException>(() => allocator.Number("db", "collision", 100));
            Assert.Equal(101, allocator.Number("db", "b")); Assert.Throws<GenerationValidationException>(() => allocator.Number("db", "c"));
        }

        [Theory]
        [InlineData("upper")]
        [InlineData("lower")]
        [InlineData("pad")]
        [InlineData("text")]
        public void RestrictedFunctionsHaveInvariantConcreteResults(string function)
        {
            var naming = new GenerationNamingEngine(new NamingPart(), "en-US");
            var context = new Dictionary<string, JsonElement> { ["id"] = JsonSerializer.SerializeToElement("idi"), ["name"] = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["en-US"] = "Motor" }) };
            Assert.Equal(function switch { "upper" => "IDI", "lower" => "idi", "pad" => "0007", _ => "Motor" }, naming.Expand(function switch { "upper" => "{{upper(id)}}", "lower" => "{{lower(upper(id))}}", "pad" => "{{pad(7,4)}}", _ => "{{text(name)}}" }, context));
        }

        [Theory]
        [InlineData("{{eval('x')}}")]
        [InlineData("{{device.id + 1}}")]
        [InlineData("{{missing.field}}")]
        [InlineData("{{pad(1,257)}}")]
        public void ExpressionsCannotRunCodeOrInventMissingReferences(string expression)
        {
            Assert.Throws<GenerationValidationException>(() => new GenerationNamingEngine(new NamingPart(), "en-US").Expand(expression, new Dictionary<string, JsonElement>()));
        }

        [Theory]
        [InlineData("name")]
        [InlineData("duplicate")]
        [InlineData("binding")]
        [InlineData("cycle")]
        [InlineData("identity")]
        [InlineData("partial")]
        [InlineData("limit")]
        public void SelfChecksRejectUnsafeExpansion(string scenario)
        {
            var fixture = new PlanningFixture();
            switch (scenario)
            {
                case "name": fixture.Machine.Devices[0].Id = "invalid-id"; break;
                case "duplicate": fixture.Emits.Add(fixture.Emits[1]!.DeepClone()); break;
                case "binding": fixture.Emits[0]!["bind"]!["signal.run"] = "MissingTag"; break;
                case "cycle": fixture.Sources["sources/motor.scl"] = fixture.Sources["sources/motor.scl"].Replace("#Run := #Feedback", "#Run := \"IDB_M01\".Run", StringComparison.Ordinal); break;
                case "identity": fixture.Observed.ProjectIdentity = "other"; break;
                case "partial": fixture.Observed.Complete = false; break;
                default: fixture.Options.MaximumObjects = 1; break;
            }
            Assert.Throws<GenerationValidationException>(() => fixture.Build());
        }

        [Fact]
        public void ConditionalOptionalEmitsUseExistsEqualsAndMembership()
        {
            var fixture = new PlanningFixture(); fixture.Machine.Devices[0].Io["overload"] = "auto";
            fixture.Emits[1]!["when"] = JsonNode.Parse("{\"in\":{\"path\":\"signal.role\",\"values\":[\"feedback\",\"run\"]}}");
            fixture.Emits.Add(JsonNode.Parse("{\"kind\":\"plc.tag\",\"forEach\":\"signals\",\"when\":{\"equals\":{\"path\":\"signal.role\",\"value\":\"overload\"}},\"table\":\"{{unit.tagTable}}\",\"name\":\"{{naming.tag(device,signal)}}\",\"address\":\"{{alloc.io(signal)}}\"}"));
            Assert.Single(fixture.Build().Expected.Tags, t => t.Name.EndsWith("OVERLOAD", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("17", false)]
        [InlineData("19", false)]
        [InlineData("20", true)]
        [InlineData("21", true)]
        public void AvailabilityUsesExplicitReleaseRoster(string release, bool present)
        {
            var availability = GenerationAvailability.Get(release, "CreatePlcBlockGroup"); Assert.Equal(present ? "present" : "absent", availability.Tool); Assert.Equal("NOT RUN", availability.Native);
            var fixture = new PlanningFixture(release); fixture.Documents["structure.json"]["groups"]!.AsArray().Add(JsonNode.Parse("{\"id\":\"units\",\"kind\":\"blocks\",\"path\":\"Units/{{unit.id}}\",\"required\":true}"));
            var result = fixture.Build(); Assert.Equal(!present, result.Plan.Unavailable.Any(u => u.Phase == "plcStructure"));
        }

        [Fact]
        public void ExistingSourceToolBehaviorStaysCurrentNotAccepted()
        {
            var source = GenerationAvailability.Get("21", "GenerateBlocksFromExternalSource"); Assert.Equal("present", source.Tool); Assert.Equal("current", source.BehaviorPolicy); Assert.Equal("NOT RUN", source.Native);
            Assert.Equal("absent", GenerationAvailability.Get("21", "MadeUpTool").Tool);
        }

        [Fact]
        public void OrderingSupportsMoreThan256GeneratedStepsAndBindsArguments()
        {
            var fixture = new PlanningFixture(); fixture.Machine.Devices.Clear();
            for (var i = 1; i <= 100; i++) fixture.Machine.Devices.Add(new MachineDescriptionDevicesItem { Id = "M" + i.ToString("D3", CultureInfo.InvariantCulture), Type = "motor", Parent = "EM01", Station = "PLC1", Name = new Dictionary<string, string> { ["en-US"] = "Motor" }, Io = new Dictionary<string, string> { ["feedback"] = "auto", ["run"] = "auto" } });
            var result = fixture.Build(); Assert.True(result.Plan.Steps.Count > 256);
            var known = new HashSet<string>();
            foreach (var step in result.Plan.Steps) { Assert.All(step.DependsOn, id => Assert.Contains(id, known)); known.Add(step.Id); Assert.Equal(step.ArgumentDigest, CanonicalJson.Hash(JsonSerializer.SerializeToElement(step.Arguments))); }
            var call = result.Plan.Steps.Single(s => s.Expect.Contains == "FC_U01_Calls" && s.Tool == "GenerateBlocksFromExternalSource");
            Assert.True(call.DependsOn.Count >= 300);
        }

        [Fact]
        public void ReadbackOrderingCannotChangePlanIdOrHash()
        {
            var fixture = new PlanningFixture(); fixture.Observed = PlanningFixture.Copy(fixture.Build().Expected); fixture.Observed.Tags.RemoveAt(0);
            var first = fixture.Build(); fixture.Observed.Blocks.Reverse(); fixture.Observed.Tags.Reverse(); fixture.Observed.ExternalSources.Reverse();
            Assert.Equal(first.CanonicalPlan, fixture.Build().CanonicalPlan);
        }

        private static int Count(ProjectModel model) => model.Devices.Count + model.Blocks.Count + model.Types.Count + model.TagTables.Count + model.Tags.Count + model.Groups.Count + model.Networks.Count + model.Alarms.Count + model.Screens.Count + model.ExternalSources.Count;
    }
}
