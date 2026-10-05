using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using TiaGitAddIn.Models.Lad;
using TiaGitAddIn.Models.Sact;
using TiaGitAddIn.Services;
using TiaGitAddIn.Services.SimaticMl;

namespace TiaMcpServer.ModelContextProtocol
{
    internal static partial class PlcLadderDrawing
    {
        internal static List<LadNetworkPairLayout> LayoutComparison(SactCompareResult comparison) => LadLayoutEngine.LayoutAll(comparison);
        internal static LadNetworkLayout Layout(NetworkSourceDefinition network) => LadLayoutEngine.LayoutBody(SimaticMlToSactMapper.MapNetworkBody(network), CompareState.Equal);

        internal static string Svg(LadNetworkLayout? layout)
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
