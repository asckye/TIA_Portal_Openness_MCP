using PlcNative = TiaMcp.Adapters.Native.Plc.PlcBlockPrimitives;
using System;
using TiaMcp.Adapters.Native.Plc;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.ExternalSources;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        public PlcSoftwareDetails ReadSoftwareInfo(string softwarePath)
        {
            var selected=ReadSelection(softwarePath);
            var attributes=ReadAttributes(selected.Value);
            return new PlcSoftwareDetails {
                Name=PlcNative.Name(selected.Value), Attributes=attributes, Description=selected.Value.ToString(),
                Meta=new Dictionary<string,object> {
                    ["softwarePath"]=selected.ExactPath,["scope"]="ordinary PLC software engineering attributes",
                    ["unreadableAttributes"]=attributes.Where(a=>a.Value is string s && s.StartsWith("<unreadable: ",StringComparison.Ordinal))
                        .Select(a=>a.Name).ToArray()
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
            var blocks=PlcNative.BlockGroup(selected.Value);
            if(blocks==null) throw new InvalidOperationException("PLC resolved but BlockGroup is unavailable.");
            var sections=new List<PlcSoftwareTreeNode> {
                SnapshotBlocks(blocks,"Program blocks",selected.ExactPath+"/blocks",warnings,budget,0)
            };
            var types=PlcNative.TypeGroup(selected.Value);
            if(types==null) throw new InvalidOperationException("PLC resolved but TypeGroup is unavailable; incomplete tree is not returned.");
            sections.Add(SnapshotTypes(types,"PLC data types",selected.ExactPath+"/types",budget,0));
            foreach (var unit in PlcUnitScope.Units(selected.Value))
            {
                var path = selected.ExactPath + "/units/" + Uri.EscapeDataString(unit.Key);
                budget.Add(unit.Key, path, unit.Key);
                sections.Add(new PlcSoftwareTreeNode { Name = unit.Key, Path = path, Kind = "unit", Display = unit.Key + " [Software unit]", Children = new[] {
                    SnapshotBlocks(PlcUnitScope.Blocks(selected.Value, unit.Value), "Program blocks", path + "/blocks", warnings, budget, 0),
                    SnapshotTypes(PlcUnitScope.Types(selected.Value, unit.Value), "PLC data types", path + "/types", budget, 0),
                    SnapshotTables(PlcUnitScope.Tags(selected.Value, unit.Value), "PLC tags", path + "/tag-tables", budget, 0),
                    SnapshotSources(PlcUnitScope.Sources(selected.Value, unit.Value), "External sources", path + "/external-sources", budget, 0)
                }});
            }
            var snapshot=sections.ToArray();
            var tree=PlcSoftwareReadPolicy.Render(PlcNative.Name(selected.Value),snapshot);
            return new PlcSoftwareTreeDetails {
                Tree=tree, Meta=new Dictionary<string,object> {
                    ["softwarePath"]=selected.ExactPath,["scope"]=PlcSoftwareReadPolicy.Scope,
                    ["paths"]=PlcSoftwareReadPolicy.Paths(snapshot),["unavailableDisplayAttributes"]=warnings.ToArray()
                }
            };
        }
        private static PlcSoftwareTreeNode SnapshotTables(PlcTagTableGroup group, string label, string path, PlcSoftwareReadBudget budget, int depth)
        {
            Depth(depth); budget.Add(label, path, label);
            var children = new List<PlcSoftwareTreeNode>();
            foreach (var table in group.TagTables)
            {
                var address = PlcSoftwareReadPolicy.ChildPath(path, table.Name); budget.Add(table.Name, address, table.Name);
                children.Add(new PlcSoftwareTreeNode { Name = table.Name, Path = address, Kind = "tag-table", Display = table.Name });
            }
            foreach (var child in group.Groups) children
                .Add(SnapshotTables(child, child.Name, PlcSoftwareReadPolicy.ChildPath(path, child.Name), budget, depth + 1));
            return new PlcSoftwareTreeNode { Name = label, Path = path, Kind = "tag-group", Display = label, Children = children.ToArray() };
        }
        private static PlcSoftwareTreeNode SnapshotSources(PlcExternalSourceGroup group, string label, string path, PlcSoftwareReadBudget budget, int depth)
        {
            Depth(depth); budget.Add(label, path, label);
            var children = new List<PlcSoftwareTreeNode>();
            foreach (var source in group.ExternalSources)
            {
                var address = PlcSoftwareReadPolicy.ChildPath(path, source.Name); budget.Add(source.Name, address, source.Name);
                children.Add(new PlcSoftwareTreeNode { Name = source.Name, Path = address, Kind = "external-source", Display = source.Name });
            }
            foreach (var child in group.Groups) children
                .Add(SnapshotSources(child, child.Name, PlcSoftwareReadPolicy.ChildPath(path, child.Name), budget, depth + 1));
            return new PlcSoftwareTreeNode { Name = label, Path = path, Kind = "source-group", Display = label, Children = children.ToArray() };
        }
        private static PlcSoftwareTreeNode SnapshotBlocks(PlcBlockGroup group,string label,string path,List<string> warnings,PlcSoftwareReadBudget budget,
                int depth)
        {
            Depth(depth);
            budget.Add(label,path,label);
            var children=new List<PlcSoftwareTreeNode>();
            foreach(var block in PlcNative.Blocks(group))
            {
                var name=PlcNative.Name(block); var objectPath=PlcSoftwareReadPolicy.ChildPath(path,name);
                var type=block.GetType().Name;
                if(type=="ArrayDB" || type=="GlobalDB" || type=="InstanceDB") type="DB";
                // Reuses the already-compiled IEngineeringObject.GetAttribute API;
                // Number is optional display metadata, never an object identity.
                var number=OptionalTreeAttribute(()=>((IEngineeringObject)block).GetAttribute("Number"),objectPath+"/Number","?",warnings);
                var language=OptionalTreeAttribute(()=>PlcNative.Language(block),objectPath+"/ProgrammingLanguage","unavailable",warnings);
                var display=name+" ["+type+number+", "+language+"]";
                budget.Add(name,objectPath,display);
                children.Add(new PlcSoftwareTreeNode { Name=name,Path=objectPath,Kind="block",Display=display });
            }
            foreach(var child in PlcNative.Groups(group)) children
                .Add(SnapshotBlocks(child,PlcNative.Name(child),PlcSoftwareReadPolicy.ChildPath(path,PlcNative.Name(child)),warnings,budget,depth+1));
            return new PlcSoftwareTreeNode { Name=label,Path=path,Kind="block-group",Display=label,Children=children.ToArray() };
        }
        private static PlcSoftwareTreeNode SnapshotTypes(PlcTypeGroup group,string label,string path,PlcSoftwareReadBudget budget,int depth)
        {
            Depth(depth);
            budget.Add(label,path,label);
            var children=new List<PlcSoftwareTreeNode>();
            foreach(var item in PlcNative.Types(group))
            {
                var name=PlcNative.Name(item); var type=item.GetType().Name;
                var objectPath=PlcSoftwareReadPolicy.ChildPath(path,name); var display=name+" ["+(type=="PlcStruct"?"UDT":type)+"]";
                budget.Add(name,objectPath,display);
                children.Add(new PlcSoftwareTreeNode { Name=name,Path=objectPath,Kind="type",Display=display });
            }
            foreach(var child in PlcNative.Groups(group)) children
                .Add(SnapshotTypes(child,PlcNative.Name(child),PlcSoftwareReadPolicy.ChildPath(path,PlcNative.Name(child)),budget,depth+1));
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
