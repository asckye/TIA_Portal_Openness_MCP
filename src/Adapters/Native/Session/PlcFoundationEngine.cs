using System.Xml.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using TiaMcpServer.Siemens;
using TiaMcp.Adapters.Contracts;
#if PLC_SAFETY
using EngineeringProject = Siemens.Engineering.ProjectBase;
#else
using EngineeringProject = Siemens.Engineering.Project;
#endif

namespace TiaMcp.PlcFoundation
{
    /// <summary>
    /// Shared typed PLC operations for the selected release.
    /// Native acceptance is tracked in docs/reference/real-machine-ledger.md.
    /// The host must supply exact assembly resolution and serialize calls on one STA.
    /// </summary>
    public sealed partial class PlcFoundationEngine : IDisposable
    {
        private readonly int ownerThread;
        private TiaPortal? portal;
        private EngineeringProject? project;
        private readonly PlcLifecycleState lifecycle=new PlcLifecycleState();
#if PLC_SAFETY
        private Siemens.Engineering.Multiuser.LocalSession? localSession;
#endif
        private bool disposed;
        private readonly PlcDisconnectState disconnect = new PlcDisconnectState();
        private bool? ownsPortal;
        public string ReleaseKey { get; }

        public PlcFoundationEngine(string releaseKey, string selectedPublicApiDirectory)
        {
            var compiled = typeof(PlcFoundationEngine).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(a => a.Key == "TiaReleaseKey").Value!;
            PlcFoundationPolicy.RequireRelease(compiled, releaseKey);
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("PLC operations require an owning STA thread.");
            var contract = OpennessReleaseContract.For(releaseKey);
            contract = OpennessReleaseContract.For(releaseKey, contract.CoreAssemblyIdentity, selectedPublicApiDirectory);
            contract.RequireAssembly(typeof(TiaPortal).Assembly);
            if (releaseKey == "21")
            {
                var expected = new AssemblyName("Siemens.Engineering.Step7, Version=21.0.0.0, Culture=neutral, PublicKeyToken=29bfe5fdf4ba5d3b");
                var actual = typeof(PlcSoftware).Assembly;
                EngineeringAssemblyIdentity.RequireMatch(expected, actual.GetName(), actual.Location);
                if (!string.Equals(Path.GetDirectoryName(actual.Location), Path.GetFullPath(selectedPublicApiDirectory).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    throw new FileLoadException("Step7 must come from the selected V21 API directory.", actual.Location);
            }
            ReleaseKey = releaseKey;
            ownerThread = Thread.CurrentThread.ManagedThreadId;
        }

        private void Check(bool allowDisconnected=false)
        {
            if (disposed) throw new ObjectDisposedException(nameof(PlcFoundationEngine));
            if (!allowDisconnected) disconnect.RequireActive();
            if (Thread.CurrentThread.ManagedThreadId != ownerThread)
                throw new InvalidOperationException("Use the owning STA thread; cross-thread native access is refused.");
        }
        private TiaPortal Portal() { Check(); sessionCandidateAdapter?.VerifyOwnedState(); disconnect.RequireActive(); return portal ?? throw new InvalidOperationException("Attach to an explicitly selected TIA process first."); }
        private EngineeringProject Project() { Portal(); lifecycle.RequireBound(); return project ?? throw new InvalidOperationException("Bind, open or create an explicit project first."); }

        private void RequireHardwareCatalogBinding()
        {
            Check();
            if (portal == null || project == null)
                throw new AdapterPreconditionException("Hardware catalog tools require ConnectPortal followed by AttachOpenProject first.", "session", false);
        }

        // No process launch, automatic process selection, kill, project upgrade or UI interaction.
        public PlcConnectionResult Attach(int processId)
        {
            Check();
            disconnect.RequireActive();
            lifecycle.RequireAttach(processId);
            var process = TiaPortal.GetProcesses().SingleOrDefault(p => p.Id == processId)
                ?? throw new AdapterPreconditionException("Selected TIA process was not found by this release's API.","processId");
            portal = process.Attach();
            lifecycle.Attached(processId);
            ownsPortal=false;
            return new PlcConnectionResult { AttemptedPids=new[]{processId} };
        }
        public PlcProjectDetails[] ListProjects()
        {
            var portal=Portal();
            var result=portal.Projects.Select(p=>ProjectDetails(p,false)).ToList();
#if PLC_SAFETY
            result.AddRange(portal.LocalSessions.Select(s=>ProjectDetails(s.Project,true)));
#endif
            return result.ToArray();
        }
        private static PlcProjectDetails ProjectDetails(EngineeringProject p,bool session) => new PlcProjectDetails { Name=p.Name,Attributes=ReadAttributes(p),Meta=new Dictionary<string,object> { ["projectFile"]=p.Path.FullName,["isLocalSession"]=session } };
        public void RequireProjectIdentity(string expectedProjectFile)
        {
            var current=Project();
            PlcLifecyclePolicy.RequireLocalSessionExecution(lifecycle.IsLocalSession,false);
            MutationIdentityPolicy.RequireSameProject(expectedProjectFile,current.Path.FullName);
        }
        public PlcMutationResult BindProject(string projectName,string expectedProjectFile)
        {
            Portal(); lifecycle.RequireUnbound();
            string full;
            try { full=Path.GetFullPath(expectedProjectFile); }
            catch(ArgumentException ex) { throw new AdapterPreconditionException("An absolute expected project file is required.","expectedProjectFile",true,ex); }
            try { MutationIdentityPolicy.RequireSameProject(expectedProjectFile,full); }
            catch(ArgumentException ex) { throw new AdapterPreconditionException(ex.Message,"expectedProjectFile",true,ex); }
            bool session=PlcLifecyclePolicy.IsSessionFile(ReleaseKey,full);
#if PLC_SAFETY
            if(session)
            {
                localSession=portal!.LocalSessions.SingleOrDefault(s=>s.Project.Name==projectName && string.Equals(s.Project.Path.FullName,full,StringComparison.OrdinalIgnoreCase)) ?? throw new AdapterPreconditionException("The exact named local session is not open.","expectedProjectFile");
                project=localSession.Project;
            }
            else
#endif
                project=portal!.Projects.SingleOrDefault(p=>p.Name==projectName && string.Equals(p.Path.FullName,full,StringComparison.OrdinalIgnoreCase)) ?? throw new AdapterPreconditionException("The exact named project is not open.","expectedProjectFile");
            lifecycle.Bound(full,false,session);
            return Mutation("BindProject",false);
        }
        public PlcMutationResult OpenProject(string path, bool dryRun = true)
        {
            var p = Portal();
            lifecycle.RequireUnbound();
            bool session=PlcLifecyclePolicy.IsSessionFile(ReleaseKey,path);
            PlcLifecyclePolicy.RequireLocalSessionExecution(session,dryRun);
            var input = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(Path.GetFullPath(path)));
            if (!input.Exists) throw new AdapterPreconditionException("Project file does not exist.", "path", true, new FileNotFoundException());
            if (p.Projects.Any(x => string.Equals(x.Path.FullName, input.FullName, StringComparison.OrdinalIgnoreCase)))
                throw new AdapterPreconditionException("Project is already open; bind it explicitly instead.","path");
#if PLC_SAFETY
            if(p.LocalSessions.Any(s=>string.Equals(s.Project.Path.FullName,input.FullName,StringComparison.OrdinalIgnoreCase))) throw new AdapterPreconditionException("Local session already open; bind it explicitly.","path");
#endif
            if (!dryRun)
            {
#if PLC_SAFETY
                if(session) { localSession=p.LocalSessions.Open(input); project=localSession.Project; }
                else
#endif
                    project=p.Projects.Open(input);
                lifecycle.Bound(project.Path.FullName,true,session);
            }
            var result=Mutation("OpenProject",dryRun); result.ProjectFile=dryRun ? input.FullName : project!.Path.FullName; return result;
        }
        public PlcMutationResult CreateProject(string directoryPath, string projectName, bool dryRun = true)
        {
            var p = Portal();
            PlcFoundationPolicy.RequireName(projectName);
            lifecycle.RequireUnbound();
            var parent = new DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(Path.GetFullPath(directoryPath)));
            if (!parent.Exists || Directory.Exists(Path.Combine(parent.FullName, projectName)) || File.Exists(Path.Combine(parent.FullName, projectName)))
                throw new AdapterPreconditionException("Use an existing parent directory and a new project name.",!parent.Exists ? "directoryPath" : "projectName");
            var expected=PlcLifecyclePolicy.CreationFile(ReleaseKey,parent.FullName,projectName);
            if (!dryRun) { project = p.Projects.Create(parent, projectName); lifecycle.Bound(project.Path.FullName,true,false); }
            var result=Mutation("CreateProject",dryRun); result.ProjectFile=dryRun ? expected : project!.Path.FullName; return result;
        }
        public PlcMutationResult SaveProject(bool dryRun = true)
        {
            PlcLifecyclePolicy.RequireLocalSessionExecution(lifecycle.IsLocalSession,dryRun);
            var p = Project();
            if (!dryRun)
            {
#if PLC_SAFETY
                if(localSession!=null) localSession.Save(); else
#endif
                    ((Project)p).Save();
            }
            return Mutation("SaveProject", dryRun);
        }
        public PlcMutationResult CloseProject(bool dryRun = true)
        {
            PlcLifecyclePolicy.RequireLocalSessionExecution(lifecycle.IsLocalSession,dryRun);
            var p = Project();
            lifecycle.RequireClose(p.IsModified);
            var file=p.Path.FullName;
            if (!dryRun)
            {
#if PLC_SAFETY
                if(localSession!=null) { localSession.Close(); localSession=null; } else
#endif
                    ((Project)p).Close();
                project=null; lifecycle.Unbound();
            }
            var result=Mutation("CloseProject",dryRun); result.ProjectFile=file; return result;
        }
        public void UnbindProject() { Check(); lifecycle.Unbound(); project = null;
#if PLC_SAFETY
            localSession=null;
#endif
        }

