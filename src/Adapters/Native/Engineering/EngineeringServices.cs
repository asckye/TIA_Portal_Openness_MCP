using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.Settings;
#if ENGINEERING_CUSTOM_IDENTITY
using Siemens.Engineering.CustomIdentity;
#endif
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Native.Plc;
using TiaMcp.Adapters.Engineering;
using static TiaMcp.Adapters.Hardware.HardwareAddressPrimitives;
#if PLC_SAFETY
using EngineeringProject = Siemens.Engineering.ProjectBase;
#else
using EngineeringProject = Siemens.Engineering.Project;
#endif

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        public HardwareAddressingReply ManageProjectLanguage(string action="read", string culture="", bool dryRun=true)
            => RunEngineeringServicesStep("ManageProjectLanguage", meta => {
                if (!new[] { "read", "activate", "deactivate", "setEditing", "setReference" }.Contains(action)) throw new ArgumentException("action must be one of: read/activate/deactivate/setEditing/setReference (case-sensitive).");
                var settings=project!.LanguageSettings;
                Dictionary<string, object?> Snapshot() => new Dictionary<string, object?> {
                    ["activeCultures"]=new List<object?>(settings.ActiveLanguages.Select(x=>(object)x.Culture.Name).ToArray()),
                    ["editingCulture"]=settings.EditingLanguage?.Culture.Name,
                    ["referenceCulture"]=settings.ReferenceLanguage?.Culture.Name };
                meta["before"]=Snapshot(); meta["dryRun"]=dryRun; meta["mayHaveChanged"]=false;
                if(action=="read") return "Project active, editing and reference languages read.";
                var selected=settings.Languages.Find(CultureInfo.GetCultureInfo(culture)) ?? throw new NotSupportedException("Language not supported by this project.");
                bool active=settings.ActiveLanguages.Any(x=>x.Culture.Name==selected.Culture.Name);
                if((action=="setEditing" || action=="setReference") && !active) throw new InvalidOperationException("Activate language explicitly before selecting it.");
                if(action=="deactivate" && (settings.EditingLanguage?.Culture.Name==selected.Culture.Name || settings.ReferenceLanguage?.Culture.Name==selected.Culture.Name)) throw new InvalidOperationException("Cannot deactivate current editing/reference language.");
                if(dryRun) return "Project language preview; no changes.";
                using var access=HardwareEditAccess(); meta["mayHaveChanged"]=true;
                switch(action) {
                    case "activate": if(!active) settings.ActiveLanguages.Add(selected); break;
                    case "deactivate": if(active) settings.ActiveLanguages.Remove(selected); break;
                    case "setEditing": settings.EditingLanguage=selected; break;
                    case "setReference": settings.ReferenceLanguage=selected; break;
                }
                var after=Snapshot(); meta["after"]=after;
                bool nowActive=settings.ActiveLanguages.Any(x=>x.Culture.Name==selected.Culture.Name);
                bool verified=action=="activate" ? nowActive : action=="deactivate" ? !nowActive : action=="setEditing" ? settings.EditingLanguage?.Culture.Name==selected.Culture.Name : settings.ReferenceLanguage?.Culture.Name==selected.Culture.Name;
                if(!verified) throw new InvalidOperationException("Project language readback differs.");
                return "Project language changed and verified; no save/compile/download.";
            });
        public HardwareAddressingReply ExportProjectTexts(string filePath, string sourceCulture, string targetCulture, bool dryRun = true)
            => RunEngineeringServicesStep("ExportProjectTexts", meta => {
                var file = EngineeringFileOutput.Plan(filePath); var source = CultureInfo.GetCultureInfo(sourceCulture); var target = CultureInfo.GetCultureInfo(targetCulture);
                var signature = new[] { typeof(FileInfo), typeof(CultureInfo), typeof(CultureInfo) };
                if (project!.GetType().GetMethod("ExportProjectTexts", signature) == null) throw new NotSupportedException("Native culture-pair text export unavailable.");
                meta["dryRun"] = dryRun; meta["mayHaveWrittenFiles"] = false; meta["sourceCulture"] = source.Name; meta["targetCulture"] = target.Name;
                if (!dryRun)
                {
                    meta["mayHaveWrittenFiles"] = true;
                    PlcGroupOperations.Call(project, "ExportProjectTexts", signature, file, source, target);
                    meta["apiCallSuccess"] = true; meta["dataComplete"] = false;
                    meta["file"] = EngineeringFileOutput.Verify(file);
                }
                return dryRun ? "Project text export preview." : "Native project text file exported and hashed; no project modification.";
            });
        public HardwareAddressingReply ImportProjectTexts(string filePath, bool updateSourceLanguage, bool dryRun = true)
            => RunEngineeringServicesStep("ImportProjectTexts", meta => {
                var file = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(filePath)); if (!file.Exists) throw new FileNotFoundException("Text import file not found.");
                var signature = new[] { typeof(FileInfo), typeof(bool) };
                if (project!.GetType().GetMethod("ImportProjectTexts", signature) == null) throw new NotSupportedException("Native project text import unavailable.");
                using var access = dryRun ? null : HardwareEditAccess();
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["filePath"] = file.FullName; meta["updateSourceLanguage"] = updateSourceLanguage;
                if (!dryRun) {
                    meta["mayHaveChanged"] = true;
                    var result = PlcGroupOperations.Call(project, "ImportProjectTexts", signature, file, updateSourceLanguage);
                    meta["nativeResult"] = HardwareScalarEvidence.Read(result);
                    PlcSoftwareValues.RecordNativeResult(meta, PlcGroupOperations.Get(result, "State"), meta["nativeResult"]);
                    meta["apiCallSuccess"] = true; meta["operationSuccess"] = TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded(meta["nativeResultState"]?.ToString()); meta["dataComplete"] = false;
                }
                return dryRun ? "Native project text import preview; file contents not applied." : "Native import completed; translated text changes require separate export/readback verification. No explicit save/compile/download.";
            });
        private IEngineeringObject ExactIdentifiableObject(string kind, string[] devicePath, string[] itemPath, string softwarePath, string objectPath, Dictionary<string, object?> meta)
        {
            if (kind == "device" || kind == "deviceItem")
            {
                var hardware = ExactHardware(devicePath, kind == "device" ? Array.Empty<string>() : itemPath); meta["ownerPath"] = HardwareAddressPrimitives.HardwareOwnerPath(hardware);
                return hardware;
            }
            var plc = EngineeringPlcSession.ExactPlcForEngineering(softwarePath, false); var parts = PlcGroupOperations.Parts(objectPath);
            object root = kind == "plcBlock" ? plc.BlockGroup : kind == "plcType" ? (object)plc.TypeGroup : plc.TagTableGroup;
            var group = PlcGroupOperations.Group(root, string.Join("/", parts.Take(parts.Length - 1)));
            var collection = PlcGroupOperations.Get(group, kind == "plcBlock" ? "Blocks" : kind == "plcType" ? "Types" : "TagTables");
            return PlcGroupOperations.Find(collection, parts.Last()) as IEngineeringObject ?? throw new PlcSoftwareException("NotFound", "Exact " + kind + " not found: " + objectPath);
        }
        private static Dictionary<string, object?> IdentifiedObjectRow(IEngineeringObject target)
        {
            var row = new Dictionary<string, object?> { ["objectClass"] = target.GetType().Name };
            try { row["name"] = target.GetType().GetProperty("Name")?.GetValue(target)?.ToString(); } catch /* swallow(probe-optional): objects without a readable Name retain their class identity */ { }
            if (target is ISystemObject systemObject) { try { row["isSystemObject"] = systemObject.IsSystemObject; } catch (Exception ex) { row["isSystemObjectError"] = ex.GetBaseException().Message; } }
            return row;
        }

