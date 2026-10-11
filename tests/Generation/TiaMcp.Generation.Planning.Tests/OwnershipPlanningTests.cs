using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.Generation;
using Xunit;

namespace TiaMcp.Generation.Planning.Tests
{
    public sealed class OwnershipPlanningTests
    {
        public static IEnumerable<object[]> Releases => PlanningFixture.Releases.Select(r => new object[] { r });
        public static IEnumerable<object[]> SourceOwnership => PlanningFixture.Releases.SelectMany(r => new[] { new object[] { r, false }, new object[] { r, true } });
        [Theory, MemberData(nameof(SourceOwnership))]
        public void RetainedOwnedOrExplicitlyAdoptedSourceCanFillMissingObjectsWithVerifiedCustody(string release, bool adopt)
        {
            var fixture = new PlanningFixture(release);
            fixture.Machine.Target.Project.ProjectIdentity = "C:/Projects/Fixture/Fixture.ap" + release;
            fixture.Observed.ProjectIdentity = fixture.Machine.Target.Project.ProjectIdentity;
            var initial = fixture.Build(); fixture.Observed = PlanningFixture.Copy(initial.Expected);
            fixture.Observed.Blocks.RemoveAll(b => b.Name == "FB_Motor");
            fixture.Options.Ownership = initial.Plan.Objects;
            foreach (var item in fixture.Options.Ownership)
            {
                item.State = "owned"; item.Witness = new GenerationWitness { Kind = "content", Tool = "read-fixture" };
                fixture.Options.Witnesses[item.Key] = item.Witness;
            }
            var original = initial.Plan.Steps.Single(s => s.Key == "PLC1/block/FB_Motor");
            string name = original.Arguments["externalSourceName"].GetString()!;
            var source = fixture.Options.Ownership.Single(o => o.Key == "PLC1/externalSource/" + name);
            var witness = new GenerationWitness { Kind = "external-source", Tool = "ListPlcExternalSources" };
            witness.Values["present"] = JsonSerializer.SerializeToElement(true);
            witness.Values["name"] = JsonSerializer.SerializeToElement(name);
            witness.Values["contentHash"] = JsonSerializer.SerializeToElement(source.Artifact!.Sha256);
            source.Witness = witness; fixture.Options.Witnesses[source.Key] = witness;
            if (adopt) { source.State = "unknown"; source.Witness = null; fixture.Options.Adopt.Add(source.Key); }
            var plan = fixture.Build().Plan;
            var replacement = Assert.Single(plan.Steps, s => s.Key.EndsWith("/replace", StringComparison.Ordinal));
            Assert.Equal(StepReviewRules.Delete, replacement.Review!.Rule);
            var import = Assert.Single(plan.Steps, s => s.Tool == "ImportPlcExternalSource"); Assert.Contains(replacement.Id, import.DependsOn);
            var projection = new JsonObject { ["release"] = release, ["projectFile"] = plan.Target.ProjectIdentity, ["processId"] = 42,
                ["softwarePath"] = "PLC1", ["groupPath"] = "", ["sourceName"] = name, ["targetIdentity"] = "native-source-identity",
                ["status"] = "planned", ["executed"] = false, ["attempted"] = false, ["requiresSessionReset"] = false,
                ["planHash"] = new string('a', 64), ["sourcesBefore"] = JsonSerializer.SerializeToNode(fixture.Observed.ExternalSources.Select(s => s.Name)) };
            var verdict = StepReviewRules.Compare(plan, replacement, projection, new RunFacts(plan), 42);
            Assert.True(verdict.Matches, verdict.Differences.ToJsonString());
            plan.Objects.Single(o => o.Key == source.Key).Witness = null;
            Assert.False(StepReviewRules.Compare(plan, replacement, projection, new RunFacts(plan), 42).Matches);
        }
        [Theory, MemberData(nameof(Releases))]
        public void OwnershipClassifiesUnchangedModifiedRegenerationUnknownAdoptionAndMissing(string release)
        {
            var desired = new ProjectModel { Blocks = new List<ProjectBlock> { new ProjectBlock { Station = "PLC1", Name = "FB_A", CodeFingerprint = "code-v1" } } };
            var present = PlanningFixture.Copy(desired); present.Blocks[0].CodeFingerprint = null;
            var item = ProjectModelComparison.Describe(desired).Single(); item.State = "owned";
            item.Witness = new GenerationWitness { Kind = release is "20" or "21" ? "plc-object-fingerprints" : "modified-date", Tool = release is "20" or "21" ? "GetPlcObjectFingerprints" : "GetPlcBlockInfo" };
            item.Witness.Values["value"] = JsonSerializer.SerializeToElement("post-save-v1");
            var options = new GenerationPlanningOptions { Ownership = new List<GenerationRecordObject> { item } }; options.Witnesses[item.Key] = item.Witness;
            Assert.Equal("owned-unchanged", ProjectModelComparison.Compare(desired, present, options).Single().Detail);
            desired.Blocks[0].CodeFingerprint = "code-v2";
            Assert.Equal("regeneration-required", ProjectModelComparison.Compare(desired, present, options).Single().Detail);
            options.Witnesses[item.Key] = new GenerationWitness { Kind = item.Witness.Kind, Tool = item.Witness.Tool };
            Assert.Equal("modified-after-generation", ProjectModelComparison.Compare(desired, present, options).Single().Detail);
            item.State = "unknown"; item.Witness = null;
            Assert.Equal("unknown-outcome", ProjectModelComparison.Compare(desired, present, options).Single().Detail);
            options.Adopt.Add(item.Key);
            var adopted = ProjectModelComparison.Compare(desired, present, options).Single(); Assert.Equal("skip", adopted.Operation); Assert.Equal("adopted", adopted.Detail);
            options.Adopt.Clear(); present.Blocks.Clear();
            Assert.Equal("create", ProjectModelComparison.Compare(desired, present, options).Single().Operation);
            options.Adopt.Add(item.Key); Assert.Throws<ArgumentException>(() => ProjectModelComparison.Compare(desired, present, options));
        }
        [Theory, MemberData(nameof(Releases))]
        public void CleanupDependsOnEverySuccessfulCompileAndRecordDependsOnSave(string release)
        {
            var fixture = new PlanningFixture(release); fixture.Options.RecordPath = "C:/Projects/Fixture/Fixture.tiagen.json";
            var plan = fixture.Build().Plan;
            var compile = plan.Steps.Where(s => s.Phase == "compile").Select(s => s.Id).ToArray(); Assert.NotEmpty(compile);
            var cleanup = plan.Steps.Where(s => s.Phase == "cleanup").ToArray();
            Assert.Equal(plan.Steps.Count(s => s.Tool == "ImportPlcExternalSource"), cleanup.Length);
            foreach (var step in cleanup) { Assert.Equal(StepReviewRules.Delete, step.Review!.Rule); Assert.All(compile, id => Assert.Contains(id, step.DependsOn)); }
            var save = plan.Steps.Single(s => s.Tool == "SaveProject"); Assert.All(cleanup, s => Assert.Contains(s.Id, save.DependsOn));
            var record = plan.Steps.Single(s => s.Tool == "PublishGenerationRecord"); Assert.Contains(save.Id, record.DependsOn); Assert.Equal(StepReviewRules.Record, record.Review!.Rule);
        }
    }
}
