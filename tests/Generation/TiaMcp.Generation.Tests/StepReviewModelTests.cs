using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.Generation;
using Xunit;

namespace TiaMcp.Generation.Tests
{
    public sealed class StepReviewModelTests
    {
        [Fact]
        public void Unbound_transition_accepts_the_first_verified_project_identity()
        {
            var after = new JsonObject { ["processId"] = 123, ["processStartUtc"] = "2026-11-04T00:00:00Z",
                ["projectPath"] = "C:/Fixture.ap21", ["generation"] = "c6474692c3e740658035993a02ea915e" };
            var tree = new JsonObject { ["devices"] = new JsonArray() };
            Assert.True(StepReviewRules.Transition(new JsonObject(), after, "C:/Fixture.ap21", tree).Matches);
            after["processId"] = null;
            Assert.False(StepReviewRules.Transition(new JsonObject(), after, "C:/Fixture.ap21", tree).Matches);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Project_transition_compares_generation_as_an_opaque_value(bool guid, bool unchanged)
        {
            JsonNode previous = guid ? JsonValue.Create("c6474692c3e740658035993a02ea915e")! : JsonValue.Create(7L)!;
            JsonNode next = unchanged ? previous.DeepClone()
                : guid ? JsonValue.Create("ee1d86cbdfd1483c9d772d68c251728a")! : JsonValue.Create(3L)!;
            var before = new JsonObject { ["processId"] = 123, ["processStartUtc"] = "2026-11-04T00:00:00Z", ["generation"] = previous };
            var after = (JsonObject)before.DeepClone();
            after["generation"] = next; after["projectPath"] = "C:/Fixture.ap21";
            var verdict = StepReviewRules.Transition(before, after, "C:/Fixture.ap21", new JsonObject { ["devices"] = new JsonArray() });
            Assert.Equal(!unchanged, verdict.Matches);
        }

        [Theory]
        [InlineData("cleanup")]
        [InlineData("record")]
        public void NewPhasesRoundTripInPlanOne(string phase)
        {
            var json = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Examples", "plan.json")))!;
            json["phases"]!.AsArray().Add(phase);
            var plan = GenerationDocuments.Load<GenerationPlan>(json.ToJsonString());
            Assert.Contains(phase, plan.Phases); Assert.Contains(phase, GenerationDocuments.Canonical(plan));
        }
        [Theory]
        [InlineData("EnsureSubnet")]
        [InlineData("AttachDeviceNodeToSubnet")]
        public void Network_write_review_requires_the_planned_project_and_a_tool_hash(string tool)
        {
            var plan = new GenerationPlan();
            plan.Target.ProjectIdentity = "C:/Fixture.ap21";
            var step = new GenerationPlanStepsItem { Tool = tool, Review = new GenerationStepReview {
                Rule = StepReviewRules.WritePreview } };
            step.Review.Expect["projectFile"] = JsonSerializer.SerializeToElement(plan.Target.ProjectIdentity);
            step.Review.Witness["planHash"] = "sha256";
            step.Arguments["dryRun"] = JsonSerializer.SerializeToElement(false);
            step.Arguments["expectedProjectFile"] = JsonSerializer.SerializeToElement(plan.Target.ProjectIdentity);
            Assert.True(StepReviewRules.Supports(step));
            Assert.True((bool?)StepReviewRules.PreviewArguments(step)["dryRun"]);
            var projection = StepReviewRules.Project(new JsonObject { ["planHash"] = new string('a', 64),
                ["targetIdentity"] = new JsonObject { ["identity"] = new JsonObject { ["projectPath"] = plan.Target.ProjectIdentity } } });
            Assert.True(StepReviewRules.Compare(plan, step, projection, new RunFacts(plan), 123).Matches);
            var final = StepReviewRules.FinalArguments(step, projection);
            Assert.Equal(new string('a', 64), (string?)final["expectedPlanHash"]);
            Assert.False((bool?)final["dryRun"]);
            projection["projectFile"] = "C:/Other.ap21";
            Assert.False(StepReviewRules.Compare(plan, step, projection, new RunFacts(plan), 123).Matches);
            projection["projectFile"] = plan.Target.ProjectIdentity;
            projection["planHash"] = "unreviewed";
            Assert.False(StepReviewRules.Compare(plan, step, projection, new RunFacts(plan), 123).Matches);
            Assert.Throws<ArgumentException>(() => StepReviewRules.FinalArguments(step, projection));
        }

        [Fact]
        public void OnlyExpectedPlanHashMayBeLateFilled()
        {
            var step = new GenerationPlanStepsItem { Tool = "ImportPlcExternalSource", Review = new GenerationStepReview { Rule = StepReviewRules.Import } };
            step.Arguments["dryRun"] = JsonSerializer.SerializeToElement(false);
            var preview = StepReviewRules.PreviewArguments(step); Assert.True((bool?)preview["dryRun"]);
            var final = StepReviewRules.FinalArguments(step, new JsonObject { ["planHash"] = new string('a', 64) });
            Assert.Equal(new string('a',64), (string?)final["expectedPlanHash"]); Assert.False((bool?)final["dryRun"]);
            step.Review.LateArguments.Add("sourceName"); Assert
                .Throws<ArgumentException>(() => StepReviewRules.FinalArguments(step, new JsonObject { ["planHash"] = new string('a',64) }));
            step.Review.LateArguments.Remove("sourceName"); step.Arguments["expectedPlanHash"] = JsonSerializer.SerializeToElement("forged");
            Assert.Throws<ArgumentException>(() => StepReviewRules.PreviewArguments(step));
        }
    }
}
