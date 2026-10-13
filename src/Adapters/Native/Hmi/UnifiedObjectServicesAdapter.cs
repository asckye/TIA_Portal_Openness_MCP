using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Plc;
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;


namespace TiaMcp.Adapters.Native.Hmi
{
    internal sealed class UnifiedObjectServicesAdapter
    {
        private readonly IUnifiedSession _session;

        public UnifiedObjectServicesAdapter(IUnifiedSession session) => _session = session;

        public HmiMessage ReadUnifiedObjectProperties(string softwarePath, string objectPathJson = "[]", int offset = 0, int limit = 100)
            => _session.RunHmiStepTool("GetUnifiedObjectProperties", meta => {
                if (offset < 0 || limit < 1 || limit > 500) throw new ArgumentException("offset >= 0, limit 1..500 required.");
                var target = UnifiedObjectAddress.Resolve(_session.ExactUnifiedRoot(softwarePath), objectPathJson);
                var items = target is IEnumerable && target is not string ? PlcGroupOperations.Items(target).ToArray() : new[] { target };
                var rows = new HmiArray(items.Skip(offset).Take(limit).Select(x => { var row = UnifiedObjectAddress.Read(x); var colors = (HmiObject)UnifiedUiModelLogic.Scalars(x).Field("values")!; foreach (var pair in colors) if (row["values"]!.Field(pair.Key) == null) ((HmiObject)row["values"]!)[pair.Key] = pair.Value?.DeepClone(); return (object)row; }).ToArray());
                meta["softwarePath"] = softwarePath; meta["objectPath"] = UnifiedObjectAddress.Parse(objectPathJson);
                meta["scope"] = "Public scalar values (colors as #AARRGGBB) plus property schema. Other complex values are excluded; explicitly address their properties in another request. Live pagination.";
                meta["records"] = rows; meta["expectedCount"] = items.Length; meta["actualCount"] = rows.Count;
                meta["nextOffset"] = offset + rows.Count < items.Length ? offset + rows.Count : (int?)null;
                meta["truncated"] = offset + rows.Count < items.Length; meta["apiCallSuccess"] = true;
                meta["dataComplete"] = offset == 0 && rows.Count == items.Length && rows.All(r => r!.Field("dataComplete")!.GetValue<bool>());
                meta["fullObjectComplete"] = offset == 0 && rows.Count == items.Length && rows.All(r => r!.Field("fullObjectComplete")!.GetValue<bool>());
                return "Selected Unified object/collection read; inspect scope, exclusions and pagination.";
            });

        public HmiMessage UpdateUnifiedObjectProperties(string softwarePath, string objectPathJson, string propertiesJson, bool dryRun = true)
            => _session.RunHmiStepTool("SetUnifiedObjectProperties", meta => {
                UnifiedObjectAddress.Parse(objectPathJson);
                var changes = UnifiedRequestValues.Parse(propertiesJson) as HmiObject ?? throw new ArgumentException("propertiesJson must be an object.");
                if (changes.Count == 0 || changes.ContainsKey("Name") || changes.ContainsKey("Parent")) throw new ArgumentException("Nonempty scalar changes required; renaming/reparenting excluded.");
                using var access = dryRun ? null : _session.AcquireHmiEditAccess();
                var target = UnifiedObjectAddress.Resolve(_session.ExactUnifiedRoot(softwarePath), objectPathJson);
                if (target.GetType().Name == "HmiRuntimeSetting") throw new NotSupportedException("Use SetUnifiedRuntimeSettings for root settings and its confirmation/readback flow; this adapter only edits nested runtime settings.");
                // Nested parts (alarm class RaisedState, log Settings/Backup/Segment ...) and System.Drawing.Color leaves are accepted; collections still need their dedicated tools.
                var prepared = UnifiedUiModelLogic.PrepareNested(target.GetType(), changes);
                meta["softwarePath"] = softwarePath; meta["objectPath"] = UnifiedObjectAddress.Parse(objectPathJson);
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["before"] = UnifiedUiModelLogic.Tree(target, 1);
                meta["requestedProperties"] = changes.DeepClone();
                if (!dryRun) { UnifiedUiModelLogic.ApplyNested(target, prepared, meta); meta["after"] = UnifiedUiModelLogic.Tree(target, 1); }
                return dryRun ? "Nested Unified property edit preview (scalars, colors, nested parts); TIA semantics not validated." : "Nested values written and read back. No save, compile or download; partial edits are not rolled back.";
            });

