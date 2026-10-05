using System;
using System.Collections.Generic;
using System.Linq;
using TiaMcp.Logic.V4.Inputs;

namespace TiaMcp.Logic.V4.Construction
{
    // Foundation is deliberately narrower than the full engine's pure builders.
    // Sources: OfflineCompositionBuilders.cs:20, OfflineBlockCompositionBuilders.cs:14,
    // OfflineLadderBuilders.cs:17, OfflineXmlBuilders.cs:18. No host capability is inferred here.
    internal static class FoundationConstructionValidation
    {
        private static readonly HashSet<string> LadderTypes = new HashSet<string>(StringComparer.Ordinal)
        { "Bool", "Byte", "Word", "DWord", "LWord", "SInt", "USInt", "Int", "UInt", "DInt", "UDInt", "LInt", "ULInt", "Real", "LReal" };

        internal static void Validate(ConstructionSpec spec)
        {
            switch (spec)
            {
                case UdtSpec udt:
                    Require(!string.IsNullOrWhiteSpace(udt.Name) && udt.Members.Count > 0, "UDT requires a name and members.");
                    break;
                case PlcTagTableSpec table:
                    Require(table.Tags.Count > 0, "Tag table must not be empty.");
                    break;
                case GlobalDbSpec db:
                    Require(db.StaticMembers.Count > 0, "GlobalDB members must not be empty.");
                    Literal(db.DbName);
                    foreach (var m in db.StaticMembers) { OptionalElement(m.EffectiveComment); OptionalElement(m.StartValue ?? ""); }
                    break;
                case StructuredTextSpec st:
                    StructuredText(st);
                    break;
                case SclBlockSpec block:
                    var fb = block as FbBlockSpec;
                    Interface(false, fb == null, block.Inputs, block.Outputs, fb?.Inouts, fb?.Statics, fb?.Temps);
                    StructuredText(block.StructuredText);
                    break;
                case LadFcBlockSpec lad:
                    Identifier(lad.BlockName);
                    Interface(true, true, lad.Inputs, lad.Outputs);
                    Require(lad.Networks.Count >= 1, "LAD requires networks.");
                    ConstructionJson.Limit(lad.Networks.Count, ConstructionJson.MaxNetworks);
                    break;
            }
            int parameters = 0;
            foreach (var call in ConstructionAdapter.Calls(spec))
            {
                Identifier(call.CallName);
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "en", "eno" };
                foreach (var p in call.Parameters)
                {
                    ConstructionJson.Limit(++parameters, ConstructionJson.MaxItems);
                    Identifier(p.Name);
                    Require(names.Add(p.Name), "Duplicate or reserved call port.");
                    Require(p.Section == "Input" || p.Section == "Output", "Unsupported call direction.");
                    DataType(p.DataType);
                    if (p.EffectiveSource == CallSourceKind.Constant)
                        Require(p.Section == "Input" && !string.IsNullOrWhiteSpace(p.ConstantValue), "Constant requires a nonempty input value.");
                    else
                    {
                        Require(p.EffectiveSource == CallSourceKind.Global, "Foundation supports global or constant call sources only.");
                        Require(p.SymbolPath!.Count >= 1, "Call symbol path requires segments.");
                        ConstructionJson.Limit(p.SymbolPath.Count, 32);
                        foreach (var segment in p.SymbolPath) Identifier(segment);
                    }
                }
            }
        }

        private static void Interface(bool ladder, bool reserveReturn, params IReadOnlyList<Member>?[] sections)
        {
            int count = 0;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (reserveReturn) names.Add("Ret_Val");
            foreach (var member in sections.Where(s => s != null).SelectMany(s => s!))
            {
                ConstructionJson.Limit(++count, ConstructionJson.MaxItems);
                Require(names.Add(member.Name), "Duplicate or reserved interface name.");
                Require(member.EffectiveComment.Length == 0 || !string.IsNullOrWhiteSpace(member.EffectiveComment), "Whitespace-only member comment is unsupported.");
                if (ladder) { Identifier(member.Name); DataType(member.Datatype); }
            }
        }

