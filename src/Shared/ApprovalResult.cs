#if !TIA_GENERATION_PLANNER
extern alias PlcRendering;
using EnvelopeMutation = PlcRendering::TiaMcp.Logic.V4.EnvelopeMutation;
#else
using EnvelopeMutation = TiaMcp.Logic.V4.EnvelopeMutation;
#endif
using System.Text.Json.Nodes;

namespace TiaOpenness.Shared
{
    internal static class ApprovalResult
    {
        internal static JsonNode Decorate(JsonNode body, bool disabled, string? requestId = null)
        {
            var copy = body.DeepClone();
            if (copy["schemaVersion"]?.GetValue<int?>() != 4 || copy["meta"] is not JsonObject meta) return copy;
            return EnvelopeMutation.Decorate(copy, disabled, requestId);
        }
    }
}