        private sealed class Located<T>
        {
            internal readonly string Path;
            internal readonly T Value;
            internal Located(string path, T value) { Path = path; Value = value; }
        }
        private static string Child(string path, string name) => path.Length == 0 ? PlcFoundationPolicy.Segment(name) : path + "/" + PlcFoundationPolicy.Segment(name);
        private static PlcObjectInfo Info(string path, string name, string kind) => new PlcObjectInfo { Path = path, Name = name, Kind = kind };
        private PlcMutationResult Mutation(string operation, bool dryRun, IEnumerable<string>? names = null) =>
            new PlcMutationResult { Operation = operation, Executed = !dryRun, AffectedNames = names?.ToArray() ?? new string[0], ProjectFile=project?.Path.FullName };
        private static void Depth(int depth) { if (depth > 128) throw new InvalidOperationException("Object hierarchy exceeds the traversal depth limit."); }

        private IEnumerable<Located<PlcSoftware>> Plcs()
        {
            var p = Project();
            foreach (var d in p.Devices) foreach (var s in DevicePlcs(d, "devices", 0)) yield return s;
            foreach (var d in p.UngroupedDevicesGroup.Devices) foreach (var s in DevicePlcs(d, "ungrouped", 0)) yield return s;
            foreach (var g in p.DeviceGroups) foreach (var s in GroupPlcs(g, "device-groups", 0)) yield return s;
        }
        private static IEnumerable<Located<PlcSoftware>> GroupPlcs(DeviceUserGroup group, string path, int depth)
        {
            Depth(depth); path = Child(path, group.Name);
            foreach (var d in group.Devices) foreach (var s in DevicePlcs(d, path + "/devices", depth + 1)) yield return s;
            foreach (var g in group.Groups) foreach (var s in GroupPlcs(g, path + "/groups", depth + 1)) yield return s;
        }
        private static IEnumerable<Located<PlcSoftware>> DevicePlcs(Device device, string path, int depth)
        {
            Depth(depth); path = Child(path, device.Name);
            foreach (var item in device.DeviceItems) foreach (var s in ItemPlcs(item, path, depth + 1)) yield return s;
        }
        private static IEnumerable<Located<PlcSoftware>> ItemPlcs(DeviceItem item, string path, int depth)
        {
            Depth(depth); path = Child(path, item.Name);
            var container = ((IEngineeringServiceProvider)item).GetService<SoftwareContainer>();
            if (container?.Software is PlcSoftware plc) yield return new Located<PlcSoftware>(path, plc);
            foreach (var child in item.DeviceItems) foreach (var s in ItemPlcs(child, path, depth + 1)) yield return s;
        }
        public PlcObjectInfo[] ListPlcs() => Plcs().Select(x => Info(x.Path, x.Value.Name, "plc-software")).ToArray();
        private PlcSoftware Plc(string path) => ReadSelection(path).Value;