        private static void StructuredText(StructuredTextSpec spec)
        {
            Require(spec.Operations.Count > 0, "StructuredText operations must not be empty.");
            Require((spec.FirstUid ?? 21) >= 1 && (spec.FirstUid ?? 21) <= 1000000000, "firstUid must be 1..1000000000.");
            foreach (var op in spec.Operations)
            {
                if (op is IndentedStatement indented)
                    Require((indented.Indent ?? 0) >= 0 && (indented.Indent ?? 0) <= 4096, "indent must be 0..4096.");
                switch (op)
                {
                    case AssignmentStatement assignment:
                        Symbol(assignment.Target);
                        if (assignment.Source != null) Symbol(assignment.Source);
                        else { Require(!string.IsNullOrWhiteSpace(assignment.LiteralValue), "Assignment literal must not be empty."); Literal(assignment.LiteralValue!); }
                        break;
                    case ConditionStatement condition: Symbol(condition.Condition); break;
                    case TokenStatement token: Attribute(token.Text); break;
                    case LiteralStatement literal: Literal(literal.Value); break;
                    case BlankStatement blank: Require((blank.Count ?? 1) >= 1 && (blank.Count ?? 1) <= 4096, "blank count must be 1..4096."); break;
                    case SymbolStatement symbol:
                        Attribute(symbol.Name);
                        if (symbol.Op == "global")
                            Require(!symbol.Name.Contains('"') && !symbol.Name.Contains('#') && !symbol.Name.Split('.').Any(string.IsNullOrWhiteSpace), "Unsupported global path.");
                        else if (symbol.Op == "local")
                            Require(symbol.Name == symbol.Name.Trim() && !symbol.Name.Contains('.') && !symbol.Name.Contains('#') && !symbol.Name.Contains('"'), "Single local name required.");
                        else Symbol(symbol.Name);
                        break;
                    case LineStatement line:
                        Require(line.Items.Count > 0, "Line items must not be empty.");
                        foreach (var item in line.Items)
                            if (item.Kind == LineItemKind.Literal) Literal(item.Text);
                            else if (item.Kind == LineItemKind.Symbol) Symbol(item.Text);
                            else Attribute(item.Text);
                        break;
                }
            }
        }

        private static void Attribute(string text) => Require(text.IndexOfAny(new[] { '\r', '\n', '\t' }) < 0, "Unsupported XML attribute whitespace.");
        private static void Literal(string text) => Require(!text.Contains('\r'), "Unsupported XML element carriage return.");
        private static void OptionalElement(string text)
        {
            Require(text.Length == 0 || !string.IsNullOrWhiteSpace(text), "Whitespace-only optional element is unsupported.");
            Literal(text);
        }
        private static void Symbol(string value)
        {
            Attribute(value);
            Require(!string.IsNullOrWhiteSpace(value) && value == value.Trim(), "Unsupported symbol whitespace.");
            var text = value.StartsWith("#", StringComparison.Ordinal) ? value.Substring(1) : value;
            if (text.Contains('"'))
            {
                Require(!value.StartsWith("#", StringComparison.Ordinal) && text.Length >= 3 && text[0] == '"'
                    && text[text.Length - 1] == '"' && text.Count(c => c == '"') == 2, "Unsupported quoted symbol path.");
                text = text.Substring(1, text.Length - 2);
            }
            Require(!text.Split('.').Any(s => string.IsNullOrWhiteSpace(s) || s != s.Trim()), "Invalid symbol path.");
        }
        private static void Identifier(string text) => Require(InputGuard.Identifier(text) && (char.IsLetter(text[0]) || text[0] == '_'), "Simple PLC identifier required.");
        private static void DataType(string text) => Require(LadderTypes.Contains(text), "Unsupported ladder datatype.");
        private static void Require(bool condition, string message) => ConstructionJson.Require(condition, message);
    }
}
