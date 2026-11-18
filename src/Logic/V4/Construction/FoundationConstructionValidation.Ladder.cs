using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TiaMcp.Logic.V4.Inputs;

namespace TiaMcp.Logic.V4.Construction
{
    internal static partial class FoundationConstructionValidation
    {
        internal const int MaxRungParts = 256;

        internal static void Rungs(ConstructionNode value, IReadOnlyCollection<string>? localMembers = null)
        {
            if (value is LadFcBlockSpec lad)
            {
                var locals = (lad.Inputs ?? Array.Empty<Member>()).Concat(lad.Outputs ?? Array.Empty<Member>()).Select(m => m.Name).ToArray();
                for (int i = 0; i < lad.Networks.Count; i++)
                    if (lad.Networks[i].Rung is LadRung rung)
                        Rung(rung, locals, "/networks/" + i.ToString(CultureInfo.InvariantCulture) + "/rung");
            }
            else if (value is LadRung rung) Rung(rung, localMembers ?? Array.Empty<string>(), "");
            else if (value is LadNetwork network && network.Rung != null)
                Rung(network.Rung, localMembers ?? Array.Empty<string>(), "/rung");
        }

        internal static void Rung(LadRung rung, IReadOnlyCollection<string> localMembers, string pointer)
        {
            var locals = new HashSet<string>(localMembers, StringComparer.Ordinal);
            int parts = 0;
            void Id(string name, string at)
            {
                if (!InputGuard.Identifier(name) || !(char.IsLetter(name[0]) || name[0] == '_'))
                    throw new InputRejectionException().At(at, "simple PLC identifier");
            }
            void Operand(LadOperand operand, string at)
            {
                parts++;
                if (operand.Components != null)
                {
                    for (int i = 0; i < operand.Components.Count; i++)
                    {
                        var component = operand.Components[i];
                        Id(component.Name, at + "/" + operand.Kind + "/" + i + (component.Index == null ? "" : "/name"));
                    }
                    if (operand.Kind == "local" && !locals.Contains(operand.Components[0].Name))
                        throw new InputRejectionException().At(at, "symbol rooted in a declared input or output");
                }
                else if (operand.Kind == "constant") Id(operand.Value!, at + "/constant");
            }
            void Conditions(IReadOnlyList<LadCondition> series, string at)
            {
                for (int i = 0; i < series.Count; i++)
                {
                    string here = at + "/" + i.ToString(CultureInfo.InvariantCulture);
                    var condition = series[i];
                    if (condition.Paths != null)
                    {
                        for (int j = 0; j < condition.Paths.Count; j++)
                            Conditions(condition.Paths[j], here + "/paths/" + j.ToString(CultureInfo.InvariantCulture));
                    }
                    else if (condition.Kind == "compare")
                    {
                        Operand(condition.Left!, here + "/left");
                        Operand(condition.Right!, here + "/right");
                    }
                    else
                    {
                        Operand(condition.Operand!, here + "/operand");
                        if (condition.Memory != null) Operand(condition.Memory, here + "/memory");
                    }
                    parts++;
                }
            }
            void Outputs(IReadOnlyList<LadOutput> outputs, string at)
            {
                for (int i = 0; i < outputs.Count; i++)
                {
                    string here = at + "/" + i.ToString(CultureInfo.InvariantCulture);
                    var output = outputs[i];
                    parts++;
                    if (output.Kind == "move")
                    {
                        Operand(output.In!, here + "/in");
                        for (int j = 0; j < output.Out!.Count; j++)
                            Operand(output.Out[j], here + "/out/" + j.ToString(CultureInfo.InvariantCulture));
                        if (output.Then != null) Outputs(output.Then, here + "/then");
                    }
                    else Operand(output.Operand!, here + "/operand");
                }
            }
            if (rung.Outputs.Count == 0 && (rung.FanOut == null || rung.FanOut.Count == 0))
                throw new InputRejectionException().At(pointer + "/outputs", "at least one output overall");
            Conditions(rung.Series, pointer + "/series");
            Outputs(rung.Outputs, pointer + "/outputs");
            if (rung.FanOut != null)
                for (int i = 0; i < rung.FanOut.Count; i++)
                {
                    string branch = pointer + "/fanOut/" + i.ToString(CultureInfo.InvariantCulture);
                    Conditions(rung.FanOut[i].Series, branch + "/series");
                    Outputs(rung.FanOut[i].Outputs, branch + "/outputs");
                }
            InputGuard.Limit(parts, MaxRungParts);
        }
    }
}
