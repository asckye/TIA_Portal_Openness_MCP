using System;
using System.Collections.Generic;
using System.Linq;
namespace TiaMcp.Adapters.Native.Plc
{
    internal static class PlcMotionPolicy
    {
        internal const string MotionNs = "Siemens.Engineering.SW.TechnologicalObjects.Motion.";
        internal const string IdentProvider = "Siemens.Engineering.SW.TechnologicalObjects.Ident.IdentTechnologicalObjectProvider";
        internal const string SupervisionProvider = "Siemens.Engineering.SW.Supervision.SupervisionProvider";
        internal const string SupervisionSettingsProvider = "Siemens.Engineering.SW.Supervision.SupervisionSettingsProvider";
        internal const string GraphicsProvider = "Siemens.Engineering.Hmi.Globalization.GraphicsProvider";
        internal static readonly string[] MotionActions = { "read", "addMasterValue", "removeMasterValue", "createMapping", "updateMapping", "deleteMapping", "connect", "disconnect", "connectIdent" };
        internal static readonly IReadOnlyDictionary<string, (string Service, string Property)> MasterValueAspects = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["synchronousSetPoint"] = (MotionNs + "SynchronousAxisMasterValues", "SetPointCoupling"),
            ["synchronousActualValue"] = (MotionNs + "SynchronousAxisMasterValues", "ActualValueCoupling"),
            ["synchronousDelayed"] = (MotionNs + "SynchronousAxisMasterValues", "DelayedCoupling"),
            ["conveyorSetPoint"] = (MotionNs + "ConveyorTrackingLeadingValues", "SetPointCoupling"),
            ["conveyorActualValue"] = (MotionNs + "ConveyorTrackingLeadingValues", "ActualValueCoupling"),
            ["conveyorDelayed"] = (MotionNs + "ConveyorTrackingLeadingValues", "DelayedCoupling"),
            ["superimposingSetPoint"] = (MotionNs + "SuperimposingAxes", "SetPointCoupling"),
        };
        internal static readonly IReadOnlyDictionary<string, (string Service, string Property)> MappingAspects = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["toMapping"] = (MotionNs + "InterpreterMappings", "TechnologicalObjectMapping"),
            ["dbMemberMapping"] = (MotionNs + "InterpreterMappings", "DBMemberMapping"),
        };
        internal static readonly IReadOnlyDictionary<string, (string Service, string Property)> ConnectionAspects = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["actor"] = (MotionNs + "AxisHardwareConnectionProvider", "ActorInterface"),
            ["sensor"] = (MotionNs + "AxisHardwareConnectionProvider", "SensorInterface"),
            ["torque"] = (MotionNs + "AxisHardwareConnectionProvider", "TorqueInterface"),
            ["encoder"] = (MotionNs + "EncoderHardwareConnectionProvider", "SensorInterface"),
            ["measuringInput"] = (MotionNs + "MeasuringInputHardwareConnectionProvider", ""),
            ["outputCam"] = (MotionNs + "OutputCamHardwareConnectionProvider", ""),
        };
        // Overloads present in the V21 XML per interface type; bit addresses for actor/sensor/torque stay with ConfigureMotionHardwareConnection.
        internal static readonly IReadOnlyDictionary<string, string[]> ConnectionModes = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["actor"] = new[] { "deviceItem", "deviceItems", "dbMember", "plcTag", "channel" },
            ["sensor"] = new[] { "deviceItem", "deviceItems", "dbMember", "plcTag", "channel" },
            ["torque"] = new[] { "deviceItem", "deviceItems", "dbMember" },
            ["encoder"] = new[] { "deviceItem", "deviceItems", "dbMember", "plcTag", "addresses", "channel" },
            ["measuringInput"] = new[] { "address", "deviceItemChannel", "channel" },
            ["outputCam"] = new[] { "address", "plcTag", "channel" },
        };
        internal static readonly string[] ReadableMotionServices =
        {
            MotionNs + "AxisHardwareConnectionProvider", MotionNs + "EncoderHardwareConnectionProvider",
            MotionNs + "MeasuringInputHardwareConnectionProvider", MotionNs + "OutputCamHardwareConnectionProvider",
            MotionNs + "OutputCamMeasuringInputContainer", MotionNs + "SynchronousAxisMasterValues",
            MotionNs + "ConveyorTrackingLeadingValues", MotionNs + "SuperimposingAxes", MotionNs + "InterpreterMappings",
            MotionNs + "CamDataSupport", MotionNs + "InterpreterProgramSupport", IdentProvider,
        };
        internal static string MotionCategory(string action, string aspect)
        {
            if (!MotionActions.Contains(action)) throw new ArgumentException("action must be one of: " + string.Join("/", MotionActions) + ".");
            aspect ??= "";
            if (action == "read" || action == "connectIdent")
            {
                if (aspect != "") throw new ArgumentException("aspect must be empty for " + action + ".");
                return action == "read" ? "read" : "ident";
            }
            if (action.EndsWith("MasterValue", StringComparison.Ordinal))
            {
                if (!MasterValueAspects.ContainsKey(aspect)) throw new ArgumentException("aspect must be one of: " + string.Join("/", MasterValueAspects.Keys) + ".");
                return "masterValue";
            }
            if (action.EndsWith("Mapping", StringComparison.Ordinal))
            {
                if (!MappingAspects.ContainsKey(aspect)) throw new ArgumentException("aspect must be one of: " + string.Join("/", MappingAspects.Keys) + ".");
                return "mapping";
            }
            if (!ConnectionAspects.ContainsKey(aspect)) throw new ArgumentException("aspect must be one of: " + string.Join("/", ConnectionAspects.Keys) + ".");
            return "connection";
        }

        internal static void RequireConnectionMode(string aspect, string mode)
        {
            if (!ConnectionModes[aspect].Contains(mode))
                throw new ArgumentException("Target " + mode + " is not a native Connect overload for aspect " + aspect + " (allowed: " + string.Join("/", ConnectionModes[aspect]) + "). Bit addresses for actor/sensor/torque are handled by ConfigureMotionHardwareConnection.");
        }
        internal static string RequireAction(string action, string[] allowed)
        {
            if (!allowed.Contains(action)) throw new ArgumentException("action must be one of: " + string.Join("/", allowed) + ".");
            return action;
        }
        internal static string RequireName(string value, string what)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.IndexOfAny(new[] { '/', '\\' }) >= 0) throw new ArgumentException("Exact " + what + " (single nonempty segment) required.");
            return value;
        }
        internal static void RequirePagination(int offset, int limit)
        {
            if (offset < 0 || limit < 1 || limit > 500) throw new ArgumentException("offset >= 0 and limit 1..500 required.");
        }
        internal static string ShortTypeName(string fullName) => fullName.Substring(fullName.LastIndexOf('.') + 1);
    }
}
