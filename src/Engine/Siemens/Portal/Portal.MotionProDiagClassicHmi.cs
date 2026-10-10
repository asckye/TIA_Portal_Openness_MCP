using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.TechnologicalObjects;
using Siemens.Engineering.SW.TechnologicalObjects.Motion;
using TiaMcpServer.ModelContextProtocol;
using Logic = TiaMcpServer.Siemens.MotionProDiagClassicHmiLogic;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        // Scalar read plus one bounded level of references/collections; no parent navigation.
        private static JsonObject DescribeNode(object target, int depth)
        {
            var row = EngineeringScalarProperties.Read(target);
            var references = new JsonObject(); var failures = row["failures"]!.AsArray();
            foreach (var p in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name))
            {
                if (p.GetIndexParameters().Length != 0 || p.GetMethod?.IsPublic != true || p.Name == "Parent" || EngineeringScalarProperties.Scalar(p.PropertyType) || p.PropertyType == typeof(object)) continue;
                try
                {
                    var value = p.GetValue(target);
                    if (value == null) { references[p.Name] = null; continue; }
                    if (value is IEnumerable && value is not string)
                    {
                        if (depth > 0) { references[p.Name] = new JsonObject { ["type"] = value.GetType().FullName, ["enumerated"] = false }; continue; }
                        var items = EngineeringGroupOperations.Items(value).Take(501).ToArray();
                        references[p.Name] = new JsonObject { ["type"] = value.GetType().FullName, ["count"] = Math.Min(items.Length, 500), ["truncated"] = items.Length > 500,
                            ["items"] = new JsonArray(items.Take(500).Select(i => (JsonNode)DescribeNode(i, depth + 1)).ToArray()) };
                    }
                    else references[p.Name] = depth > 0 ? new JsonObject { ["type"] = value.GetType().FullName, ["name"] = EngineeringDynamicAccess.Name(value) } : DescribeNode(value, depth + 1);
                }
                catch (Exception ex) { failures.Add(new JsonObject { ["property"] = p.Name, ["error"] = ex.GetBaseException().Message }); if (HmiReadSafety.ConnectionUnavailable(ex)) throw; }
            }
            row["references"] = references; row["dataComplete"] = failures.Count == 0;
            return row;
        }

        private PlcTag ExactPlcTag(string softwarePath, string tagPath)
            => new TiaMcp.Adapters.Native.Plc.PlcOrganisationAdapter(new TiaMcpServer.Worker.EnginePlcOrganisationSession(this)).ExactPlcTag(softwarePath, tagPath);

        private DeviceItem ExactDeviceItem(string[] devicePath, string[] itemPath)
            => ExactEngineeringHardware(new JsonArray(devicePath.Select(x => (JsonNode)x).ToArray()).ToJsonString(), new JsonArray(itemPath.Select(x => (JsonNode)x).ToArray()).ToJsonString()) as DeviceItem
               ?? throw new ArgumentException("itemPath must identify a device item, not a device.");

        private HmiTarget ExactClassicHmi(string softwarePath)
        {
            var software = ResolveSoftwareContainerUncached(softwarePath)?.Software ?? throw new PortalException(PortalErrorCode.NotFound, "Exact HMI software not found: " + softwarePath);
            return software as HmiTarget ?? throw new NotSupportedException("Selected software is not a classic WinCC HmiTarget (" + software.GetType().FullName + "); Unified targets use the Unified tools.");
        }
    }
}
