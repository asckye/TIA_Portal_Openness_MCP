using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.TechnologicalObjects;
using Siemens.Engineering.SW.TechnologicalObjects.Motion;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using Logic = TiaMcp.Adapters.Native.Plc.PlcMotionPolicy;
using WatchNative = TiaMcp.Adapters.WatchTechnologyPrimitives;

namespace TiaMcp.Adapters.Native.Plc
{
    public sealed partial class PlcOrganisationAdapter
    {
        public HardwareAddressingReply ManageTechnologyObject(string softwarePath, string objectPath, string action, string typeIdentifier,
            string version, string parameter, HardwareScalar value, string valueError, bool dryRun = true)
            => _session.RunHmiStepTool("ManageTechnologyObject", meta => {
                meta["mayHaveChanged"] = false;
                if (!new[] { "read", "create", "delete", "setParameter" }.Contains(action)) throw new ArgumentException("action must be read/create/delete/setParameter.");
                var parts = PlcGroupOperations.Parts(objectPath);
                bool writing = action != "read" && !dryRun;
                using var access = writing ? _session.AcquireHmiEditAccess() : null;
                var plc = _session.ExactPlcForEngineering(softwarePath, writing);
                var group = PlcGroupOperations.Group(WatchNative.TechnologyGroup(plc), string.Join("/", parts.Take(parts.Length - 1)));
                var collection = PlcGroupOperations.Get(group, "TechnologicalObjects");
                var target = PlcGroupOperations.Find(collection, parts.Last());
                meta["objectPath"] = objectPath; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (action == "create")
                {
                    if (target != null) throw new InvalidOperationException("Technology object already exists.");
                    if (string.IsNullOrWhiteSpace(typeIdentifier)) throw new ArgumentException("Official technology type identifier required.");
                    // Validate the technology version before parsing. Native observation: 1515F FW V2.9 uses TO V6.0.
                    // TIA version/date were not recorded; see docs/reference/real-machine-ledger.md.
                    if (!Version.TryParse(version, out var apiVersion) || apiVersion.Major < 1)
                        throw new ArgumentException("version must be the technology object version as 'major.minor', e.g. \"6.0\" (TIA V17 / FW 2.9), \"7.0\" (FW 3.0), \"8.0\" (FW 3.1); got '" + version + "'. TIA rejects a version the CPU firmware does not support.");
                    meta["typeIdentifier"] = typeIdentifier; meta["version"] = version;
                    if (writing)
                    {
                        meta["mayHaveChanged"] = true; MutationStarted = true;
                        // Typed TechnologicalInstanceDBComposition.Create(name, typeIdentifier, version) with Find readback.
                        if (collection is TechnologicalInstanceDBComposition typedCollection)
                        {
                            TechnologicalInstanceDB created = WatchNative.Create(typedCollection, parts.Last(), typeIdentifier, apiVersion);
                            if (WatchNative.Find(typedCollection, parts.Last()) == null) throw new InvalidOperationException("Technology object not found by readback after Create.");
                            target = created; meta["after"] = TechnologyObjectRow(created);
                        }
                        else
                        {
                            target = PlcGroupOperations.Call(collection, "Create", new[] { typeof(string), typeof(string), typeof(Version) }, parts.Last(), typeIdentifier, apiVersion);
                            meta["after"] = HardwareScalarEvidence.Read(target);
                        }
                    }
                }
                else
                {
                    if (target == null) throw new InvalidOperationException("Exact technology object not found.");
                    if (action == "delete")
                    {
                        meta["before"] = HardwareScalarEvidence.Read(target);
                        if (writing)
                        {
                            meta["mayHaveChanged"] = true; MutationStarted = true;
                            PlcGroupOperations.Call(target, "Delete", Type.EmptyTypes);
                            if (PlcGroupOperations.Find(collection, parts.Last()) != null) throw new InvalidOperationException("Object remains after Delete.");
                            meta["verifiedAbsent"] = true;
                        }
                    }
                    else
                    {
                        var parameters = PlcGroupOperations.Get(target, "Parameters");
                        TechnologicalParameterComposition? typedParameters = parameters as TechnologicalParameterComposition;
                        if (target is TechnologicalInstanceDB typedObject) meta["object"] = TechnologyObjectRow(typedObject);
                        if (string.IsNullOrEmpty(parameter))
                        {
                            if (action == "setParameter") throw new ArgumentException("Exact parameter name required.");
                            var all = PlcGroupOperations.Items(parameters).Select(x => x is TechnologicalParameter tp ? ParameterRow(tp) : HardwareScalarEvidence.Read(x)).ToArray();
                            meta["parameters"] = all.Cast<object?>().ToArray();
                            meta["actualCount"] = all.Length; meta["dataComplete"] = all.All(x => (x.TryGetValue("dataComplete", out var complete) ? complete as bool? : null) ?? !x.ContainsKey("valueError"));
                        }
                        else
                        {
                            // Typed TechnologicalParameterComposition.Find(name) with the reflective lookup as fallback.
                            var p = (typedParameters != null ? WatchNative.Find(typedParameters, parameter) : null) ?? PlcGroupOperations.Find(parameters, parameter) ?? throw new InvalidOperationException("Parameter not found: " + parameter);
                            meta["before"] = p is TechnologicalParameter typedBefore ? ParameterRow(typedBefore) : HardwareScalarEvidence.Read(p);
                            if (action == "setParameter")
                            {
                                if (valueError.Length != 0) throw new ArgumentException(valueError);
                                var edits = HardwareAddressingPolicy.Prepare(p.GetType(), new Dictionary<string, HardwareScalar> { ["Value"] = value });
                                var existingValue = p.GetType().GetProperty("Value")!.GetValue(p);
                                if (existingValue != null && edits[0].Property.PropertyType == typeof(object))
                                    edits[0] = (edits[0].Property, HardwareAddressingPolicy.ConvertValue(value, existingValue.GetType()));
                                meta["requestedValue"] = HardwareAddressingPolicy.ConvertValue(value, typeof(object));
                                if (writing) { MutationStarted = true; HardwareScalarEvidence.Apply(p, edits, meta); meta["after"] = p is TechnologicalParameter typedAfter ? ParameterRow(typedAfter) : HardwareScalarEvidence.Read(p); }
                            }
                        }
                    }
                }
                return writing ? "Native technology object operation completed; no save/compile/download." : "Technology object read/preview completed; no changes.";
            });