        private static IEnumerable<Located<PlcBlockGroup>> BlockGroups(PlcBlockGroup group, string path = "", int depth = 0)
        {
            Depth(depth); yield return new Located<PlcBlockGroup>(path, group);
            foreach (var child in group.Groups) foreach (var g in BlockGroups(child, Child(path, child.Name), depth + 1)) yield return g;
        }
        private static IEnumerable<Located<PlcTypeGroup>> TypeGroups(PlcTypeGroup group, string path = "", int depth = 0)
        {
            Depth(depth); yield return new Located<PlcTypeGroup>(path, group);
            foreach (var child in group.Groups) foreach (var g in TypeGroups(child, Child(path, child.Name), depth + 1)) yield return g;
        }
        private static IEnumerable<Located<PlcTagTableGroup>> TagGroups(PlcTagTableGroup group, string path = "", int depth = 0)
        {
            Depth(depth); yield return new Located<PlcTagTableGroup>(path, group);
            foreach (var child in group.Groups) foreach (var g in TagGroups(child, Child(path, child.Name), depth + 1)) yield return g;
        }
        private IEnumerable<Located<PlcBlock>> Blocks(string plc) => BlockGroups(Plc(plc).BlockGroup).SelectMany(g => g.Value.Blocks.Select(b => new Located<PlcBlock>(Child(g.Path, b.Name), b)));
        private IEnumerable<Located<PlcType>> Types(string plc) => TypeGroups(Plc(plc).TypeGroup).SelectMany(g => g.Value.Types.Select(t => new Located<PlcType>(Child(g.Path, t.Name), t)));
        private IEnumerable<Located<PlcTagTable>> Tables(string plc) => TagGroups(Plc(plc).TagTableGroup).SelectMany(g => g.Value.TagTables.Select(t => new Located<PlcTagTable>(Child(g.Path, t.Name), t)));
        private Located<PlcTagTable> SelectTable(string plc, string path)
        {
            return PlcExchangePolicy.SelectTable(Tables(plc),path,x=>x.Path,x=>x.Value.Name);
        }
        private PlcTagTable Table(string plc, string path) => SelectTable(plc,path).Value;
        public PlcObjectInfo[] ListBlocks(string plc) => Blocks(plc).Select(x => Info(x.Path, x.Value.Name, x.Value.ProgrammingLanguage.ToString())).ToArray();
        public PlcObjectInfo[] ListTypes(string plc) => Types(plc).Select(x => Info(x.Path, x.Value.Name, "plc-type")).ToArray();
        public PlcObjectInfo[] ListTagTables(string plc) => Tables(plc).Select(x => Info(x.Path, x.Value.Name, "tag-table")).ToArray();
        public PlcObjectInfo[] ListTags(string plc, string table) { var selected=SelectTable(plc,table); return selected.Value.Tags.Select(t => new PlcObjectInfo { Name = t.Name, Path = Child(selected.Path, t.Name), Kind = "tag", DataType = t.DataTypeName, Value = t.LogicalAddress }).ToArray(); }
        public PlcObjectInfo[] ListUserConstants(string plc, string table) { var selected=SelectTable(plc,table); return selected.Value.UserConstants.Select(c => new PlcObjectInfo { Name = c.Name, Path = Child(selected.Path, c.Name), Kind = "user-constant", DataType = c.DataTypeName, Value = c.Value }).ToArray(); }
        public PlcObjectInfo[] ListSystemConstants(string plc, string table) { var selected=SelectTable(plc,table); return selected.Value.SystemConstants.Select(c => new PlcObjectInfo { Name = c.Name, Path = Child(selected.Path, c.Name), Kind = "system-constant", DataType = c.DataTypeName, Value = c.Value }).ToArray(); }

