using System.Diagnostics;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TiaMcp.PlcWorker;

internal static class AdapterSourceClosureTests
{
    // Source inventory plus actual MSBuild item evaluation; never substitutes for a typed native build.
    internal static void Run(Action<bool,string> check,[CallerFilePath] string currentFile="")
    {
        var src=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(currentFile)!,"../../../src"));
        var props=XDocument.Load(Path.Combine(src,"Adapters/build/Adapter.Sources.props"));
        var files=props.Descendants("AdapterSource").Select(x=>(string)x.Attribute("Include")!).Select(path=>
        {
            check(path.StartsWith("$(AdapterSourceRoot)/",StringComparison.Ordinal),"Adapter source uses verified source root");
            return Path.GetFullPath(Path.Combine(src,path.Substring("$(AdapterSourceRoot)/".Length)));
        }).ToArray();
            var domains=Path.GetFullPath(Path.Combine(src,"Shared/shared-native"));
        var primitives=Directory.GetFiles(domains,"*.props").OrderBy(path=>path,StringComparer.Ordinal)
            .SelectMany(path=>XDocument.Load(path).Descendants("TiaSharedNativePrimitive").Select(item=>
            {
                var include=(string)item.Attribute("Include")!;
                check(include.StartsWith("$(MSBuildThisFileDirectory)",StringComparison.Ordinal)
                    && include.IndexOfAny(new[]{'*','?'})<0,"Domain primitive is an explicit source path: "+include);
                return Path.GetFullPath(Path.Combine(domains,include.Substring("$(MSBuildThisFileDirectory)".Length)));
            })).ToArray();
        files=files.Concat(primitives).ToArray();
        check(files.Length==files.Distinct(StringComparer.Ordinal).Count(),"Explicit adapter source inventory has no duplicates");
        CheckEvaluatedCompileItems(src,files,primitives,check);
        CheckEnginePrimitiveSelection(src,primitives,check);
        var code=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var path in files)
        {
            var relative=Path.GetRelativePath(src,path).Replace('\\','/');
            check(File.Exists(path),"Adapter source exists: "+relative);
            code[relative]=File.ReadAllText(path);
        }
        foreach(var folder in new[]{"Native","Policy"})
            foreach(var file in Directory.GetFiles(Path.Combine(src,"Adapters",folder),"*.cs",SearchOption.AllDirectories))
                check(code.ContainsKey(Path.GetRelativePath(src,file).Replace('\\','/')),"Every native source/policy has explicit adapter inclusion: "+Path.GetFileName(file));
        foreach(var type in typeof(TiaMcp.PlcFoundation.PlcObjectInfo).Assembly.GetExportedTypes().Where(t=>t.Namespace=="TiaMcp.PlcFoundation"))
            check(!code.Values.Any(text=>Regex.IsMatch(text,@"\b(?:class|enum)\s+"+Regex.Escape(type.Name)+@"\b")),"Moved DTO is not recompiled in adapters: "+type.Name);
        HashSet<string> Methods(IEnumerable<string> texts)=>Regex.Matches(string.Join("\n",texts),@"public\s+(?:[\w<>?\[\],]+\s+)+([A-Za-z]+)\s*\(").Select(m=>m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        // Facade forwarding must not hide an omitted engine implementation.
        var engineCode=code.Where(x=>!x.Key.EndsWith("/OpennessAdapter.cs",StringComparison.Ordinal)
            && !x.Key.Contains("/Native/Studio/",StringComparison.Ordinal)).ToDictionary(x=>x.Key,x=>x.Value);
        var all=Methods(engineCode.Values);
        foreach(var op in WorkerOperations.Names) check(all.Contains(op),"Worker operation exists in actual adapter source inventory: "+op);
        // Simulated source omissions must be caught for every operation-bearing module.
        foreach(var entry in engineCode)
        {
            var owned=Methods(new[]{entry.Value}).Intersect(WorkerOperations.Names,StringComparer.Ordinal).ToArray();
            if(owned.Length==0) continue;
            var without=Methods(engineCode.Where(x=>x.Key!=entry.Key).Select(x=>x.Value));
            check(owned.Any(op=>!without.Contains(op)),"Omitting operation module is detected: "+entry.Key);
        }
        var studioCode=code.Where(x=>x.Key.Contains("/Native/Studio/",StringComparison.Ordinal)
            && !x.Key.EndsWith("/StudioAdapter.cs",StringComparison.Ordinal)).ToDictionary(x=>x.Key,x=>x.Value);
        foreach(var operation in new[]{"Connect","Disconnect","GetState","OpenProject","GetProjectInfo","SaveProject","CloseProject",
            "ListDevices","FindPlcDeviceId","ListBlocks","ExportBlocks","ImportBlocks","ListTagTables","ListTags","CompileDevice",
            "ListWorkspaces","CreateWorkspace","MapProject","GetStatus","Sync"})
            check(Methods(studioCode.Values).Contains(operation),"Studio operation exists in native inventory: "+operation);
        foreach(var entry in studioCode)
        {
            var owned=Methods(new[]{entry.Value});
            var without=Methods(studioCode.Where(x=>x.Key!=entry.Key).Select(x=>x.Value));
            check(owned.Except(without).Any(),"Omitting Studio native module is detected: "+entry.Key);
        }
    }
    private static void CheckEnginePrimitiveSelection(string src,string[] primitives,Action<bool,string> check)
    {
        var viaLogic=new[]{"NativeInputPolicy.cs","HostFailurePolicy.cs","SessionBehavior.cs","NativePathSelection.cs","NativeExportPolicy.cs"};
        foreach(var release in new[]{"20","21"})
        foreach(var enabled in new[]{false,true})
        {
            var start=new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")??"dotnet")
            {
                WorkingDirectory=src,UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true
            };
            foreach(var argument in new[]{"msbuild",Path.Combine(src,"Engine","TiaMcpServer.V"+release+".csproj"),
                "-nologo","-p:TiaPortalLocation="+Path.Combine(src,"unused-evaluation-only"),
                "-p:TiaSharedAdapterPaths="+enabled.ToString().ToLowerInvariant(),"-getItem:Compile","-getProperty:DefineConstants"})
                start.ArgumentList.Add(argument);
            using var process=Process.Start(start)??throw new InvalidOperationException("Could not evaluate engine primitive selection.");
            var output=process.StandardOutput.ReadToEndAsync();
            var error=process.StandardError.ReadToEndAsync();
            if(!process.WaitForExit(60000))
            {
                process.Kill(entireProcessTree:true);
                throw new TimeoutException("Engine primitive selection evaluation timed out.");
            }
            check(process.ExitCode==0,"Engine primitive selection evaluates: V"+release+" shared="+enabled+" "+error.GetAwaiter().GetResult());
            using var result=JsonDocument.Parse(output.GetAwaiter().GetResult());
            var compile=result.RootElement.GetProperty("Items").GetProperty("Compile").EnumerateArray();
            foreach(var primitive in primitives)
                check(compile.Count(item=>string.Equals(Path.GetFullPath(item.GetProperty("FullPath").GetString()!),primitive,StringComparison.OrdinalIgnoreCase))==(viaLogic.Contains(Path.GetFileName(primitive))||enabled?0:1),
                    "Engine owns native primitives locally and pure behavior through Logic: V"+release+" shared="+enabled+" / "+Path.GetFileName(primitive));
            var defines=result.RootElement.GetProperty("Properties").GetProperty("DefineConstants").GetString()!.Split(';');
            check(defines.Contains("TIA_ENGINE_LOCAL_PRIMITIVES")==!enabled,"Engine-local namespace selection matches the source link: V"+release+" shared="+enabled);
        }
    }
    // Evaluation only: no targets, restore, native compiler, worker, or Siemens assembly load.
    private static void CheckEvaluatedCompileItems(string src,string[] files,string[] primitives,Action<bool,string> check)
    {
        var adapters=Path.Combine(src,"Adapters");
        var projects=Directory.GetFiles(adapters,"Adapter.*.csproj",SearchOption.AllDirectories);
        check(projects.Length==8,"Evaluate all eight exact adapter projects");
        var comparer=OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal;
        var expected=files.ToHashSet(comparer);
        foreach(var project in projects.OrderBy(path=>path,StringComparer.Ordinal))
        {
            var start=new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")??"dotnet")
            {
                WorkingDirectory=adapters,UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true
            };
            foreach(var argument in new[]{"msbuild",project,"-nologo","-getItem:Compile,AdapterSource,TiaSharedNativePrimitive,ProjectReference","-getProperty:DefineConstants"}) start.ArgumentList.Add(argument);
            start.Environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"]="false";
            start.Environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"]="false";
            start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"]="1";
            using var process=Process.Start(start)??throw new InvalidOperationException("Could not start MSBuild evaluation.");
            var output=process.StandardOutput.ReadToEndAsync();
            var error=process.StandardError.ReadToEndAsync();
            if(!process.WaitForExit(60000))
            {
                process.Kill(entireProcessTree:true);
                throw new TimeoutException("Adapter MSBuild evaluation timed out: "+project);
            }
            var stdout=output.GetAwaiter().GetResult();
            var stderr=error.GetAwaiter().GetResult();
            check(process.ExitCode==0,"Adapter MSBuild evaluation succeeded: "+project+"\n"+stderr+stdout);
            using var result=JsonDocument.Parse(stdout);
            var items=result.RootElement.GetProperty("Items");
            string[] Paths(string item)=>items.GetProperty(item).EnumerateArray().Select(value=>Path.GetFullPath(value.GetProperty("FullPath").GetString()!)).ToArray();
            var sources=Paths("AdapterSource").Concat(Paths("TiaSharedNativePrimitive")).ToArray();
            check(primitives.ToHashSet(comparer).SetEquals(Paths("TiaSharedNativePrimitive")),"Adapter imports every explicit domain primitive: "+project);
            var compile=Paths("Compile");
            var release=Path.GetFileName(project);
            var contracts=Path.Combine(src,"Adapters.Contracts");
            check(Paths("ProjectReference").Count(path=>comparer.Equals(path,Path.Combine(contracts,"TiaMcp.Adapters.Contracts.csproj")))==1,"Adapter references Contracts exactly once: "+release);
            check(!compile.Any(path=>path.StartsWith(contracts+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)),"Adapter does not source-link contract DTOs: "+release);
            check(result.RootElement.GetProperty("Properties").GetProperty("DefineConstants").GetString()!.Split(';').Contains("TIA_ADAPTER_INTERNAL_VERSIONING"),"Adapter version catalog copies are internal: "+release);
            check(!Paths("ProjectReference").Any(path=>path.Contains("TiaOpenness.Core",StringComparison.Ordinal)
                || path.Contains("TiaOpenness.Contracts",StringComparison.Ordinal)),"Studio native code does not depend on legacy host/JSON contracts: "+release);
            var key=release.Substring("Adapter.".Length).Replace(".csproj", "", StringComparison.Ordinal);
            var defines=result.RootElement.GetProperty("Properties").GetProperty("DefineConstants").GetString()!.Split(';');
            check(defines.Contains("STUDIO_VCI")==!new[]{"14sp1","15.1"}.Contains(key),"Studio VCI release support: "+release);
            check(defines.Contains("STUDIO_VCI_INITIAL")==new[]{"16","17"}.Contains(key),"Studio initial VCI support: "+release);
            check(defines.Contains("STUDIO_VCI_MODERN")==new[]{"20","21"}.Contains(key),"Studio modern VCI support: "+release);
            check(expected.SetEquals(sources),"Evaluated adapter inventory matches explicit allowlist: "+release);
            check(sources.Length==sources.Distinct(comparer).Count(),"Evaluated adapter inventory has no duplicates: "+release);
            foreach(var source in expected)
                check(compile.Count(path=>comparer.Equals(path,source))==1,"Allowlisted source is compiled exactly once: "+release+" / "+Path.GetFileName(source));
            foreach(var name in new[]{"PlcDeviceAdd.cs","PlcDeviceAddPolicy.cs"})
                check(compile.Any(path=>comparer.Equals(path,Path.Combine(src,"Adapters","Native","Hardware",name))),"Device-add source survives MSBuild item evaluation: "+release+" / "+name);
        }
        var studio=Path.GetFullPath(Path.Combine(src,"Studio/Openness"));
        var legacyProps=XDocument.Load(Path.Combine(studio,"Studio.Common.props"));
        var legacySources=legacyProps.Descendants("Compile").Select(x=>(string)x.Attribute("Include")!).ToArray();
        foreach(var name in new[]{"EngineeringExtensions","HmiNavigator","OpennessSession","OpennessVersionControl","PlcNavigator"})
        {
            check(!File.Exists(Path.Combine(studio,name+".cs")),"Studio native source has one owner: "+name);
            check(legacySources.Count(path=>path.EndsWith("/Adapters/Native/Studio/"+name+".cs",StringComparison.Ordinal))==1,
                "Legacy Studio links the moved source exactly once: "+name);
        }
        check(legacySources.Contains("../OpennessSessionFactory.cs"),"Legacy Studio factory remains in the original assembly");
        check(legacySources.Count(path=>path.EndsWith("/Adapters/Native/Vci/VersionControlPrimitives.cs",StringComparison.Ordinal))==1,
            "Legacy Studio links the shared VCI primitives exactly once");
        check(!legacySources.Any(path=>path.EndsWith("/StudioAdapter.cs",StringComparison.Ordinal)),"Legacy Studio does not switch to the woven profile yet");
    }
}
