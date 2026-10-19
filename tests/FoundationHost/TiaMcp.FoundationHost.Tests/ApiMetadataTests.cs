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
            CheckHardwareBranches(Path.Combine(apiRoot,directory),key,check);
            CheckSchemaFragments(Path.Combine(apiRoot,directory),key,check);
            CheckDownloadPrompts(Path.Combine(apiRoot,directory),key,check);
            CheckMembers(core,"Siemens.Engineering.Online.OnlineProvider",new[]{"State"},true,check);
            if(key!="14sp1") CheckMembers(core,"Siemens.Engineering.Online.RHOnlineProvider",new[]{"PrimaryState","BackupState"},true,check);
            var identity=AssemblyName.GetAssemblyName(core);
            using(var exceptionStream=File.OpenRead(core))
            using(var exceptionPe=new PEReader(exceptionStream))
            {
                var exceptions=exceptionPe.GetMetadataReader();
                bool HasEngineeringBase(string name)
                {
                    var type=exceptions.TypeDefinitions.Select(exceptions.GetTypeDefinition).Single(t=>exceptions.GetString(t.Namespace)=="Siemens.Engineering" && exceptions.GetString(t.Name)==name);
                    while(type.BaseType.Kind==HandleKind.TypeDefinition)
                    {
                        type=exceptions.GetTypeDefinition((TypeDefinitionHandle)type.BaseType);
                        if(exceptions.GetString(type.Name)=="EngineeringException" && exceptions.GetString(type.Namespace)=="Siemens.Engineering") return true;
                    }
                    return false;
                }
                check(HasEngineeringBase("EngineeringTargetInvocationException"),key+" native invocation diagnostics are recoverable EngineeringException");
                check(!HasEngineeringBase("NonRecoverableException"),key+" fatal native exception is outside the recoverable catch");
            }
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
            CheckMembers(plc,"Siemens.Engineering.SW.ExternalSources.PlcExternalSourceComposition",new[]{"CreateFromFile","Find"},false,check);
            using(var sourceStream=File.OpenRead(plc))
            using(var sourcePe=new PEReader(sourceStream))
            {
                var sourceMd=sourcePe.GetMetadataReader();
                var sourceType=sourceMd.TypeDefinitions.Select(sourceMd.GetTypeDefinition).Single(t=>sourceMd.GetString(t.Namespace)=="Siemens.Engineering.SW.ExternalSources" && sourceMd.GetString(t.Name)=="PlcExternalSource");
                var generation=sourceType.GetMethods().Select(sourceMd.GetMethodDefinition).Where(m=>sourceMd.GetString(m.Name)=="GenerateBlocksFromSource").ToArray();
                var parameterless=generation.Single(m=>m.GetParameters().Select(sourceMd.GetParameter).Count(p=>p.SequenceNumber>0)==0);
                check(sourceMd.GetBlobBytes(parameterless.Signature)[2]==0x01,key+" native parameterless generation returns void");
                var withOption=generation.Where(m=>m.GetParameters().Select(sourceMd.GetParameter).Count(p=>p.SequenceNumber>0)==1).ToArray();
                check(withOption.Length==(key=="14sp1"?0:1),key+" exact native generation option availability");
                var sourceXml=System.Xml.Linq.XDocument.Load(Path.ChangeExtension(plc,".xml"));
                var create=sourceXml.Descendants("member").Single(m=>(string?)m.Attribute("name")=="M:Siemens.Engineering.SW.ExternalSources.PlcExternalSourceComposition.CreateFromFile(System.String,System.String)");
                check(create.Elements("param").Select(p=>(string?)p.Attribute("name")).SequenceEqual(new[]{"name","path"}),key+" official CreateFromFile argument order");
                if(key!="14sp1")
                {
                    var result=sourceXml.Descendants("member").Single(m=>(string?)m.Attribute("name")=="M:Siemens.Engineering.SW.ExternalSources.PlcExternalSource.GenerateBlocksFromSource(Siemens.Engineering.SW.ExternalSources.GenerateBlockOption)");
                    check(result.Element("returns")!.Value.Contains("IList<Siemens.Engineering.IEngineeringObject>"),key+" official generation returns the actual engineering objects");
                }
            }
            CheckMembers(plc,"Siemens.Engineering.SW.Blocks.PlcBlock",new[]{"Name","ProgrammingLanguage","MemoryLayout","IsConsistent","HeaderName","ModifiedDate","IsKnowHowProtected"},true,check);
            CheckMembers(plc,"Siemens.Engineering.SW.Types.PlcType",new[]{"Name","IsConsistent","ModifiedDate","IsKnowHowProtected"},true,check);
            foreach(var apiType in new[]{"Siemens.Engineering.SW.Blocks.PlcBlock","Siemens.Engineering.SW.Types.PlcType","Siemens.Engineering.SW.Tags.PlcTagTable"}) CheckMembers(plc,apiType,new[]{"Export"},false,check);
            foreach(var apiType in new[]{"Siemens.Engineering.SW.Blocks.PlcBlockComposition","Siemens.Engineering.SW.Types.PlcTypeComposition","Siemens.Engineering.SW.Tags.PlcTagTableComposition"}) CheckMembers(plc,apiType,new[]{"Import"},false,check);
            var framework="net48";
            var adapterDirectory=key=="14sp1"?"V14Sp1":key=="15.1"?"V15_1":"V"+key;
            var facade=Path.Combine(adapterRoot,adapterDirectory,"bin",key,"Release",framework,$"TiaMcp.Adapter.{key}.dll");
            var workerDirectory=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(adapterRoot))!,"PlcWorker","bin",key,"Release",framework);
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
            var references=md.MemberReferences.Select(md.GetMemberReference).Select(m=>md.GetString(m.Name)).ToHashSet();
            check(references.Contains("get_SystemBlockGroups"),key+" system block groups have a compiled native path");
            check(references.Contains("get_SystemTypeGroups")== (key!="14sp1"),key+" system type groups are compiled only where the API exists");
            check(md.GetString(md.GetAssemblyDefinition().Name)=="TiaMcp.Adapter."+key,"Compiled adapter identity "+key);
            foreach(var name in new[]{"TiaVersionCatalog","TiaVersionDescriptor"})
            {
                var catalogType=md.TypeDefinitions.Select(md.GetTypeDefinition).Single(t=>md.GetString(t.Namespace)=="TiaMcp.Versioning" && md.GetString(t.Name)==name);
                check((catalogType.Attributes & TypeAttributes.VisibilityMask)==TypeAttributes.NotPublic,"Adapter versioning copy is internal: "+key+"/"+name);
            }
            check(md.AssemblyReferences.Select(md.GetAssemblyReference).Any(a=>md.GetString(a.Name)=="TiaMcp.Adapters.Contracts"),"Adapter PE references shared contracts "+key);
            check(File.Exists(Path.Combine(workerDirectory,"TiaMcp.Adapters.Contracts.dll")),"Worker deployment includes shared contracts "+key);
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
                ["ImportPlcExternalSource"]=new[]{"softwarePath","groupPath","filePath","dryRun","expectedPlanHash","confirm","expectedProjectFile"},
                ["GenerateBlocksFromExternalSource"]=new[]{"softwarePath","externalSourceName","dryRun","expectedPlanHash","confirm","expectedProjectFile"},
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
    private static void CheckSchemaFragments(string apiDirectory,string key,Action<bool,string> check)
    {
        var schemas=Path.Combine(key=="21"?Path.GetDirectoryName(apiDirectory)!:apiDirectory,"Schemas");
        var scratch=Path.Combine(Path.GetTempPath(),"p8-official-fragments-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var source=Path.Combine(scratch,"fragment.xml");
            foreach(var kind in new[]{("SW.InterfaceSections", "Interface", "Sections", ""),
                ("SW.PlcBlocks.LADFBD", "FlgNet", "FlgNet", "<Parts/>"),
                ("SW.PlcBlocks.SCL", "StructuredText", "StructuredText", ""),
                ("SW.PlcBlocks.STL", "STL", "StatementList", ""),
                ("SW.PlcBlocks.Graph", "Graph", "Graph", "")})
            {
                var files=Directory.GetFiles(schemas,kind.Item1+"*.xsd");
                if(files.Length==0) files=new[]{Path.Combine(schemas,kind.Item1+".xsd")};
                foreach(var schema in files)
                {
                    var match=System.Text.RegularExpressions.Regex.Match(Path.GetFileName(schema),@"_v([0-9]+)\.xsd$");
                    var version=match.Success?match.Groups[1].Value:"1";
                    var ns="http://www.siemens.com/automation/Openness/SW/"+(kind.Item2=="Interface"?"Interface/":"NetworkSource/"+kind.Item2+"/")+"v"+version;
                    File.WriteAllText(source,"<Document><"+kind.Item3+" xmlns='"+ns+"'>"+kind.Item4+"</"+kind.Item3+"></Document>");
                    var result=global::TiaMcpServer.ModelContextProtocol.PlcSchemaValidation.Validate(source,schemas);
                    bool supported=key!="14sp1" || kind.Item2=="Interface";
                    check(result["validatedFragments"]!.AsArray().Count==(supported?1:0),key+" exact official fragment match "+Path.GetFileName(schema));
                    check(result["skippedFragments"]!.AsArray().Count==(supported?0:1),key+" missing/unsuffixed schemas remain explicit skips "+kind.Item3);
                    check((bool)result["fragmentSchemasPassed"]! == (supported && kind.Item2!="Graph"),key+" official XSD validates minimal fragments and rejects incomplete Graph "+kind.Item3);
                    File.WriteAllText(source,"<Document><"+kind.Item3+" xmlns='"+ns+"'><Unexpected/></"+kind.Item3+"></Document>");
                    var invalid=global::TiaMcpServer.ModelContextProtocol.PlcSchemaValidation.Validate(source,schemas);
                    check(!(bool)invalid["fragmentSchemasPassed"]! && (!supported || (int)invalid["errorCount"]!>0),key+" official XSD rejects malformed "+kind.Item3);
                }
            }
        }
        finally { Directory.Delete(scratch,true); }
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

    // The download prompt policy answers with the release's own enum names (read from the SDK metadata of every release):
    // V17-V21 DataBlockReinitializationSelections is exactly NoAction/StopPlcAndReinitialize, each opt-in prompt offers the
    // selection it is allowed or refused with, and every CompareResultState name is a state the online compare classifies.
    private static void CheckDownloadPrompts(string directory,string key,Action<bool,string> check)
    {
        var enums=new Dictionary<string,string[]>(StringComparer.Ordinal);
        string[]? compareStates=null;
        foreach(var dll in Directory.GetFiles(directory,"Siemens.Engineering*.dll"))
        {
            using var stream=File.OpenRead(dll);
            using var pe=new PEReader(stream);
            if(!pe.HasMetadata) continue;
            var md=pe.GetMetadataReader();
            foreach(var type in md.TypeDefinitions.Select(md.GetTypeDefinition))
            {
                string ns=md.GetString(type.Namespace), name=md.GetString(type.Name);
                string[] Values()=>type.GetFields().Select(md.GetFieldDefinition).Where(f=>(f.Attributes&FieldAttributes.Static)!=0).Select(f=>md.GetString(f.Name)).ToArray();
                if(ns=="Siemens.Engineering.Compare" && name=="CompareResultState") compareStates=Values();
                if((ns.EndsWith(".Download.Configurations",StringComparison.Ordinal) || ns.EndsWith(".Upload.Configurations",StringComparison.Ordinal)) && name.EndsWith("Selections",StringComparison.Ordinal))
                    enums[name]=Values();
            }
        }
        bool reinitialization=key is "17" or "18" or "19" or "20" or "21";
        check(enums.ContainsKey("DataBlockReinitializationSelections")==reinitialization,key+" DataBlockReinitialization prompt exists exactly on V17-V21");
        if(reinitialization)
        {
            var values=enums["DataBlockReinitializationSelections"];
            check(values.OrderBy(v=>v,StringComparer.Ordinal).SequenceEqual(new[]{"NoAction","StopPlcAndReinitialize"}),key+" DataBlockReinitializationSelections is exactly NoAction/StopPlcAndReinitialize");
            var keep=new global::TiaMcpServer.Siemens.DownloadPromptPolicy().Decide("DataBlockReinitialization",values,false,false);
            var reset=new global::TiaMcpServer.Siemens.DownloadPromptPolicy{KeepActualValues=false}.Decide("DataBlockReinitialization",values,false,false);
            check(keep.Value=="NoAction" && reset.Value=="StopPlcAndReinitialize",key+" DataBlockReinitialization is answered with this release's own enum names");
        }
        check(enums.ContainsKey("DataBlockReinitializationOrKeepActualValuesSelections")==(key=="21"),key+" DataBlockReinitializationOrKeepActualValues prompt exists exactly on V21");
        if(enums.TryGetValue("DataBlockReinitializationOrKeepActualValuesSelections",out var orKeep))
        {
            var reset=new global::TiaMcpServer.Siemens.DownloadPromptPolicy{KeepActualValues=false}.Decide("DataBlockReinitializationOrKeepActualValues",orKeep,false,false);
            var noStop=new global::TiaMcpServer.Siemens.DownloadPromptPolicy{KeepActualValues=false,StopBeforeDownload=false}.Decide("DataBlockReinitializationOrKeepActualValues",orKeep,false,false);
            var keep=new global::TiaMcpServer.Siemens.DownloadPromptPolicy().Decide("DataBlockReinitializationOrKeepActualValues",orKeep,false,false);
            check(keep.Value=="KeepActualValues" && reset.Value=="StopPlcAndReinitialize" && noStop.Value=="NoAction",key+" DataBlockReinitializationOrKeepActualValues follows the STOP rule with this release's own enum names");
        }
        foreach(var prompt in global::TiaMcpServer.Siemens.DownloadPromptPolicy.OptInPrompts)
        {
            if(!enums.TryGetValue(prompt+"Selections",out var values))
            {
                check(key=="14sp1" || prompt=="SafetyProgram" && key is "15.1" or "16" or "17" or "18" || prompt=="ActiveTestCanPreventDownload" && key=="15.1",key+" "+prompt+" is absent only where the SDK lacks it");
                continue;
            }
            var allowed=new global::TiaMcpServer.Siemens.DownloadPromptPolicy{AllowSafetyProgramDownload=true,AllowHardwareDifferences=true,AllowActiveTestCancel=true,AllowExpandedDownload=true,AllowIdentificationDataLoad=true}.Decide(prompt,values,false,false);
            var refused=new global::TiaMcpServer.Siemens.DownloadPromptPolicy().Decide(prompt,values,false,false);
            check(allowed.Kind==global::TiaMcpServer.Siemens.DownloadPromptPolicy.AnswerKind.Selection && refused.Kind==global::TiaMcpServer.Siemens.DownloadPromptPolicy.AnswerKind.Refused
                && (prompt=="SafetyProgram" ? refused.Value==null : refused.Value!=null),key+" "+prompt+" allow and refuse selections exist in this release's enum");
        }
        var known=new[]{"ObjectsIdentical","FolderContentsIdentical","CompareIrrelevant","ObjectsDifferent","LeftMissing","RightMissing","FolderContentsDifferent","FolderContainsDifferencesOwnStateDifferent","FolderContentEqualOwnStateDifferent"};
        check(compareStates!=null && compareStates.All(known.Contains),key+" every CompareResultState name is classified by the online compare");
        check(compareStates!=null && compareStates.Contains("CompareIrrelevant")==(key!="14sp1") && (key=="14sp1" || !global::TiaMcp.Adapters.Contracts.OnlineTransitionPolicy.IsCompareDifference("CompareIrrelevant")),key+" CompareIrrelevant is no difference where the release has it");
    }

    private static void CheckHardwareBranches(string directory,string key,Action<bool,string> check)
    {
        bool directAssignment=false,serviceAssignment=false,attributeDelegate=false;
        var types=new HashSet<string>();
        foreach(var dll in Directory.GetFiles(directory,"Siemens.Engineering*.dll"))
        {
            using var stream=File.OpenRead(dll); using var pe=new PEReader(stream);
            if(!pe.HasMetadata) continue;
            var md=pe.GetMetadataReader();
            foreach(var handle in md.TypeDefinitions)
            {
                var type=md.GetTypeDefinition(handle);
                string name=md.GetString(type.Namespace)+"."+md.GetString(type.Name);
                types.Add(name);
                foreach(var methodHandle in type.GetMethods())
                {
                    var method=md.GetMethodDefinition(methodHandle);
                    string member=md.GetString(method.Name);
                    if(member=="AssignProcessImageToOrganizationBlock")
                    {
                        directAssignment |= name=="Siemens.Engineering.HW.Address";
                        serviceAssignment |= name=="Siemens.Engineering.SW.ProcessImageProvider";
                    }
                    if(name=="Siemens.Engineering.IEngineeringObject" && member=="SetAttributes")
                        attributeDelegate |= method.GetParameters().Select(md.GetParameter).Count(p=>p.SequenceNumber>0)==2;
                }
            }
        }
        check(key=="21" ? serviceAssignment && !directAssignment : directAssignment,
            key+" F19 process-image branch binds the released native entry point");
        check(attributeDelegate==(key is "18" or "19" or "20" or "21"),
            key+" F19 attribute-write branch matches the real delegate overload");
        int version=key=="14sp1"?14:key=="15.1"?15:int.Parse(key);
        foreach(var branch in new Dictionary<string,int> {
            ["Siemens.Engineering.HW.Systemdiagnostics.Settings.SystemdiagnosticsSettingsDataProvider"]=17,
            ["Siemens.Engineering.HW.HardwareCatalog.HardwareCatalog"]=18,
            ["Siemens.Engineering.HW.TransferArea"]=15,
            ["Siemens.Engineering.HW.Features.MrpDomainOwner"]=16,
            ["Siemens.Engineering.HW.Features.SyncDomainOwner"]=16,
            ["Siemens.Engineering.HW.Features.MrpInstancesOwner"]=20,
            ["Siemens.Engineering.HW.Features.TelecontrolManagement"]=20,
            ["Siemens.Engineering.HW.Features.DefaultWebPagesFeature"]=21,
            ["Siemens.Engineering.HW.Features.CertificateManagementConfiguration"]=18,
            ["Siemens.Engineering.HW.Utilities.CardReaderPscProvider"]=15 })
            check(types.Contains(branch.Key)==(version>=branch.Value),key+" B2 released API branch "+branch.Key);
        check(types.Any(name=>name.StartsWith("Siemens.Engineering.HW.CommunicationConnections.",StringComparison.Ordinal))==(version>=21),
            key+" B2 communication connections are V21 only");
    }
}
