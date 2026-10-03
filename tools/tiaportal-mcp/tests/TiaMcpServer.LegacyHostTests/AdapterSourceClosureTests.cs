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
        var src=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(currentFile)!,"../../src"));
        var props=XDocument.Load(Path.Combine(src,"TiaMcp.Adapters/build/Adapter.Sources.props"));
        var files=props.Descendants("AdapterSource").Select(x=>(string)x.Attribute("Include")!).ToArray();
        check(files.Length==files.Distinct(StringComparer.Ordinal).Count(),"Explicit adapter source inventory has no duplicates");
        CheckEvaluatedCompileItems(src,files,check);
        var code=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var path in files)
        {
            check(path.StartsWith("$(AdapterSourceRoot)/",StringComparison.Ordinal),"Adapter source uses verified source root");
            var relative=path.Substring("$(AdapterSourceRoot)/".Length);
            var full=Path.Combine(src,relative);
            check(File.Exists(full),"Adapter source exists: "+relative);
            code[relative]=File.ReadAllText(full);
        }
        foreach(var folder in new[]{"Native","Policy"})
            foreach(var file in Directory.GetFiles(Path.Combine(src,"TiaMcp.Adapters",folder),"*.cs",SearchOption.AllDirectories))
                check(code.ContainsKey(Path.GetRelativePath(src,file).Replace('\\','/')),"Every native source/policy has explicit adapter inclusion: "+Path.GetFileName(file));
        foreach(var type in typeof(TiaMcp.PlcFoundation.PlcObjectInfo).Assembly.GetExportedTypes().Where(t=>t.Namespace=="TiaMcp.PlcFoundation"))
            check(!code.Values.Any(text=>Regex.IsMatch(text,@"\b(?:class|enum)\s+"+Regex.Escape(type.Name)+@"\b")),"Moved DTO is not recompiled in adapters: "+type.Name);
        HashSet<string> Methods(IEnumerable<string> texts)=>Regex.Matches(string.Join("\n",texts),@"public\s+(?:[\w<>?\[\],]+\s+)+([A-Za-z]+)\s*\(").Select(m=>m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        // Facade forwarding must not hide an omitted engine implementation.
        var engineCode=code.Where(x=>!x.Key.EndsWith("/OpennessAdapter.cs",StringComparison.Ordinal)).ToDictionary(x=>x.Key,x=>x.Value);
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
    }
    // Evaluation only: no targets, restore, native compiler, worker, or Siemens assembly load.
    private static void CheckEvaluatedCompileItems(string src,string[] files,Action<bool,string> check)
    {
        var adapters=Path.Combine(src,"TiaMcp.Adapters");
        var projects=Directory.GetFiles(adapters,"Adapter.*.csproj",SearchOption.AllDirectories);
        check(projects.Length==8,"Evaluate all eight exact adapter projects");
        var comparer=OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal;
        var expected=files.Select(path=>Path.GetFullPath(Path.Combine(src,path.Substring("$(AdapterSourceRoot)/".Length)))).ToHashSet(comparer);
        foreach(var project in projects.OrderBy(path=>path,StringComparer.Ordinal))
        {
            var start=new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")??"dotnet")
            {
                WorkingDirectory=adapters,UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true
            };
            foreach(var argument in new[]{"msbuild",project,"-nologo","-getItem:Compile,AdapterSource,ProjectReference","-getProperty:DefineConstants"}) start.ArgumentList.Add(argument);
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
            var sources=Paths("AdapterSource");
            var compile=Paths("Compile");
            var release=Path.GetFileName(project);
            var contracts=Path.Combine(src,"TiaMcp.Adapters.Contracts");
            check(Paths("ProjectReference").Count(path=>comparer.Equals(path,Path.Combine(contracts,"TiaMcp.Adapters.Contracts.csproj")))==1,"Adapter references Contracts exactly once: "+release);
            check(!compile.Any(path=>path.StartsWith(contracts+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)),"Adapter does not source-link contract DTOs: "+release);
            check(result.RootElement.GetProperty("Properties").GetProperty("DefineConstants").GetString()!.Split(';').Contains("TIA_ADAPTER_INTERNAL_VERSIONING"),"Adapter version catalog copies are internal: "+release);
            check(expected.SetEquals(sources),"Evaluated adapter inventory matches explicit allowlist: "+release);
            check(sources.Length==sources.Distinct(comparer).Count(),"Evaluated adapter inventory has no duplicates: "+release);
            foreach(var source in expected)
                check(compile.Count(path=>comparer.Equals(path,source))==1,"Allowlisted source is compiled exactly once: "+release+" / "+Path.GetFileName(source));
            foreach(var name in new[]{"PlcDeviceAdd.cs","PlcDeviceAddPolicy.cs"})
                check(compile.Any(path=>comparer.Equals(path,Path.Combine(src,"TiaMcp.Adapters","Native","Hardware",name))),"Device-add source survives MSBuild item evaluation: "+release+" / "+name);
        }
    }
}
