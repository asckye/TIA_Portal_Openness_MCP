using System;
using System.IO;
using System.Linq;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters
{
    internal static class PlcExchangePolicy
    {
        internal static T Exact<T>(System.Collections.Generic.IEnumerable<T> items,Func<T,string> path,string selected,string parameter="path")
        {
            var matches=items.Where(item=>string.Equals(path(item),selected,StringComparison.Ordinal)).Take(2).ToArray();
            if(matches.Length==0) throw AdapterPreconditionException.NotFound("Object not found at exact path: "+selected,parameter);
            if(matches.Length>1) throw new AdapterPreconditionException("Ambiguous object path: "+selected,parameter);
            return matches[0];
        }
        internal static T SelectTable<T>(System.Collections.Generic.IEnumerable<T> tables,string selected,Func<T,string> path,Func<T,string> name)
        {
            string canonical = ObjectPathEncoding.NormalizePath(selected,"table");
            var exact=tables.Where(item=>string.Equals(path(item),selected,StringComparison.Ordinal)
                || string.Equals(path(item),canonical,StringComparison.Ordinal)).Take(2).ToArray();
            if(exact.Length==1) return exact[0];
            if(exact.Length>1) throw new AdapterPreconditionException("Ambiguous tag table path: "+selected,"table");
            if(!selected.Contains("/"))
            {
                var names=tables.Where(item=>string.Equals(name(item),selected,StringComparison.Ordinal)).Take(2).ToArray();
                if(names.Length==1) return names[0];
                if(names.Length>1) throw new AdapterPreconditionException("Ambiguous tag table name: "+selected,"table");
            }
            throw AdapterPreconditionException.NotFound("Tag table not found at exact encoded path or unique raw name: "+selected,"table");
        }
        internal static string ObjectPath(string path,bool allowRoot=false,string parameter="groupPath")
        {
            return Native.Plc.PlcObjectPath.Format(Native.Plc.PlcObjectPath.Parts(path, allowRoot, parameter));
        }

        internal static FileInfo ExportDestination(string directory,string objectPath,bool preservePath)
        {
            var root=new DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(directory));
            if(!root.Exists) throw new AdapterPreconditionException("Export directory must already exist.","exportPath");
            var segments=ObjectPath(objectPath,false,"exportPath").Split('/').Select(Uri.UnescapeDataString).ToArray();
            foreach(var name in segments) PlcFoundationPolicy.RequireName(name);
            var relative=preservePath ? Path.Combine(segments) : segments.Last();
            var file=Path.GetFullPath(Path.Combine(root.FullName,relative+".xml"));
            var prefix=root.FullName.TrimEnd('\\','/')+Path.DirectorySeparatorChar;
            if(!file.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))
                throw new AdapterPreconditionException("Export target escapes the selected directory.","exportPath");
            // Preview never creates directories or removes an existing export.
            return PlcFoundationPolicy.XmlOutput(file);
        }
    }
}
