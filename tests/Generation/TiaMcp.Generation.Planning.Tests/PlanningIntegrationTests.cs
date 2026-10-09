using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using TiaMcp.Logic.Generation;
using Xunit;

namespace TiaMcp.Generation.Planning.Tests
{
    public sealed class PlanningIntegrationTests
    {
        [Theory]
        [InlineData("member")]
        [InlineData("contract")]
        [InlineData("output")]
        public void ReferencesAndInterfaceContractsCannotGenerateInvalidCalls(string scenario)
        {
            var fixture = new PlanningFixture();
            if (scenario == "member") fixture.Emits[0]!["bind"]!["signal.feedback"] = "IDB_M01.MissingMember";
            else if (scenario == "contract") fixture.Documents["library.json"]["types"]![0]!["interface"]!["in"]![0]!["name"] = "MissingParameter";
            else fixture.Emits[0]!["bind"]!["signal.run"] = "TRUE";
            Assert.Throws<GenerationValidationException>(() => fixture.Build());
        }

        [Theory]
        [InlineData("block")]
        [InlineData("udt")]
        [InlineData("number")]
        [InlineData("address")]
        public void BindingTypesRejectWholeBlocksWrongUdtsAndIncompatibleLiterals(string scenario)
        {
            var fixture = new PlanningFixture();
            fixture.Emits[0]!["bind"]!["signal.feedback"] = scenario switch { "block" => "IDB_M01", "udt" => "IDB_M01.State", "number" => "1", _ => "%IW32" };
            if (scenario == "udt") fixture.Sources["sources/motor.scl"] = "TYPE \"UDT_State\"\nSTRUCT\n    Ready : Bool;\nEND_STRUCT;\nEND_TYPE\n" + fixture.Sources["sources/motor.scl"].Replace("VAR_INPUT", "VAR\n    State : \"UDT_State\";\nEND_VAR\nVAR_INPUT", StringComparison.Ordinal);
            Assert.Contains(Assert.Throws<GenerationValidationException>(() => fixture.Build()).Errors, e => e.Rule == "type");
        }

        [Fact]
        public void BooleanAndBitAddressInputsRemainValidBindings()
        {
            foreach (var binding in new[] { "TRUE", "%I0.0" })
            {
                var fixture = new PlanningFixture(); fixture.Emits[0]!["bind"]!["signal.feedback"] = binding;
                Assert.Empty(fixture.Build().Plan.Conflicts);
            }
        }
        [Fact]
        public void WidgetExpansionRetainsEveryDeviceAndAllocatesDistinctSlots()
        {
            var fixture = new PlanningFixture(); fixture.Documents["sources/widget.json"] = JsonNode.Parse("{\"label\":\"{{device.id}}\"}")!.AsObject();
            fixture.Emits.Add(JsonNode.Parse("{\"kind\":\"hmi.widget\",\"screen\":\"Overview\",\"template\":\"sources/widget.json\",\"slot\":\"auto\"}"));
            var result = fixture.Build(); var screen = Assert.Single(result.Expected.Screens); Assert.Equal(new[] { 0, 1 }, screen.Widgets.Select(w => w.Slot));
            Assert.Equal(new[] { "M01", "M02" }, screen.Widgets.Select(w => w.Device)); Assert.NotEqual(screen.Widgets[0].TemplateFingerprint, screen.Widgets[1].TemplateFingerprint);
            fixture.Observed = PlanningFixture.Copy(result.Expected); Assert.Empty(fixture.Build().Plan.Steps);
        }

