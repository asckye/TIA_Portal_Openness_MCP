using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TiaMcp.Adapters
{
    // Pure snapshot and renderer. Snapshot enumeration must complete before rendering.
    internal sealed class PlcSoftwareTreeNode
    {
        internal string Name = "";
        internal string Path = "";
        internal string Kind = "";
        internal string Display = "";
        internal PlcSoftwareTreeNode[] Children = new PlcSoftwareTreeNode[0];
    }
    internal sealed class PlcSoftwareReadBudget
    {
        private int nodes;
        private long characters;
        internal void Add(string name,string path,string display)
        {
            nodes++; characters+=(long)name.Length+path.Length+display.Length;
            if(nodes>10000 || characters>1024*1024) throw new InvalidOperationException("Software tree exceeds the 10000-node/one-Mi-character snapshot budget.");
        }
    }
    internal static class PlcSoftwareReadPolicy
    {
        internal const string Scope = "ordinary PLC root/user block and type groups; excludes system groups, software units, tag tables, external sources and technology objects";
        internal static string ChildPath(string parent,string name) => parent + "/" + Uri.EscapeDataString(name);
        internal static string Render(string softwareName,PlcSoftwareTreeNode[] sections)
        {
            if(string.IsNullOrWhiteSpace(softwareName)) throw new InvalidOperationException("PLC software has no name.");
            var text=new StringBuilder().AppendLine(softwareName+" [PLC Software]");
            var paths=new HashSet<string>(StringComparer.Ordinal);
            var budget=new PlcSoftwareReadBudget();
            for(int i=0;i<sections.Length;i++) Append(text,sections[i],"",i==sections.Length-1,0,paths,budget);
            return text.ToString();
        }
        private static void Append(StringBuilder text,PlcSoftwareTreeNode node,string prefix,bool last,int depth,HashSet<string> paths,PlcSoftwareReadBudget budget)
        {
            if(depth>128) throw new InvalidOperationException("Software tree exceeds 128 levels.");
            if(string.IsNullOrWhiteSpace(node.Name) || string.IsNullOrWhiteSpace(node.Path) || !paths.Add(node.Kind+"\0"+node.Path))
                throw new InvalidOperationException("Software tree contains an empty or duplicate object identity.");
            budget.Add(node.Name,node.Path,node.Display);
            if(text.Length+prefix.Length+node.Display.Length+8>2*1024*1024) throw new InvalidOperationException("Software tree exceeds two Mi characters of display text.");
            text.AppendLine(prefix+(last?"└── ":"├── ")+node.Display);
            var next=prefix+(last?"    ":"│   ");
            for(int i=0;i<node.Children.Length;i++) Append(text,node.Children[i],next,i==node.Children.Length-1,depth+1,paths,budget);
        }
        internal static Dictionary<string,string>[] Paths(PlcSoftwareTreeNode[] sections)
        {
            // Render validates identity/depth first; this is only called on that snapshot.
            return sections.SelectMany(section=>Flatten(section).Select(n=>new Dictionary<string,string> {
                ["name"]=n.Name,["path"]=n.Path,["kind"]=n.Kind,
                ["objectPath"]=n.Path==section.Path ? "" : n.Path.Substring(section.Path.Length+1),
                ["selectorParameter"]=n.Kind=="block" ? "blockPath" : n.Kind=="type" ? "typePath" : "groupPath"
            })).ToArray();
        }
        private static IEnumerable<PlcSoftwareTreeNode> Flatten(PlcSoftwareTreeNode node)
        {
            yield return node;
            foreach(var child in node.Children) foreach(var item in Flatten(child)) yield return item;
        }
    }
}
