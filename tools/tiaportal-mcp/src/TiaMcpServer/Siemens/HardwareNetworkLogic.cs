using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    // Pure validation/catalog helpers for the hardware-network family (IO systems, sync/MRP domains, transfer areas,
    // channels, addressing, device user groups, device users, port interconnections). No Siemens.Engineering dependency.
    internal static class HardwareNetworkLogic
    {
        // Dynamic attributes named in the official chapters ("Functions on networks" / "Functions on device items").
        // Presence is decided by TIA at runtime; every read is captured per attribute, never assumed.
        internal static readonly string[] IoSystemAttributes = { "MultipleUseIoSystem", "UseIoSystemNameAsDeviceNameExtension", "MaxNumberIWlanLinksPerSegment", "IsochronousTiToAutoCalculation", "IsochronousTi", "IsochronousTo" };
        internal static readonly string[] IoControllerAttributes = { "SyncRole", "PnDeviceNumber" };
        internal static readonly string[] IoConnectorAttributes = { "PnUpdateTimeAutoCalculation", "PnUpdateTime", "PnUpdateTimeAdaption", "PnWatchdogFactor", "PnWatchdogTime", "RtClass", "SyncRole", "PnDeviceNumber" };
        internal static readonly string[] SyncDomainAttributes = { "HighPerformanceActive", "FastForwardingActive" };
        internal static readonly string[] MrpDomainAttributes = { "IsDefault", "ManagerOutsideOfProjectActive" };
        internal static readonly string[] TransferAreaAttributes = { "Comment", "TransferUpdateTime", "SharedDeviceAccessConfigured", "UpdateAlarm", "RecordIndex" };
        internal static readonly string[] MulticastTransferAreaAttributes = { "TransferUpdateTime" };
        internal static readonly string[] ChannelAttributes = { "ChannelAddress", "ChannelWidth" };
        internal static readonly string[] AddressAttributes = { "Context", "ProcessImage", "IsochronousMode", "InterruptObNumber" };

        internal static readonly string[] IoSystemActions = { "create", "delete", "update", "connect", "disconnect" };
        internal static readonly string[] DomainKinds = { "sync", "mrp" };
        internal static readonly string[] DomainActions = { "create", "delete", "update", "addParticipant" };
        internal static readonly string[] TransferAreaKinds = { "standard", "multicast" };
        internal static readonly string[] TransferAreaActions = { "create", "createReceiver", "delete", "update", "createMappingRule", "updateMappingRule", "deleteMappingRule" };
        internal static readonly string[] MulticastActions = { "create", "createReceiver", "delete", "update" };
        internal static readonly string[] UserFamilies = { "webserver", "simpleWebserver", "opcUa" };
        internal static readonly string[] UserActions = { "read", "create", "delete", "setPassword", "setPermissions", "setActive", "rename" };
        internal static readonly string[] PortActions = { "read", "connect", "disconnect" };
        internal static readonly string[] DeviceGroupActions = { "read", "create", "rename", "deleteEmpty" };

        // Enum member names of the V20/V21 PublicAPI (Siemens.Engineering.HW). Parsed case-sensitively by the engine at runtime.
        internal static readonly string[] TransferAreaTypes = { "None", "MS", "CD", "F_PS", "TM", "IN", "OUT", "MSI", "MSO", "MSO_LOCAL", "RECORD_WRITE_STO", "RECORD_WRITE_PUB", "RECORD_READ_STO", "RECORD_READ_PUB",
            "MSI_MSO", "IN_OUT", "LOCAL_RECORD_STO", "LOCAL_RECORD_PUB", "SUB_MSI", "SUB_MSO", "SUB_LOCAL_RECORD_STO_READ", "SUB_LOCAL_RECORD_PUB_READ", "PROFISAFE_IN12_OUT6", "PROFISAFE_IN6_OUT12",
            "F_Proxy_CD", "DDX", "ISOC_STATUS_CONTROL", "ISOCHRON_IN", "ISOCHRON_OUT", "F_CD" };
        internal static readonly string[] TransferAreaDirections = { "None", "LocalToPartner", "PartnerToLocal", "Bidirectional" };
        internal static readonly string[] ChannelTypes = { "None", "Analog", "Digital", "Technology" };
        internal static readonly string[] ChannelIoTypes = { "None", "Input", "Output", "Complex" };
        internal static readonly string[] AddressIoTypes = { "None", "Input", "Output", "Substitute", "Diagnosis" };
        internal static readonly string[] WebserverPermissions = { "None", "DoDiagnosis", "ReadTag", "ModifyTag", "ReadTagStatus", "ModifyTagStatus", "AcknowledgeMessages", "OpenUserDefinedWebPages", "WriteUserDefinedWebPages",
            "ReadFiles", "ModifyFiles", "ChangeOperatingMode", "FlashLed", "WriteFirmware", "ChangeSystemParameter", "ChangeApplicationParameter", "Backup", "Restore", "FAdmin", "ManageUserDefinedWebPages" };
        internal static readonly string[] SimpleWebserverPermissions = { "None", "ReadOnly", "ReadWrite" };

        internal static string RequireOneOf(string value, string[] allowed, string parameter) => HardwareServicesLogic.RequireOneOf(value, allowed, parameter);
        internal static string RequireExactName(string value, string parameter) => HardwareServicesLogic.RequireExactName(value, parameter);

        internal static JsonObject ParseObject(string json, string parameter)
        {
            if (string.IsNullOrWhiteSpace(json)) return new JsonObject();
            if (json.Length > 32768) throw new ArgumentException(parameter + " exceeds 32 KiB.");
            var obj = JsonNode.Parse(json) as JsonObject ?? throw new ArgumentException(parameter + " must be a JSON object.");
            if (obj.Count > 50) throw new ArgumentException(parameter + ": at most 50 entries.");
            foreach (var pair in obj)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || !pair.Key.All(c => char.IsLetterOrDigit(c) || c == '_')) throw new ArgumentException(parameter + ": invalid key '" + pair.Key + "'.");
                if (pair.Value is JsonObject || pair.Value is JsonArray) throw new ArgumentException(parameter + ": only scalar values are supported (" + pair.Key + ").");
            }
            return obj;
        }
        internal static string[] ParseNames(string json, string parameter, int max = 64)
        {
            if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
            var array = JsonNode.Parse(json) as JsonArray ?? throw new ArgumentException(parameter + " must be a JSON array of strings.");
            if (array.Count > max) throw new ArgumentException(parameter + ": at most " + max + " entries.");
            var names = array.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) && s != null ? s : throw new ArgumentException(parameter + ": every entry must be a string.")).ToArray();
            if (names.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException(parameter + ": empty entry.");
            if (names.Distinct(StringComparer.Ordinal).Count() != names.Length) throw new ArgumentException(parameter + ": duplicate entry.");
            return names;
        }
        // Flag names are validated here and joined for Enum.Parse (flags accept "A, B"); "None" cannot be combined.
        internal static string JoinFlags(string[] names, string[] allowed, string parameter)
        {
            if (names.Length == 0) throw new ArgumentException(parameter + " must name at least one permission.");
            foreach (var name in names) RequireOneOf(name, allowed, parameter);
            if (names.Length > 1 && names.Contains("None", StringComparer.Ordinal)) throw new ArgumentException(parameter + ": 'None' cannot be combined with other permissions.");
            return string.Join(", ", names);
        }
        internal static string[] SplitFlags(string? value) => string.IsNullOrWhiteSpace(value) ? Array.Empty<string>() : value!.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
        internal static string RequirePassword(string password, string parameter)
        {
            if (string.IsNullOrEmpty(password) || password.Length > 128 || password.Trim().Length == 0) throw new ArgumentException(parameter + " must be 1-128 characters (never echoed).");
            return password;
        }
        // A channel is identified by the (Type, IoType, Number) triple of ChannelComposition.Find; all three or none.
        internal static bool ChannelIdentityGiven(string channelType, string channelIoType, int channelNumber)
        {
            bool any = !string.IsNullOrEmpty(channelType) || !string.IsNullOrEmpty(channelIoType) || channelNumber >= 0;
            bool all = !string.IsNullOrEmpty(channelType) && !string.IsNullOrEmpty(channelIoType) && channelNumber >= 0;
            if (any && !all) throw new ArgumentException("channelType, channelIoType and channelNumber must be given together.");
            if (all) { RequireOneOf(channelType, ChannelTypes, "channelType"); RequireOneOf(channelIoType, ChannelIoTypes, "channelIoType"); }
            return all;
        }
        internal static void RequirePosition(int positionNumber, int extendedPositionNumber)
        {
            if (positionNumber < -1 || positionNumber > 65535 || extendedPositionNumber < -1 || extendedPositionNumber > 65535) throw new ArgumentException("positionNumber/extendedPositionNumber must be -1 (unset) or 0..65535.");
            if (extendedPositionNumber >= 0 && positionNumber < 0) throw new ArgumentException("extendedPositionNumber requires positionNumber.");
        }
        internal static void RequireLength(int length, string parameter)
        {
            if (length < -1 || length > 1024) throw new ArgumentException(parameter + " must be -1 (unset) or 0..1024 bytes.");
        }
        internal static string[] StandardTransferAreaActions => TransferAreaActions;
        internal static void ValidateTransferAreaRequest(string kind, string action, string name, string type, int positionNumber, int extendedPositionNumber, int length, int ruleIndex)
        {
            RequireOneOf(kind, TransferAreaKinds, "kind");
            RequireOneOf(action, kind == "multicast" ? MulticastActions : TransferAreaActions, "action");
            RequirePosition(positionNumber, extendedPositionNumber); RequireLength(length, "length");
            if (ruleIndex < -1) throw new ArgumentException("ruleIndex must be -1 (unset) or >= 0.");
            if (action == "create" || action == "createReceiver")
            {
                RequireOneOf(type, TransferAreaTypes, "type");
                if (kind == "standard" && string.IsNullOrEmpty(name)) throw new ArgumentException("create needs the exact transfer area name.");
                if (action == "createReceiver" && kind != "multicast") throw new ArgumentException("createReceiver is only valid for kind=multicast.");
            }
            if (action.EndsWith("MappingRule", StringComparison.Ordinal) && kind != "standard") throw new ArgumentException("Mapping rules exist only on standard transfer areas.");
            if ((action == "updateMappingRule" || action == "deleteMappingRule") && ruleIndex < 0) throw new ArgumentException(action + " needs ruleIndex.");
            if (action != "create" && action != "createReceiver" && string.IsNullOrEmpty(name) && positionNumber < 0) throw new ArgumentException(action + " needs the exact transfer area name or positionNumber.");
        }
        internal static void ValidateUserRequest(string family, string action, string userName, string password, string[] permissionNames, string newName = "")
        {
            RequireOneOf(family, UserFamilies, "family"); RequireOneOf(action, UserActions, "action");
            if (action != "read") RequireExactName(userName, "userName");
            if (action == "rename") RequireExactName(newName, "newName");
            if (family == "simpleWebserver" && (action == "create" || action == "delete")) throw new NotSupportedException("SimpleWebserverUserComposition has no Create/Delete; SIWAREX users are fixed slots (use setActive/rename/setPermissions/setPassword).");
            if (family != "simpleWebserver" && (action == "setActive" || action == "rename")) throw new NotSupportedException(action + " exists only for simpleWebserver users (Active/UserName are read-only elsewhere).");
            if (family == "opcUa" && action == "setPermissions") throw new NotSupportedException("OpcUaUser has no permissions.");
            if (action == "create" || action == "setPassword") RequirePassword(password, "password");
            if (action == "setPermissions" || (action == "create" && family == "webserver"))
                JoinFlags(permissionNames, family == "simpleWebserver" ? SimpleWebserverPermissions : WebserverPermissions, "permissionsJson");
            if (family == "simpleWebserver" && action == "setPermissions" && permissionNames.Length != 1) throw new ArgumentException("simpleWebserver permissions take exactly one of None/ReadOnly/ReadWrite.");
        }
        internal static void ValidateDomainRequest(string kind, string action, string name)
        {
            RequireOneOf(kind, DomainKinds, "kind"); RequireOneOf(action, DomainActions, "action");
            RequireExactName(name, "name");
        }
        internal static void ValidateIoSystemRequest(string action, string name, string subnetName, string ioSystemName)
        {
            RequireOneOf(action, IoSystemActions, "action");
            if (action == "connect" && (string.IsNullOrEmpty(subnetName) || string.IsNullOrEmpty(ioSystemName))) throw new ArgumentException("connect needs subnetName and ioSystemName of the target IO system.");
            if (action == "create" && name.Length > 256) throw new ArgumentException("name too long.");
        }
        internal static string[] PortAllowedActions => PortActions;
    }
}