        public HmiMessage UpdateUnifiedMultilingualProperty(string softwarePath, string objectPathJson, string property, string culture, string rawText, bool dryRun = true)
            => _session.RunHmiStepTool("SetUnifiedMultilingualProperty", meta => {
                using var access = dryRun ? null : _session.AcquireHmiEditAccess();
                var target = UnifiedObjectAddress.Resolve(_session.ExactUnifiedRoot(softwarePath), objectPathJson);
                UnifiedMultilingualText.Validate(target, property, culture);
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["objectPath"] = UnifiedObjectAddress.Parse(objectPathJson);
                meta["before"] = UnifiedMultilingualText.Read(PlcGroupOperations.Get(target, property));
                if (!dryRun)
                {
                    var result = UnifiedMultilingualText.WriteDetailed(target, property, rawText, culture);
                    meta["result"] = result; meta["mayHaveChanged"] = result["mayHaveChanged"]!.DeepClone();
                    meta["operationSuccess"] = result["verified"]!.DeepClone();
                }
                return dryRun ? "Existing language entry edit preview." : "Inspect verified and nonTargetUnchanged; no automatic save/compile/download.";
            });

        public HmiMessage ValidateUnifiedObject(string softwarePath, string objectPathJson)
            => _session.RunHmiStepTool("ValidateUnifiedObject", meta => {
                var target = UnifiedObjectAddress.Resolve(_session.ExactUnifiedRoot(softwarePath), objectPathJson);
                var result = PlcGroupOperations.Call(target, "Validate", Type.EmptyTypes);
                var rows = new HmiArray();
                foreach (var item in PlcGroupOperations.Items(result))
                {
                    var row = UnifiedRequestScalar.Read(item);
                    foreach (var key in new[] { "Errors", "Warnings" })
                    {
                        var values = PlcGroupOperations.Get(item, key);
                        row[key] = new HmiArray(PlcGroupOperations.Items(values).Select(x => UnifiedRequestScalar.Scalar(x.GetType()) ? UnifiedRequestScalar.Json(x) : UnifiedRequestScalar.Read(x)).ToArray());
                    }
                    rows.Add(row);
                }
                meta["objectPath"] = UnifiedObjectAddress.Parse(objectPathJson); meta["diagnostics"] = rows;
                meta["apiCallSuccess"] = true; meta["actualCount"] = rows.Count;
                meta["validationPassed"] = rows.All(r => r!.Field("Errors")!.Array().Count == 0);
                meta["dataComplete"] = rows.All(r => r!.Field("dataComplete")!.GetValue<bool>());
                return "Native object Validate completed; inspect validationPassed separately. No compilation or project modification.";
            });

        public HmiMessage GetUnifiedCrossReferences(string softwarePath, string objectPathJson, string filter = "AllObjects")
            => _session.RunHmiStepTool("GetUnifiedCrossReferences", meta => {
                var target = UnifiedObjectAddress.Resolve(_session.ExactUnifiedRoot(softwarePath), objectPathJson);
                var serviceType = typeof(global::Siemens.Engineering.IEngineeringObject).Assembly.GetType("Siemens.Engineering.CrossReference.CrossReferenceService", true)!;
                var provider = typeof(global::Siemens.Engineering.IEngineeringServiceProvider);
                if (!provider.IsInstanceOfType(target)) throw new NotSupportedException("Selected object is not an engineering service provider.");
                var service = provider.GetMethod("GetService")!.MakeGenericMethod(serviceType).Invoke(target, null)
                    ?? throw new NotSupportedException("CrossReferenceService unavailable on selected object.");
                var method = serviceType.GetMethods().Single(m => m.Name == "GetCrossReferences" && m.GetParameters().Length == 1);
                var filterValue = UnifiedRequestScalar.ConvertValue(HmiValues.Value(filter), method.GetParameters()[0].ParameterType);
                var raw = method.Invoke(service, new[] { filterValue }) ?? throw new InvalidOperationException("Null native result.");
                var rows = new HmiArray(); int visited = 0;
                foreach (var source in PlcGroupOperations.Items(PlcGroupOperations.Get(raw, "Sources")))
                {
                    if (++visited > 20000) throw new InvalidOperationException("Cross-reference traversal limit; collection not complete.");
                    var s = UnifiedRequestScalar.Read(source); var refs = new HmiArray(); s["references"] = refs;
                    foreach (var reference in PlcGroupOperations.Items(PlcGroupOperations.Get(source, "References")))
                    {
                        if (++visited > 20000) throw new InvalidOperationException("Cross-reference traversal limit; collection not complete.");
                        var r = UnifiedRequestScalar.Read(reference); var locations = new HmiArray(); r["locations"] = locations;
                        foreach (var location in PlcGroupOperations.Items(PlcGroupOperations.Get(reference, "Locations")))
                        {
                            if (++visited > 20000) throw new InvalidOperationException("Cross-reference traversal limit; collection not complete.");
                            locations.Add(UnifiedRequestScalar.Read(location));
                        }
                        refs.Add(r);
                    }
                    rows.Add(s);
                }
                meta["records"] = rows; meta["actualCount"] = rows.Count; meta["traversalComplete"] = true;
                meta["apiCallSuccess"] = true; meta["dataComplete"] = false;
                meta["scope"] = "Decoded native cross references. Completeness of dynamic script names and every native result field is not asserted.";
                return "Unified native cross references read; no script execution or runtime expression expansion.";
            });

