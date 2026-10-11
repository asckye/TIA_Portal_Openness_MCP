using System.Collections.Generic;
using System.Text.Json;

namespace TiaMcp.Logic.Generation
{
    public sealed class GenerationPlan
    {
        public string Schema { get; set; } = "";
        public string PlanId { get; set; } = "";
        public string PlanHash { get; set; } = "";
        public GenerationPlanPackage Package { get; set; } = new GenerationPlanPackage();
        public string MachineHash { get; set; } = "";
        public GenerationPlanTarget Target { get; set; } = new GenerationPlanTarget();
        public Dictionary<string, GenerationPlanObserved> Observed { get; set; } = new Dictionary<string, GenerationPlanObserved>();
        public List<string> Phases { get; set; } = new List<string>();
        public List<GenerationPlanStepsItem> Steps { get; set; } = new List<GenerationPlanStepsItem>();
        public List<GenerationPlanSkippedItem> Skipped { get; set; } = new List<GenerationPlanSkippedItem>();
        public List<GenerationPlanConflictsItem> Conflicts { get; set; } = new List<GenerationPlanConflictsItem>();
        public List<GenerationPlanUnavailableItem> Unavailable { get; set; } = new List<GenerationPlanUnavailableItem>();
        public List<FileDigest> Artifacts { get; set; } = new List<FileDigest>();
        public List<GenerationRecordObject> Objects { get; set; } = new List<GenerationRecordObject>();
        public List<string> Adopt { get; set; } = new List<string>();
        public GenerationPlanSelfCheck SelfCheck { get; set; } = new GenerationPlanSelfCheck();
    }

    public sealed class GenerationPlanPackage
    {
        public string Id { get; set; } = "";
        public string Version { get; set; } = "";
        public string Hash { get; set; } = "";
    }

    public sealed class GenerationPlanTarget
    {
        public string Release { get; set; } = "";
        public string ProjectIdentity { get; set; } = "";
        public string Mode { get; set; } = "";
        public string? SoftwarePath { get; set; }
    }

    public sealed class GenerationPlanStepsItem
    {
        public string Id { get; set; } = "";
        public string Phase { get; set; } = "";
        public string Key { get; set; } = "";
        public string Op { get; set; } = "";
        public string Tool { get; set; } = "";
        public Dictionary<string, JsonElement> Arguments { get; set; } = new Dictionary<string, JsonElement>();
        public List<string> DependsOn { get; set; } = new List<string>();
        public string ArgumentDigest { get; set; } = "";
        public GenerationPlanStepsItemAvailability Availability { get; set; } = new GenerationPlanStepsItemAvailability();
        public GenerationPlanStepsItemExpect Expect { get; set; } = new GenerationPlanStepsItemExpect();
        public GenerationStepReview? Review { get; set; }
    }

    public sealed class GenerationPlanObserved
    {
        public List<string> SourceNames { get; set; } = new List<string>();
        public List<string> ObjectKeys { get; set; } = new List<string>();
    }

    public sealed class GenerationStepReview
    {
        public string Mode { get; set; } = "tool-preview";
        public string Rule { get; set; } = "";
        public List<string> LateArguments { get; set; } = new List<string> { "expectedPlanHash" };
        public Dictionary<string, JsonElement> Expect { get; set; } = new Dictionary<string, JsonElement>();
        public Dictionary<string, string> Witness { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, List<string>> Forbid { get; set; } = new Dictionary<string, List<string>>();
        public Dictionary<string, JsonElement> Effect { get; set; } = new Dictionary<string, JsonElement>();
    }

    public sealed class GenerationPlanStepsItemAvailability
    {
        public string Tool { get; set; } = "";
        public string BehaviorPolicy { get; set; } = "";
        public string Native { get; set; } = "";
    }

    public sealed class GenerationPlanStepsItemExpect
    {
        public string Readback { get; set; } = "";
        public string? Contains { get; set; }
        public string? Fingerprint { get; set; }
    }

    public sealed class GenerationPlanSkippedItem
    {
        public string Key { get; set; } = "";
        public string Reason { get; set; } = "";
    }

    public sealed class GenerationPlanConflictsItem
    {
        public string Key { get; set; } = "";
        public string Reason { get; set; } = "";
        public string Detail { get; set; } = "";
    }

    public sealed class GenerationPlanUnavailableItem
    {
        public string Phase { get; set; } = "";
        public string Reason { get; set; } = "";
    }

    public sealed class GenerationPlanSelfCheck
    {
        public int Errors { get; set; }
        public int Warnings { get; set; }
    }
}