        // ---- ReadTechnologyObjectTree ---------------------------------------------------------------------------------------------------
        private Dictionary<string, object?> TechnologyGroupRow(TechnologicalInstanceDBGroup group, bool includeParameters, int depth, int maxDepth)
        {
            TechnologicalInstanceDBComposition objects = WatchNative.Objects(group);
#if PLC_TECH_GROUP_READ
            TechnologicalInstanceDBUserGroupComposition groups = WatchNative.Groups(group);
            int groupCount = WatchNative.Count(groups);
#else
            int groupCount = 0;
#endif
            var row = new Dictionary<string, object?> { ["name"] = WatchNative.Name(group), ["groupClass"] = group.GetType().Name, ["objectCount"] = WatchNative.Count(objects), ["groupCount"] = groupCount };
            row["technologicalObjects"] = PlcGroupOperations.Items(objects).Cast<TechnologicalInstanceDB>().Take(200).Select(db =>
            {
                var o = TechnologyObjectRow(db);
                if (includeParameters) Safe(o, "parameters", () => { TechnologicalParameterComposition parameters = WatchNative.Parameters(db); return PlcGroupOperations.Items(parameters).Cast<TechnologicalParameter>().Take(500).Select(p => (object?)ParameterRow(p)).ToArray(); });
                return (object?)o;
            }).ToArray();
#if PLC_TECH_GROUP_READ
            if (depth < maxDepth) row["groups"] = PlcGroupOperations.Items(groups).Cast<TechnologicalInstanceDBUserGroup>().Select(g => (object?)TechnologyGroupRow(g, includeParameters, depth + 1, maxDepth)).ToArray();
            else row["groupsTruncated"] = WatchNative.Count(groups) > 0;
#else
            if (depth < maxDepth) row["groups"] = Array.Empty<object?>();
            else row["groupsTruncated"] = false;
            row["groupsCapability"] = "requiresV19";
#endif
            return row;
        }
        public HardwareAddressingReply ReadTechnologyObjectTree(string softwarePath, string groupPath = "", bool includeParameters = false, bool includeMotionView = false, int maxDepth = 4)
            => _session.RunHmiStepTool("GetTechnologyObjectTree", meta => {
                meta["mayHaveChanged"] = false;
                if (maxDepth < 1 || maxDepth > 16) throw new ArgumentException("maxDepth 1..16 required.");
                var plc = _session.ExactPlcForEngineering(softwarePath, false);
                TechnologicalInstanceDBGroup root = (TechnologicalInstanceDBGroup)PlcGroupOperations.Group(WatchNative.TechnologyGroup(plc), groupPath);
                meta["groupPath"] = groupPath; meta["tree"] = TechnologyGroupRow(root, includeParameters, 1, maxDepth);
#if !PLC_TECH_GROUP_READ
                meta["dataComplete"] = false;
                meta["versionLimitations"] = "Technology-object user groups require V19; this release reads root objects only.";
#endif
                if (includeMotionView)
                {
                    var views = new Dictionary<string, object?>();
                    foreach (TechnologicalInstanceDB db in PlcGroupOperations.Items(WatchNative.Objects(root)).Cast<TechnologicalInstanceDB>().Take(50)) Safe(views, WatchNative.Name(db), () => TypedMotionView(db));
                    meta["motionViews"] = views;
                }
                meta["apiCallSuccess"] = true;
                meta["scope"] = "TechnologicalInstanceDBGroup Name / TechnologicalObjects (TechnologicalInstanceDB Name, Number, OfSystemLibElement, OfSystemLibVersion, IsConsistent, parameter count) / Groups recursive to maxDepth; includeParameters adds TechnologicalParameter Name / Value (first 500 per object); includeMotionView adds the typed hardware interfaces, master values and mappings of the root group's objects (first 50). No modification.";
                return "Technology object tree read; no modification.";
            });
    }
}