        private PlcMutationResult Export(string operation, string file, bool dryRun, Action<FileInfo> export)
        {
            var output = PlcFoundationPolicy.XmlOutput(file);
            string? recovery=null;
            if(!dryRun) recovery=PlcExportPublication.Publish(output,export);
            var result=Mutation(operation,dryRun); result.OutputFile=output.FullName; result.RecoveryDirectory=recovery; return result;
        }
        public PlcMutationResult ExportBlock(string softwarePath, string blockPath, string exportPath, bool preservePath=false, bool dryRun = true)
        {
            var selected=ReadSelection(softwarePath);
            var path=PlcExchangePolicy.ObjectPath(blockPath,false,"blockPath");
            var blocks=BlockGroups(selected.Value.BlockGroup).SelectMany(g=>g.Value.Blocks.Select(b=>new Located<PlcBlock>(Child(g.Path,b.Name),b)));
            var target=PlcExchangePolicy.Exact(blocks,x=>x.Path,path,"blockPath").Value;
            TiaOpenness.Shared.NativeExportPolicy.RequireConsistent("blocks",target.IsConsistent?new string[0]:new[]{blockPath},"blockPath");
            var file=PlcExchangePolicy.ExportDestination(exportPath,path,preservePath);
            var capability=PlcBlockXmlPolicy.Export(ReleaseKey,target.ProgrammingLanguage.ToString());
            var result=Export("ExportBlock",file.FullName,dryRun,f=>{ RequireTargetOffline(selected); target.Export(f,ExportOptions.None); });
            capability.Apply(result); return result;
        }
        public PlcMutationResult ExportType(string softwarePath, string exportPath, string typePath, bool preservePath=false, bool dryRun = true)
        {
            var selected=ReadSelection(softwarePath);
            var path=PlcExchangePolicy.ObjectPath(typePath,false,"typePath");
            var types=TypeGroups(selected.Value.TypeGroup).SelectMany(g=>g.Value.Types.Select(t=>new Located<PlcType>(Child(g.Path,t.Name),t)));
            var target=PlcExchangePolicy.Exact(types,x=>x.Path,path,"typePath").Value;
            TiaOpenness.Shared.NativeExportPolicy.RequireConsistent("types",target.IsConsistent?new string[0]:new[]{typePath},"typePath");
            var file=PlcExchangePolicy.ExportDestination(exportPath,path,preservePath);
            return Export("ExportType", file.FullName, dryRun, f => { RequireTargetOffline(selected); target.Export(f, ExportOptions.None); });
        }
        public PlcMutationResult ExportTagTable(string softwarePath, string tagTableName, string exportPath, bool dryRun = true)
        {
            var tables=ReadPlc(softwarePath).TagTableGroup.TagTables;
            var target=PlcExchangePolicy.Exact(tables,t=>t.Name,tagTableName,"tagTableName");
            return Export("ExportTagTable", exportPath, dryRun, f => target.Export(f, ExportOptions.None));
        }
        public PlcMutationResult ImportBlocks(string softwarePath, string groupPath, string importPath, bool overwrite = false, bool dryRun = true)
        {
            var selected=ReadSelection(softwarePath);
            var input = PlcFoundationPolicy.XmlInput(importPath);
            var target = PlcExchangePolicy.Exact(BlockGroups(selected.Value.BlockGroup), x => x.Path, PlcExchangePolicy.ObjectPath(groupPath,true,"groupPath"),"groupPath").Value;
            return ImportXml("ImportBlocks",input,dryRun,()=>WithTargetOffline(selected,()=>target.Blocks.Import(input,overwrite ? ImportOptions.Override : ImportOptions.None).Select(b=>b.Name)),()=>PlcBlockXmlPolicy.Import(ReleaseKey,input.FullName), document => PlcFoundationPolicy.RequireImportAvailable(document, "SW.Blocks.", overwrite,
                name => BlockGroups(selected.Value.BlockGroup).Any(g => PlcFoundationPolicy.SymbolExists(g.Value.Blocks, selectedName => g.Value.Blocks.Find(selectedName), b => b.Name, name))), backups: !overwrite ? null : document => target.Blocks.Where(b => ImportNames(document).Contains(b.Name)).Select(b => new TiaOpenness.Shared.NativeExportPolicy.RecoveryTarget { Object = groupPath + "/" + b.Name, InspectBlocker = () => TiaOpenness.Shared.NativeExportPolicy.ExportBlocker(b.IsConsistent, b.IsKnowHowProtected), Export = file => WithTargetOffline(selected, () => { b.Export(file, ExportOptions.None); return true; }) }));
        }
        public PlcMutationResult ImportTypes(string softwarePath, string groupPath, string importPath, bool overwrite = false, bool dryRun = true)
        {
            var selected=ReadSelection(softwarePath);
            var input = PlcFoundationPolicy.XmlInput(importPath);
            var target = PlcExchangePolicy.Exact(TypeGroups(selected.Value.TypeGroup), x => x.Path, PlcExchangePolicy.ObjectPath(groupPath,true,"groupPath"),"groupPath").Value;
            return ImportXml("ImportTypes",input,dryRun,()=>WithTargetOffline(selected,()=>target.Types.Import(input,overwrite ? ImportOptions.Override : ImportOptions.None).Select(t=>t.Name)), precheck: document => PlcFoundationPolicy.RequireImportAvailable(document, "SW.Types.", overwrite,
                name => TypeGroups(selected.Value.TypeGroup).Any(g => PlcFoundationPolicy.SymbolExists(g.Value.Types, selectedName => g.Value.Types.Find(selectedName), t => t.Name, name))), backups: !overwrite ? null : document => target.Types.Where(t => ImportNames(document).Contains(t.Name)).Select(t => new TiaOpenness.Shared.NativeExportPolicy.RecoveryTarget { Object = groupPath + "/" + t.Name, InspectBlocker = () => TiaOpenness.Shared.NativeExportPolicy.ExportBlocker(t.IsConsistent), Export = file => WithTargetOffline(selected, () => { t.Export(file, ExportOptions.None); return true; }) }));
        }
        public PlcMutationResult ImportTagTables(string softwarePath, string folderPath, string importPath, bool overwrite = false, bool dryRun = true)
        {
            var input = PlcFoundationPolicy.XmlInput(importPath);
            var target = PlcExchangePolicy.Exact(TagGroups(ReadPlc(softwarePath).TagTableGroup), x => x.Path, PlcExchangePolicy.ObjectPath(folderPath,true,"folderPath"),"folderPath").Value;
            return ImportXml("ImportTagTables",input,dryRun,()=>target.TagTables.Import(input,overwrite ? ImportOptions.Override : ImportOptions.None).Select(t=>t.Name), precheck: document => PlcFoundationPolicy.RequireImportAvailable(document, "SW.Tags.PlcTagTable", overwrite,
                name => TagGroups(ReadPlc(softwarePath).TagTableGroup).Any(g => PlcFoundationPolicy.SymbolExists(g.Value.TagTables, selectedName => g.Value.TagTables.Find(selectedName), t => t.Name, name))), backups: !overwrite ? null : document => target.TagTables.Where(t => ImportNames(document).Contains(t.Name)).Select(t => new TiaOpenness.Shared.NativeExportPolicy.RecoveryTarget { Object = folderPath + "/" + t.Name, Export = file => t.Export(file, ExportOptions.None) }));
        }
        private static string[] ImportNames(System.Xml.Linq.XDocument document) => document.Root!.Elements().Where(e => e.Name.LocalName.StartsWith("SW.", StringComparison.Ordinal)).SelectMany(e => e.Elements("AttributeList").Elements("Name")).Select(e => e.Value).ToArray();
        private PlcMutationResult ImportXml(string operation,FileInfo input,bool dryRun,Func<IEnumerable<string>> import,Func<PlcBlockXmlCapability>? format=null, Action<System.Xml.Linq.XDocument>? precheck=null, Func<System.Xml.Linq.XDocument, IEnumerable<TiaOpenness.Shared.NativeExportPolicy.RecoveryTarget>>? backups=null)
        {
            using(var stream=TiaOpenness.Shared.NativeInputPolicy.OpenRead(input.FullName,"importPath"))
            using(var sha=System.Security.Cryptography.SHA256.Create())
            {
                // Keep the file read-locked from validation/hash through native import.
                PlcFoundationPolicy.XmlInput(input.FullName, out var document);
                precheck?.Invoke(document);
                var hash=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();
                string? recoveryDirectory=null,recoveryWarning=null; var recoveryFiles=new Dictionary<string,string>(); var recoverySkipped=new Dictionary<string,string>[0];
                try
                {
                    var capability=format?.Invoke();
                    if(!dryRun) capability?.RequireImport();
                    if(!dryRun && backups!=null)
                    {
                        var recovery=TiaOpenness.Shared.NativeExportPolicy.SingleImportRecovery(backups(document),PlcBatchImportRecovery.Directory,TiaOpenness.Shared.DataLocations.Current.RecoveryAttemptedPath);
                        recoveryDirectory=recovery.Directory; recoveryWarning=recovery.Warning; recoveryFiles=recovery.Files; recoverySkipped=recovery.Skipped.ToArray();
                    }
                    var result=Mutation(operation,dryRun,dryRun ? null : import());
                    capability?.Apply(result);
                    result.InputFile=input.FullName; result.InputSha256=hash; result.RecoveryDirectory=recoveryDirectory; result.RecoveryFiles=recoveryFiles; result.RecoveryStatus=recoveryWarning!=null ? "backup-skipped" : recoveryDirectory!=null ? "backup-ready" : "not-needed";
                    result.RecoveryWarning=recoveryWarning; result.RecoverySkipped=recoverySkipped;
                    return result;
                }
                catch(Exception ex)
                {
                    ex.Data["inputFile"]=input.FullName; ex.Data["inputSha256"]=hash;
                    if(recoveryDirectory!=null) ex.Data["recoveryDirectory"]=recoveryDirectory;
                    if(!ex.Data.Contains("recoveryFiles")) ex.Data["recoveryFiles"]=recoveryFiles;
                    if(recoveryWarning!=null) { ex.Data["recoveryStatus"]="backup-skipped";ex.Data["recoveryWarning"]=recoveryWarning;ex.Data["recoverySkipped"]=recoverySkipped;ex.Data["attemptedPath"]=TiaOpenness.Shared.DataLocations.Current.RecoveryAttemptedPath; }
                    throw;
                }
            }
        }
        public PlcMutationResult CreateTagTable(string plc, string group, string name, bool dryRun = true)
        {
            PlcFoundationPolicy.RequireName(name);
            var target = PlcFoundationPolicy.Exact(TagGroups(Plc(plc).TagTableGroup), x => x.Path, group,"group").Value;
            if (target.TagTables.Find(name) != null) throw new AdapterPreconditionException("Tag table already exists.","name");
            if (!dryRun) target.TagTables.Create(name);
            return Mutation("CreateTagTable", dryRun, new[] { name });
        }
        public PlcMutationResult CreateTag(string plc, string table, string name, string dataType, string address, bool dryRun = true)
        {
            PlcFoundationPolicy.RequireName(name);
            if (string.IsNullOrWhiteSpace(dataType) || string.IsNullOrWhiteSpace(address)) throw new AdapterPreconditionException("Data type and logical address are required.",string.IsNullOrWhiteSpace(dataType) ? "dataType" : "address");
            var target = Table(plc, table);
            if (target.Tags.Find(name) != null) throw new AdapterPreconditionException("Tag already exists.","name");
            RequireUniqueSymbol(plc, name);
            if (!dryRun) target.Tags.Create(name, dataType, address);
            return Mutation("CreateTag", dryRun, new[] { name });
        }
        public PlcMutationResult CreateUserConstant(string plc, string table, string name, string dataType, string value, bool dryRun = true)
        {
            PlcFoundationPolicy.RequireName(name);
            if (string.IsNullOrWhiteSpace(dataType) || string.IsNullOrWhiteSpace(value)) throw new AdapterPreconditionException("Data type and value are required.",string.IsNullOrWhiteSpace(dataType) ? "dataType" : "value");
            var target = Table(plc, table);
            if (target.UserConstants.Find(name) != null) throw new AdapterPreconditionException("User constant already exists.","name");
            RequireUniqueSymbol(plc, name);
            if (!dryRun) target.UserConstants.Create(name, dataType, value);
            return Mutation("CreateUserConstant", dryRun, new[] { name });
        }
        // Tags and constants share one symbol namespace per PLC: TIA refuses a duplicate in any table only inside the
        // native Create (seen on the V14 SP1 VM as an unknown outcome). Look up each composition before Create.
        private void RequireUniqueSymbol(string plc, string name)
        {
            // Finish cheap exact lookups across all tables before any case probe or symbol enumeration.
            var table = PlcFoundationPolicy.SymbolOwner(Tables(plc).Select(t => t.Value),
                t => t.Tags.Find(name) != null || t.UserConstants.Find(name) != null || t.SystemConstants.Find(name) != null,
                t => PlcFoundationPolicy.SymbolExists(t.Tags, selectedName => t.Tags.Find(selectedName), tag => tag.Name, name, true)
                    || PlcFoundationPolicy.SymbolExists(t.UserConstants, selectedName => t.UserConstants.Find(selectedName), c => c.Name, name, true)
                    || PlcFoundationPolicy.SymbolExists(t.SystemConstants, selectedName => t.SystemConstants.Find(selectedName), c => c.Name, name, true));
            if (table != null) throw new AdapterPreconditionException("A tag or constant with this name already exists in tag table '" + table.Name + "'; PLC symbol names are unique per PLC.", "name");
        }
        public PlcCompileResult CompileSoftware(string softwarePath, string password="", bool dryRun = true)
        {
            PlcCompilePolicy.RequirePasswordCapability(ReleaseKey,password);
            var selected=ReadSelection(softwarePath);
            var compiler = ((IEngineeringServiceProvider)selected.Value).GetService<ICompilable>()
                ?? throw new AdapterPreconditionException("The selected software does not provide ICompilable.","softwarePath",false);
            if(dryRun) return new PlcCompileResult { Executed=false,ProjectFile=Project().Path.FullName };
            RequireTargetOffline(selected); var unobservedDevices=CheckProjectOfflineProviders();
            CompilerResult result;
#if PLC_SAFETY
            Siemens.Engineering.Safety.SafetyAdministration? admin=null;
            bool loggedOnHere=false;
            if(password.Length!=0)
            {
                if(ReleaseKey=="21")
                {
                    var assembly=typeof(Siemens.Engineering.Safety.SafetyAdministration).Assembly;
                    EngineeringAssemblyIdentity.RequireMatch(new AssemblyName("Siemens.Engineering.Safety, Version=21.0.0.0, Culture=neutral, PublicKeyToken=29bfe5fdf4ba5d3b"),assembly.GetName(),assembly.Location);
                    if(!string.Equals(Path.GetDirectoryName(assembly.Location),Path.GetDirectoryName(typeof(PlcSoftware).Assembly.Location),StringComparison.OrdinalIgnoreCase)) throw new FileLoadException("Safety must come from the selected V21 API directory.");
                }
                admin=((IEngineeringServiceProvider)(DeviceItem)selected.Context!).GetService<Siemens.Engineering.Safety.SafetyAdministration>()
                    ?? throw new AdapterPreconditionException("The selected PLC does not provide SafetyAdministration; password was not ignored.","softwarePath",false);
                if(!admin.IsLoggedOnToSafetyOfflineProgram)
                {
                    using(var secure=new System.Net.NetworkCredential("",password).SecurePassword)
                    { try { admin.LoginToSafetyOfflineProgram(secure); } catch { throw new InvalidOperationException("Safety offline login failed; password and native login details are not echoed."); } }
                    loggedOnHere=true;
                }
            }
            try { RequireTargetOffline(selected); unobservedDevices=CheckProjectOfflineProviders(); result=compiler.Compile(); }
            catch(Exception ex)
            {
                if(loggedOnHere) { try { admin!.LogoffFromSafetyOfflineProgram(); } catch /* swallow(teardown): preserve the original compile failure and attach the safety logoff failure as cleanup evidence */ { ex.Data["safetyCleanup"]="Safety logoff also failed; inspect the session manually."; } }
                throw;
            }
            if(loggedOnHere) { try { admin!.LogoffFromSafetyOfflineProgram(); } catch { throw new InvalidOperationException("Compile returned but safety logoff failed; session outcome is unknown."); } }
#else
            RequireTargetOffline(selected); unobservedDevices=CheckProjectOfflineProviders(); result=compiler.Compile();
#endif
            var response=new PlcCompileResult { Executed=true, ProjectFile=Project().Path.FullName, State=result.State.ToString(), ErrorCount=result.ErrorCount,WarningCount=result.WarningCount,OfflineStateNotExposedByDevices=unobservedDevices };
            PlcCompilePolicy.Classify(response,Messages(result.Messages,0));
            return response;
        }
        private static IEnumerable<PlcDiagnostic> Messages(CompilerResultMessageComposition messages, int depth, string parentPath = "")
        {
            Depth(depth);
            foreach (var m in messages)
            {
                var parts=new List<string> { "State="+m.State };
                if(!string.IsNullOrWhiteSpace(m.Description)) parts.Add("Description="+m.Description);
                string path = string.IsNullOrWhiteSpace(m.Path) ? parentPath : m.Path;
                if(!string.IsNullOrWhiteSpace(path)) parts.Add("Path="+path);
                parts.Add("DateTime="+m.DateTime.ToString("O"));
                parts.Add("ErrorCount="+m.ErrorCount);
                parts.Add("WarningCount="+m.WarningCount);
                yield return new PlcDiagnostic { State=m.State.ToString(),Description=m.Description ?? "",Formatted=string.Join("; ",parts),HasChildren=m.Messages.Count!=0 };
                foreach (var child in Messages(m.Messages, depth + 1, path)) yield return child;
            }
        }
        public PlcDisconnectResult Disconnect()
        {
            Check(true);
            var result=disconnect.Execute(lifecycle.ProcessId,ownsPortal,()=>portal!.Dispose());
            portal=null; project=null; lifecycle.Detached();
#if PLC_SAFETY
            localSession=null;
#endif
            return result;
        }
        public void Dispose()
        {
            if (disposed) return;
            Check(true);
            // Never retry an attempted detach, including one whose acknowledgement was lost.
            try { if (!disconnect.Attempted) Disconnect(); }
            finally { disposed=true; }
        }
    }
}