        public HmiMessage ExportUnifiedEngineeringList(string softwarePath, string category, string name, string destinationDirectory, bool dryRun = true)
            => _session.RunHmiStepTool("ExportUnifiedEngineeringList", meta => {
                if (category != "textLists" && category != "graphicLists" && category != "systemTextLists") throw new ArgumentException("Expected textLists/graphicLists/systemTextLists.");
                if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Exact list name required; unbounded export refused.");
                var collection = PlcGroupOperations.Get(_session.ExactUnifiedRoot(softwarePath), _session.UnifiedCollection(category));
                if (PlcGroupOperations.Find(collection, name) == null) throw new InvalidOperationException("Exact list not found.");
                var signature = new[] { typeof(DirectoryInfo), typeof(string) };
                if (collection.GetType().GetMethod("Export", signature) == null) throw new NotSupportedException("Native Export(DirectoryInfo,string) unavailable.");
                if (!Path.IsPathRooted(destinationDirectory)) throw new ArgumentException("Absolute export directory required.");
                var dir = new DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(destinationDirectory));
                if (dir.Exists) throw new InvalidOperationException("Use a new export directory; overwrites refused.");
                meta["dryRun"] = dryRun; meta["destination"] = dir.FullName; meta["projectModified"] = false;
                if (dryRun) return "Native export preview; no file written.";
                dir.Create();
                var native = PlcGroupOperations.Call(collection, "Export", signature, dir, name);
                meta["apiCallSuccess"] = true;
                var files = PlcGroupOperations.Items(native).Select(x => x as FileInfo ?? throw new InvalidOperationException("Unexpected native output entry; export not complete.")).ToArray();
                if (files.Length == 0) throw new InvalidOperationException("Native export returned no files.");
                var records = new HmiArray();
                foreach (var file in files)
                {
                    if (!file.FullName.StartsWith(dir.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !file.Exists || file.Length == 0) throw new InvalidOperationException("Native output missing, empty or outside selected directory.");
                    using var sha = SHA256.Create(); using var stream = file.OpenRead();
                    records.Add(new HmiObject { ["path"] = file.FullName, ["bytes"] = file.Length, ["sha256"] = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant() });
                }
                meta["files"] = records; meta["actualCount"] = files.Length; meta["dataComplete"] = false;
                meta["nativeFilesVerified"] = true; meta["scope"] = "Native files hashed and checked; list entries are not independently compared.";
                return "Native list export files verified. No save/compile/download.";
            });

