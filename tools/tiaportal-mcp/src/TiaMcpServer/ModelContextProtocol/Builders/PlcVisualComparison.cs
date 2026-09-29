using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaGitAddIn.Models.Lad;
using TiaGitAddIn.Models.Sact;
using TiaGitAddIn.Services;
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
            var pairs = graphical ? LadLayoutEngine.LayoutAll(diff) : new System.Collections.Generic.List<LadNetworkPairLayout>();
            var structural = OfflineAnalysisLogic.CompareFiles(leftPath, rightPath, 0, OfflineAnalysisLogic.MaxLimit);
            var data = new JsonObject
            {
                ["left"] = left.Blocks[0].Name, ["right"] = right.Blocks[0].Name,
                ["state"] = diff.State.ToString(), ["graphicalLadAvailable"] = graphical,
                ["comparison"] = JsonNode.Parse(JsonSerializer.Serialize(diff)),
                ["layouts"] = JsonNode.Parse(JsonSerializer.Serialize(pairs)),
                ["documentDiff"] = structural, ["dataComplete"] = structural["dataComplete"]?.GetValue<bool>() == true,
                ["scope"] = "Offline exported-document comparison. LAD visualization is simplified; raw structural diff remains available. No TIA compilation or project modification."
            };
            var b = new StringBuilder("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>PLC comparison</title><style>body{font:15px system-ui;margin:24px;background:#f6f8fa;color:#182433}h1{font-size:24px}.pair{display:grid;grid-template-columns:1fr 1fr;gap:16px}.side{overflow:auto;background:white;border:1px solid #ccd4dd;padding:12px}svg text{font:12px monospace}pre{white-space:pre-wrap;overflow-wrap:anywhere;background:white;padding:16px}.legend{margin:20px 0}summary{cursor:pointer}svg{min-width:100%}@media(max-width:800px){.pair{grid-template-columns:1fr}}</style><h1>");
            b.Append(E(left.Blocks[0].Name)).Append(" → ").Append(E(right.Blocks[0].Name)).Append("</h1><p>Exported PLC document review</p><div class=\"legend\">Green: added · Red: removed · Amber: changed / rewired · Grey: unchanged</div>");
            foreach (var pair in pairs)
            {
                b.Append("<h2>Network ").Append(pair.NetworkNumber).Append(" — ").Append(E(pair.Title)).Append("</h2><div class=\"pair\"><section class=\"side\"><h3>Before</h3>")
                    .Append(Svg(pair.Left)).Append("</section><section class=\"side\"><h3>After</h3>").Append(Svg(pair.Right)).Append("</section></div>");
            }
            if (!graphical) b.Append("<p>LAD graphics are unavailable for this language; inspect the structural comparison below.</p>");
            b.Append("<details open><summary>Interface and document changes</summary><pre>").Append(E(structural.ToJsonString(new JsonSerializerOptions { WriteIndented = true })))
                .Append("</pre></details><p>Simplified layout; validate engineering changes with the TIA compiler.</p></html>");
            html = b.ToString();
            return data;
        }

        private static string Svg(LadNetworkLayout? layout)
        {
            if (layout == null) return "<p>No network on this side.</p>";
            int width = Math.Max(480, (layout.ColumnCount + 1) * 190), height = Math.Max(160, (layout.RowCount + 1) * 120);
            var b = new StringBuilder("<svg role=\"img\" aria-label=\"Ladder network\" xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ");
            b.Append(width).Append(' ').Append(height).Append("\">");
            foreach (var wire in layout.Wires)
                b.Append("<line stroke=\"#8291a2\" stroke-width=\"2\" x1=\"").Append(95 + wire.FromColumn * 190).Append("\" y1=\"").Append(60 + wire.FromRow * 120)
                    .Append("\" x2=\"").Append(95 + wire.ToColumn * 190).Append("\" y2=\"").Append(60 + wire.ToRow * 120).Append("\"/>");
            foreach (var e in layout.Elements)
            {
                int x = 35 + e.Column * 190, y = 30 + e.Row * 120;
                string color = e.DiffState == CompareState.MissingOnLeft ? "#dcfce7" : e.DiffState == CompareState.MissingOnRight ? "#fee2e2" : e.DiffState == CompareState.Changed ? "#fef3c7" : "#f1f5f9";
                string symbol = e.ElementType == LadElementType.Contact ? "─| |─" : e.ElementType == LadElementType.NegatedContact ? "─|/|─" : e.ElementType == LadElementType.Coil ? "─( )─" : e.ElementType == LadElementType.Powerrail ? "│" : e.DisplayName;
                b.Append("<g><title>").Append(E(e.ElementType + " " + e.DisplayName + " " + e.Operand + " " + e.Comment)).Append("</title><rect rx=\"5\" stroke=\"#475569\" fill=\"")
                    .Append(color).Append("\" x=\"").Append(x).Append("\" y=\"").Append(y).Append("\" width=\"120\" height=\"60\"/><text text-anchor=\"middle\" x=\"")
                    .Append(x + 60).Append("\" y=\"").Append(y + 25).Append("\">").Append(E(symbol)).Append("</text><text text-anchor=\"middle\" x=\"").Append(x + 60).Append("\" y=\"")
                    .Append(y + 48).Append("\">").Append(E(e.Operand)).Append("</text></g>");
            }
            return b.Append("</svg>").ToString();
        }
        private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");
    }
}
