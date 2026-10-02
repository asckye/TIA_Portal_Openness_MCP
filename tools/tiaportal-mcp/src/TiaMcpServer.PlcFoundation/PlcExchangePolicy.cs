using System;
using System.IO;
using System.Linq;

namespace TiaMcp.PlcFoundation
{
    internal static class PlcExchangePolicy
    {
        internal static T Exact<T>(System.Collections.Generic.IEnumerable<T> items,Func<T,string> path,string selected)
        {
            var matches=items.Where(item=>string.Equals(path(item),selected,StringComparison.Ordinal)).Take(2).ToArray();
            if(matches.Length!=1) throw new ArgumentException((matches.Length==0 ? "Object not found at exact path: " : "Ambiguous object path: ")+selected);
            return matches[0];
        }
        internal static string ObjectPath(string path,bool allowRoot=false)
        {
            if(path=="" && allowRoot) return "";
            if(string.IsNullOrWhiteSpace(path) || path.Contains("\\")) throw new ArgumentException("Use a nonempty slash-separated object path.");
            return string.Join("/",path.Split('/').Select(segment=>
            {
                var name=Uri.UnescapeDataString(segment);
                if(string.IsNullOrWhiteSpace(name) || name=="." || name=="..") throw new ArgumentException("Empty and traversal object path segments are refused.");
                return Uri.EscapeDataString(name);
            }));
        }
        internal static FileInfo ExportDestination(string directory,string objectPath,bool preservePath)
        {
            var root=new DirectoryInfo(directory);
            if(!root.Exists) throw new ArgumentException("Export directory must already exist.");
            var segments=ObjectPath(objectPath).Split('/').Select(Uri.UnescapeDataString).ToArray();
            foreach(var name in segments) PlcFoundationPolicy.RequireName(name);
            var relative=preservePath ? Path.Combine(segments) : segments.Last();
            var file=Path.GetFullPath(Path.Combine(root.FullName,relative+".xml"));
            var prefix=root.FullName.TrimEnd('\\','/')+Path.DirectorySeparatorChar;
            if(!file.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Export target escapes the selected directory.");
            // Preview never creates directories or removes an existing export.
            return PlcFoundationPolicy.XmlOutput(file);
        }
    }
}