        public HmiMessage ManageUnifiedLoggingTag(string softwarePath, string tagPathJson, string action = "read", string name = "", string propertiesJson = "{}", bool dryRun = true)
            => _session.RunHmiStepTool("ManageUnifiedLoggingTag", meta => {
                if (!new[] { "read", "create", "update", "delete" }.Contains(action)) throw new ArgumentException("action must be one of: read/create/update/delete (case-sensitive).");
                bool write = action != "read" && !dryRun;
                using var access = write ? _session.AcquireHmiEditAccess() : null;
                var root = _session.ExactUnifiedRoot(softwarePath); var tag = UnifiedObjectAddress.Resolve(root, tagPathJson);
                if (tag.GetType().FullName != "Siemens.Engineering.HmiUnified.HmiTags.HmiTag") throw new ArgumentException("tagPathJson must identify one ordinary Unified tag.");
                var collection = PlcGroupOperations.Get(tag, "LoggingTags");
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["tagPath"] = UnifiedObjectAddress.Parse(tagPathJson);
                if (action == "read")
                {
                    var items = string.IsNullOrEmpty(name) ? PlcGroupOperations.Items(collection).ToArray() : new[] { PlcGroupOperations.Find(collection, name) ?? throw new InvalidOperationException("Logging tag not found.") };
                    meta["records"] = new HmiArray(items.Select(x => (object)UnifiedObjectAddress.Read(x)).ToArray());
                    meta["apiCallSuccess"] = true; meta["dataComplete"] = false; meta["scope"] = "Scalar fields only; complex definitions excluded.";
                    meta["actualCount"] = items.Length; meta["expectedCount"] = items.Length; meta["truncated"] = false;
                    return "Logging tags read in scalar scope; inspect exclusions.";
                }
                if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Exact logging tag name required.");
                var target = PlcGroupOperations.Find(collection, name);
                if ((action == "create") == (target != null)) throw new InvalidOperationException(action == "create" ? "Logging tag exists." : "Logging tag not found.");
                var changes = UnifiedRequestValues.Parse(propertiesJson) as HmiObject ?? throw new ArgumentException("propertiesJson must be an object.");
                if (changes.ContainsKey("Name") || (action == "delete" && changes.Count != 0)) throw new ArgumentException("Renaming or properties on delete are not supported.");
                if (changes.ContainsKey("DataLog") && PlcGroupOperations.Find(PlcGroupOperations.Get(root, "DataLogs"), changes["DataLog"]!.GetValue<string>()) == null) throw new InvalidOperationException("Requested DataLog does not exist.");
                var type = target?.GetType() ?? collection.GetType().GetMethod("Create", new[] { typeof(string) })?.ReturnType ?? throw new NotSupportedException("Native LoggingTag.Create unavailable.");
                var prepared = UnifiedRequestScalar.Prepare(type, changes);
                meta["requestedProperties"] = changes.DeepClone(); if (target != null) meta["before"] = UnifiedObjectAddress.Read(target);
                if (!write) return "Logging tag preview. TimeSpan properties use invariant c format; TIA semantics checked only on execution.";
                meta["mayHaveChanged"] = true;
                if (action == "create") target = PlcGroupOperations.Call(collection, "Create", new[] { typeof(string) }, name);
                if (action == "delete")
                {
                    PlcGroupOperations.Call(target!, "Delete", Type.EmptyTypes);
                    if (PlcGroupOperations.Find(collection, name) != null) throw new InvalidOperationException("Logging tag remains after deletion.");
                    meta["verifiedAbsent"] = true;
                }
                else { UnifiedRequestScalar.Apply(target!, prepared, meta); meta["after"] = UnifiedObjectAddress.Read(target!); }
                return "Logging tag operation completed and read back; no save/compile/download.";
            });

        public HmiMessage SetUnifiedLogDuration(string softwarePath, string durationPathJson, string kind, uint days, uint hours, uint minutes, uint seconds, uint hundredNanoseconds, bool dryRun = true)
            => _session.RunHmiStepTool("SetUnifiedLogDuration", meta => {
                if (kind != "log" && kind != "segment") throw new ArgumentException("kind must be log/segment.");
                if (hours > 23 || minutes > 59 || seconds > 59 || hundredNanoseconds > 9999999) throw new ArgumentException("Use normalized time components.");
                using var access = dryRun ? null : _session.AcquireHmiEditAccess();
                var target = UnifiedObjectAddress.Resolve(_session.ExactUnifiedRoot(softwarePath), durationPathJson);
                var expected = "Siemens.Engineering.HmiUnified.HmiLogging.HmiLoggingCommon." + (kind == "log" ? "LogDuration" : "SegmentDuration");
                if (target.GetType().FullName != expected) throw new NotSupportedException("Path must identify " + expected);
                string suffix = kind == "log" ? "LogDuration" : "SegmentDuration";
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                meta["before"] = UnifiedRequestScalar.Json(PlcGroupOperations.Call(target, "GetString" + suffix, Type.EmptyTypes));
                if (!dryRun)
                {
                    meta["mayHaveChanged"] = true;
                    PlcGroupOperations.Call(target, "Set" + suffix, Enumerable.Repeat(typeof(uint), 5).ToArray(), days, hours, minutes, seconds, hundredNanoseconds);
                    meta["after"] = UnifiedRequestScalar.Json(PlcGroupOperations.Call(target, "GetString" + suffix, Type.EmptyTypes));
                    meta["numericReadback"] = UnifiedRequestScalar.Json(PlcGroupOperations.Call(target, "GetDouble" + suffix, Type.EmptyTypes));
                    meta["exactValueVerified"] = false;
                }
                return dryRun ? "Duration edit preview." : "Native duration setter completed; string/numeric readback supplied, independent unit conversion not asserted. No save/compile/download.";
            });

