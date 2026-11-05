using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.Generation;
using Xunit;

namespace TiaMcp.Generation.Planning.Tests
{
    public sealed class StagedPlanningTests
    {
        public static IEnumerable<object[]> Releases => PlanningFixture.Releases.Select(r => new object[] { r });
        private static PlanningFixture WriteFixture(string release)
        {
            var fixture = new PlanningFixture(release);
            fixture.Observed.Devices.Clear();
            fixture.Machine.Target.Project.ProjectIdentity = fixture.Observed.ProjectIdentity = "C:/Projects/Fixture.ap" + release;
            fixture.Emits[0]!["group"] = "Program";
            return fixture;
        }
        [Theory, MemberData(nameof(Releases))]
        public void DeviceAndMoveWritesCarryTheirOwnReviewedPreview(string release)
        {
            var result = WriteFixture(release).Build();
            var writes = result.Plan.Steps.Where(step => step.Tool is "CreateDevice" or "MovePlcBlockToGroup").ToArray();
            Assert.Contains(writes, step => step.Tool == "CreateDevice");
            Assert.Contains(writes, step => step.Tool == "MovePlcBlockToGroup");
            foreach (var step in writes)
            {
                Assert.Equal("write-preview/1", step.Review?.Rule);
                Assert.True(StepReviewRules.Supports(step));
                Assert.False(step.Arguments["dryRun"].GetBoolean());
                Assert.False(step.Arguments.ContainsKey("expectedPlanHash"));
                Assert.True((bool?)StepReviewRules.PreviewArguments(step)["dryRun"]);
                if (step.Tool == "MovePlcBlockToGroup")
                {
                    Assert.True(step.Arguments.ContainsKey("targetGroupPath"));
                    Assert.False(step.Arguments.ContainsKey("groupPath"));
                }
            }
            Assert.All(result.Plan.Steps.Where(step => step.Tool == "CreatePlcBlockGroup"),
                step => Assert.False(step.Arguments["dryRun"].GetBoolean()));
        }

        [Theory]
        [InlineData("CreateDevice")]
        [InlineData("MovePlcBlockToGroup")]
        public void WriteReviewRequiresHashAndPlannedProject(string tool)
        {
            var result = WriteFixture("21").Build();
            var step = result.Plan.Steps.First(item => item.Tool == tool);
            string project = result.Plan.Target.ProjectIdentity!;
            var good = new JsonObject { ["planHash"] = new string('a', 64), ["projectFile"] = project };
            var facts = new RunFacts(result.Plan);
            Assert.True(StepReviewRules.Compare(result.Plan, step, good, facts, 123).Matches);
            Assert.Equal(new string('a', 64), (string?)StepReviewRules.FinalArguments(step, good)["expectedPlanHash"]);
            good["planHash"] = "bad";
            Assert.False(StepReviewRules.Compare(result.Plan, step, good, facts, 123).Matches);
            Assert.Throws<ArgumentException>(() => StepReviewRules.FinalArguments(step, good));
            good["planHash"] = new string('a', 64); good["projectFile"] = "C:/Other.ap21";
            Assert.False(StepReviewRules.Compare(result.Plan, step, good, facts, 123).Matches);
        }

