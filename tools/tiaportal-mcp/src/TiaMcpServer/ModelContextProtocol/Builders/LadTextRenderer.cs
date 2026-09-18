using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// Decodes a SimaticML block export (FlgNet ladder + StructuredText SCL) into readable text so a
    /// model/human can analyze ladder logic WITHOUT hand-parsing wires. Siemens-free: works purely on
    /// the exported XML string. For each LAD network it reconstructs the power-flow as a boolean-ish
    /// expression (series = ' · ', parallel = ' + '), shows coils/boxes with their operands, and — the
    /// hard-to-spot thing — flags contacts whose operand is a LITERAL CONSTANT (e.g. a normally-open
    /// contact wired to FALSE permanently disables its rung).
    /// </summary>
    public static class LadTextRenderer
    {
        public static string Render(string xml)
        {
            XDocument doc;
            try { doc = XDocument.Parse(xml); }
            catch (Exception ex) { return "Could not parse block XML: " + ex.Message; }

            StripNamespaces(doc);

            var sb = new StringBuilder();
            var units = doc.Descendants("SW.Blocks.CompileUnit").ToList();
            if (units.Count == 0)
            {
                return "No LAD/SCL networks found (block may be a DB/UDT, or an empty program).";
            }

            int netNo = 0;
            foreach (var unit in units)
            {
                netNo++;
                var lang = unit.Descendants("ProgrammingLanguage").FirstOrDefault()?.Value?.Trim() ?? "?";
                var title = FirstMultilingual(unit, "Title");
                var comment = FirstMultilingual(unit, "Comment");
                sb.Append($"── 程序段 {netNo}");
                if (!string.IsNullOrWhiteSpace(title)) sb.Append($" · {title}");
                sb.Append($"  [{lang}]\n");
                if (!string.IsNullOrWhiteSpace(comment)) sb.Append($"   注释: {comment}\n");

                var flg = unit.Descendants("FlgNet").FirstOrDefault();
                if (flg != null && (lang.Equals("LAD", StringComparison.OrdinalIgnoreCase) || lang.Equals("FBD", StringComparison.OrdinalIgnoreCase)))
                {
                    sb.Append(RenderLadNetwork(flg));
                }
                else if (lang.Equals("SCL", StringComparison.OrdinalIgnoreCase) || lang.Equals("STL", StringComparison.OrdinalIgnoreCase))
                {
                    var text = RenderStructuredText(unit);
                    sb.Append(string.IsNullOrWhiteSpace(text)
                        ? "   (无代码或纯声明)\n"
                        : IndentBlock(text, "   "));
                }
                else
                {
                    sb.Append("   (无 FlgNet / 不支持的语言)\n");
                }
                sb.Append('\n');
            }
            return sb.ToString().TrimEnd() + "\n";
        }

        // ---- LAD network ----

        private sealed class Part
        {
            public string UId = "";
            public string Name = "";
            public bool Negated;                 // contact/coil operand negated
            public string? Instance;             // timer/counter/FB instance name
            public bool IsCall;                  // <Call> element: FB/FC block call box
            public Dictionary<string, string> Operands = new();  // pin name -> operand text
            // Call parameter pin -> declared section (Input / Output / InOut). Tells wire direction for
            // user-named pins, which the generic in*/out* pin-name heuristic cannot see.
            public Dictionary<string, string> ParamSection = new(StringComparer.OrdinalIgnoreCase);
        }

        private static string RenderLadNetwork(XElement flg)
        {
            var parts = new Dictionary<string, Part>();
            var accessText = new Dictionary<string, (string text, bool literal)>();

            foreach (var acc in flg.Descendants("Access"))
            {
                var uid = acc.Attribute("UId")?.Value;
                if (uid == null) continue;
                accessText[uid] = ReadAccess(acc);
            }
            foreach (var p in flg.Descendants("Part"))
            {
                var uid = p.Attribute("UId")?.Value;
                if (uid == null) continue;
                var part = new Part { UId = uid, Name = p.Attribute("Name")?.Value ?? "?" };
                part.Negated = p.Elements("Negated").Any();
                part.Instance = p.Descendants("Instance").Descendants("Component").FirstOrDefault()?.Attribute("Name")?.Value;
                parts[uid] = part;
            }
            // Block calls are <Call>, not <Part>: <Call UId><CallInfo Name BlockType><Instance/><Parameter Name Section/>…
            foreach (var c in flg.Descendants("Call"))
            {
                var uid = c.Attribute("UId")?.Value;
                if (uid == null) continue;
                var info = c.Element("CallInfo");
                var part = new Part { UId = uid, Name = info?.Attribute("Name")?.Value ?? "?", IsCall = true };
                var inst = c.Descendants("Instance").FirstOrDefault();
                if (inst != null) part.Instance = ReadAccess(inst).text;
                foreach (var prm in info?.Elements("Parameter") ?? Enumerable.Empty<XElement>())
                {
                    var pn = prm.Attribute("Name")?.Value;
                    var sec = prm.Attribute("Section")?.Value;
                    if (!string.IsNullOrEmpty(pn) && !string.IsNullOrEmpty(sec)) part.ParamSection[pn!] = sec!;
                }
                parts[uid] = part;
            }

            // Wires: bind operands (Access -> part.pin) and build flow edges (srcPart.out -> dstPart.in).
            // dstKey "(uid,pin)" -> source描述 (RAIL or "uid:pin")
            var flowSource = new Dictionary<string, string>();
            foreach (var wire in flg.Descendants("Wires").Elements("Wire"))
            {
                var ends = wire.Elements().ToList();
                var idents = ends.Where(e => e.Name.LocalName == "IdentCon").Select(e => e.Attribute("UId")?.Value).Where(v => v != null).ToList();
                var names = ends.Where(e => e.Name.LocalName == "NameCon")
                                .Select(e => (uid: e.Attribute("UId")?.Value, pin: e.Attribute("Name")?.Value)).Where(t => t.uid != null).ToList();
                bool hasRail = ends.Any(e => e.Name.LocalName == "Powerrail");

                // operand binding: an Access (IdentCon) tied to a part pin (NameCon).
                // A LITERAL bound to a CONTACT operand is the important tell: a normally-open contact
                // wired to 0/FALSE permanently OPENS (disables) its rung; NC or 1/TRUE permanently CLOSES.
                // Literals on compare/move pins are normal, so only annotate contacts.
                if (idents.Count > 0)
                {
                    foreach (var nc in names)
                    {
                        if (parts.TryGetValue(nc.uid!, out var pt) && accessText.TryGetValue(idents[0]!, out var at))
                        {
                            var pin = nc.pin ?? "operand";
                            string text = at.text;
                            if (at.literal && IsContact(pt.Name) && pin == "operand")
                            {
                                bool truthy = at.text is "1" or "TRUE" or "True";
                                bool falsy = at.text is "0" or "FALSE" or "False";
                                // NO contact: passes when operand true; NC (Negated): passes when operand false.
                                bool alwaysOpen = (!pt.Negated && falsy) || (pt.Negated && truthy);
                                bool alwaysClosed = (!pt.Negated && truthy) || (pt.Negated && falsy);
                                text += alwaysOpen ? " ⟨恒断·禁用本行⟩" : alwaysClosed ? " ⟨恒通⟩" : " ⟨常量触点⟩";
                            }
                            pt.Operands[pin] = text;
                        }
                    }
                }

                // flow: split named endpoints into sources (out-like) and destinations (in-like).
                // On a call box the pins carry the block's own parameter names, so consult the declared
                // section first and only fall back to the in*/out* naming convention.
                string? Section(string? uid, string? pin)
                    => uid != null && pin != null && parts.TryGetValue(uid, out var cp) && cp.IsCall
                       && cp.ParamSection.TryGetValue(pin, out var sec) ? sec : null;
                bool IsOut(string? uid, string? pin)
                {
                    var sec = Section(uid, pin);
                    if (sec != null) return sec.Equals("Output", StringComparison.OrdinalIgnoreCase);
                    return pin != null && (pin.Equals("out", StringComparison.OrdinalIgnoreCase)
                        || pin.Equals("eno", StringComparison.OrdinalIgnoreCase) || pin == "Q" || pin.StartsWith("out"));
                }
                bool IsIn(string? uid, string? pin)
                {
                    var sec = Section(uid, pin);
                    if (sec != null) return !sec.Equals("Output", StringComparison.OrdinalIgnoreCase);
                    return pin != null && (pin.Equals("in", StringComparison.OrdinalIgnoreCase)
                        || pin.Equals("en", StringComparison.OrdinalIgnoreCase) || pin.Equals("pre", StringComparison.OrdinalIgnoreCase) || pin.StartsWith("in"));
                }

                var sources = names.Where(t => IsOut(t.uid, t.pin)).ToList();
                var dests = names.Where(t => IsIn(t.uid, t.pin)).ToList();
                foreach (var d in dests)
                {
                    string key = d.uid + ":" + d.pin;
                    if (hasRail && sources.Count == 0) flowSource[key] = "RAIL";
                    else if (sources.Count > 0) flowSource[key] = sources[0].uid + ":" + sources[0].pin;
                    else if (hasRail) flowSource[key] = "RAIL";
                }
            }

            // Render every output element: coils and boxes that write (Move/Call/Set...). Trace their EN/in.
            var sb = new StringBuilder();
            var outputs = parts.Values.Where(p => IsCoil(p.Name) || IsWritingBox(p.Name) || p.IsCall).ToList();
            if (outputs.Count == 0)
            {
                // Fallback: just list the parts + operands so nothing is opaque.
                foreach (var p in parts.Values)
                    sb.Append($"   · {p.Name}{FormatOperands(p)}\n");
                return sb.Length == 0 ? "   (空网络)\n" : sb.ToString();
            }

            var guard = new HashSet<string>();
            foreach (var outp in outputs)
            {
                if (IsCoil(outp.Name))
                {
                    string inKey = outp.UId + ":in";
                    string expr = flowSource.TryGetValue(inKey, out var src) ? TraceChain(src, parts, flowSource, guard) : "?";
                    string coil = CoilGlyph(outp.Name);
                    string operand = outp.Operands.TryGetValue("operand", out var o) ? o : "?";
                    sb.Append($"   {operand} {coil}  ⇐  {(string.IsNullOrEmpty(expr) ? "RAIL(恒通)" : expr)}\n");
                }
                else // writing box (MOVE / block call) driven by EN
                {
                    string enKey = outp.UId + ":en";
                    string en = flowSource.TryGetValue(enKey, out var src) ? TraceChain(src, parts, flowSource, guard) : "";
                    string box = outp.IsCall ? DescribeCall(outp, parts, flowSource, guard) : DescribeBox(outp);
                    sb.Append($"   当 [{(string.IsNullOrEmpty(en) ? "RAIL(恒通)" : en)}] 时: {box}\n");
                }
            }
            return sb.ToString();
        }

        // CALL "Block"[instance](pin=operand, pin=⟨traced power-flow⟩, …): operands bound directly plus
        // Bool inputs that are driven by contact logic instead of an operand.
        private static string DescribeCall(Part p, Dictionary<string, Part> parts, Dictionary<string, string> flowSource, HashSet<string> guard)
        {
            var items = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pin in p.ParamSection.Keys.Concat(p.Operands.Keys))
            {
                if (!seen.Add(pin) || pin.Equals("en", StringComparison.OrdinalIgnoreCase) || pin.Equals("eno", StringComparison.OrdinalIgnoreCase)) continue;
                if (p.Operands.TryGetValue(pin, out var v)) items.Add($"{pin}={v}");
                else if (flowSource.TryGetValue(p.UId + ":" + pin, out var src))
                {
                    var expr = TraceChain(src, parts, flowSource, guard);
                    items.Add($"{pin}=⟨{(string.IsNullOrEmpty(expr) ? "RAIL" : expr)}⟩");
                }
            }
            return CallLabel(p) + (items.Count == 0 ? "()" : "(" + string.Join(", ", items) + ")");
        }

        private static string CallLabel(Part p)
            => $"CALL \"{p.Name}\"" + (p.Instance != null ? $"[{p.Instance}]" : "");

        // Trace power flow backward from a source node "uid:pin" (or RAIL) into a series/parallel expression.
        private static string TraceChain(string node, Dictionary<string, Part> parts, Dictionary<string, string> flowSource, HashSet<string> guard)
        {
            if (node == "RAIL" || string.IsNullOrEmpty(node)) return "";
            if (!guard.Add(node)) return "…";  // cycle guard
            try
            {
                var uid = node.Split(':')[0];
                if (!parts.TryGetValue(uid, out var p)) return "?";

                if (IsContact(p.Name))
                {
                    string upstream = flowSource.TryGetValue(uid + ":in", out var src) ? TraceChain(src, parts, flowSource, guard) : "";
                    string lit = (p.Negated ? "/" : "") + (p.Operands.TryGetValue("operand", out var o) ? o : "?");
                    return Series(upstream, lit);
                }
                if (IsCompare(p.Name))
                {
                    string upstream = flowSource.TryGetValue(uid + ":pre", out var src) ? TraceChain(src, parts, flowSource, guard) : "";
                    var a = p.Operands.TryGetValue("in1", out var i1) ? i1 : "?";
                    var b = p.Operands.TryGetValue("in2", out var i2) ? i2 : "?";
                    string cmp = $"({a} {CompareGlyph(p.Name)} {b})";
                    return Series(upstream, cmp);
                }
                if (p.Name == "O")  // OR box: inputs in1,in2,... are parallel branches
                {
                    var branches = new List<string>();
                    foreach (var pin in p.Operands.Keys.Concat(new[] { "in1", "in2", "in3", "in4" }).Distinct())
                    {
                        if (!pin.StartsWith("in")) continue;
                        if (flowSource.TryGetValue(uid + ":" + pin, out var src))
                            branches.Add(TraceChain(src, parts, flowSource, guard));
                    }
                    branches = branches.Where(b => !string.IsNullOrEmpty(b)).Distinct().ToList();
                    return branches.Count == 0 ? "" : "(" + string.Join(" + ", branches) + ")";
                }
                if (p.Name == "Not")  // RLO inverter: in -> out
                {
                    string upstream = flowSource.TryGetValue(uid + ":in", out var src) ? TraceChain(src, parts, flowSource, guard) : "";
                    return $"NOT({(string.IsNullOrEmpty(upstream) ? "RAIL" : upstream)})";
                }
                if (p.IsCall)  // a block output pin (eno / Bool output) feeding downstream logic
                {
                    string en = flowSource.TryGetValue(uid + ":en", out var s3) ? TraceChain(s3, parts, flowSource, guard) : "";
                    var colon = node.IndexOf(':');
                    var pin = colon >= 0 ? node.Substring(colon + 1) : "eno";
                    return Series(en, $"{CallLabel(p)}.{pin}");
                }
                // timers / edges / other boxes producing power at Q/out. Power enters at 'in' (contacts, edges),
                // 'IN' (timers/counters) or 'en' (MOVE and other EN/ENO boxes).
                string upstreamBox = "";
                foreach (var pin in new[] { "in", "IN", "en" })
                {
                    if (flowSource.TryGetValue(uid + ":" + pin, out var s2)) { upstreamBox = TraceChain(s2, parts, flowSource, guard); break; }
                }
                string box = DescribeBoxInline(p);
                return Series(upstreamBox, box);
            }
            finally { guard.Remove(node); }
        }

        private static string Series(string upstream, string term)
            => string.IsNullOrEmpty(upstream) ? term : upstream + " · " + term;

        // ---- helpers ----

        private static bool IsContact(string n) => n == "Contact";
        private static bool IsCoil(string n) => n == "Coil" || n == "SCoil" || n == "RCoil" || n == "SetCoil" || n == "ResetCoil";
        private static bool IsCompare(string n) => n is "Eq" or "Ne" or "Gt" or "Lt" or "Ge" or "Le";
        private static bool IsWritingBox(string n) => n == "Move" || n == "Call";
        private static string CoilGlyph(string n) => n switch
        {
            "SCoil" or "SetCoil" => "(S)",
            "RCoil" or "ResetCoil" => "(R)",
            _ => "( )"
        };
        private static string CompareGlyph(string n) => n switch
        {
            "Eq" => "==", "Ne" => "<>", "Gt" => ">", "Lt" => "<", "Ge" => ">=", "Le" => "<=", _ => "?"
        };

        private static string DescribeBox(Part p)
        {
            if (p.Name == "Move")
            {
                var src = p.Operands.TryGetValue("in", out var i) ? i : "?";
                var dst = p.Operands.TryGetValue("out1", out var o) ? o : (p.Operands.TryGetValue("out", out var o2) ? o2 : "?");
                return $"MOVE {src} → {dst}";
            }
            return DescribeBoxInline(p);
        }

        private static string DescribeBoxInline(Part p)
        {
            var name = p.Name;
            if (p.Instance != null) name += $"[{p.Instance}]";
            var ops = FormatOperands(p);
            // timers commonly produce power at Q; note it
            if (p.Name is "TP" or "TON" or "TOF" or "TONR") return $"{name}{ops}.Q";
            if (p.Name is "PBox" or "NBox" or "P_TRIG" or "N_TRIG" or "Coil_P" or "Coil_N") return $"{name}{ops}(边沿)";
            return $"{name}{ops}";
        }

        private static string FormatOperands(Part p)
        {
            if (p.Operands.Count == 0) return "";
            var kv = p.Operands.Where(k => k.Key != "operand" || IsContact(p.Name) || IsCoil(p.Name))
                               .Select(k => p.Operands.Count == 1 && k.Key == "operand" ? k.Value : $"{k.Key}={k.Value}");
            var s = string.Join(", ", kv);
            return string.IsNullOrEmpty(s) ? "" : $"({s})";
        }

        private static (string text, bool literal) ReadAccess(XElement acc)
        {
            var scope = acc.Attribute("Scope")?.Value ?? "";
            if (scope.Contains("Constant"))
            {
                // literal / typed: <Constant><ConstantValue>…; named (LocalConstant/GlobalConstant): <Constant Name="…"/>
                var named = acc.Descendants("Constant").FirstOrDefault()?.Attribute("Name")?.Value;
                if (!string.IsNullOrEmpty(named)) return (scope.Contains("Global") ? $"\"{named}\"" : $"#{named}", true);
                var v = acc.Descendants("ConstantValue").FirstOrDefault()?.Value?.Trim() ?? "?";
                return (v, true);
            }
            var address = acc.Element("Address");
            if (address != null) return (AddressText(address), false);
            // symbol: join Component names with '.'
            var comps = acc.Descendants("Component").Select(c => c.Attribute("Name")?.Value).Where(v => !string.IsNullOrEmpty(v)).ToList();
            var name = string.Join(".", comps);
            if (string.IsNullOrEmpty(name)) name = "?";
            return (scope.Contains("Global") ? $"\"{name}\"" : $"#{name}", false);
        }

        // <Address Area="Input" Type="Bool" BitOffset="3"/> -> %I0.3 ; Area=Memory Type=Word BitOffset=80 -> %MW10
        private static string AddressText(XElement address)
        {
            var area = address.Attribute("Area")?.Value ?? "";
            var type = address.Attribute("Type")?.Value ?? "";
            var prefix = area switch
            {
                "Input" or "PeripheryInput" => "I",
                "Output" or "PeripheryOutput" => "Q",
                "Memory" => "M",
                _ => area
            };
            if (!int.TryParse(address.Attribute("BitOffset")?.Value, out var bits)) return "%" + prefix + "?";
            var size = type switch
            {
                "Bool" => "",
                "Byte" or "SInt" or "USInt" or "Char" => "B",
                "Word" or "Int" or "UInt" => "W",
                "DWord" or "DInt" or "UDInt" or "Real" or "Time" => "D",
                "LWord" or "LInt" or "ULInt" or "LReal" or "LTime" => "L",
                _ => ""
            };
            return "%" + prefix + size + (bits / 8) + (type == "Bool" ? "." + (bits % 8) : "");
        }

        // ---- StructuredText (SCL/STL) ----
        //
        // The StructuredText body is a flat token stream, but an <Access> is a TREE: a call carries its
        // parameter list (<CallInfo><Parameter>… with nested Token/Blank/Access), an array component carries
        // its index <Access>, and named constants have no ConstantValue at all. Rendering must recurse, or
        // every call, named constant and absolute address silently vanishes from the output.

        private static string RenderStructuredText(XElement unit)
        {
            var st = unit.Descendants("StructuredText").FirstOrDefault();
            if (st == null) return "";
            var sb = new StringBuilder();
            RenderStNodes(st.Elements(), sb);
            return sb.ToString();
        }

        private static void RenderStNodes(IEnumerable<XElement> nodes, StringBuilder sb)
        {
            foreach (var node in nodes)
            {
                switch (node.Name.LocalName)
                {
                    case "Text": sb.Append(node.Value); break;
                    case "Token":
                    case "StlToken": sb.Append(node.Attribute("Text")?.Value ?? ""); break;
                    case "Blank": sb.Append(' ', ParseNum(node, 1)); break;
                    case "NewLine": sb.Append('\n'); break;
                    case "Access": sb.Append(RenderStAccess(node)); break;
                    case "CallInfo":
                    case "Instruction": RenderStCallInfo(node, sb); break;
                    case "Parameter": RenderStParameter(node, sb); break;
                    case "Comment":
                    {
                        var ct = CommentText(node);
                        if (ct.Length > 0) sb.Append("(*").Append(ct).Append("*)");
                        break;
                    }
                    case "LineComment":
                    {
                        var ct = CommentText(node);
                        if (ct.Length > 0) sb.Append("//").Append(ct);
                        break;
                    }
                    case "StlStatement":
                        RenderStNodes(node.Elements(), sb);
                        if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append('\n');
                        break;
                    default:
                        RenderStNodes(node.Elements(), sb);  // unknown wrapper: never drop what is inside it
                        break;
                }
            }
        }

        private static string CommentText(XElement node)
            => string.Concat(node.Descendants("Text").Select(t => t.Value));

        private static string RenderStAccess(XElement acc)
        {
            var scope = acc.Attribute("Scope")?.Value ?? "";
            var sb = new StringBuilder();

            var callInfo = acc.Element("CallInfo") ?? acc.Element("Instruction");
            if (callInfo != null)
            {
                RenderStCallInfo(callInfo, sb);
                RenderStNodes(acc.Elements().Where(e => e != callInfo), sb);  // e.g. trailing tokens
                return sb.ToString();
            }
            var symbol = acc.Element("Symbol");
            if (symbol != null)
                return Qualify(scope, symbol.Elements("Component").Select(ComponentText).ToList());

            var constant = acc.Element("Constant");
            if (constant != null)
            {
                var named = constant.Attribute("Name")?.Value;
                if (!string.IsNullOrEmpty(named)) return Qualify(scope, new List<string> { named! });
                return constant.Element("ConstantValue")?.Value?.Trim() ?? "?";
            }
            var address = acc.Element("Address");
            if (address != null) return AddressText(address);

            // <Instance Scope="…"><Component …/></Instance> and similar: components directly under the node
            var direct = acc.Elements("Component").Select(ComponentText).ToList();
            if (direct.Count > 0) return Qualify(scope, direct);

            RenderStNodes(acc.Elements(), sb);
            return sb.ToString();
        }

        // Local: #a.b   Global: "DB".a.b   (a global symbol quotes only its first component, like the SCL editor)
        private static string Qualify(string scope, List<string> comps)
        {
            if (comps.Count == 0) return "";
            if (scope.StartsWith("Local", StringComparison.OrdinalIgnoreCase)) return "#" + string.Join(".", comps);
            if (scope.StartsWith("Global", StringComparison.OrdinalIgnoreCase))
                return "\"" + comps[0] + "\"" + (comps.Count > 1 ? "." + string.Join(".", comps.Skip(1)) : "");
            return string.Join(".", comps);
        }

        // Component Name="arr" with nested index <Access>es -> arr[#i, #j]; SliceAccessModifier="X0" -> arr.%X0
        private static string ComponentText(XElement component)
        {
            var name = component.Attribute("Name")?.Value ?? "";
            var indexes = component.Elements("Access").Select(RenderStAccess).ToList();
            var text = indexes.Count > 0 ? name + "[" + string.Join(", ", indexes) + "]" : name;
            var slice = component.Attribute("SliceAccessModifier")?.Value;
            if (!string.IsNullOrEmpty(slice)) text += ".%" + slice;
            return text;
        }

        // FC / instruction:  Name(params)     FB via instance:  #inst⟨FbName⟩(params)  /  "DB"⟨FbName⟩(params)
        // The FB type is not textually present in SCL for instance calls; the ⟨…⟩ annotation keeps it visible.
        private static void RenderStCallInfo(XElement callInfo, StringBuilder sb)
        {
            var name = callInfo.Attribute("Name")?.Value ?? "?";
            var instance = callInfo.Element("Instance");
            if (instance != null)
            {
                var inst = RenderStAccess(instance);
                sb.Append(string.IsNullOrEmpty(inst) ? name : inst + "⟨" + name + "⟩");
            }
            else sb.Append(name);
            RenderStNodes(callInfo.Elements().Where(e => e.Name.LocalName != "Instance"), sb);
        }

        // <Parameter Name="IN"><Token ":="/>…</Parameter> -> IN := …
        // A positional argument (conversion/math functions) has no ':='/'=>' token, so its Name is not printed.
        private static void RenderStParameter(XElement parameter, StringBuilder sb)
        {
            var name = parameter.Attribute("Name")?.Value;
            var first = parameter.Elements().FirstOrDefault(e => e.Name.LocalName != "Blank");
            var firstText = first?.Name.LocalName == "Token" ? first.Attribute("Text")?.Value : null;
            if (!string.IsNullOrEmpty(name) && (firstText == ":=" || firstText == "=>"))
            {
                sb.Append(name);
                if (parameter.Elements().FirstOrDefault()?.Name.LocalName != "Blank") sb.Append(' ');
            }
            RenderStNodes(parameter.Elements(), sb);
        }

        private static int ParseNum(XElement e, int def)
            => int.TryParse(e.Attribute("Num")?.Value, out var n) ? n : def;

        private static string FirstMultilingual(XElement unit, string composition)
        {
            var mt = unit.Elements("ObjectList").Elements("MultilingualText")
                         .FirstOrDefault(m => m.Attribute("CompositionName")?.Value == composition);
            var txt = mt?.Descendants("Text").FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Value))?.Value;
            return txt?.Trim() ?? "";
        }

        private static string IndentBlock(string text, string indent)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            return string.Join("\n", lines.Select(l => indent + l)) + "\n";
        }

        private static void StripNamespaces(XDocument doc)
        {
            foreach (var e in doc.Descendants())
            {
                e.Name = e.Name.LocalName;
                var atts = e.Attributes()
                    .Where(a => !a.IsNamespaceDeclaration)
                    .Select(a => new XAttribute(a.Name.LocalName, a.Value)).ToList();
                e.ReplaceAttributes(atts);
            }
        }
    }
}
