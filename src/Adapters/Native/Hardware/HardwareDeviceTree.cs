using System.Collections.Generic;
using System.Linq;
using System.Text;
using Siemens.Engineering.HW;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        internal static void BuildDeviceItemTree(StringBuilder sb, DeviceItem node, List<bool> ancestorStates, int depth, int maxDepth)
        {
            if (depth >= maxDepth) return;

            var items = node.Items?.ToList();
            var children = node.DeviceItems?.ToList() ?? new List<DeviceItem>();
            if (items != null)
            {
                var childSet = new HashSet<DeviceItem>(children);
                items.RemoveAll(item => item is DeviceItem child && childSet.Contains(child));
                for (int i = 0; i < items.Count; i++)
                {
                    var it = items[i];
                    var isLast = i == items.Count - 1 && children.Count == 0;
                    sb.AppendLine($"{GetTreePrefixStatic(ancestorStates, isLast)}{it.Name} [Hardware Component]");
                }
            }

            for (int i = 0; i < children.Count; i++)
            {
                var child = children[i];
                var isLast = i == children.Count - 1;
                sb.AppendLine($"{GetTreePrefixStatic(ancestorStates, isLast)}{child.Name} [DeviceItem]");
                BuildDeviceItemTree(sb, child, new List<bool>(ancestorStates) { isLast }, depth + 1, maxDepth);
            }
        }
        private static string GetTreePrefixStatic(List<bool> ancestorStates, bool isLast)
        {
            var prefix = new StringBuilder();
            for (int i = 0; i < ancestorStates.Count; i++)
            {
                prefix.Append(ancestorStates[i] ? "    " : "│   ");
            }
            prefix.Append(isLast ? "└── " : "├── ");
            return prefix.ToString();
        }
    }
}
