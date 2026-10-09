using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Siemens.Engineering;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Online;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Plc;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        private readonly string organisationWorkerIdentity = Guid.NewGuid().ToString("N");
        public Func<SeedHmiRequest, PlcSeedHmiReply>? SeedReferenceHmiImport { get; set; }
        public PlcSeedHmiReply SeedReferenceHmi(SeedHmiRequest request, bool dryRun = false) { Check(); if (dryRun) throw new ArgumentException("Seed imports have no preview operation."); return (SeedReferenceHmiImport ?? throw new NotSupportedException("Seed HMI imports depend on the B7 engine worker port in this release."))(request); }

        public Func<object, bool, PlcCompilerEvidence>? CollectPlcCompilerEvidence { get; set; }
        public Func<PlcSoftwareRequest, PlcVerifiedCandidate>? ValidatePlcVerifiedCandidate { get; set; }
        public Func<PlcVerifiedExecution, string>? ExecutePlcVerifiedWorkflow { get; set; }
        public IPlcOrganisationSession? EngineeringPlcOrganisationSession { get; set; }

        public PlcSoftwareReply ExecutePlcOrganisation(PlcSoftwareRequest request, bool dryRun = true)
        {
            Check();
            if (request.DryRun != dryRun) throw new ArgumentException("Conflicting PLC preview mode.");
            string? unavailable = PlcSoftwareCapabilities.Unsupported(ReleaseKey, request.Operation, request.Action, request.Family, request.UnitName, request.UnitKind, request.TargetKind, request.CopyMode, request.GenerateOption);
            if (unavailable != null) throw new NotSupportedException(unavailable);
            var adapter = new PlcOrganisationAdapter(EngineeringPlcOrganisationSession ?? new OrganisationSession(this));
            var reply = new PlcSoftwareReply();
            Func<object, bool, PlcCompilerEvidence> compilerEvidence = (result, diagnose) => CollectPlcCompilerEvidence!(PlcOrganisationAdapter.ObserveCompiler((Siemens.Engineering.Compiler.CompilerResult)result), diagnose);
            try
            {
                HardwareAddressingReply? step = null;
                switch (request.Operation)
                {
                    case "DeletePlcBlock": reply.Data = adapter.DeletePlcBlock(request.SoftwarePath, request.Path, dryRun, request.CrossReferences); break;
                    case "DeletePlcTagTable": reply.Data = adapter.DeletePlcTagTable(request.SoftwarePath, request.Path, dryRun, request.CrossReferences); break;
                    case "DeletePlcType": reply.Data = adapter.DeletePlcType(request.SoftwarePath, request.Path, dryRun, request.CrossReferences); break;
                    case "DeleteEmptyPlcBlockGroup": reply.Data = adapter.DeleteEmptyPlcBlockGroup(request.SoftwarePath, request.GroupPath, dryRun); break;
                    case "CreatePlcTypeGroup": reply.Data = adapter.CreatePlcTypeGroup(request.SoftwarePath, request.GroupPath, dryRun); break;
                    case "CreatePlcBlockGroup":
                        reply.Found = adapter.EnsurePlcBlockGroup(request.SoftwarePath, request.GroupPath, out var created) != null;
                        reply.Created = created.ToArray(); break;
                    case "MovePlcBlockToGroup": reply.Message = adapter.MoveBlockToGroup(request.SoftwarePath, request.Name, request.GroupPath, request.AutoCreateGroup); break;
                    case "ManagePlcUserGroup": step = adapter.ManagePlcUserGroup(request.SoftwarePath, request.Family, request.GroupPath, request.Action, request.NewName, dryRun); break;
                    case "ManagePlcBlockProtection": step = adapter.ManagePlcBlockProtection(request.SoftwarePath, request.Path, request.Action, request.Password, request.Confirm, dryRun); break;
                    case "ListPlcSystemGroups": step = adapter.ReadPlcSystemGroups(request.SoftwarePath, request.UnitName, request.UnitKind, request.IncludeBlocks, request.MaxDepth); break;
                    case "CreatePlcInstanceDb": step = adapter.CreatePlcInstanceDb(request.SoftwarePath, request.Path, request.Name, request.GroupPath, request.AutoNumber, request.Number, dryRun); break;
                    case "GeneratePlcSourceFromBlocks": step = adapter.GeneratePlcSourceFromBlocks(request.SoftwarePath, request.Paths, request.FilePath, dryRun, request.ValidationError); break;
                    case "SetPlcProgram": step = adapter.UpdatePlcProgram(request.SoftwarePath, request.Confirm, dryRun); break;
                    case "GetPlcTagTableConstants": step = adapter.ReadPlcTagTableConstants(request.SoftwarePath, request.Path, request.Kind, request.UnitName, request.UnitKind, request.Offset, request.Limit); break;
                    case "ManagePlcExternalSources": step = adapter.ManagePlcExternalSources(request.SoftwarePath, request.Action, request.Name, request.UnitName, request.UnitKind, request.GroupPath, request.FilePath, request.LibraryName, request.MasterCopyPath, request.CopyMode, request.GenerateOption, request.TargetKind, request.TargetGroupPath, request.NewName, request.Confirm, dryRun); break;
                    case "ManagePlcTagDefinition": step = adapter.ManagePlcTagDefinition(request); break;
                    case "GetPlcCrossReferences":
                        reply.References = new PlcCrossReferences(EngineeringPlcOrganisationSession ?? new OrganisationSession(this)).GetCrossReferences(request.SoftwarePath, request.Path, request.Kind, request.Filter, out var reason, out var queried, request.UnitName, request.UnitKind)?.ToArray();
                        reply.Reason = reason; reply.Queried = queried; break;
                    case "ImportPlcBlockVerified": step = adapter.ImportPlcBlockVerified(request, ValidatePlcVerifiedCandidate!, ExecutePlcVerifiedWorkflow!); break;
                    case "BuildAndImportPlcArtifact":
                    case "RepairAndReimportPlcBlock":
                    case "ImportPlcTagTablesFromDirectory":
                    case "SeedProjectFromReference":
                        if (request.Action == "importBlockDirectory" && request.Operation == "SeedProjectFromReference") reply.Batch = adapter.ImportBlocksFromDirectory(request.SoftwarePath, request.GroupPath, request.FilePath, request.RegexName, request.Overwrite);
                        else if (request.Action == "importBlock") reply.Found = adapter.ImportBlock(request.SoftwarePath, request.GroupPath, request.FilePath);
                        else if (request.Action == "importType") reply.Found = adapter.ImportType(request.SoftwarePath, request.GroupPath, request.FilePath);
                        else if (request.Action == "importTagTable") { adapter.ImportPlcTagTable(request.SoftwarePath, request.GroupPath, request.FilePath); reply.Found = true; }
                        else if (request.Action == "softwarePath") reply.Message = (EngineeringPlcOrganisationSession ?? new OrganisationSession(this)).PlcSoftwarePath((EngineeringPlcOrganisationSession ?? new OrganisationSession(this)).GetPlcSoftware(request.SoftwarePath));
                        else if (request.Action == "compile") {
                            var watch = System.Diagnostics.Stopwatch.StartNew();
                            reply.Compiler = compilerEvidence(adapter.CompileSoftware(request.SoftwarePath, request.Password), request.Operation == "RepairAndReimportPlcBlock");
                            if (request.Operation == "RepairAndReimportPlcBlock") { reply.Compiler.Meta["compileElapsedMs"] = watch.ElapsedMilliseconds; reply.Compiler.Meta["softwarePath"] = request.SoftwarePath; reply.Compiler.Meta["timestamp"] = TiaMcpServer.ModelContextProtocol.ResponseClock.Now; }
                        }
                        else throw new ArgumentException("Unregistered PLC artifact action: " + request.Action);
                        break;
                    case "DescribePlcBlockLogic":
                        var exported = adapter.ExportBlock(request.SoftwarePath, request.Path, request.FilePath);
                        reply.Found = exported != null;
                        if (exported != null) reply.Data = new Dictionary<string, object?> { ["Name"] = exported.Name, ["ProgrammingLanguage"] = exported.ProgrammingLanguage.ToString() };
                        break;
                    case "CompileDevice": step = adapter.CompileDevice(request.DevicePath, request.ItemPath, compilerEvidence); break;
                    case "CompileHmiDiagnostics":
                        var compileWatch = System.Diagnostics.Stopwatch.StartNew();
                        reply.Compiler = compilerEvidence(adapter.CompileSoftware(request.SoftwarePath), true);
                        reply.Compiler.Meta["compileElapsedMs"] = compileWatch.ElapsedMilliseconds; reply.Compiler.Meta["softwarePath"] = request.SoftwarePath; reply.Compiler.Meta["timestamp"] = TiaMcpServer.ModelContextProtocol.ResponseClock.Now;
                        break;
                    default: throw new ArgumentException("Unregistered PLC organisation operation: " + request.Operation);
                }
                if (step != null)
                {
                    reply.Message = step.Message; reply.Meta = step.Meta;
                    reply.RequiresSessionReset = step.RequiresSessionReset; reply.MayHaveChanged = step.MayHaveChanged;
                }
            }
            catch (Exception error) when (error.GetBaseException() is not NonRecoverableException)
            {
                reply.Failure = new PlcSoftwareFailure { Code = error is PlcSoftwareException native ? native.Code : error.GetType().Name, Message = error.Message,
                    Candidates = (error as PlcSoftwareException)?.Candidates?.ToArray() };
                reply.MayHaveChanged = adapter.MutationStarted; reply.RequiresSessionReset = adapter.MutationStarted;
                for (Exception? current = error; current != null; current = current.InnerException)
                    if (current.Data["nativeResultEvidence"] is Dictionary<string, object?> evidence) reply.Failure.Evidence = evidence;
            }
            reply.MayHaveChanged |= adapter.MutationStarted;
            return reply;
        }

        private sealed class OrganisationSession : IPlcOrganisationSession
        {
            private readonly PlcFoundationEngine engine;
            internal OrganisationSession(PlcFoundationEngine engine) => this.engine = engine;
            public Siemens.Engineering.HW.HardwareObject ExactEngineeringHardware(string[] devicePath, string[] itemPath) => engine.ExactHardware(devicePath, itemPath);
            public string PlcSoftwarePath(PlcSoftware? software) => software == null ? "" : engine.Plcs().Single(p => object.Equals(p.Value, software)).Path;
            public string ReleaseKey => engine.ReleaseKey;
            public void RecordExportPath(string? path) => engine.EngineeringRecordExportPath?.Invoke(path);
            public PlcTypeGroup? GetPlcTypeGroupByPath(string path, string groupPath)
            {
                object root = engine.ReadSelection(path).Value.TypeGroup;
                foreach (var part in PlcGroupOperations.Parts(groupPath, true)) { var next = PlcGroupOperations.Find(PlcGroupOperations.Get(root, "Groups"), part); if (next == null) return null; root = next; }
                return (PlcTypeGroup)root;
            }
            public string GetPlcBlockGroupPath(PlcBlockGroup group)
            {
                var names = new List<string>(); object? value = group;
                while (value is PlcBlockUserGroup child) { names.Insert(0, child.Name); value = child.Parent; }
                return string.Join("/", names);
            }
            public bool IsProjectNull() => engine.HardwareProjectMissing();
            public TiaPortal? CurrentPortal => engine.portal;
            public char[] RegexChars => new[] { '.', '*', '+', '?', '^', '$', '[', ']', '(', ')', '{', '}', '|', '\\' };
            public IEqualityComparer<object> ReferenceEqualityComparer => ReferenceComparer.Instance;
            public IDisposable AcquireHmiEditAccess() => engine.HardwareEditAccess();
            public SoftwareContainer? GetSoftwareContainer(string path) => engine.ResolveEngineeringSoftwareContainer != null
                ? engine.ResolveEngineeringSoftwareContainer(path) as SoftwareContainer : ReadContainer(path);
            public SoftwareContainer? ResolveSoftwareContainerUncached(string path) => GetSoftwareContainer(path);
            private SoftwareContainer ReadContainer(string path)
            {
                var values = new List<PlcReadCandidate<SoftwareContainer>>();
                void Item(Siemens.Engineering.HW.DeviceItem item, string device, string[] groups, string prefix, int depth)
                {
                    Depth(depth); string exact = Child(prefix, item.Name);
                    var container = ((IEngineeringServiceProvider)item).GetService<SoftwareContainer>();
                    var software = container?.Software;
                    if (software != null) values.Add(new PlcReadCandidate<SoftwareContainer> { Value = container!, ExactPath = exact, Device = device, Host = software.Name, Groups = groups, Context = item });
                    foreach (var child in item.DeviceItems) Item(child, device, groups, exact, depth + 1);
                }
                void Device(Siemens.Engineering.HW.Device device, string[] groups, string prefix)
                { foreach (var item in device.DeviceItems) Item(item, device.Name, groups, Child(prefix, device.Name), 0); }
                void Group(Siemens.Engineering.HW.DeviceUserGroup group, string[] parents, string prefix, int depth)
                {
                    Depth(depth); string exact = Child(prefix, group.Name); var groups = parents.Concat(new[] { group.Name }).ToArray();
                    foreach (var device in group.Devices) Device(device, groups, exact + "/devices");
                    foreach (var child in group.Groups) Group(child, groups, exact + "/groups", depth + 1);
                }
                var project = engine.Project();
                foreach (var device in project.Devices) Device(device, Array.Empty<string>(), "devices");
                foreach (var group in project.DeviceGroups) Group(group, Array.Empty<string>(), "device-groups", 0);
                foreach (var device in project.UngroupedDevicesGroup.Devices) Device(device, Array.Empty<string>(), "ungrouped");
                return PlcReadPathPolicy.Select(values, path).Value;
            }

            public PlcSoftware? GetPlcSoftware(string path) => engine.ReadSelection(path).Value;
            public List<(string Path, bool? Consistent)> ReadPlcConsistency(string path)
            {
                var plc = GetPlcSoftware(path)!;
                var rows = new List<(string Path, bool? Consistent)>();
                IEnumerable<(string Path, object Value)> Objects(object root, string collection, string prefix = "", int depth = 0)
                {
                    if (depth > 64) throw new InvalidOperationException("Group depth exceeds 64; result is incomplete.");
                    foreach (var value in PlcGroupOperations.Items(PlcGroupOperations.Get(root, collection))) yield return (prefix + PlcGroupOperations.Get(value, "Name"), value);
                    foreach (var sub in PlcGroupOperations.Items(PlcGroupOperations.Get(root, "Groups")))
                        foreach (var value in Objects(sub, collection, prefix + PlcGroupOperations.Get(sub, "Name") + "/", depth + 1)) yield return value;
                    var system = root.GetType().GetProperty("SystemBlockGroups");
                    if (system != null && collection == "Blocks") foreach (var sub in PlcGroupOperations.Items(system.GetValue(root)!))
                        foreach (var value in Objects(sub, collection, prefix + PlcGroupOperations.Get(sub, "Name") + "/", depth + 1)) yield return value;
                }
                void Scope(object? unit, string prefix)
                {
                    foreach (var item in Objects(BlockRootOf(plc, unit), "Blocks")) rows.Add((prefix + item.Path, ((PlcBlock)item.Value).IsConsistent));
                    foreach (var item in Objects(TypeRootOf(plc, unit), "Types")) rows.Add((prefix + "Types/" + item.Path, ((PlcType)item.Value).IsConsistent));
                }
                return TiaMcpServer.ModelContextProtocol.InvocationJournal.Native("PLC.consistency.rootAndUnits", () => {
                    Scope(null, "root:/");
#if PLC_SOFTWARE_CROSS_REFERENCES
                    var provider = TiaMcpServer.ModelContextProtocol.InvocationJournal.Native("PlcUnitProvider.GetService", () => plc.GetService<Siemens.Engineering.SW.Units.PlcUnitProvider>());
                    if (provider != null) {
                        foreach (var unit in provider.UnitGroup.Units) Scope(unit, "unit:" + unit.Name + "/");
                        foreach (var unit in provider.UnitGroup.SafetyUnits) Scope(unit, "safety:" + unit.Name + "/");
                    }
#endif
                    return rows;
                });
            }
            public PlcSoftware? ResolvePlc(string path, bool write) => ExactPlcForEngineering(path, write);
            public object? ResolvePlcTagTableGroup(PlcSoftware plc) => plc.TagTableGroup;
            public T? ResolvePlcService<T>(string path, PlcSoftware plc) where T : class, IEngineeringService
            {
                var selected = engine.ReadSelection(path);
                return (selected.Context as IEngineeringServiceProvider)?.GetService<T>()
                    ?? (selected.DeviceContext as IEngineeringServiceProvider)?.GetService<T>();
            }
            public PlcSoftware ExactPlcForEngineering(string path, bool write)
            {
                var selected = engine.ReadSelection(path);
                if (write) engine.RequireTargetOffline(selected);
                return selected.Value;
            }
            public object ExactMasterCopyPlcSource(string path, string objectPath, bool block)
                => (block ? (object?)GetBlock(path, objectPath) : GetType(path, objectPath))
                    ?? throw new PlcSoftwareException("NotFound", "Exact PLC object not found: " + objectPath);
            public void VerifyBinding(string operation) { engine.Check(); engine.RequireProjectIdentity(engine.ReadState().ProjectFile); }
            public string BindingIdentity() => engine.organisationWorkerIdentity + "\n" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(engine.project!) + "\n" + engine.ReadState().ProjectFile + "\n" + engine.ReadState().ProcessId;
            public Project? CurrentProject => engine.Project() as Project;
            public Siemens.Engineering.Library.MasterCopies.MasterCopy ExactMasterCopy(string libraryName, string path)
            {
                object library = string.IsNullOrEmpty(libraryName) ? (object)engine.Project().ProjectLibrary : engine.portal!.GlobalLibraries.FirstOrDefault(l => l.Name == libraryName) ?? throw new PlcSoftwareException("NotFound", "Exact global library not found: " + libraryName);
                var parts = PlcGroupOperations.Parts(path);
                object folder = PlcGroupOperations.Get(library, "MasterCopyFolder");
                foreach (var part in parts.Take(parts.Length - 1)) folder = PlcGroupOperations.Find(PlcGroupOperations.Get(folder, "Folders"), part) ?? throw new PlcSoftwareException("NotFound", "Master-copy folder not found: " + part);
                return (Siemens.Engineering.Library.MasterCopies.MasterCopy)(PlcGroupOperations.Find(PlcGroupOperations.Get(folder, "MasterCopies"), parts.Last()) ?? throw new PlcSoftwareException("NotFound", "Exact master copy not found: " + path));
            }
            public object ExactObjectUnder(object root, string path, string collection, string label)
            {
                var parts = PlcGroupOperations.Parts(path);
                var group = PlcGroupOperations.Group(root, string.Join("/", parts.Take(parts.Length - 1)));
                return PlcGroupOperations.Find(PlcGroupOperations.Get(group, collection), parts.Last()) ?? throw new PlcSoftwareException("NotFound", "Exact " + label + " not found: " + path);
            }
            public object? OptionalUnit(PlcSoftware plc, string unitName, string unitKind)
            {
                if (string.IsNullOrEmpty(unitName)) return null;
                if (unitKind != "unit" && unitKind != "safety") throw new ArgumentException("unitKind must be unit or safety.");
                var type = typeof(PlcSoftware).Assembly.GetType("Siemens.Engineering.SW.Units.PlcUnitProvider")
                    ?? throw new NotSupportedException("PLC units are unavailable in this API.");
                var provider = typeof(IEngineeringServiceProvider).GetMethod("GetService")!.MakeGenericMethod(type).Invoke(plc, null)
                    ?? throw new NotSupportedException("PLC unit provider is unavailable on this PLC.");
                var group = PlcGroupOperations.Get(provider, "UnitGroup");
                return PlcGroupOperations.Find(PlcGroupOperations.Get(group, unitKind == "safety" ? "SafetyUnits" : "Units"), unitName)
                    ?? throw new PlcSoftwareException("NotFound", "Exact PLC unit not found: " + unitName);
            }
            public PlcBlockGroup BlockRootOf(PlcSoftware plc, object? unit) => unit == null ? plc.BlockGroup : (PlcBlockGroup)PlcGroupOperations.Get(unit, "BlockGroup");
            public PlcTypeGroup TypeRootOf(PlcSoftware plc, object? unit) => unit == null ? plc.TypeGroup : (PlcTypeGroup)PlcGroupOperations.Get(unit, "TypeGroup");
            public PlcBlock? GetBlock(string path, string blockPath)
            {
                var parts = PlcGroupOperations.Parts(blockPath); var root = engine.ReadSelection(path).Value.BlockGroup;
                return ((PlcBlockGroup)PlcGroupOperations.Group(root, string.Join("/", parts.Take(parts.Length - 1)))).Blocks.Find(parts.Last());
            }
            public List<PlcBlock>? GetBlocks(string path, string regex = "")
            {
                var all = engine.Blocks(path).Select(x => x.Value);
                return (regex.Length == 0 ? all : all.Where(b => Regex.IsMatch(b.Name, regex, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))).ToList();
            }
            public PlcType? GetType(string path, string typePath)
            {
                var parts = PlcGroupOperations.Parts(typePath); var root = engine.ReadSelection(path).Value.TypeGroup;
                return ((PlcTypeGroup)PlcGroupOperations.Group(root, string.Join("/", parts.Take(parts.Length - 1)))).Types.Find(parts.Last());
            }
            public string GetBlockPath(PlcBlock block)
            {
                var names = new List<string> { block.Name }; object? parent = block.Parent;
                while (parent is PlcBlockUserGroup group) { names.Insert(0, group.Name); parent = group.Parent; }
                return string.Join("/", names);
            }
            public void GetBlocksRecursive(PlcBlockGroup root, List<PlcBlock> result)
                => result.AddRange(BlockGroups(root).SelectMany(g => g.Value.Blocks));
            public PlcBlockGroup? GetPlcBlockGroupByPath(string path, string groupPath)
            {
                object current = engine.ReadSelection(path).Value.BlockGroup;
                foreach (var segment in PlcGroupOperations.Parts(groupPath, true))
                { var next = PlcGroupOperations.Find(PlcGroupOperations.Get(current, "Groups"), segment); if (next == null) return null; current = next; }
                return (PlcBlockGroup)current;
            }
            public List<string>? GetPlcTagTables(string path, out string? reason)
            { reason = null; return engine.Tables(path).Select(t => t.Path).ToList(); }
            public string AvailablePlcPathsSuffix() => " Available PLC paths: " + string.Join(", ", engine.Plcs().Select(p => p.Path));
            public bool CompilerLoggingEnabled => false;
            public void LogCompilerMessage(string path, string state, string description, int errors, int warnings, DateTime time, int nested) { }
            public void GroupCreated(string name) { }
            public HardwareAddressingReply RunHmiStepTool(string tool, Func<Dictionary<string, object?>, string> action) => engine.RunHardwareAddressStep(tool, action);
            public List<PlcCrossReference>? GetCrossReferences(string path, string objectPath, string kind, string filter, out string? reason, out bool queried)
            => new PlcCrossReferences(this).GetCrossReferences(path, objectPath, kind, filter, out reason, out queried, "", "unit");
            public string? CrossReferenceRefusal(string path) => new PlcCrossReferences(this).CrossReferenceRefusal(path);
            public List<PlcCrossReference> TryFlattenCrossReferenceResult(object result, string fallback) => PlcCrossReferences.TryFlattenCrossReferenceResult(result, fallback);
            public bool RecoverableAuditError(Exception error) => error.GetBaseException() is not NonRecoverableException;
            public object? DocumentMessages(object messages) => null;
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
            public int GetHashCode(object value) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
        }
    }
}
