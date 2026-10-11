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
        [Fact]
        public void OnlyExpectedPlanHashMayBeLateFilled()
        {
            var step = new GenerationPlanStepsItem { Tool = "ImportPlcExternalSource", Review = new GenerationStepReview { Rule = StepReviewRules.Import } };
            step.Arguments["dryRun"] = JsonSerializer.SerializeToElement(false);
            var preview = StepReviewRules.PreviewArguments(step); Assert.True((bool?)preview["dryRun"]);
            var final = StepReviewRules.FinalArguments(step, new JsonObject { ["planHash"] = new string('a', 64) });
            Assert.Equal(new string('a',64), (string?)final["expectedPlanHash"]); Assert.False((bool?)final["dryRun"]);
            step.Review.LateArguments.Add("sourceName"); Assert.Throws<ArgumentException>(() => StepReviewRules.FinalArguments(step, new JsonObject { ["planHash"] = new string('a',64) }));
            step.Review.LateArguments.Remove("sourceName"); step.Arguments["expectedPlanHash"] = JsonSerializer.SerializeToElement("forged");
            Assert.Throws<ArgumentException>(() => StepReviewRules.PreviewArguments(step));
        }
    }
}
