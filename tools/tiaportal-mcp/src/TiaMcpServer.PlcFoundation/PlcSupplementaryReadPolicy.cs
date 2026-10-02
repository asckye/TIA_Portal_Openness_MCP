using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.PlcFoundation
{
    public sealed class PlcTechnologyReadRow
    {
        public string Name { get; set; } = "";
        public string? OfSystemLibElement { get; set; }
        public string? OfSystemLibVersion { get; set; }
        public string[] UnavailableAttributes { get; set; } = new string[0];
        public string Folder { get; set; } = "";
    }
    public sealed class PlcSupplementaryReadResult
    {
        public string SoftwarePath { get; set; } = "";
        public string ReleaseKey { get; set; } = "";
        public string Scope { get; set; } = "";
        public object Items { get; set; } = new string[0];
    }
    internal static class PlcSupplementaryReadPolicy
    {
        internal const string WatchScope = "ordinary PLC root/user-group watch-table paths only; force tables, entries and live values excluded";
        internal const string TechnologyScope = "ordinary PLC root/user-group top-level technology-object engineering metadata only; sub-level TOs, parameters and live values excluded";
        internal const string TechnologyRootScope = "ordinary PLC root top-level technology-object engineering metadata only; no user-group composition in this API; sub-level TOs, parameters and live values excluded";
        internal static string TechnologyReadScope(string key) => key=="19" || key=="20" || key=="21" ? TechnologyScope : TechnologyRootScope;
        internal static void RequireRelease(string key,bool watch=false)
        {
            if(!new[]{"14sp1","15.1","16","17","18","19","20","21"}.Contains(key)) throw new NotSupportedException("Exact supported release key required; original V14/V15 and unknown releases are excluded.");
            if(watch && key=="14sp1") throw new NotSupportedException("The V14 SP1 PublicAPI lacks PlcSoftware.WatchAndForceTableGroup; no guessed reflection fallback is permitted.");
        }
        internal static string Name(string value)
        {
            if(value==null || value.Length>4096 || string.IsNullOrWhiteSpace(value) || value=="." || value=="..") throw new InvalidOperationException("Missing or invalid native object name.");
            return value;
        }
        internal static T ReadSnapshot<T>(Func<T> read)
        {
            try { return read(); }
            catch(Exception) { throw new InvalidOperationException("Supplementary PLC engineering read failed. No incomplete result was returned; verify the selected project and API metadata locally."); }
        }
        // Genuine typed getters are supplied by the adapter; tests use explicit
        // fake delegates. Getter failures/nulls are optional metadata, not empty
        // invented values. Validation stays outside the catch so bounds fail closed.
        internal static PlcTechnologyReadRow TechnologyMetadata(string name,Func<string?> element,Func<Version?> version)
        {
            var unavailable=new List<string>();
            string? Read<T>(string attribute,Func<T> getter,Func<T,string?> format)
            {
                T value;
                try { value=getter(); }
                catch(Exception) { unavailable.Add(attribute); return null; }
                var text=format(value);
                if(text==null) { unavailable.Add(attribute); return null; }
                return Name(text);
            }
            var row=new PlcTechnologyReadRow {
                Name=Name(name),
                OfSystemLibElement=Read("OfSystemLibElement",element,value=>value),
                OfSystemLibVersion=Read("OfSystemLibVersion",version,value=>value?.ToString())
            };
            row.UnavailableAttributes=unavailable.ToArray(); return row;
        }
        internal static string[] WatchPaths<T>(T root,Func<T,IEnumerable<string>> names,Func<T,IEnumerable<T>> groups,Func<T,string> name) where T:class
        {
            var result=new List<string>(); var visited=new HashSet<T>(); long size=0;
            Action<T,string,int> walk=null!;
            walk=(group,folder,depth)=> {
                if(depth>128 || !visited.Add(group) || visited.Count>10000) throw new InvalidOperationException("Invalid watch group tree.");
                size+=folder.Length;
                if(size>1024*1024) throw new InvalidOperationException("Watch group budget exceeded.");
                foreach(var item in names(group))
                {
                    var segment=Uri.EscapeDataString(Name(item));
                    var path=folder.Length==0?segment:folder+"/"+segment;
                    size+=path.Length;
                    if(result.Count>=10000 || size>1024*1024) throw new InvalidOperationException("Watch snapshot budget exceeded.");
                    result.Add(path);
                }
                var siblings=new HashSet<string>(StringComparer.Ordinal);
                foreach(var child in groups(group))
                {
                    var segment=Uri.EscapeDataString(Name(name(child)));
                    if(!siblings.Add(segment)) throw new InvalidOperationException("Duplicate watch group identity.");
                    walk(child,folder.Length==0?segment:folder+"/"+segment,depth+1);
                }
            };
            walk(root,"",0); return WatchNames(result);
        }
        internal static string[] WatchNames(IEnumerable<string> source)
        {
            var result=new List<string>(); var seen=new HashSet<string>(StringComparer.Ordinal); long size=0;
            foreach(var name in source)
            {
                Name(name); size+=name.Length;
                if(!seen.Add(name)) throw new InvalidOperationException("Duplicate watch-table name.");
                if(result.Count>=10000 || size>1024*1024) throw new InvalidOperationException("Watch-table snapshot budget exceeded.");
                result.Add(name);
            }
            return result.OrderBy(n=>n,StringComparer.Ordinal).ToArray();
        }
        // Delegates enumerate genuine typed compositions in production. Offline tests
        // explicitly supply fake delegates, not fake Siemens assemblies or namespaces.
        internal static PlcTechnologyReadRow[] Technologies<T>(T root,Func<T,IEnumerable<PlcTechnologyReadRow>> rows,Func<T,IEnumerable<T>> groups,Func<T,string> name) where T:class
        {
            var result=new List<PlcTechnologyReadRow>(); var visited=new HashSet<T>(); var paths=new HashSet<string>(StringComparer.Ordinal); long size=0;
            Action<T,string,int> walk=null!;
            walk=(group,folder,depth)=> {
                if(depth>128 || !visited.Add(group) || visited.Count>10000) throw new InvalidOperationException("Invalid, cyclic or oversized technology group tree.");
                size+=folder.Length;
                if(size>1024*1024) throw new InvalidOperationException("Technology group path budget exceeded.");
                foreach(var row in rows(group))
                {
                    Name(row.Name);
                    if(row.OfSystemLibElement!=null) Name(row.OfSystemLibElement);
                    if(row.OfSystemLibVersion!=null) Name(row.OfSystemLibVersion);
                    var path=folder+"/"+Uri.EscapeDataString(row.Name);
                    size+=(long)path.Length+(row.OfSystemLibElement?.Length??0)+(row.OfSystemLibVersion?.Length??0);
                    if(!paths.Add(path) || result.Count>=10000 || size>1024*1024) throw new InvalidOperationException("Invalid or oversized technology-object snapshot.");
                    row.Folder=folder; result.Add(row);
                }
                var siblings=new HashSet<string>(StringComparer.Ordinal);
                foreach(var child in groups(group))
                {
                    var segment=Uri.EscapeDataString(Name(name(child)));
                    if(!siblings.Add(segment)) throw new InvalidOperationException("Duplicate technology-group identity.");
                    walk(child,folder.Length==0?segment:folder+"/"+segment,depth+1);
                }
            };
            walk(root,"",0); return result.ToArray();
        }
    }
}
