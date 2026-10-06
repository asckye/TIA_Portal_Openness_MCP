using System;
using System.Linq;
using System.Text.RegularExpressions;
using Siemens.Engineering;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using System.Collections.Generic;

namespace TiaMcp.PlcFoundation
{
    public sealed partial class PlcFoundationEngine
    {
        // The owning worker host selects the policy before accepting any request.
        public bool SourceCandidateEnabled { get; set; }

        public string ReadProjectTree()
        {
            var project=Project(); var text=new System.Text.StringBuilder();
            text.AppendLine(project.Name);
            foreach(var device in project.Devices) ProjectTreeDevice(text,device,"devices",1);
            foreach(var group in project.DeviceGroups) ProjectTreeGroup(text,group,"device-groups",1);
            text.AppendLine("  Ungrouped devices");
            foreach(var device in project.UngroupedDevicesGroup.Devices) ProjectTreeDevice(text,device,"ungrouped",2);
            return text.ToString();
        }
        private static void ProjectTreeGroup(System.Text.StringBuilder text,DeviceUserGroup group,string path,int depth)
        {
            Depth(depth); path=Child(path,group.Name);
            text.AppendLine(new string(' ',depth*2)+"Group: "+group.Name);
            foreach(var device in group.Devices) ProjectTreeDevice(text,device,path+"/devices",depth+1);
            foreach(var child in group.Groups) ProjectTreeGroup(text,child,path+"/groups",depth+1);
        }
        private static void ProjectTreeDevice(System.Text.StringBuilder text,Device device,string path,int depth)
        {
            Depth(depth); path=Child(path,device.Name);
            text.AppendLine(new string(' ',depth*2)+"Device: "+device.Name);
            foreach(var item in device.DeviceItems) ProjectTreeItem(text,item,path,depth+1);
        }
        private static void ProjectTreeItem(System.Text.StringBuilder text,DeviceItem item,string path,int depth)
        {
            Depth(depth); path=Child(path,item.Name);
            text.AppendLine(new string(' ',depth*2)+"Item: "+item.Name);
            var container=((IEngineeringServiceProvider)item).GetService<SoftwareContainer>();
            if(container?.Software!=null) text.AppendLine(new string(' ',(depth+1)*2)+container.Software.GetType().Name+": "+container.Software.Name+" [softwarePath="+path+"]");
            foreach(var child in item.DeviceItems) ProjectTreeItem(text,child,path,depth+1);
        }
        private PlcSoftware ReadPlc(string softwarePath) => ReadSelection(softwarePath).Value;
        private PlcReadCandidate<PlcSoftware> ReadSelection(string softwarePath)
        {
            var p=Project();
            var candidates=new List<PlcReadCandidate<PlcSoftware>>();
            foreach(var device in p.Devices) ReadDeviceCandidates(device,new string[0],"devices",candidates);
            foreach(var group in p.DeviceGroups) ReadGroupCandidates(group,new string[0],"device-groups",candidates,0);
            var ungrouped=new List<PlcReadCandidate<PlcSoftware>>();
            foreach(var device in p.UngroupedDevicesGroup.Devices) ReadDeviceCandidates(device,new string[0],"ungrouped",ungrouped);
            foreach(var candidate in ungrouped) { candidate.AllowLegacyAlias=false; candidates.Add(candidate); }
            if (SourceCandidateEnabled)
            {
                var exact = new List<TiaMcp.Adapters.Contracts.Candidates.PlcPathTarget<PlcReadCandidate<PlcSoftware>>>();
                foreach (var c in candidates)
                {
                    var query = new[] { c.ExactPath, c.Value.Name };
                    var same = exact.FirstOrDefault(r => object.Equals(r.Value.Value, c.Value));
                    if (same == null) exact.Add(new TiaMcp.Adapters.Contracts.Candidates.PlcPathTarget<PlcReadCandidate<PlcSoftware>> { Value = c, Path = c.ExactPath, QueryNames = query });
                    else same.QueryNames = same.QueryNames.Concat(query).Distinct(StringComparer.Ordinal).ToArray();
                }
                return TiaMcp.Adapters.Contracts.Candidates.PlcPathSelection.Select(exact, softwarePath).Value;
            }
            return PlcReadPathPolicy.Select(candidates,softwarePath);
        }
        private static void ReadGroupCandidates(DeviceUserGroup group,string[] parents,string path,List<PlcReadCandidate<PlcSoftware>> result,int depth)
        {
            Depth(depth);
            var names=parents.Concat(new[]{group.Name}).ToArray();
            path=Child(path,group.Name);
            foreach(var device in group.Devices) ReadDeviceCandidates(device,names,path+"/devices",result);
            foreach(var child in group.Groups) ReadGroupCandidates(child,names,path+"/groups",result,depth+1);
        }
        private static void ReadDeviceCandidates(Device device,string[] groups,string path,List<PlcReadCandidate<PlcSoftware>> result)
        {
            path=Child(path,device.Name);
            foreach(var item in device.DeviceItems) ReadItemCandidates(item,device.Name,groups,path,result,0,device);
        }
        private static void ReadItemCandidates(DeviceItem item,string device,string[] groups,string path,List<PlcReadCandidate<PlcSoftware>> result,int depth,Device deviceContext)
        {
            Depth(depth); path=Child(path,item.Name);
            var container=((IEngineeringServiceProvider)item).GetService<SoftwareContainer>();
            if(container?.Software is PlcSoftware plc) result.Add(new PlcReadCandidate<PlcSoftware> { Value=plc,ExactPath=path,Groups=groups,Device=device,Host=item.Name,Context=item,DeviceContext=deviceContext });
            foreach(var child in item.DeviceItems) ReadItemCandidates(child,device,groups,path,result,depth+1,deviceContext);
        }
        private static PlcAttributeValue[] ReadAttributes(IEngineeringObject item)
        {
            return item.GetAttributeInfos().Select(info =>
            {
                object? value;
                try { value = item.GetAttribute(info.Name); }
                catch (Exception ex) { value = "<unreadable: " + ex.GetType().Name + ">"; }
                if (value != null)
                {
                    var type = value.GetType();
                    if (!(type.IsPrimitive || type.IsEnum || value is string || value is decimal || value is DateTime || value is TimeSpan || value is Guid))
                    { try { value = value.ToString(); } catch /* swallow(native-fallback): an attribute value that cannot render as text is represented by its type name in the read result */ { value = "<" + type.Name + ">"; } }
                }
                return new PlcAttributeValue { Name = info.Name, Value = value, AccessMode = Enum.GetName(typeof(EngineeringAttributeAccessMode), info.AccessMode) };
            }).ToArray();
        }
        private static string? OptionalNamespace(object value)
        {
            // A whitelisted read-only optional property, matching the V17 helper.
            // Absence is represented as null; never invoke a caller-selected member.
            var property = value.GetType().GetProperty("Namespace");
            return property != null && property.PropertyType == typeof(string) && property.GetIndexParameters().Length == 0
                ? (string?)property.GetValue(value, null) : null;
        }
        private static PlcBlockDetails BlockDetails(PlcBlock b) => new PlcBlockDetails {
            Name=b.Name, TypeName=b.GetType().Name, Namespace=OptionalNamespace(b),
            ProgrammingLanguage=Enum.GetName(typeof(ProgrammingLanguage),b.ProgrammingLanguage),
            MemoryLayout=Enum.GetName(typeof(MemoryLayout),b.MemoryLayout), IsConsistent=b.IsConsistent,
            HeaderName=b.HeaderName, ModifiedDate=b.ModifiedDate, IsKnowHowProtected=b.IsKnowHowProtected,
            Attributes=ReadAttributes(b), Description=b.ToString()
        };
        private static PlcTypeDetails TypeDetails(PlcType t) => new PlcTypeDetails {
            Name=t.Name, TypeName=t.GetType().Name, Namespace=OptionalNamespace(t), IsConsistent=t.IsConsistent,
            ModifiedDate=t.ModifiedDate, IsKnowHowProtected=t.IsKnowHowProtected, Attributes=ReadAttributes(t), Description=t.ToString()
        };
        private static Regex? NameFilter(string regexName) => string.IsNullOrEmpty(regexName) ? null : new Regex(regexName, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250));
        // Root external sources only, matching the historical V17 profile. No
        // generation, import, deletion, external-file read or software-unit traversal.
        public string[] ReadExternalSourceNames(string softwarePath) =>
            ReadPlc(softwarePath).ExternalSourceGroup.ExternalSources.Select(source=>source.Name).ToArray();
        public PlcBlockDetails[] ReadBlocks(string softwarePath, string regexName = "")
        {
            var filter=NameFilter(regexName);
            return BlockGroups(ReadPlc(softwarePath).BlockGroup).SelectMany(g=>g.Value.Blocks).Where(b=>filter==null || filter.IsMatch(b.Name)).Select(BlockDetails).ToArray();
        }
        public PlcBlockDetails ReadBlockInfo(string softwarePath, string blockPath)
        {
            var path=PlcExchangePolicy.ObjectPath(blockPath);
            var blocks=BlockGroups(ReadPlc(softwarePath).BlockGroup).SelectMany(g=>g.Value.Blocks.Select(b=>new Located<PlcBlock>(Child(g.Path,b.Name),b)));
            return BlockDetails(PlcExchangePolicy.Exact(blocks,x=>x.Path,path).Value);
        }
        public PlcTypeDetails ReadTypeInfo(string softwarePath, string typePath)
        {
            var path=PlcExchangePolicy.ObjectPath(typePath);
            var types=TypeGroups(ReadPlc(softwarePath).TypeGroup).SelectMany(g=>g.Value.Types.Select(t=>new Located<PlcType>(Child(g.Path,t.Name),t)));
            return TypeDetails(PlcExchangePolicy.Exact(types,x=>x.Path,path).Value);
        }
        public PlcTypeDetails[] ReadTypes(string softwarePath, string regexName = "")
        {
            var filter=NameFilter(regexName);
            return TypeGroups(ReadPlc(softwarePath).TypeGroup).SelectMany(g=>g.Value.Types).Where(t=>filter==null || filter.IsMatch(t.Name)).Select(TypeDetails).ToArray();
        }
        // V17 ResolvePlcTagTablesCollection selects root TagTableGroup.TagTables only.
        public string[] ReadTagTableNames(string softwarePath) => ReadPlc(softwarePath).TagTableGroup.TagTables.Select(t=>t.Name).ToArray();
        public PlcBlockHierarchy ReadBlockHierarchy(string softwarePath) => Hierarchy(ReadPlc(softwarePath).BlockGroup,0);
        private static PlcBlockHierarchy Hierarchy(PlcBlockGroup group,int depth)
        {
            Depth(depth);
            return new PlcBlockHierarchy { Name=group.Name, Blocks=group.Blocks.Select(BlockDetails).ToArray(), Groups=group.Groups.Select(g=>Hierarchy(g,depth+1)).ToArray() };
        }
    }
}
