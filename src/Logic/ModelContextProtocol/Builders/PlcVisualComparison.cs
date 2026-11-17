using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaGitAddIn.Models.Comparison;
using TiaGitAddIn.Models.Lad;
using TiaGitAddIn.Models.Sact;
using TiaGitAddIn.Services;
using TiaGitAddIn.Services.Comparison;
using TiaGitAddIn.Services.SimaticMl;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>HTML adapter for the MIT TiaGitAddIn.Core parser/comparer/layout; no TIA or browser automation.</summary>
    public static class PlcVisualComparison
    {
        public static JsonObject Compare(string leftPath, string rightPath, out string html)
        {
            foreach (var path in new[] { leftPath, rightPath })
                if (!Path.IsPathRooted(path) || !File.Exists(path)) throw new ArgumentException("Two existing absolute SimaticML paths are required.");
            var left = SimaticMlParser.Parse(leftPath);
            var right = SimaticMlParser.Parse(rightPath);
            if (left.Blocks.Count != 1 || right.Blocks.Count != 1)
                throw new NotSupportedException("Select one block per document; multi-block exports cannot be silently reduced to the first block.");
            bool graphical = left.Blocks[0].ProgrammingLanguage == "LAD" && right.Blocks[0].ProgrammingLanguage == "LAD";
            var diff = SimaticMlComparer.Compare(left, right);
            var interfaceDifferences = new JsonArray();
            var members = new InterfaceComparer().Compare(InterfaceSnapshotBuilder.Build(left.Blocks[0]),
                InterfaceSnapshotBuilder.Build(right.Blocks[0]));
            var leftRemanence = RawRemanence(left.Blocks[0]);
            var rightRemanence = RawRemanence(right.Blocks[0]);
            foreach (var section in members.Sections)
                foreach (var member in section.Members) NormalizeMember(member, diff.Interface!, interfaceDifferences, leftRemanence, rightRemanence);
            diff.Interface!.State = diff.Interface.Members.Any(row => row.State != CompareState.Equal) ? CompareState.Changed : CompareState.Equal;
            diff.State = diff.Interface.State == CompareState.Equal && diff.Content!.State == CompareState.Equal ? CompareState.Equal : CompareState.Changed;
            var pairs = graphical ? PlcLadderDrawing.LayoutComparison(diff) : new System.Collections.Generic.List<LadNetworkPairLayout>();
            var structural = OfflineAnalysisLogic.CompareFiles(leftPath, rightPath, 0, OfflineAnalysisLogic.MaxLimit);
            var data = new JsonObject
            {
                ["left"] = left.Blocks[0].Name, ["right"] = right.Blocks[0].Name,
                ["state"] = diff.State.ToString(), ["graphicalLadAvailable"] = graphical,
                ["comparison"] = JsonNode.Parse(JsonSerializer.Serialize(diff)),
                ["interfaceDifferences"] = interfaceDifferences,
                ["layouts"] = JsonNode.Parse(JsonSerializer.Serialize(pairs)),
                ["documentDiff"] = structural, ["dataComplete"] = structural["dataComplete"]?.GetValue<bool>() == true,
                ["scope"] =
                    "Offline exported-document comparison. LAD visualization is simplified; raw structural diff remains available. No TIA compilation or " +
                    "project modification."
            };
            var b =
                new StringBuilder("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><meta name=\"viewport\" " +
                "content=\"width=device-width\"><title>PLC comparison</title><style>body{font:15px " +
                "system-ui;margin:24px;background:#f6f8fa;color:#182433}h1{font-size:24px}.pair{display:grid;grid-template-columns:1fr " +
                "1fr;gap:16px}.side{overflow:auto;background:white;border:1px solid #ccd4dd;padding:12px}svg text{font:12px " +
                "monospace}pre{white-space:pre-wrap;overflow-wrap:anywhere;background:white;padding:16px}.legend{margin:20px " +
                "0}summary{cursor:pointer}svg{min-width:100%}@media(max-width:800px){.pair{grid-template-columns:1fr}}</style><h1>");
            b.Append(E(left.Blocks[0].Name)).Append(" → ").Append(E(right.Blocks[0].Name))
                .Append("</h1><p>Exported PLC document review</p><div class=\"legend\">Green: added · Red: removed · Amber: changed / rewired · Grey: unchanged</div>");
            foreach (var pair in pairs)
            {
                b.Append("<h2>Network ").Append(pair.NetworkNumber).Append(" — ").Append(E(pair.Title))
                    .Append("</h2><div class=\"pair\"><section class=\"side\"><h3>Before</h3>")
                    .Append(PlcLadderDrawing.Svg(pair.Left)).Append("</section><section class=\"side\"><h3>After</h3>").Append(PlcLadderDrawing.Svg(pair.Right))
                        .Append("</section></div>");
            }
            if (!graphical) b.Append("<p>LAD graphics are unavailable for this language; inspect the structural comparison below.</p>");
            b.Append("<details open><summary>Interface and document changes</summary><pre>")
                .Append(E(structural.ToJsonString(new JsonSerializerOptions { WriteIndented = true })))
                .Append("</pre></details><p>Simplified layout; validate engineering changes with the TIA compiler.</p></html>");
            html = b.ToString();
            return data;
        }

        private static Dictionary<(string Section, string Path), string> RawRemanence(BlockDefinition block)
        {
            var declared = new Dictionary<(string, string), string>();
            void Add(string section, string prefix, InterfaceMember member)
            {
                string name = member.Name.Normalize(NormalizationForm.FormC);
                string path = prefix.Length == 0 ? name : prefix + "/" + name;
                if (member.RawAttributes.TryGetValue("Remanence", out var value)) declared[(section, path)] = value ?? "";
                foreach (var child in member.Children) Add(section, path, child);
            }
            foreach (var section in block.InterfaceSections)
            {
                string name = InterfaceSectionOrder.Canonical.FirstOrDefault(canonical =>
                    string.Equals(canonical, section.Name.Trim(), StringComparison.OrdinalIgnoreCase)) ?? section.Name.Normalize(NormalizationForm.FormC);
                foreach (var member in section.Members) Add(name, "", member);
            }
            return declared;
        }

        private static bool NormalizeMember(InterfaceMemberComparison member, SactInterfaceResult legacy, JsonArray differences,
            Dictionary<(string Section, string Path), string> leftRemanence, Dictionary<(string Section, string Path), string> rightRemanence)
        {
            bool childrenEqual = true;
            foreach (var child in member.Children) childrenEqual &= NormalizeMember(child, legacy, differences, leftRemanence, rightRemanence);
            var anchor = member.Left ?? member.Right!;
            var changes = member.FieldChanges.Where(change => change.Field != InterfaceFieldKind.Retain && !DefaultOnly(change)).ToList();
            if (member.Left != null && member.Right != null)
            {
                leftRemanence.TryGetValue((anchor.Section, anchor.Path), out var left);
                rightRemanence.TryGetValue((anchor.Section, anchor.Path), out var right);
                if (!string.Equals(left, right, StringComparison.Ordinal))
                    changes.Add(new InterfaceFieldChange(InterfaceFieldKind.Retain, null, left, right));
            }
            foreach (var change in changes)
                differences.Add(new JsonObject {
                    ["section"] = anchor.Section, ["member"] = anchor.Path.Replace('/', '.'),
                    ["field"] = change.Field.ToString(), ["key"] = change.Key, ["left"] = change.LeftValue, ["right"] = change.RightValue
                });
            bool equal = member.Left != null && member.Right != null && changes.Count == 0 && childrenEqual;
            if (member.Left != null && member.Right != null)
                foreach (var row in legacy.Members.Where(row => row.Section == anchor.Section && row.Name == anchor.Path.Replace('/', '.')))
                    row.State = equal ? CompareState.Equal : CompareState.Changed;
            return equal;
        }

        private static bool DefaultOnly(InterfaceFieldChange change)
        {
            if (change.Field == InterfaceFieldKind.Informative)
                return change.LeftValue == "Unspecified" && change.RightValue == "False"
                    || change.LeftValue == "False" && change.RightValue == "Unspecified";
            return change.Field == InterfaceFieldKind.Accessibility
                && (change.LeftValue == null && change.RightValue == "Public" || change.LeftValue == "Public" && change.RightValue == null);
        }

        private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");
    }
}
