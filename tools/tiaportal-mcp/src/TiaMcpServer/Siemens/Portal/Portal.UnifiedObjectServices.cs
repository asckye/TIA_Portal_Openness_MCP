using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        private object ExactUnifiedRoot(string softwarePath)
        {
            var hmi = ResolveHmiSoftwareOrThrow(softwarePath);
            if (hmi.GetType().FullName != "Siemens.Engineering.HmiUnified.HmiSoftware") throw new NotSupportedException("Unified HMI required.");
            return hmi;
        }
        public ResponseMessage ReadUnifiedObjectProperties(string softwarePath, string objectPathJson = "[]", int offset = 0, int limit = 100)
            => RunHmiStepTool("ReadUnifiedObjectProperties", meta => {
                if (offset < 0 || limit < 1 || limit > 500) throw new ArgumentException("offset >= 0, limit 1..500 required.");
                var target = EngineeringObjectAddress.Resolve(ExactUnifiedRoot(softwarePath), objectPathJson);
                var items = target is IEnumerable && target is not string ? EngineeringGroupOperations.Items(target).ToArray() : new[] { target };
                var rows = new JsonArray(items.Skip(offset).Take(limit).Select(x => { var row = EngineeringObjectAddress.Read(x); var colors = (JsonObject)UnifiedUiModelLogic.Scalars(x)["values"]!; foreach (var pair in colors) if (row["values"]![pair.Key] == null) ((JsonObject)row["values"]!)[pair.Key] = pair.Value?.DeepClone(); return (JsonNode)row; }).ToArray());
                meta["softwarePath"] = softwarePath; meta["objectPath"] = EngineeringObjectAddress.Parse(objectPathJson);
                meta["scope"] = "Public scalar values (colors as #AARRGGBB) plus property schema. Other complex values are excluded; explicitly address their properties in another request. Live pagination.";
                meta["records"] = rows; meta["expectedCount"] = items.Length; meta["actualCount"] = rows.Count;
                meta["nextOffset"] = offset + rows.Count < items.Length ? offset + rows.Count : (int?)null;
                meta["truncated"] = offset + rows.Count < items.Length; meta["apiCallSuccess"] = true;
                meta["dataComplete"] = offset == 0 && rows.Count == items.Length && rows.All(r => r!["dataComplete"]!.GetValue<bool>());
                meta["fullObjectComplete"] = offset == 0 && rows.Count == items.Length && rows.All(r => r!["fullObjectComplete"]!.GetValue<bool>());
                return "Selected Unified object/collection read; inspect scope, exclusions and pagination.";
            });
        public ResponseMessage UpdateUnifiedObjectProperties(string softwarePath, string objectPathJson, string propertiesJson, bool dryRun = true)
            => RunHmiStepTool("UpdateUnifiedObjectProperties", meta => {
                EngineeringObjectAddress.Parse(objectPathJson);
                var changes = JsonNode.Parse(propertiesJson) as JsonObject ?? throw new ArgumentException("propertiesJson must be an object.");
                if (changes.Count == 0 || changes.ContainsKey("Name") || changes.ContainsKey("Parent")) throw new ArgumentException("Nonempty scalar changes required; renaming/reparenting excluded.");
                using var access = dryRun ? null : AcquireHmiEditAccess();
                var target = EngineeringObjectAddress.Resolve(ExactUnifiedRoot(softwarePath), objectPathJson);
                if (target.GetType().Name == "HmiRuntimeSetting") throw new NotSupportedException("Use UpdateUnifiedRuntimeSettings for root settings and its confirmation/readback flow; this adapter only edits nested runtime settings.");
                // Nested parts (alarm class RaisedState, log Settings/Backup/Segment ...) and System.Drawing.Color leaves are accepted; collections still need their dedicated tools.
                var prepared = UnifiedUiModelLogic.PrepareNested(target.GetType(), changes);
                meta["softwarePath"] = softwarePath; meta["objectPath"] = EngineeringObjectAddress.Parse(objectPathJson);
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["before"] = UnifiedUiModelLogic.Tree(target, 1);
                meta["requestedProperties"] = changes.DeepClone();
                if (!dryRun) { UnifiedUiModelLogic.ApplyNested(target, prepared, meta); meta["after"] = UnifiedUiModelLogic.Tree(target, 1); }
                return dryRun ? "Nested Unified property edit preview (scalars, colors, nested parts); TIA semantics not validated." : "Nested values written and read back. No save, compile or download; partial edits are not rolled back.";
            });
        public ResponseMessage UpdateUnifiedMultilingualProperty(string softwarePath, string objectPathJson, string property, string culture, string rawText, bool dryRun = true)
            => RunHmiStepTool("UpdateUnifiedMultilingualProperty", meta => {
                using var access = dryRun ? null : AcquireHmiEditAccess();
                var target = EngineeringObjectAddress.Resolve(ExactUnifiedRoot(softwarePath), objectPathJson);
                UnifiedMultilingualText.Validate(target, property, culture);
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["objectPath"] = EngineeringObjectAddress.Parse(objectPathJson);
                meta["before"] = UnifiedMultilingualText.Read(EngineeringGroupOperations.Get(target, property));
                if (!dryRun)
                {
                    var result = UnifiedMultilingualText.WriteDetailed(target, property, rawText, culture);
                    meta["result"] = result; meta["mayHaveChanged"] = result["mayHaveChanged"]!.DeepClone();
                    meta["operationSuccess"] = result["verified"]!.DeepClone();
                }
                return dryRun ? "Existing language entry edit preview." : "Inspect verified and nonTargetUnchanged; no automatic save/compile/download.";
            });
        public ResponseMessage ValidateUnifiedObject(string softwarePath, string objectPathJson)
            => RunHmiStepTool("ValidateUnifiedObject", meta => {
                var target = EngineeringObjectAddress.Resolve(ExactUnifiedRoot(softwarePath), objectPathJson);
                var result = EngineeringGroupOperations.Call(target, "Validate", Type.EmptyTypes);
                var rows = new JsonArray();
                foreach (var item in EngineeringGroupOperations.Items(result))
                {
                    var row = EngineeringScalarProperties.Read(item);
                    foreach (var key in new[] { "Errors", "Warnings" })
                    {
                        var values = EngineeringGroupOperations.Get(item, key);
                        row[key] = new JsonArray(EngineeringGroupOperations.Items(values).Select(x => EngineeringScalarProperties.Scalar(x.GetType()) ? EngineeringScalarProperties.Json(x) : EngineeringScalarProperties.Read(x)).ToArray());
                    }
                    rows.Add(row);
                }
                meta["objectPath"] = EngineeringObjectAddress.Parse(objectPathJson); meta["diagnostics"] = rows;
                meta["apiCallSuccess"] = true; meta["actualCount"] = rows.Count;
                meta["validationPassed"] = rows.All(r => r!["Errors"]!.AsArray().Count == 0);
                meta["dataComplete"] = rows.All(r => r!["dataComplete"]!.GetValue<bool>());
                return "Native object Validate completed; inspect validationPassed separately. No compilation or project modification.";
            });
        public ResponseMessage GetUnifiedCrossReferences(string softwarePath, string objectPathJson, string filter = "AllObjects")
            => RunHmiStepTool("GetUnifiedCrossReferences", meta => {
                var target = EngineeringObjectAddress.Resolve(ExactUnifiedRoot(softwarePath), objectPathJson);
                var serviceType = typeof(global::Siemens.Engineering.IEngineeringObject).Assembly.GetType("Siemens.Engineering.CrossReference.CrossReferenceService", true)!;
                var provider = typeof(global::Siemens.Engineering.IEngineeringServiceProvider);
                if (!provider.IsInstanceOfType(target)) throw new NotSupportedException("Selected object is not an engineering service provider.");
                var service = provider.GetMethod("GetService")!.MakeGenericMethod(serviceType).Invoke(target, null)
                    ?? throw new NotSupportedException("CrossReferenceService unavailable on selected object.");
                var method = serviceType.GetMethods().Single(m => m.Name == "GetCrossReferences" && m.GetParameters().Length == 1);
                var filterValue = EngineeringScalarProperties.ConvertValue(JsonValue.Create(filter), method.GetParameters()[0].ParameterType);
                var raw = method.Invoke(service, new[] { filterValue }) ?? throw new InvalidOperationException("Null native result.");
                var rows = new JsonArray(); int visited = 0;
                foreach (var source in EngineeringGroupOperations.Items(EngineeringGroupOperations.Get(raw, "Sources")))
                {
                    if (++visited > 20000) throw new InvalidOperationException("Cross-reference traversal limit; collection not complete.");
                    var s = EngineeringScalarProperties.Read(source); var refs = new JsonArray(); s["references"] = refs;
                    foreach (var reference in EngineeringGroupOperations.Items(EngineeringGroupOperations.Get(source, "References")))
                    {
                        if (++visited > 20000) throw new InvalidOperationException("Cross-reference traversal limit; collection not complete.");
                        var r = EngineeringScalarProperties.Read(reference); var locations = new JsonArray(); r["locations"] = locations;
                        foreach (var location in EngineeringGroupOperations.Items(EngineeringGroupOperations.Get(reference, "Locations")))
                        {
                            if (++visited > 20000) throw new InvalidOperationException("Cross-reference traversal limit; collection not complete.");
                            locations.Add(EngineeringScalarProperties.Read(location));
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
        public ResponseMessage ExportUnifiedEngineeringList(string softwarePath, string category, string name, string destinationDirectory, bool dryRun = true)
            => RunHmiStepTool("ExportUnifiedEngineeringList", meta => {
                if (category != "textLists" && category != "graphicLists" && category != "systemTextLists") throw new ArgumentException("Expected textLists/graphicLists/systemTextLists.");
                if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Exact list name required; unbounded export refused.");
                var collection = EngineeringGroupOperations.Get(ExactUnifiedRoot(softwarePath), UnifiedCollection(category));
                if (EngineeringGroupOperations.Find(collection, name) == null) throw new InvalidOperationException("Exact list not found.");
                var signature = new[] { typeof(DirectoryInfo), typeof(string) };
                if (collection.GetType().GetMethod("Export", signature) == null) throw new NotSupportedException("Native Export(DirectoryInfo,string) unavailable.");
                if (!Path.IsPathRooted(destinationDirectory)) throw new ArgumentException("Absolute export directory required.");
                var dir = new DirectoryInfo(destinationDirectory);
                if (dir.Exists) throw new InvalidOperationException("Use a new export directory; overwrites refused.");
                meta["dryRun"] = dryRun; meta["destination"] = dir.FullName; meta["projectModified"] = false;
                if (dryRun) return "Native export preview; no file written.";
                dir.Create();
                var native = EngineeringGroupOperations.Call(collection, "Export", signature, dir, name);
                meta["apiCallSuccess"] = true;
                var files = EngineeringGroupOperations.Items(native).Select(x => x as FileInfo ?? throw new InvalidOperationException("Unexpected native output entry; export not complete.")).ToArray();
                if (files.Length == 0) throw new InvalidOperationException("Native export returned no files.");
                var records = new JsonArray();
                foreach (var file in files)
                {
                    if (!file.FullName.StartsWith(dir.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !file.Exists || file.Length == 0) throw new InvalidOperationException("Native output missing, empty or outside selected directory.");
                    using var sha = SHA256.Create(); using var stream = file.OpenRead();
                    records.Add(new JsonObject { ["path"] = file.FullName, ["bytes"] = file.Length, ["sha256"] = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant() });
                }
                meta["files"] = records; meta["actualCount"] = files.Length; meta["dataComplete"] = false;
                meta["nativeFilesVerified"] = true; meta["scope"] = "Native files hashed and checked; list entries are not independently compared.";
                return "Native list export files verified. No save/compile/download.";
            });
    }
}
