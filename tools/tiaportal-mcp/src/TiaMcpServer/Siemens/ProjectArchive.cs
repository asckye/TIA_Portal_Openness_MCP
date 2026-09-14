using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    internal static class ProjectArchive
    {
        internal static JsonObject Create(object project,string archivePath,bool dryRun=true)
        {
            var method=project.GetType().GetMethods().SingleOrDefault(m=>m.Name=="Archive"&&m.GetParameters().Length==3&&
                m.GetParameters()[0].ParameterType==typeof(DirectoryInfo)&&m.GetParameters()[1].ParameterType==typeof(string)&&
                m.GetParameters()[2].ParameterType.IsEnum);
            if(method==null)throw new PortalException(PortalErrorCode.NotSupportedOnVersion,"Native project Archive API unavailable on this project/session; use its supported project workflow.");
            var modified=PropertyPathReader.Read(project,"IsModified");
            if(!modified.Success || modified.Value is not bool isModified)throw new PortalException(PortalErrorCode.InvalidState,"Cannot verify saved project state.");
            if(isModified)throw new PortalException(PortalErrorCode.InvalidState,"Project has unsaved changes. Save explicitly before archiving; no automatic save is performed.");
            var path=Path.GetFullPath(archivePath);
            if(!System.Text.RegularExpressions.Regex.IsMatch(path,@"(?i)\.zap(20|21)$"))throw new PortalException(PortalErrorCode.InvalidParams,"Use a .zap20 or .zap21 file path matching the project version.");
            var version=project.GetType().Assembly.GetName().Version?.Major;
            if((version==20||version==21)&&!path.EndsWith(".zap"+version,StringComparison.OrdinalIgnoreCase))throw new PortalException(PortalErrorCode.InvalidParams,"Archive extension does not match the TIA API version.");
            if(File.Exists(path))throw new PortalException(PortalErrorCode.InvalidParams,"Archive target already exists; choose a new file name.");
            var mode=method.GetParameters()[2].ParameterType;
            if(!Enum.GetNames(mode).Contains("Compressed"))throw new PortalException(PortalErrorCode.NotSupportedOnVersion,"Compressed archive mode unavailable.");
            var result=new JsonObject{["success"]=true,["dryRun"]=dryRun,["path"]=path,["kind"]="NativeProjectArchive",
                ["mode"]="Compressed",["retrievalTested"]=false,["projectSavedByTool"]=false};
            if(dryRun)return result;
            var export=EngineeringExport.Export(new ArchiveWriter(project,method,Enum.Parse(mode,"Compressed")),path,overwrite:false);
            foreach(var kv in export)result[kv.Key]=kv.Value?.DeepClone();
            result["operationSuccess"]=result["success"]?.DeepClone();
            return result;
        }
        private sealed class ArchiveWriter
        {
            private readonly object project;private readonly MethodInfo method;private readonly object mode;
            internal ArchiveWriter(object p,MethodInfo m,object value){project=p;method=m;mode=value;}
            public void Export(FileInfo path)=>method.Invoke(project,new object[]{path.Directory!,path.Name,mode});
        }
    }
}
