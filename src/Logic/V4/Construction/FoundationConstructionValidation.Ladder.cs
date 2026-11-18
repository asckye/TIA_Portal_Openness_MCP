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

        internal static void Rungs(ConstructionNode value, IReadOnlyCollection<string>? localMembers = null, string outputReleaseKey = "21")
        {
            if (value is LadFcBlockSpec lad)
            {
                var locals = (lad.Inputs ?? Array.Empty<Member>()).Concat(lad.Outputs ?? Array.Empty<Member>()).Select(m => m.Name).ToArray();
                for (int i = 0; i < lad.Networks.Count; i++)
                    if (lad.Networks[i].Rung is LadRung rung)
                        Rung(rung, locals, "/networks/" + i.ToString(CultureInfo.InvariantCulture) + "/rung", outputReleaseKey);
            }
            else if (value is LadRung rung) Rung(rung, localMembers ?? Array.Empty<string>(), "", outputReleaseKey);
            else if (value is LadNetwork network && network.Rung != null)
                Rung(network.Rung, localMembers ?? Array.Empty<string>(), "/rung", outputReleaseKey);
        }

        internal static void Rung(LadRung rung, IReadOnlyCollection<string> localMembers, string pointer, string outputReleaseKey = "21")
        {
            var locals = new HashSet<string>(localMembers, StringComparer.Ordinal);
            int parts = 0;
            void Id(string name, string at)
            {
                if (!InputGuard.Identifier(name) || !(char.IsLetter(name[0]) || name[0] == '_'))
                    throw new InputRejectionException().At(at, "simple PLC identifier");
            }
            void Operand(LadOperand operand, string at, bool access = true)
            {
                if (access) parts++;
                if (operand.Components != null)
                {
                    for (int i = 0; i < operand.Components.Count; i++)
                    {
                        var component = operand.Components[i];
                        Id(component.Name, at + "/" + operand.Kind + "/" + i + (component.Index == null ? "" : "/name"));
                        if (component.Index != null && outputReleaseKey == "14sp1")
                            throw new InputRejectionException().At(at + "/" + operand.Kind + "/" + i + "/index", "array indices from V15.1");
                    }
                    if (operand.Kind == "local" && !locals.Contains(operand.Components[0].Name))
                        throw new InputRejectionException().At(at, "symbol rooted in a declared input or output");
                }
                else if (operand.Kind == "constant") Id(operand.Value!, at + "/constant");
            }
            void Required(bool valid, string at, string expected)
            {
                if (!valid) throw new InputRejectionException().At(at, expected);
            }
            void Symbol(LadOperand operand, string at)
            {
                Required(operand.Components != null, at, "symbol operand");
                Operand(operand, at);
            }
            void Counter(LadOutput output, string here)
            {
                Operand(output.Instance!, here + "/instance", false);
                Operand(output.Pv!, here + "/pv");
                Required(output.Pv!.Kind != "literal" || output.Pv.DataType == output.ValueType,
                    here + "/pv/literal/type", "literal type matching valueType");
                Required(output.DataType == "CTD" || output.Reset != null, here + "/reset", "explicit reset series");
                Required(output.DataType == "CTU" || output.Load != null, here + "/load", "explicit load series");
                Required(output.DataType != "CTUD" || output.Down != null, here + "/down", "explicit down series");
                Required(output.DataType != "CTD" || output.Reset == null, here + "/reset", "reset only on CTU/CTUD");
                Required(output.DataType != "CTU" || output.Load == null, here + "/load", "load only on CTD/CTUD");
                Required(output.DataType == "CTUD" || output.Down == null, here + "/down", "down only on CTUD");
                Required(output.DataType == "CTUD" || output.Qd == null, here + "/qd", "qd only on CTUD");
                if (output.Reset != null) Conditions(output.Reset, here + "/reset");
                if (output.Load != null) Conditions(output.Load, here + "/load");
                if (output.Down != null) Conditions(output.Down, here + "/down");
                if (output.Cv != null) Symbol(output.Cv, here + "/cv");
                if (output.Q != null) Outputs(output.Q, here + "/q");
                if (output.Qd != null) Outputs(output.Qd, here + "/qd");
            }
            void Call(LadOutput output, string here)
            {
                Id(output.Block!, here + "/block");
                Required(!SystemInstruction(output.Block!), here + "/block", "user FC/FB; known instruction names and MC_ prefix are reserved");
                Required(output.BlockType != "FB" || output.InstanceDb != null, here + "/instance", "global instance DB for FB");
                Required(output.BlockType != "FC" || output.InstanceDb == null, here + "/instance", "no instance on FC");
                if (output.InstanceDb != null) Id(output.InstanceDb, here + "/instance");
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int j = 0; j < output.Pins!.Count; j++)
                {
                    var pin = output.Pins[j];
                    string at = here + "/pins/" + j.ToString(CultureInfo.InvariantCulture);
                    Id(pin.Name, at + "/name");
                    Required(!pin.Name.Equals("en", StringComparison.OrdinalIgnoreCase)
                        && !pin.Name.Equals("eno", StringComparison.OrdinalIgnoreCase) && names.Add(pin.Name),
                        at + "/name", "unique pin name other than en/eno");
                    if (pin.Section == "Output" || pin.Section == "InOut") Symbol(pin.Value, at + "/value");
                    else Operand(pin.Value, at + "/value");
                }
                if (output.Then != null) Outputs(output.Then, here + "/then");
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
                    else if (output.Kind == "timer")
                    {
                        Operand(output.Instance!, here + "/instance", false);
                        Operand(output.Pt!, here + "/pt");
                        Required(output.Pt!.Kind != "typed" || !(output.Pt.Value!.StartsWith("LT#", StringComparison.OrdinalIgnoreCase)
                            || output.Pt.Value.StartsWith("LTIME#", StringComparison.OrdinalIgnoreCase)), here + "/pt", "Time operand, not LTime");
                        if (output.Et != null) Symbol(output.Et, here + "/et");
                        if (output.Q != null) Outputs(output.Q, here + "/q");
                    }
                    else if (output.Kind == "counter") Counter(output, here);
                    else if (output.Kind == "call") Call(output, here);
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

        private static bool SystemInstruction(string name)
        {
            if (name.StartsWith("MC_", StringComparison.OrdinalIgnoreCase)) return true;
            switch (name.ToUpperInvariant())
            {
                case "TON": case "TOF": case "TP": case "TONR":
                case "CTU": case "CTD": case "CTUD": case "R_TRIG": case "F_TRIG":
                case "MOVE": case "ADD": case "SUB": case "MUL": case "DIV": case "MOD":
                    return true;
                default: return false;
            }
        }
    }
}
