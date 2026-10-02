using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

internal static class ApiMetadataTests
{
    // PEReader reads bytes only. No Assembly.Load/LoadFrom or native code execution.
    internal static void Run(string apiRoot,string adapterRoot,Action<bool,string> check)
    {
        foreach(var key in new[]{"14sp1","15.1","16","17","18","19","20","21"})
        {
            string directory=key=="14sp1" ? "TIA_V14SP1_PublicAPI/V14 SP1" : $"TIA_V{key}_PublicAPI/V{key}"+(key=="21"?"/net48":"");
            var core=Path.Combine(apiRoot,directory,key=="21"?"Siemens.Engineering.Base.dll":"Siemens.Engineering.dll");
            CheckMembers(core,"Siemens.Engineering.Online.OnlineProvider",new[]{"State"},true,check);
            if(key!="14sp1") CheckMembers(core,"Siemens.Engineering.Online.RHOnlineProvider",new[]{"PrimaryState","BackupState"},true,check);
            var identity=AssemblyName.GetAssemblyName(core);
            check(identity.Version!.ToString()==(key=="14sp1"?"14.0.1.0":key=="15.1"?"15.1.0.0":key+".0.0.0"),"Exact core version "+key);
            check(Convert.ToHexString(identity.GetPublicKeyToken()!).ToLowerInvariant()==(key=="21"?"29bfe5fdf4ba5d3b":"d29ec89bac048f84"),"Exact signer "+key);
            CheckMembers(core,"Siemens.Engineering.IEngineeringObject",new[]{"GetAttributeInfos","GetAttribute"},false,check);
            CheckMembers(core,"Siemens.Engineering.Compiler.ICompilable",new[]{"Compile"},false,check);
            CheckMembers(core,"Siemens.Engineering.Compiler.CompilerResult",new[]{"State","ErrorCount","WarningCount","Messages"},true,check);
            CheckMembers(core,"Siemens.Engineering.Compiler.CompilerResultMessage",new[]{"State","Description","Path","DateTime","Messages"},true,check);
            if(key is "14sp1" or "15.1" or "16")
            {
                foreach(var dll in Directory.GetFiles(Path.Combine(apiRoot,directory),"*.dll"))
                {
                    using var input=File.OpenRead(dll); using var metadataPe=new PEReader(input);
                    if(!metadataPe.HasMetadata) continue;
                    var reader=metadataPe.GetMetadataReader();
                    check(!reader.TypeDefinitions.Select(reader.GetTypeDefinition).Any(t=>reader.GetString(t.Namespace)=="Siemens.Engineering.Safety" && reader.GetString(t.Name)=="SafetyAdministration"),"Password gate justified by supplied native DLL metadata "+key+"/"+Path.GetFileName(dll));
                    check(!reader.TypeDefinitions.Select(reader.GetTypeDefinition).Any(t=>reader.GetString(t.Namespace)=="Siemens.Engineering.Multiuser" && reader.GetString(t.Name)=="LocalSession"),"Local-session gate justified by supplied native DLL metadata "+key+"/"+Path.GetFileName(dll));
                }
            }
            else
            {
                var safety=key=="21"?Path.Combine(apiRoot,directory,"Siemens.Engineering.Safety.dll"):core;
                CheckMembers(safety,"Siemens.Engineering.Safety.SafetyAdministration",new[]{"LoginToSafetyOfflineProgram","LogoffFromSafetyOfflineProgram"},false,check);
                CheckMembers(safety,"Siemens.Engineering.Safety.SafetyAdministration",new[]{"IsLoggedOnToSafetyOfflineProgram"},true,check);
                if(key=="21") check(AssemblyName.GetAssemblyName(safety).FullName=="Siemens.Engineering.Safety, Version=21.0.0.0, Culture=neutral, PublicKeyToken=29bfe5fdf4ba5d3b","V21 split safety identity");
                CheckMembers(core,"Siemens.Engineering.Multiuser.LocalSession",new[]{"Save","Close"},false,check);
                CheckMembers(core,"Siemens.Engineering.Multiuser.LocalSessionComposition",new[]{"Open"},false,check);
                CheckMembers(core,"Siemens.Engineering.Multiuser.LocalSession",new[]{"Project"},true,check);
            }
            CheckMembers(core,"Siemens.Engineering.Project",new[]{"Save","Close"},false,check);
            CheckMembers(core,"Siemens.Engineering.ProjectComposition",new[]{"Open","Create"},false,check);
            var plc=key=="21"?Path.Combine(apiRoot,directory,"Siemens.Engineering.Step7.dll"):core;
            foreach(var nativeType in new[]{"PlcTagTable","PlcTag","PlcUserConstant","PlcConstant"})
            {
                var names=nativeType=="PlcTagTable" ? new[]{"Tags","UserConstants","SystemConstants"} : nativeType=="PlcTag" ? new[]{"Name","DataTypeName","LogicalAddress"} : new[]{"Name","DataTypeName","Value"};
                CheckMembers(plc,"Siemens.Engineering.SW.Tags."+nativeType,names,true,check);
                var xml=System.Xml.Linq.XDocument.Load(Path.ChangeExtension(plc,".xml"));
                foreach(var property in names) check(xml.Descendants("member").Any(m=>(string?)m.Attribute("name")=="P:Siemens.Engineering.SW.Tags."+nativeType+"."+property),key+" actual declaration XML "+nativeType+"."+property);
            }
            using(var declarationStream=File.OpenRead(plc))
            using(var declarationPe=new PEReader(declarationStream))
            {
                var declarationMd=declarationPe.GetMetadataReader();
                var system=declarationMd.TypeDefinitions.Select(declarationMd.GetTypeDefinition).Single(t=>declarationMd.GetString(t.Namespace)=="Siemens.Engineering.SW.Tags" && declarationMd.GetString(t.Name)=="PlcSystemConstant");
                check(system.BaseType.Kind==HandleKind.TypeDefinition && declarationMd.GetString(declarationMd.GetTypeDefinition((TypeDefinitionHandle)system.BaseType).Name)=="PlcConstant",key+" actual system constant inherits verified PlcConstant properties");
            }
            CheckMembers(plc,"Siemens.Engineering.SW.PlcSoftware",new[]{"ExternalSourceGroup"},true,check);
            CheckMembers(plc,"Siemens.Engineering.SW.ExternalSources.PlcExternalSourceGroup",new[]{"ExternalSources"},true,check);
            CheckMembers(plc,"Siemens.Engineering.SW.ExternalSources.PlcExternalSource",new[]{"Name"},true,check);
            CheckMembers(plc,"Siemens.Engineering.SW.Blocks.PlcBlock",new[]{"Name","ProgrammingLanguage","MemoryLayout","IsConsistent","HeaderName","ModifiedDate","IsKnowHowProtected"},true,check);
            CheckMembers(plc,"Siemens.Engineering.SW.Types.PlcType",new[]{"Name","IsConsistent","ModifiedDate","IsKnowHowProtected"},true,check);
            foreach(var apiType in new[]{"Siemens.Engineering.SW.Blocks.PlcBlock","Siemens.Engineering.SW.Types.PlcType","Siemens.Engineering.SW.Tags.PlcTagTable"}) CheckMembers(plc,apiType,new[]{"Export"},false,check);
            foreach(var apiType in new[]{"Siemens.Engineering.SW.Blocks.PlcBlockComposition","Siemens.Engineering.SW.Types.PlcTypeComposition","Siemens.Engineering.SW.Tags.PlcTagTableComposition"}) CheckMembers(plc,apiType,new[]{"Import"},false,check);
            var framework=key is "14sp1" or "15.1" or "16" ? "net461":"net48";
            var adapterDirectory=key=="14sp1"?"V14Sp1":key=="15.1"?"V15_1":"V"+key;
            var facade=Path.Combine(adapterRoot,adapterDirectory,"bin",key,"Release",framework,$"TiaMcp.Adapter.{key}.dll");
            var workerDirectory=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(adapterRoot))!,"TiaMcpServer.PlcWorker","bin",key,"Release",framework);
            using(var workerStream=File.OpenRead(Path.Combine(workerDirectory,$"TiaMcp.PlcWorker.{key}.exe")))
            using(var workerPe=new PEReader(workerStream))
            {
                var workerMd=workerPe.GetMetadataReader();
                var refs=workerMd.AssemblyReferences.Select(workerMd.GetAssemblyReference).Select(a=>workerMd.GetString(a.Name)).ToArray();
                check(refs.Count(n=>n.StartsWith("TiaMcp.Adapter.",StringComparison.Ordinal))==1 && refs.Contains("TiaMcp.Adapter."+key),"Worker PE binds exactly selected adapter "+key);
                check(!refs.Any(n=>n.StartsWith("TiaMcp.PlcFoundation",StringComparison.Ordinal)),"Worker PE has no former Foundation reference "+key);
            }
            check(!Directory.GetFiles(workerDirectory,"Siemens.Engineering*.dll").Any(),"Worker output does not redistribute Siemens binaries "+key);
            using var stream=File.OpenRead(facade);
            using var pe=new PEReader(stream);
            var md=pe.GetMetadataReader();
            check(md.GetString(md.GetAssemblyDefinition().Name)=="TiaMcp.Adapter."+key,"Compiled adapter identity "+key);
            foreach(var nativeRef in md.AssemblyReferences.Select(md.GetAssemblyReference).Where(a=>md.GetString(a.Name).StartsWith("Siemens.Engineering",StringComparison.Ordinal)))
            {
                var expected=AssemblyName.GetAssemblyName(Path.Combine(apiRoot,directory,md.GetString(nativeRef.Name)+".dll"));
                check(nativeRef.Version==expected.Version && md.GetBlobBytes(nativeRef.PublicKeyOrToken).SequenceEqual(expected.GetPublicKeyToken()!),"Compiled adapter exact Siemens reference "+key+"/"+expected.Name);
            }
            var type=md.TypeDefinitions.Select(md.GetTypeDefinition).Single(t=>md.GetString(t.Name)=="PlcFoundationEngine");
            foreach(var name in new[]{"ListTags","ListUserConstants","ListSystemConstants"})
            {
                var method=type.GetMethods().Select(md.GetMethodDefinition).Single(m=>md.GetString(m.Name)==name);
                var names=method.GetParameters().Select(md.GetParameter).Where(p=>p.SequenceNumber>0).Select(p=>md.GetString(p.Name)).ToArray();
                check(names.SequenceEqual(new[]{"plc","table"}),key+" compiled declaration reader parameter contract "+name);
            }
            foreach(var name in new[]{"ReadBlocks","ReadTypes","ReadBlockHierarchy","ReadTagTableNames","ReadExternalSourceNames"})
            {
                var method=type.GetMethods().Select(md.GetMethodDefinition).Single(m=>md.GetString(m.Name)==name);
                var parameters=method.GetParameters().Select(md.GetParameter).Where(p=>p.SequenceNumber>0).ToArray();
                check(md.GetString(parameters[0].Name)=="softwarePath","Real compiled facade parameter "+key+"/"+name);
                bool filtered=name is "ReadBlocks" or "ReadTypes";
                check(parameters.Length==(filtered?2:1),"Real compiled facade arity "+key+"/"+name);
                if(filtered) check(md.GetString(parameters[1].Name)=="regexName" && (parameters[1].Attributes & ParameterAttributes.Optional)!=0,"Real optional regex parameter "+key);
            }
            foreach(var pair in new Dictionary<string,string[]> {
                ["CompileSoftware"]=new[]{"softwarePath","password","dryRun"},
                ["ExportBlock"]=new[]{"softwarePath","blockPath","exportPath","preservePath","dryRun"},
                ["ExportType"]=new[]{"softwarePath","exportPath","typePath","preservePath","dryRun"},
                ["ExportTagTable"]=new[]{"softwarePath","tagTableName","exportPath","dryRun"},
                ["ImportBlocks"]=new[]{"softwarePath","groupPath","importPath","overwrite","dryRun"},
                ["ImportTypes"]=new[]{"softwarePath","groupPath","importPath","overwrite","dryRun"},
                ["ImportTagTables"]=new[]{"softwarePath","folderPath","importPath","overwrite","dryRun"}})
            {
                var method=type.GetMethods().Select(md.GetMethodDefinition).Single(m=>md.GetString(m.Name)==pair.Key);
                var parameters=method.GetParameters().Select(md.GetParameter).Where(p=>p.SequenceNumber>0).ToArray();
                check(parameters.Select(p=>md.GetString(p.Name)).SequenceEqual(pair.Value),"Real exchange facade parameter contract "+key+"/"+pair.Key);
                check((parameters.Last().Attributes & ParameterAttributes.Optional)!=0,"Real optional preview parameter "+key+"/"+pair.Key);
            }
            foreach(var pair in new Dictionary<string,string[]> {
                ["ReadBlockInfo"]=new[]{"softwarePath","blockPath"},["ReadTypeInfo"]=new[]{"softwarePath","typePath"},
                ["Attach"]=new[]{"processId"},["ListProjects"]=Array.Empty<string>(),
                ["BindProject"]=new[]{"projectName","expectedProjectFile"},["OpenProject"]=new[]{"path","dryRun"},
                ["CreateProject"]=new[]{"directoryPath","projectName","dryRun"},["SaveProject"]=new[]{"dryRun"},["CloseProject"]=new[]{"dryRun"},["ReadProjectTree"]=Array.Empty<string>()})
            {
                var method=type.GetMethods().Select(md.GetMethodDefinition).Single(m=>md.GetString(m.Name)==pair.Key);
                var names=method.GetParameters().Select(md.GetParameter).Where(p=>p.SequenceNumber>0).Select(p=>md.GetString(p.Name));
                check(names.SequenceEqual(pair.Value),"Real lifecycle parameter contract "+key+"/"+pair.Key);
            }
        }
    }
    private static void CheckMembers(string path,string fullName,string[] members,bool properties,Action<bool,string> check)
    {
        using var stream=File.OpenRead(path);
        using var pe=new PEReader(stream);
        var md=pe.GetMetadataReader();
        var type=md.TypeDefinitions.Select(md.GetTypeDefinition).Single(t=>md.GetString(t.Namespace)+"."+md.GetString(t.Name)==fullName);
        var names=properties ? type.GetProperties().Select(h=>md.GetString(md.GetPropertyDefinition(h).Name)).ToHashSet() : type.GetMethods().Select(h=>md.GetString(md.GetMethodDefinition(h).Name)).ToHashSet();
        foreach(var name in members) check(names.Contains(name),"Real API member "+fullName+"."+name);
    }
}