#if ENGINEERING_OBJECT_IDENTIFIER
        public HardwareAddressingReply ReadObjectIdentifier(string kind, string[] devicePath, string[] itemPath, string softwarePath = "", string objectPath = "", string identifier = "")
            => RunEngineeringServicesStep("GetObjectIdentifier", meta => {
                EngineeringServicesPolicy.ObjectSelection(kind, devicePath, itemPath, softwarePath, objectPath, identifier);
                ObjectIdentifierProvider provider = project!.GetService<ObjectIdentifierProvider>() ?? throw new NotSupportedException("ObjectIdentifierProvider service unavailable on this project.");
                meta["kind"] = kind;
                if (!string.IsNullOrEmpty(identifier))
                {
                    // Find(identifier) returns the object behind an identifier issued earlier (cross-session stable per the official page).
                    var found = provider.Find(identifier) ?? throw new PlcSoftwareException("NotFound", "ObjectIdentifierProvider.Find returned null for the identifier.");
                    meta["identifier"] = identifier; meta["found"] = IdentifiedObjectRow(found);
                    if (found is HardwareObject hardware) meta["ownerPath"] = HardwareAddressPrimitives.HardwareOwnerPath(hardware);
                    meta["apiCallSuccess"] = true; meta["dataComplete"] = true; return "Object found from its identifier; no modification.";
                }
                var target = ExactIdentifiableObject(kind, devicePath, itemPath, softwarePath, objectPath, meta);
                meta["object"] = IdentifiedObjectRow(target); meta["identifier"] = provider.GetIdentifier(target);
                meta["apiCallSuccess"] = true; meta["dataComplete"] = true;
                meta["scope"] = "ObjectIdentifierProvider.GetIdentifier / Find; official support: Device, DeviceItem, code and data blocks, PLC tags, software units, TechnologicalInstanceDB, PlcStruct.";
                return "Object identifier read; no modification.";
            });