        [Fact]
        public void ScreenTemplateExpandsAndChecksNamesInsideItsDesign()
        {
            var fixture = new PlanningFixture(); fixture.Documents["sources/screen.json"] = JsonNode.Parse("{\"screen\":{\"name\":\"Overview\",\"width\":800,\"height\":480},\"items\":[{\"type\":\"Text\",\"name\":\"Status\",\"text\":\"{{text(machine.name)}}\"}]}")!.AsObject();
            fixture.Emits.Add(JsonNode.Parse("{\"kind\":\"hmi.screen\",\"name\":\"Overview\",\"template\":\"sources/screen.json\"}"));
            Assert.Equal("Synthetic line", fixture.Build().Plan.Steps.Single(s => s.Tool == "ApplyUnifiedHmiScreenDesign").Arguments["design"].GetProperty("items")[0].GetProperty("text").GetString());
            fixture.Documents["sources/screen.json"]["items"]![0]!["name"] = "invalid-name";
            Assert.Throws<GenerationValidationException>(() => fixture.Build());
        }
        [Fact]
        public void InheritanceReplacesWholeEntryAndPreservesPinnedParentResources()
        {
            var parent = new PlanningFixture(); var child = new PlanningFixture();
            child.Documents.Clear(); child.Sources.Clear();
            child.Documents["naming.json"] = JsonNode.Parse("{\"rules\":[{\"id\":\"idb\",\"kind\":\"instanceDb\",\"template\":\"IDB_Custom_{{device.id}}\",\"pattern\":\"^IDB_Custom_[A-Z0-9]+$\"}]}")!.AsObject();
            var childPackage = child.Package("fixture.child", new PackageReference { Package = "fixture.planning", Version = "^1" });
            child.Machine.Standard.Package = "fixture.child";
            var result = GenerationPlanner.Build(childPackage, GenerationDocuments.Canonical(child.Machine), child.Observed, child.Options, new[] { parent.Package() });
            Assert.Contains(result.Expected.Blocks, b => b.Name == "IDB_Custom_M01"); Assert.Contains(result.Expected.Blocks, b => b.Name == "FB_Motor");
            var effective = EffectiveGenerationPackage.Resolve(childPackage, new[] { parent.Package() });
            var rule = effective.GetPart<NamingPart>(effective.Manifest.Parts.Naming!).Rules.Single(r => r.Id == "idb"); Assert.Null(rule.MaxLength);
            Assert.Equal(result.Plan.Package.Hash, effective.ContentHash);
            Assert.Equal(result.CanonicalPlan, GenerationPlanner.Build(childPackage, GenerationDocuments.Canonical(child.Machine), child.Observed, child.Options, new[] { parent.Package() }).CanonicalPlan);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void InheritanceRejectsMissingOrAmbiguousPinnedParents(bool ambiguous)
        {
            var child = new PlanningFixture(); var childPackage = child.Package("fixture.child", new PackageReference { Package = "fixture.planning", Version = "^1" });
            var parent = new PlanningFixture().Package();
            Assert.Throws<GenerationValidationException>(() => EffectiveGenerationPackage.Resolve(childPackage, ambiguous ? new[] { parent, parent } : Array.Empty<StandardPackage>()));
        }

        [Fact]
        public void UdtPrecedesFbEvenWhenFilesAndPhasesSuggestTheReverseOrder()
        {
            var fixture = new PlanningFixture();
            fixture.Sources["sources/types.scl"] = "TYPE \"UDT_State\"\nSTRUCT\n    Ready : Bool;\nEND_STRUCT;\nEND_TYPE\n";
            fixture.Sources["sources/motor.scl"] = fixture.Sources["sources/motor.scl"].Replace("VAR_INPUT", "VAR\n    State : \"UDT_State\";\nEND_VAR\nVAR_INPUT", StringComparison.Ordinal);
            fixture.Documents["library.json"]["types"]![0]!["implementations"]![0]!["files"]!.AsArray().Add("sources/types.scl");
            var result = fixture.Build(); var udt = result.Plan.Steps.Single(s => s.Tool == "GenerateBlocksFromExternalSource" && s.Expect.Contains == "UDT_State");
            var fb = result.Plan.Steps.Single(s => s.Tool == "GenerateBlocksFromExternalSource" && s.Expect.Contains == "FB_Motor");
            Assert.Contains(udt.Id, fb.DependsOn); Assert.True(result.Plan.Steps.IndexOf(udt) < result.Plan.Steps.IndexOf(fb));
            fixture.Observed = PlanningFixture.Copy(result.Expected); fixture.Observed.Types[0].InterfaceFingerprint = null;
            Assert.Contains(fixture.Build().Plan.Conflicts, c => c.Key.Contains("UDT_State", StringComparison.Ordinal));
        }

        [Fact]
        public void SharedSclFileIsSplitSoIdenticalExistingDeclarationsAreNotReimported()
        {
            var fixture = new PlanningFixture();
            fixture.Sources["sources/motor.scl"] = "TYPE \"UDT_State\"\nSTRUCT\n    Ready : Bool;\nEND_STRUCT;\nEND_TYPE\n" + fixture.Sources["sources/motor.scl"];
            var first = fixture.Build(); fixture.Observed.Types = PlanningFixture.Copy(first.Expected).Types;
            fixture.Observed.ExternalSources = first.Expected.ExternalSources.Where(s => first.Artifacts.Any(a => a.Path.EndsWith(s.Name, StringComparison.Ordinal) && a.Content.StartsWith("TYPE", StringComparison.Ordinal))).ToList();
            var result = fixture.Build(); Assert.DoesNotContain(result.Plan.Steps, s => s.Expect.Contains == "UDT_State"); Assert.Contains(result.Plan.Steps, s => s.Expect.Contains == "FB_Motor");
        }

        [Fact]
        public void SequenceNamesAllocateOncePerDevice()
        {
            var fixture = new PlanningFixture(); fixture.Documents["naming.json"]["rules"]!.AsArray().Single(n => n!["id"]!.GetValue<string>() == "idb")!["template"] = "IDB_{{seq('sequence')}}";
            var result = fixture.Build(); Assert.Contains(result.Expected.Blocks, b => b.Name == "IDB_100"); Assert.Contains(result.Expected.Blocks, b => b.Name == "IDB_101");
            Assert.Contains("\"IDB_100\"(", result.Artifacts.Single(a => a.Content.StartsWith("FUNCTION \"FC_U01_Calls\"", StringComparison.Ordinal)).Content);
        }

        [Theory]
        [InlineData("17", false)]
        [InlineData("21", true)]
        public void CompleteScreenDesignMapsBothWritesAndRetainsTemplateEvidence(string release, bool available)
        {
            var fixture = new PlanningFixture(release);
            fixture.Documents["sources/screen.json"] = JsonNode.Parse("{\"screen\":{\"name\":\"Overview\",\"width\":800,\"height\":480},\"items\":[]}")!.AsObject();
            fixture.Emits.Add(JsonNode.Parse("{\"kind\":\"hmi.screen\",\"name\":\"Overview\",\"template\":\"sources/screen.json\"}"));
            var result = fixture.Build(); Assert.Single(result.Expected.Screens);
            Assert.Equal(available, result.Plan.Steps.Any(s => s.Tool == "EnsureUnifiedHmiScreen")); Assert.Equal(available, result.Plan.Steps.Any(s => s.Tool == "ApplyUnifiedHmiScreenDesign"));
            if (available)
            {
                var ensure = result.Plan.Steps.Single(s => s.Tool == "EnsureUnifiedHmiScreen"); var design = result.Plan.Steps.Single(s => s.Tool == "ApplyUnifiedHmiScreenDesign"); Assert.Contains(ensure.Id, design.DependsOn);
            }
            else Assert.Contains(result.Plan.Unavailable, u => u.Phase == "hmi");
            fixture.Observed = PlanningFixture.Copy(result.Expected); Assert.Empty(fixture.Build().Plan.Steps);
            fixture.Observed.Screens[0].TemplateFingerprint = "changed"; Assert.Contains(fixture.Build().Plan.Conflicts, c => c.Key.EndsWith("/screen/Overview", StringComparison.Ordinal));
        }

        [Fact]
        public void NetworkBindingUsesExplicitPathAndInterfaceAndDependsOnSubnetCreation()
        {
            var fixture = new PlanningFixture();
            fixture.Emits.Add(JsonNode.Parse("{\"kind\":\"network\",\"when\":{\"equals\":{\"path\":\"device.id\",\"value\":\"M01\"}},\"name\":\"PN_IE\",\"arguments\":{\"deviceItemPath\":\"PLC1/CPU\",\"interfaceIndex\":1}}"));
            var result = fixture.Build(); var subnet = result.Plan.Steps.Single(s => s.Tool == "EnsureSubnet"); var attach = result.Plan.Steps.Single(s => s.Tool == "AttachDeviceNodeToSubnet");
            Assert.Contains(subnet.Id, attach.DependsOn); Assert.Equal("PLC1/CPU", attach.Arguments["deviceItemPath"].GetString()); Assert.Equal(1, attach.Arguments["interfaceIndex"].GetInt32());
        }

        [Fact]
        public void ProfinetNameRemainsPartOfReadbackComparisonWhenWritesAreUnavailable()
        {
            var fixture = new PlanningFixture(); fixture.Machine.Stations[0].ProfinetName = "line-plc";
            var result = fixture.Build(); Assert.Equal("line-plc", Assert.Single(result.Expected.Networks).ProfinetName);
            Assert.Contains(result.Plan.Unavailable, u => u.Phase == "network");
            fixture.Observed = PlanningFixture.Copy(result.Expected); Assert.Empty(fixture.Build().Plan.Steps);
            fixture.Observed.Networks[0].ProfinetName = "edited-plc";
            Assert.Contains(fixture.Build().Plan.Conflicts, c => c.Detail.Contains("profinetName", StringComparison.Ordinal));
        }

        [Fact]
        public void AlarmExpansionHasStableNumbersAndComparesLocalizedTextsWithoutInventingAnImport()
        {
            var fixture = new PlanningFixture();
            fixture.Documents["alarms.json"] = JsonNode.Parse("{\"classes\":[{\"id\":\"fault\",\"priority\":1,\"acknowledgement\":\"single\"}],\"templates\":[{\"id\":\"fault\",\"deviceType\":\"motor\",\"class\":\"fault\",\"textKey\":\"fault\",\"backend\":\"Program_Alarm\"}],\"texts\":{\"fault\":{\"en-US\":\"{{device.id}} fault\"}},\"numbering\":{\"start\":1,\"end\":9}}")!.AsObject();
            fixture.Emits.Add(JsonNode.Parse("{\"kind\":\"alarm\",\"class\":\"fault\",\"textKey\":\"fault\"}"));
            var result = fixture.Build(); Assert.Equal(new[] { 1, 2 }, result.Expected.Alarms.Select(a => a.Number)); Assert.Equal("M01 fault", result.Expected.Alarms[0].Texts["en-US"]);
            Assert.Contains(result.Plan.Unavailable, u => u.Phase == "alarms"); Assert.DoesNotContain(result.Plan.Steps, s => s.Phase == "alarms");
            fixture.Observed = PlanningFixture.Copy(result.Expected); fixture.Observed.Alarms[0].Texts["en-US"] = "Edited";
            Assert.Contains(fixture.Build().Plan.Conflicts, c => c.Detail.Contains("texts", StringComparison.Ordinal));
            fixture.Documents["alarms.json"]["numbering"]!["template"] = "1";
            Assert.Contains(Assert.Throws<GenerationValidationException>(() => fixture.Build()).Errors, e => e.Rule == "collision");
        }

        [Fact]
        public void ManualMemoryTagsDoNotBreakIoCollisionComparison()
        {
            var fixture = new PlanningFixture(); fixture.Observed.Tags.Add(new ProjectTag { Station = "PLC1", Name = "Manual", Table = "Manual", Address = "%M100.0", DataType = "Bool" });
            Assert.Empty(fixture.Build().Plan.Conflicts);
        }

        [Fact]
        public void StagingVerifiesHashesAndNeverOverwritesDifferentFiles()
        {
            var root = Path.GetFullPath(Path.Combine("bin-build", "refactor", "P8-31b-evidence", "stage-test-" + Guid.NewGuid().ToString("N")));
            var fixture = new PlanningFixture(); fixture.Options.ArtifactRoot = root; var result = fixture.Build(); result.StageArtifacts(); result.StageArtifacts();
            foreach (var artifact in result.Artifacts) Assert.Equal(artifact.Sha256, CanonicalJson.HashBytes(File.ReadAllBytes(Path.Combine(root, artifact.Path.Replace('/', Path.DirectorySeparatorChar)))));
            var first = result.Artifacts[0]; var path = Path.Combine(root, first.Path.Replace('/', Path.DirectorySeparatorChar)); File.WriteAllText(path, "Edited", new UTF8Encoding(false));
            Assert.Throws<GenerationValidationException>(() => result.StageArtifacts()); Assert.Equal("Edited", File.ReadAllText(path));
        }

        [Fact]
        public void NewProjectHasAnExplicitCreateAndFinalSaveAndExistingBackupPreservesBinding()
        {
            var fixture = new PlanningFixture(); fixture.Machine.Target.Project.Mode = "new"; fixture.Machine.Target.Project.ProjectIdentity = null; fixture.Machine.Target.Project.Name = "Line"; fixture.Machine.Target.Project.Directory = "C:/Projects";
            var result = fixture.Build(); Assert.Equal("CreateProject", result.Plan.Steps[0].Tool); Assert.Equal("SaveProject", result.Plan.Steps.Last().Tool);
            fixture = new PlanningFixture(); result = fixture.Build(); Assert.Equal("ArchiveSavedProject", result.Plan.Steps[0].Tool); Assert.DoesNotContain(result.Plan.Steps, s => s.Tool == "SaveProjectCopy");
        }

        [Fact]
        public void HashBindsStagingRootAndEditedStepArguments()
        {
            var fixture = new PlanningFixture(); var first = fixture.Build(); fixture.Options.ArtifactRoot = "C:/another-staging-root"; var second = fixture.Build();
            Assert.NotEqual(first.Plan.PlanId, second.Plan.PlanId); Assert.NotEqual(first.Plan.PlanHash, second.Plan.PlanHash);
            first.Plan.Steps[0].Arguments["archivePath"] = System.Text.Json.JsonSerializer.SerializeToElement("C:/edited.zap21");
            Assert.Throws<GenerationValidationException>(() => first.StageArtifacts());
        }
    }
}