        [Theory, MemberData(nameof(Releases))]
        public void RootSourceImportsCarryApprovedReviewAndBlocksMoveAfterGeneration(string release)
        {
            var result = BasicPackageFixture.Build(release);
            Assert.All(result.Expected.ExternalSources, s => Assert.Equal("", s.Group));
            foreach (var import in result.Plan.Steps.Where(s => s.Tool == "ImportPlcExternalSource"))
            {
                Assert.Equal("", import.Arguments["groupPath"].GetString());
                Assert.Equal(StepReviewRules.Import, import.Review!.Rule);
                Assert.Equal(new[] { "expectedPlanHash" }, import.Review.LateArguments);
                Assert.False(import.Arguments.ContainsKey("expectedPlanHash"));
            }
            foreach (var move in result.Plan.Steps.Where(s => s.Tool == "MovePlcBlockToGroup"))
            {
                Assert.False(move.Arguments["autoCreateGroup"].GetBoolean());
                var generated = result.Plan.Steps.Single(s => s.Tool == "GenerateBlocksFromExternalSource" && s.Key + "/MovePlcBlockToGroup" == move.Key);
                Assert.Contains(generated.Id, move.DependsOn);
                Assert.Equal(StepReviewRules.Generate, generated.Review!.Rule);
                Assert.Single(generated.Review.Forbid["objectKeysBefore"]);
            }
            Assert.Contains("cleanup", result.Plan.Phases); Assert.Contains("record", result.Plan.Phases);
        }
        [Theory, MemberData(nameof(Releases))]
        public void LineageIsDeterministicAndIncludedInTheSeed(string release)
        {
            var fixture = new PlanningFixture(release); var first = fixture.Build();
            fixture.Options.Lineage = new Dictionary<string, string> { ["runId"] = "run-1", ["status"] = "partial" };
            var second = fixture.Build(); Assert.NotEqual(first.Plan.PlanId, second.Plan.PlanId); Assert.Equal(second.CanonicalPlan, fixture.Build().CanonicalPlan);
            fixture.Options.Lineage["status"] = "unknown"; Assert.NotEqual(second.Plan.PlanId, fixture.Build().Plan.PlanId);
        }
        [Fact]
        public void ObservedInventoriesAreInThePlanHash()
        {
            var fixture = new PlanningFixture(); fixture.Observed.Blocks.Add(new ProjectBlock { Station = "PLC1", Name = "Manual", Group = "ManualGroup" });
            fixture.Observed.ExternalSources.Add(new ProjectExternalSource { Station = "PLC1", Name = "Manual.scl" });
            var plan = fixture.Build().Plan; Assert.Contains("block:ManualGroup/Manual", plan.Observed["PLC1"].ObjectKeys);
            Assert.Contains("Manual.scl", plan.Observed["PLC1"].SourceNames);
            var old = plan.PlanHash; plan.Observed["PLC1"].SourceNames.Clear(); Assert.NotEqual(old, GenerationDocuments.PlanHash(GenerationDocuments.Canonical(plan)));
        }
        [Theory, MemberData(nameof(Releases))]
        public void NonRootTypePlacementIsUnavailableRatherThanClaimingRootGenerationMeetsIt(string release)
        {
            var fixture = new PlanningFixture(release);
            fixture.Documents["library.json"]["types"]![0]!["kind"] = "UDT";
            fixture.Documents["library.json"]["types"]![0]!["interface"] = new JsonObject();
            fixture.Sources["sources/motor.scl"] = "TYPE \"MotorType\"\nSTRUCT\nFeedback : Bool;\nEND_STRUCT;\nEND_TYPE\n";
            fixture.Emits.Clear(); fixture.Emits.Add(new JsonObject { ["kind"] = "plc.type", ["type"] = "lib:fixture.planning/fb.motor@^1", ["group"] = "Types" });
            var result = fixture.Build();
            Assert.Contains(result.Plan.Unavailable, u => u.Phase == "plcTypes" && u.Reason.Contains("no current tool"));
            Assert.DoesNotContain(result.Plan.Steps, s => s.Phase == "plcTypes");
        }
        [Theory, MemberData(nameof(Releases))]
        public void NewProjectHasPredictedIdentityAndExecutableTransitionBeforeEveryOtherStep(string release)
        {
            var fixture = new PlanningFixture(release);
            fixture.Machine.Target.Project.Mode = "new"; fixture.Machine.Target.Project.ProjectIdentity = null;
            fixture.Machine.Target.Project.Directory = "C:/Projects"; fixture.Machine.Target.Project.Name = "Fixture";
            var result = GenerationPlanner.Build(fixture.Package(), GenerationDocuments.Canonical(fixture.Machine), null, fixture.Options);
            var create = result.Plan.Steps.First();
            Assert.Equal("CreateProject", create.Tool); Assert.Equal("transition", create.Review!.Mode);
            Assert.Equal(StepReviewRules.CreateProject, create.Review.Rule); Assert.Empty(create.Review.LateArguments);
            Assert.Equal(GenerationProjectPath.CreationFile(release, "C:/Projects", "Fixture"), result.Plan.Target.ProjectIdentity);
            Assert.Equal(result.Plan.Target.ProjectIdentity, create.Review.Expect["projectPath"].GetString());
            Assert.All(result.Plan.Steps.Skip(1), step => Assert.Contains(create.Id, step.DependsOn));
            Assert.DoesNotContain(result.Plan.Steps, step => step.Tool == "ArchiveSavedProject");
            Assert.All(result.Plan.Observed.Values, inventory => { Assert.Empty(inventory.SourceNames); Assert.Empty(inventory.ObjectKeys); });
        }
    }
}