        public HmiMessage ManageUnifiedOpcUaAlarmType(string softwarePath, string name, string nodeId, string connection, string action = "create", bool dryRun = true)
            => _session.RunHmiStepTool("ManageUnifiedOpcUaAlarmType", meta => {
                if (action != "create" && action != "update") throw new ArgumentException("create/update only; read/delete use existing engineering-object tools.");
                if (new[] { name, nodeId, connection }.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Exact name, nodeId and connection required.");
                using var access = dryRun ? null : _session.AcquireHmiEditAccess();
                var root = _session.ExactUnifiedRoot(softwarePath);
                if (PlcGroupOperations.Find(PlcGroupOperations.Get(root, "Connections"), connection) == null) throw new InvalidOperationException("Exact HMI connection not found.");
                var collection = PlcGroupOperations.Get(root, "OpcUaAlarmTypes"); var target = PlcGroupOperations.Find(collection, name);
                if ((action == "create") == (target != null)) throw new InvalidOperationException(action == "create" ? "Alarm type exists." : "Alarm type not found.");
                var nativeMethod = action == "create" ? collection.GetType().GetMethod("Create", new[] { typeof(string), typeof(string), typeof(string) }) : target!.GetType().GetMethod("SetNodeIdAndConnection", new[] { typeof(string), typeof(string) });
                if (nativeMethod == null) throw new NotSupportedException("Native OPC UA alarm binding method unavailable.");
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["name"] = name; meta["nodeId"] = nodeId; meta["connection"] = connection;
                if (!dryRun)
                {
                    meta["mayHaveChanged"] = true;
                    if (action == "create") target = PlcGroupOperations.Call(collection, "Create", new[] { typeof(string), typeof(string), typeof(string) }, nodeId, connection, name);
                    else PlcGroupOperations.Call(target!, "SetNodeIdAndConnection", new[] { typeof(string), typeof(string) }, nodeId, connection);
                    meta["after"] = UnifiedObjectAddress.Read(target!); meta["exactBindingVerified"] = false;
                }
                return dryRun ? "OPC UA alarm type edit preview." : "Native binding operation completed; readback supplied, inspect binding fields. No save/compile/download.";
            });

        private object ExactPlantViews() => _session.ExactPlantViews();

        private object ExactPlantNode(string path)
        {
            var parts=PlcGroupOperations.Parts(path);
            object current=PlcGroupOperations.Find(ExactPlantViews(),parts[0]) ?? throw new InvalidOperationException("Exact plant view not found.");
            foreach(var part in parts.Skip(1)) current=PlcGroupOperations.Find(PlcGroupOperations.Get(current,"PlantViewNodes"),part) ?? throw new InvalidOperationException("Exact plant node not found: "+part);
            return current;
        }

        public HmiMessage ReadUnifiedPlantObject(string plantPath="", string objectPathJson="[]", int offset=0, int limit=100)
            => _session.RunHmiStepTool("GetUnifiedPlantObject", meta => {
                if(offset<0 || limit<1 || limit>500) throw new ArgumentException("offset >= 0, limit 1..500 required.");
                var root=string.IsNullOrEmpty(plantPath) ? ExactPlantViews() : ExactPlantNode(plantPath);
                var target=UnifiedObjectAddress.Resolve(root,objectPathJson);
                var items=target is IEnumerable && target is not string ? PlcGroupOperations.Items(target).ToArray() : new[]{target};
                var rows=new HmiArray(items.Skip(offset).Take(limit).Select(x=>(object)UnifiedObjectAddress.Read(x)).ToArray());
                meta["plantPath"]=plantPath; meta["objectPath"]=UnifiedObjectAddress.Parse(objectPathJson);
                meta["records"]=rows; meta["expectedCount"]=items.Length; meta["actualCount"]=rows.Count;
                meta["nextOffset"]=offset+rows.Count<items.Length ? offset+rows.Count : (int?)null;
                meta["truncated"]=offset+rows.Count<items.Length; meta["apiCallSuccess"]=true; meta["dataComplete"]=false;
                meta["scope"]="Live page, scalar values and schema only. PlantObject/PlantObjectInterfaces and members require exact subsequent property paths; no implicit recursion.";
                return "Project plant view/CPM object read in bounded scalar scope.";
            });

        public HmiMessage ManageUnifiedPlantNode(string plantPath, string action, string plantObjectType="", string propertiesJson="{}", bool dryRun=true)
            => _session.RunHmiStepTool("ManageUnifiedPlantNode", meta => {
                if(!new[]{"create","update","delete"}.Contains(action)) throw new ArgumentException("action must be create/update/delete.");
                var parts=PlcGroupOperations.Parts(plantPath);
                using var access=dryRun ? null : _session.AcquireHmiEditAccess();
                var collection=parts.Length==1 ? ExactPlantViews() : PlcGroupOperations.Get(ExactPlantNode(string.Join("/",parts.Take(parts.Length-1))),"PlantViewNodes");
                var target=PlcGroupOperations.Find(collection,parts.Last());
                if((action=="create")== (target!=null)) throw new InvalidOperationException(action=="create" ? "Plant object exists." : "Exact plant object not found.");
                if(plantObjectType!="" && (parts.Length==1 || action!="create")) throw new ArgumentException("plantObjectType only applies to creating a child CPM instance.");
                var changes=UnifiedRequestValues.Parse(propertiesJson) as HmiObject ?? throw new ArgumentException("propertiesJson must be an object.");
                if(changes.ContainsKey("Name") || (action=="delete" && changes.Count!=0)) throw new ArgumentException("Rename and properties during delete are excluded.");
                if(action=="delete" && PlcGroupOperations.Items(PlcGroupOperations.Get(target!,"PlantViewNodes")).Any()) throw new InvalidOperationException("Only leaf nodes or empty plant views can be deleted; subtree deletion refused.");
                var signature=plantObjectType=="" ? new[]{typeof(string)} : new[]{typeof(string),typeof(string)};
                var type=target?.GetType() ?? collection.GetType().GetMethod("Create",signature)?.ReturnType ?? throw new NotSupportedException("Native plant creation unavailable.");
                var prepared=UnifiedRequestScalar.Prepare(type,changes);
                meta["plantPath"]=plantPath; meta["plantObjectType"]=plantObjectType; meta["dryRun"]=dryRun; meta["mayHaveChanged"]=false;
                if(target!=null) meta["before"]=UnifiedObjectAddress.Read(target);
                if(dryRun) return "Plant view/node preview. Native CPM type compatibility is not validated until execution.";
                meta["mayHaveChanged"]=true;
                if(action=="create") target=plantObjectType=="" ? PlcGroupOperations.Call(collection,"Create",signature,parts.Last()) : PlcGroupOperations.Call(collection,"Create",signature,parts.Last(),plantObjectType);
                if(action=="delete") {
                    PlcGroupOperations.Call(target!,"Delete",Type.EmptyTypes);
                    if(PlcGroupOperations.Find(collection,parts.Last())!=null) throw new InvalidOperationException("Plant object remains after delete.");
                    meta["verifiedAbsent"]=true;
                } else { UnifiedRequestScalar.Apply(target!,prepared,meta); meta["after"]=UnifiedObjectAddress.Read(target!); }
                return "Plant view/node operation completed. No save/compile/download; referenced CPM semantics require separate verification.";
            });

        public HmiMessage UpdateUnifiedPlantObject(string plantPath, string objectPathJson, string propertiesJson, bool dryRun=true)
            => _session.RunHmiStepTool("SetUnifiedPlantObject", meta => {
                using var access=dryRun ? null : _session.AcquireHmiEditAccess();
                var target=UnifiedObjectAddress.Resolve(ExactPlantNode(plantPath),objectPathJson);
                var changes=UnifiedRequestValues.Parse(propertiesJson) as HmiObject ?? throw new ArgumentException("propertiesJson must be an object.");
                if(changes.Count==0 || changes.ContainsKey("Name")) throw new ArgumentException("Nonempty properties required; renaming excluded.");
                var prepared=UnifiedRequestScalar.Prepare(target.GetType(),changes);
                meta["dryRun"]=dryRun; meta["mayHaveChanged"]=false; meta["plantPath"]=plantPath; meta["objectPath"]=UnifiedObjectAddress.Parse(objectPathJson);
                meta["before"]=UnifiedObjectAddress.Read(target);
                if(!dryRun) {UnifiedRequestScalar.Apply(target,prepared,meta);meta["after"]=UnifiedObjectAddress.Read(target);}
                return dryRun ? "Exact CPM scalar edit preview." : "CPM scalar properties changed and read back; no save/compile/download.";
            });
    }
}