#endif
        public HardwareAddressingReply ShowObjectInEditor(string kind, string[] devicePath, string[] itemPath, string softwarePath = "", string objectPath = "", bool dryRun = true)
            => RunEngineeringServicesStep("ShowObjectInEditor", meta => {
                EngineeringServicesPolicy.ObjectSelection(kind, devicePath, itemPath, softwarePath, objectPath, "");
                var target = ExactIdentifiableObject(kind, devicePath, itemPath, softwarePath, objectPath, meta);
                meta["kind"] = kind; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["object"] = IdentifiedObjectRow(target);
                IShowable showable = target as IShowable ?? throw new NotSupportedException(target.GetType().Name + " does not implement IShowable (Device and the STEP 7 blocks / types / tag tables / watch and force tables do).");
                if (dryRun) return "ShowInEditor preview; the TIA Portal UI was not touched.";
                showable.ShowInEditor(); meta["apiCallSuccess"] = true;
                return "IShowable.ShowInEditor invoked: the object is opened in the TIA Portal editor (UI only; no project change).";
            });

#if ENGINEERING_COMPILATION_PROPERTIES || ENGINEERING_COMPILATION_SERVICES
        public HardwareAddressingReply ManageProjectCompilationSettings(string action, Dictionary<string, bool> values, bool dryRun = true)
            => RunEngineeringServicesStep("ManageProjectCompilationSettings", meta => {
                meta["mayHaveChanged"] = false;
                bool writing = action == "update" && !dryRun;
                using var access = writing ? HardwareEditAccess() : null;
                var project = this.project as global::Siemens.Engineering.Project ?? throw new NotSupportedException("The bound project is a " + this.project?.GetType().Name + "; the compilation settings live on Siemens.Engineering.Project.");
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
#if ENGINEERING_COMPILATION_PROPERTIES
                meta["access"] = "Project.IsSimulationDuringBlockCompilationEnabled / IsVirtualPlcDuringBlockCompilationEnabled (V20 properties; V21 replaced them by service providers)";
                Dictionary<string, object?> State() => new Dictionary<string, object?> { ["IsSimulationDuringBlockCompilationEnabled"] = project.IsSimulationDuringBlockCompilationEnabled
#if ENGINEERING_VIRTUAL_COMPILATION_PROPERTIES
                    , ["IsVirtualPlcDuringBlockCompilationEnabled"] = project.IsVirtualPlcDuringBlockCompilationEnabled
#endif
                };
                void Write(string key, bool value) {
                    if (key == "IsSimulationDuringBlockCompilationEnabled") project.IsSimulationDuringBlockCompilationEnabled = value;
#if ENGINEERING_VIRTUAL_COMPILATION_PROPERTIES
                    else project.IsVirtualPlcDuringBlockCompilationEnabled = value;
#else
                    else throw new NotSupportedException("Virtual PLC compilation settings require V19 or later.");
#endif
                }
#else
                // V21 removed the two Project properties; the SW service providers of the project are the only access.
                PlcSimulationSettingsProvider simulation = project.GetService<PlcSimulationSettingsProvider>() ?? throw new NotSupportedException("PlcSimulationSettingsProvider unavailable on the project (V21 service replacing Project.IsSimulationDuringBlockCompilationEnabled).");
                VirtualPlcSettingsProvider virtualPlc = project.GetService<VirtualPlcSettingsProvider>() ?? throw new NotSupportedException("VirtualPlcSettingsProvider unavailable on the project (V21 service replacing Project.IsVirtualPlcDuringBlockCompilationEnabled).");
                meta["access"] = "Project.GetService<PlcSimulationSettingsProvider>() / GetService<VirtualPlcSettingsProvider>() (V21: the Project properties were removed)";
                Dictionary<string, object?> State() => new Dictionary<string, object?> { ["IsSimulationDuringBlockCompilationEnabled"] = simulation.IsSimulationDuringBlockCompilationEnabled, ["IsVirtualPlcDuringBlockCompilationEnabled"] = virtualPlc.IsVirtualPlcDuringBlockCompilationEnabled };
                void Write(string key, bool value) { if (key == "IsSimulationDuringBlockCompilationEnabled") simulation.IsSimulationDuringBlockCompilationEnabled = value; else virtualPlc.IsVirtualPlcDuringBlockCompilationEnabled = value; }
#endif
                meta["before"] = State();
                if (action == "read") return "Project simulation / virtual PLC compilation settings read.";
                meta["requested"] = values.ToDictionary(p => p.Key, p => (object?)p.Value);
                if (!writing) return "Compilation settings update preview; no changes.";
                meta["mayHaveChanged"] = true;
                foreach (var pair in values) Write(pair.Key, pair.Value);
                meta["apiCallSuccess"] = true; meta["after"] = State();
                foreach (var pair in values) if ((bool)((Dictionary<string, object?>)meta["after"]!)[pair.Key]! != pair.Value) throw new InvalidOperationException(pair.Key + " readback differs from the requested value.");
                return "Compilation settings written and read back; project not saved.";
            });

