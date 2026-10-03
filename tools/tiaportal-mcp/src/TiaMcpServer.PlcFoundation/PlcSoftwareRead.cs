using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;

namespace TiaMcp.PlcFoundation
{
    public sealed partial class PlcFoundationEngine
    {
        public PlcSoftwareDetails ReadSoftwareInfo(string softwarePath)
        {
            var selected=ReadSelection(softwarePath);
            var attributes=ReadAttributes(selected.Value);
            return new PlcSoftwareDetails {
                Name=selected.Value.Name, Attributes=attributes, Description=selected.Value.ToString(),
                Meta=new Dictionary<string,object> {
                    ["softwarePath"]=selected.ExactPath,["scope"]="ordinary PLC software engineering attributes",
                    ["unreadableAttributes"]=attributes.Where(a=>a.Value is string s && s.StartsWith("<unreadable: ",StringComparison.Ordinal)).Select(a=>a.Name).ToArray()
                }
            };
        }
        public PlcSoftwareTreeDetails ReadSoftwareTree(string softwarePath)
        {
            var selected=ReadSelection(softwarePath);
            var warnings=new List<string>();
            var budget=new PlcSoftwareReadBudget();
            // Materialize all required names/compositions first. Any failure throws and
            // prevents returning a misleading successful partial tree.
            var blocks=selected.Value.BlockGroup;
            if(blocks==null) throw new InvalidOperationException("PLC resolved but BlockGroup is unavailable.");
            var sections=new List<PlcSoftwareTreeNode> {
                SnapshotBlocks(blocks,"Program blocks",selected.ExactPath+"/blocks",warnings,budget,0)
            };
            var types=selected.Value.TypeGroup;
            if(types==null) throw new InvalidOperationException("PLC resolved but TypeGroup is unavailable; incomplete tree is not returned.");
            sections.Add(SnapshotTypes(types,"PLC data types",selected.ExactPath+"/types",budget,0));
            var snapshot=sections.ToArray();
            var tree=PlcSoftwareReadPolicy.Render(selected.Value.Name,snapshot);
            return new PlcSoftwareTreeDetails {
                Tree=tree, Meta=new Dictionary<string,object> {
                    ["softwarePath"]=selected.ExactPath,["scope"]=PlcSoftwareReadPolicy.Scope,
                    ["paths"]=PlcSoftwareReadPolicy.Paths(snapshot),["unavailableDisplayAttributes"]=warnings.ToArray()
                }
            };
        }
        private static PlcSoftwareTreeNode SnapshotBlocks(PlcBlockGroup group,string label,string path,List<string> warnings,PlcSoftwareReadBudget budget,int depth)
        {
            Depth(depth);
            budget.Add(label,path,label);
            var children=new List<PlcSoftwareTreeNode>();
            foreach(var block in group.Blocks)
            {
                var name=block.Name; var objectPath=PlcSoftwareReadPolicy.ChildPath(path,name);
                var type=block.GetType().Name;
                if(type=="ArrayDB" || type=="GlobalDB" || type=="InstanceDB") type="DB";
                // Reuses the already-compiled IEngineeringObject.GetAttribute API;
                // Number is optional display metadata, never an object identity.
                var number=OptionalTreeAttribute(()=>((IEngineeringObject)block).GetAttribute("Number"),objectPath+"/Number","?",warnings);
                var language=OptionalTreeAttribute(()=>block.ProgrammingLanguage,objectPath+"/ProgrammingLanguage","unavailable",warnings);
                var display=name+" ["+type+number+", "+language+"]";
                budget.Add(name,objectPath,display);
                children.Add(new PlcSoftwareTreeNode { Name=name,Path=objectPath,Kind="block",Display=display });
            }
            foreach(var child in group.Groups) children.Add(SnapshotBlocks(child,child.Name,PlcSoftwareReadPolicy.ChildPath(path,child.Name),warnings,budget,depth+1));
            return new PlcSoftwareTreeNode { Name=label,Path=path,Kind="block-group",Display=label,Children=children.ToArray() };
        }
        private static PlcSoftwareTreeNode SnapshotTypes(PlcTypeGroup group,string label,string path,PlcSoftwareReadBudget budget,int depth)
        {
            Depth(depth);
            budget.Add(label,path,label);
            var children=new List<PlcSoftwareTreeNode>();
            foreach(var item in group.Types)
            {
                var name=item.Name; var type=item.GetType().Name;
                var objectPath=PlcSoftwareReadPolicy.ChildPath(path,name); var display=name+" ["+(type=="PlcStruct"?"UDT":type)+"]";
                budget.Add(name,objectPath,display);
                children.Add(new PlcSoftwareTreeNode { Name=name,Path=objectPath,Kind="type",Display=display });
            }
            foreach(var child in group.Groups) children.Add(SnapshotTypes(child,child.Name,PlcSoftwareReadPolicy.ChildPath(path,child.Name),budget,depth+1));
            return new PlcSoftwareTreeNode { Name=label,Path=path,Kind="type-group",Display=label,Children=children.ToArray() };
        }
        private static string OptionalTreeAttribute(Func<object?> read,string path,string fallback,List<string> warnings)
        {
            try { var value=read(); if(value!=null) return value.ToString() ?? fallback; }
            catch(Exception ex) { warnings.Add(path+": "+ex.GetType().Name); return fallback; }
            warnings.Add(path+": null"); return fallback;
        }
    }
}
