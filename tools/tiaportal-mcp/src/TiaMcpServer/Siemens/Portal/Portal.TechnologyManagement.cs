using System;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering.SW;
using Siemens.Engineering.Online;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        private PlcSoftware ExactPlcForEngineering(string softwarePath, bool writing)
        {
            var plc = ResolveSoftwareContainerUncached(softwarePath)?.Software as PlcSoftware
                ?? throw new PortalException(PortalErrorCode.NotFound, "Exact PLC software not found: " + softwarePath);
            if (writing && ResolvePlcService<OnlineProvider>(softwarePath, plc)?.State.ToString() != "Offline")
                throw new PortalException(PortalErrorCode.InvalidState, "Confirmed Offline state is required.");
            return plc;
        }
        public ResponseMessage ManageTechnologyObject(string softwarePath, string objectPath, string action, string typeIdentifier = "",
            string version = "", string parameter = "", string valueJson = "null", bool dryRun = true)
            => RunHmiStepTool("ManageTechnologyObject", meta => {
                if (!new[] { "read", "create", "delete", "setParameter" }.Contains(action)) throw new ArgumentException("action must be read/create/delete/setParameter.");
                var parts = EngineeringGroupOperations.Parts(objectPath);
                bool writing = action != "read" && !dryRun;
                using var access = writing ? AcquireHmiEditAccess() : null;
                var plc = ExactPlcForEngineering(softwarePath, writing);
                var group = EngineeringGroupOperations.Group(plc.TechnologicalObjectGroup, string.Join("/", parts.Take(parts.Length - 1)));
                var collection = EngineeringGroupOperations.Get(group, "TechnologicalObjects");
                var target = EngineeringGroupOperations.Find(collection, parts.Last());
                meta["objectPath"] = objectPath; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (action == "create")
                {
                    if (target != null) throw new InvalidOperationException("Technology object already exists.");
                    if (string.IsNullOrWhiteSpace(typeIdentifier)) throw new ArgumentException("Official technology type identifier required.");
                    var apiVersion = Version.Parse(version);
                    meta["typeIdentifier"] = typeIdentifier; meta["version"] = version;
                    if (writing)
                    {
                        meta["mayHaveChanged"] = true;
                        target = EngineeringGroupOperations.Call(collection, "Create", new[] { typeof(string), typeof(string), typeof(Version) }, parts.Last(), typeIdentifier, apiVersion);
                        meta["after"] = EngineeringScalarProperties.Read(target);
                    }
                }
                else
                {
                    if (target == null) throw new InvalidOperationException("Exact technology object not found.");
                    if (action == "delete")
                    {
                        meta["before"] = EngineeringScalarProperties.Read(target);
                        if (writing)
                        {
                            meta["mayHaveChanged"] = true;
                            EngineeringGroupOperations.Call(target, "Delete", Type.EmptyTypes);
                            if (EngineeringGroupOperations.Find(collection, parts.Last()) != null) throw new InvalidOperationException("Object remains after Delete.");
                            meta["verifiedAbsent"] = true;
                        }
                    }
                    else
                    {
                        var parameters = EngineeringGroupOperations.Get(target, "Parameters");
                        if (string.IsNullOrEmpty(parameter))
                        {
                            if (action == "setParameter") throw new ArgumentException("Exact parameter name required.");
                            var all = EngineeringGroupOperations.Items(parameters).Select(EngineeringScalarProperties.Read).ToArray();
                            meta["parameters"] = new JsonArray(all.Cast<JsonNode>().ToArray());
                            meta["actualCount"] = all.Length; meta["dataComplete"] = all.All(x => x["dataComplete"]!.GetValue<bool>());
                        }
                        else
                        {
                            var p = EngineeringGroupOperations.Find(parameters, parameter) ?? throw new InvalidOperationException("Parameter not found: " + parameter);
                            meta["before"] = EngineeringScalarProperties.Read(p);
                            if (action == "setParameter")
                            {
                                var edits = EngineeringScalarProperties.Prepare(p.GetType(), new JsonObject { ["Value"] = JsonNode.Parse(valueJson) });
                                var existingValue = p.GetType().GetProperty("Value")!.GetValue(p);
                                if (existingValue != null && edits[0].Property.PropertyType == typeof(object))
                                    edits[0] = (edits[0].Property, EngineeringScalarProperties.ConvertValue(JsonNode.Parse(valueJson), existingValue.GetType()));
                                meta["requestedValue"] = JsonNode.Parse(valueJson);
                                if (writing) { EngineeringScalarProperties.Apply(p, edits, meta); meta["after"] = EngineeringScalarProperties.Read(p); }
                            }
                        }
                    }
                }
                return writing ? "Native technology object operation completed; no save/compile/download." : "Technology object read/preview completed; no changes.";
            });
    }
}
