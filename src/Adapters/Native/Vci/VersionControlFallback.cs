#if STUDIO_VCI
using TiaMcp.Adapters.Contracts.Candidates;
using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;

namespace TiaMcp.Adapters.Native.Vci
{
    internal sealed partial class VersionControlAdapter
    {
        private VciFallbackAdapter? fallbackAdapter;
        internal IFallbackAdapter FallbackAdapter(FallbackRequest request, Func<SessionState> binding, Action uncertain)
        {
            fallbackAdapter ??= new VciFallbackAdapter(binding, () => project()!,
                () => binding(), uncertain, request);
            fallbackAdapter.Request = request;
            fallbackAdapter.MappingTargets = () =>
            {
                int limit = int.Parse(request.Parameters["maxObjects"], System.Globalization.CultureInfo.InvariantCulture);
                if (limit < 1 || limit > 3000) CandidatePrimitives.Invalid("maxObjects");
                var targets = new List<VciMappingTarget>();
                void Walk(VcNode node, IEngineeringObject[] parents, int depth)
                {
                    if (depth > 64 || targets.Count >= limit) CandidatePrimitives.Fail("precondition", "complete-mapping-inventory");
                    if (node.Obj is Device && request.Parameters["deviceFilter"].Length > 0 && ObjName(node.Obj) != request.Parameters["deviceFilter"]) return;
                    targets.Add(new VciMappingTarget { Object = node.Obj, Name = ObjName(node.Obj), RelativePath = node.Label, Ancestors = parents });
                    if (node.Descendable) foreach (var child in TypedChildren(node)) Walk(child, parents.Concat(new[] { node.Obj }).ToArray(), depth + 1);
                }
                Walk(new VcNode { Obj = (IEngineeringObject)project()!, Label = ObjName((IEngineeringObject)project()!), Descendable = true }, Array.Empty<IEngineeringObject>(), 0);
                return targets.ToArray();
            };
            return fallbackAdapter;
        }
    }
}

#endif