#endif
        private static object InvokeNativeCompare(object subject, string method, object target)
        {
            var info = subject.GetType().GetMethods().FirstOrDefault(m => m.Name == method && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsInstanceOfType(target))
                ?? throw new NotSupportedException(subject.GetType().Name + "." + method + " does not accept " + target.GetType().Name + " on this API version.");
            try { return info.Invoke(subject, new[] { target }) ?? throw new InvalidOperationException("Native comparison returned null; no result available, do not read this as identical."); }
            catch (TargetInvocationException ex) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException ?? ex).Throw(); throw; }
        }
        private static Dictionary<string, object?> CompareRow(object element)
        {
            var type = element.GetType();
            var row = new Dictionary<string, object?> {
                ["leftName"] = type.GetProperty("LeftName")?.GetValue(element)?.ToString(), ["rightName"] = type.GetProperty("RightName")?.GetValue(element)?.ToString(),
                ["comparisonResult"] = type.GetProperty("ComparisonResult")?.GetValue(element)?.ToString(), ["detailedInformation"] = type.GetProperty("DetailedInformation")?.GetValue(element)?.ToString() };
            var left = type.GetProperty("Left")?.GetValue(element); var right = type.GetProperty("Right")?.GetValue(element);
            if (left != null) row["leftType"] = left.GetType().FullName;
            if (right != null) row["rightType"] = right.GetType().FullName;
            return row;
        }
        private static string PublishCompareResult(object result, bool includeIdentical, int maxDepth, int offset, int limit, Dictionary<string, object?> meta)
        {
            // The software compare tree is typed (CompareResult.RootElement -> CompareResultElement.Elements); the library
            // tree keeps the reflective walk (LibraryCompareResultElement lives in the Library.Compare namespace).
            var root = result is global::Siemens.Engineering.Compare.CompareResult typedResult ? (object)typedResult.RootElement : PlcGroupOperations.Get(result, "RootElement");
            if (root is global::Siemens.Engineering.Compare.CompareResultElement rootElement) meta["rootElement"] = new Dictionary<string, object?> { ["leftName"] = rootElement.LeftName, ["rightName"] = rootElement.RightName, ["comparisonResult"] = rootElement.ComparisonResult.ToString(), ["detailedInformation"] = rootElement.DetailedInformation, ["elements"] = PlcGroupOperations.Items(rootElement.Elements).Count() };
            var rows = EngineeringEvidence.FlattenCompareTree(root, e => PlcGroupOperations.Items(PlcGroupOperations.Get(e, "Elements")), CompareRow, maxDepth, 10000, out bool treeTruncated);
            meta["resultType"] = result.GetType().FullName; meta["summary"] = EngineeringEvidence.Summarize(rows); meta["totalElements"] = rows.Count; meta["treeTruncated"] = treeTruncated;
            var selected = includeIdentical ? rows : rows.Where(r => !EngineeringEvidence.IsIdentical((string?)r["comparisonResult"])).ToList();
            meta["records"] = EngineeringEvidence.Page(selected.Cast<object?>().ToList(), offset, limit, meta);
            meta["includeIdentical"] = includeIdentical; meta["apiCallSuccess"] = true;
            meta["dataComplete"] = !treeTruncated && (bool)meta["truncated"]! == false && offset == 0;
            return "Native offline comparison completed; " + rows.Count + " elements walked (summary counts all states, records exclude identical unless requested).";
        }
        public HardwareAddressingReply CompareLibraries(string leftLibraryName = "", string rightLibraryName = "", bool includeIdentical = false, int maxDepth = 8, int offset = 0, int limit = 100)
            => RunEngineeringServicesStep("CompareLibraries", meta => {
                meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false;
                EngineeringServicesPolicy.LibraryPair(leftLibraryName, rightLibraryName);
                EngineeringEvidence.ValidatePage(offset, limit); EngineeringEvidence.ValidateDepth(maxDepth);
                if (portal == null) throw new PlcSoftwareException("InvalidState", "Connect to TIA first.");
                if ((string.IsNullOrEmpty(leftLibraryName) || string.IsNullOrEmpty(rightLibraryName)) && HardwareProjectMissing()) throw new PlcSoftwareException("InvalidState", "Project library requested but no project is bound.");
                var left = ExactOpenEngineeringLibrary(leftLibraryName); var right = ExactOpenEngineeringLibrary(rightLibraryName);
                if (ReferenceEquals(left, right)) throw new ArgumentException("Left and right resolve to the same library.");
                meta["left"] = EngineeringLibraryRef(left); ((Dictionary<string, object?>)meta["left"]!)["type"] = left.GetType().FullName;
                meta["right"] = EngineeringLibraryRef(right); ((Dictionary<string, object?>)meta["right"]!)["type"] = right.GetType().FullName;
                return PublishCompareResult(InvokeNativeCompare(left, "CompareToLibrary", right), includeIdentical, maxDepth, offset, limit, meta);
            }, requiresProject: false);

        private EngineeringProject ExactOpenProject(string projectName)
        {
            var candidates = portal!.Projects.Cast<EngineeringProject>()
#if PLC_SAFETY
                .Concat(portal.LocalSessions.Select(s => (EngineeringProject)s.Project))
#endif
                .Where(p => p != null && string.Equals(p.Name, projectName, StringComparison.Ordinal)).Take(2).ToArray();
            if (candidates.Length > 1) throw new InvalidOperationException("Ambiguous open project name: " + projectName);
            return candidates.SingleOrDefault() ?? throw new PlcSoftwareException("NotFound", "Exact open project not found in this Portal instance: " + projectName);
        }
        private HardwareObject ExactHardwareInProject(EngineeringProject project, string[] devicePath, string[] itemPath)
        {
            var names = devicePath;
            Device device;
            if (names.Length == 1)
            {
                var all = TiaMcp.Adapters.Native.Hardware.ProjectDeviceWalk.Devices(project.Devices, project.DeviceGroups, project.UngroupedDevicesGroup).ToArray();
                device = (Device)(PlcGroupOperations.Find(all, names[0]) ?? throw new PlcSoftwareException("NotFound", "Exact unique device not found in project " + project.Name + ": " + names[0]));
            }
            else if (names[0] == TiaMcp.Adapters.Native.Hardware.ProjectDeviceWalk.UngroupedSegment)
            {
                if (names.Length != 2) throw new PlcSoftwareException("NotFound", "Use [\"$ungrouped\", \"<device>\"] for a device in the ungrouped devices group.");
                device = (Device)(PlcGroupOperations.Find(project.UngroupedDevicesGroup.Devices, names[1]) ?? throw new PlcSoftwareException("NotFound", "Device not found in the ungrouped devices group: " + names[1]));
            }
            else
            {
                var group = PlcGroupOperations.Find(project.DeviceGroups, names[0]) as DeviceUserGroup ?? throw new PlcSoftwareException("NotFound", "Device group not found: " + names[0]);
                foreach (var name in names.Skip(1).Take(names.Length - 2))
                    group = (DeviceUserGroup)(PlcGroupOperations.Find(group.Groups, name) ?? throw new PlcSoftwareException("NotFound", "Device group not found: " + name));
                device = (Device)(PlcGroupOperations.Find(group.Devices, names.Last()) ?? throw new PlcSoftwareException("NotFound", "Device not found: " + names.Last()));
            }
            HardwareObject current = device;
            var items = itemPath;
            if (items.Length > 64) throw new ArgumentException("Invalid item path.");
            foreach (var node in items) current = (DeviceItem)(PlcGroupOperations.Find(current.DeviceItems, node) ?? throw new PlcSoftwareException("NotFound", "Device item not found: " + node));
            return current;
        }
        public HardwareAddressingReply CompareProjects(string kind, string softwarePath, string[] devicePath, string[] itemPath, string targetProjectName, string targetSoftwarePath,
            string[] targetDevicePath, string[] targetItemPath, string targetLibraryName = "", bool includeIdentical = false, int maxDepth = 8, int offset = 0, int limit = 100, bool sameDevicePaths = false)
            => RunEngineeringServicesStep("CompareProjects", meta => {
                meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false;
                EngineeringServicesPolicy.Comparison(kind, softwarePath, devicePath, targetProjectName, targetSoftwarePath, targetDevicePath, sameDevicePaths);
                EngineeringEvidence.ValidatePage(offset, limit); EngineeringEvidence.ValidateDepth(maxDepth);
                meta["kind"] = kind; meta["scope"] = "Offline native comparison (PlcSoftware.CompareTo / HardwareObject.CompareTo / CompareToLibrary target); no online access, nothing modified.";
                EngineeringProject? other = string.IsNullOrWhiteSpace(targetProjectName) ? null : ExactOpenProject(targetProjectName);
                if (other != null && ReferenceEquals(other, project)) throw new ArgumentException("targetProjectName is the bound project; use targetSoftwarePath/targetDevicePath without targetProjectName.");
                if (other != null) meta["targetProject"] = new Dictionary<string, object?> { ["name"] = other.Name, ["path"] = other.Path?.FullName };
                object subject, target; string method;
                if (kind == "hardware")
                {
                    subject = ExactHardware(devicePath, itemPath);
                    target = other == null ? ExactHardware(targetDevicePath, targetItemPath) : ExactHardwareInProject(other, targetDevicePath, targetItemPath);
                    method = "CompareTo";
                }
                else
                {
                    subject = EngineeringPlcSession.ExactPlcForEngineering(softwarePath, false);
                    if (kind == "softwareToLibrary") { target = ExactOpenEngineeringLibrary(targetLibraryName); }
                    else if (other == null) target = EngineeringPlcSession.ExactPlcForEngineering(targetSoftwarePath, false);
                    else target = ((IEngineeringServiceProvider)ExactHardwareInProject(other, targetDevicePath, targetItemPath)).GetService<SoftwareContainer>()?.Software as PlcSoftware
                        ?? throw new PlcSoftwareException("NotFound", "Selected hardware object in the target project hosts no PLC software; address the CPU DeviceItem exactly.");
                    method = "CompareTo";
                }
                if (ReferenceEquals(subject, target)) throw new ArgumentException("Source and target resolve to the same object.");
                meta["source"] = new Dictionary<string, object?> { ["type"] = subject.GetType().FullName, ["name"] = subject.GetType().GetProperty("Name")?.GetValue(subject)?.ToString() };
                meta["target"] = new Dictionary<string, object?> { ["type"] = target.GetType().FullName, ["name"] = target.GetType().GetProperty("Name")?.GetValue(target)?.ToString() };
                return PublishCompareResult(InvokeNativeCompare(subject, method, target), includeIdentical, maxDepth, offset, limit, meta);
            });

        // ---- Settings / custom identity ----
        public HardwareAddressingReply ReadProjectSettings(string folderPath = "", string customIdentityKey = "", int offset = 0, int limit = 100)
            => RunEngineeringServicesStep("GetProjectSettings", meta => {
                meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false;
                EngineeringEvidence.ValidatePage(offset, limit);
                if (portal == null) throw new PlcSoftwareException("InvalidState", "Connect to TIA first.");
                var parts = PlcGroupOperations.Parts(folderPath, true);
                // Typed root (TiaPortal.SettingsFolders -> TiaPortalSettingsFolder.Folders / Settings -> TiaPortalSetting.Name / Value).
                TiaPortalSettingsFolderComposition rootFolders = portal.SettingsFolders; object collection = rootFolders; object? folder = null;
                foreach (var part in parts)
                {
                    folder = PlcGroupOperations.Find(collection, part) ?? throw new PlcSoftwareException("NotFound", "Settings folder not found: " + part);
                    collection = PlcGroupOperations.Get(folder, "Folders");
                }
                meta["folderPath"] = folderPath;
                meta["subFolders"] = PlcGroupOperations.Items(collection).Select(value => (object?)PlcGroupOperations.Get(value, "Name").ToString()).ToList();
                List<object?> rows; bool complete = true;
                if (folder == null) rows = PlcGroupOperations.Items(collection).Select(f => (object?)HardwareScalarEvidence.Read(f)).ToList();
                else
                {
                    var row = HardwareScalarEvidence.Read(folder); meta["folder"] = row; complete &= (bool)row["dataComplete"]!;
                    rows = PlcGroupOperations.Items(PlcGroupOperations.Get(folder, "Settings")).Select(s => (object?)HardwareScalarEvidence.Read(s)).ToList();
                }
                var page = EngineeringEvidence.Page(rows, offset, limit, meta); meta["records"] = page;
                complete &= offset == 0 && page.Count == rows.Count && page.All(r => (bool)((Dictionary<string, object?>)r!)["dataComplete"]!);
                meta["scope"] = folder == null ? "Root TIA Portal settings folders (scalar scope); pass folderPath to list a folder's settings." : "Settings of the exact folder: Name and scalar Value only; non-scalar values are excluded.";
                if (!string.IsNullOrEmpty(customIdentityKey))
                {
                    if (HardwareProjectMissing()) throw new PlcSoftwareException("InvalidState", "customIdentityKey requires a bound project.");
#if ENGINEERING_CUSTOM_IDENTITY
                    var provider = project!.GetService<CustomIdentityProvider>() ?? throw new NotSupportedException("CustomIdentityProvider service unavailable on the project root.");
                    var identity = new Dictionary<string, object?> { ["key"] = customIdentityKey };
                    try { identity["value"] = provider.Get(customIdentityKey); identity["found"] = true; }
                    catch (CustomIdentityNotFoundException) { /* swallow(probe-optional): A missing custom identity is reported as found=false, without failing the settings read. */ identity["value"] = null; identity["found"] = false; }
                    meta["customIdentity"] = identity;
#else
                    throw new NotSupportedException("CustomIdentityProvider requires V16 or later.");
#endif
                }
                meta["apiCallSuccess"] = true; meta["dataComplete"] = complete;
                return "TIA Portal settings read (read-only); nothing modified.";
            }, requiresProject: false);
    }
}
