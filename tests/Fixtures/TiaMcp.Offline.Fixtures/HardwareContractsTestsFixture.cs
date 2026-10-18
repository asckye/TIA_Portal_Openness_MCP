using TiaMcpServer;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Logic.V4.Domain;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens.Services;
using Xunit;

namespace TiaMcpServer.ModelContextProtocol
{
    public class NetworkAttribute { }
    public class ResponseNetworkInfo : ResponseMessage
    {
        public string? DeviceItemName { get; set; }
        public IEnumerable<NetworkAttribute>? Attributes { get; set; }
    }
    public class ResponseJsonReport : ResponseMessage
    {
        public bool? Ok { get; set; }
        public JsonObject? Data { get; set; }
        public string[]? Errors { get; set; }
        public string[]? Warnings { get; set; }
        public string? OutputPath { get; set; }
        public string[]? OutputFiles { get; set; }
    }
}

namespace TiaMcpServer.ModelContextProtocol
{
    public class Attribute
    {
        public string? Name { get; set; }
        public object? Value { get; set; }
        public string? AccessMode { get; set; }
    }

    public class GsdDeviceCandidate
    {
        public string? Source { get; set; }
        public string? Keyword { get; set; }
        public string? Vendor { get; set; }
        public string? ProductFamily { get; set; }
        public string? MainFamily { get; set; }
        public string? DapId { get; set; }
        public string? DapName { get; set; }
        public string? ArticleNumber { get; set; }
        public string? CatalogPath { get; set; }
        public string? Description { get; set; }
        public string? TypeIdentifier { get; set; }
        public string? TypeIdentifierNormalized { get; set; }
        public string? TypeName { get; set; }
        public string? Version { get; set; }
        public string? GsdmlPath { get; set; }
        public int? Score { get; set; }
    }

    public class HardwareCatalogCandidate
    {
        public string? Source { get; set; }
        public string? Keyword { get; set; }
        public string? ArticleNumber { get; set; }
        public string? CatalogPath { get; set; }
        public string? Description { get; set; }
        public string? TypeIdentifier { get; set; }
        public string? TypeIdentifierNormalized { get; set; }
        public string? TypeName { get; set; }
        public string? Version { get; set; }
        public bool? Insertable { get; set; }
        public int? Score { get; set; }
    }

    public class CaxExportResult
    {
        public string? DeviceName { get; set; }
        public string? FilePath { get; set; }
        public bool Success { get; set; }
        public string? State { get; set; }
        public bool? NativeBooleanResult { get; set; }
        public int ErrorCount { get; set; }
        public int WarningCount { get; set; }
        public List<string>? Messages { get; set; }
    }

    public class ResponseAttributes : ResponseMessage
    {
        public IEnumerable<Attribute>? Attributes { get; set; }
    }

    public class ResponseDeviceInfo : ResponseAttributes
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
    }

    public class ResponseDeviceItemInfo : ResponseAttributes
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
    }

    public class ResponseTree : ResponseMessage
    {
        public string? Tree { get; set; }
    }

    public class ResponseDevices : ResponseMessage
    {
        public IEnumerable<ResponseDeviceInfo>? Items { get; set; }
    }

    public class ResponseDeviceProbe : ResponseMessage
    {
        public bool? Ok { get; set; }
        public string? DeviceName { get; set; }
        public string? Family { get; set; }
        public string? MlfbUsed { get; set; }
        public string? VersionUsed { get; set; }
        public IEnumerable<string>? Attempts { get; set; }
        public string? Error { get; set; }
    }

    public class ResponseGsdDeviceSearch : ResponseMessage
    {
        public string? Keyword { get; set; }
        public int? Count { get; set; }
        public IEnumerable<GsdDeviceCandidate>? Items { get; set; }
    }

    public class ResponseHardwareCatalogSearch : ResponseMessage
    {
        public string? Keyword { get; set; }
        public int? Count { get; set; }
        public IEnumerable<HardwareCatalogCandidate>? Items { get; set; }
        public string? Error { get; set; }
    }

    public class ResponseGsdDeviceProbe : ResponseMessage
    {
        public bool? Ok { get; set; }
        public string? Keyword { get; set; }
        public string? DeviceName { get; set; }
        public string? PreferredDap { get; set; }
        public GsdDeviceCandidate? CandidateUsed { get; set; }
        public IEnumerable<GsdDeviceCandidate>? Candidates { get; set; }
        public IEnumerable<string>? Attempts { get; set; }
        public string? Error { get; set; }
    }

    public class ResponseHardwareCatalogDeviceProbe : ResponseMessage
    {
        public bool? Ok { get; set; }
        public string? Keyword { get; set; }
        public string? DeviceName { get; set; }
        public string? PreferredText { get; set; }
        public HardwareCatalogCandidate? CandidateUsed { get; set; }
        public IEnumerable<HardwareCatalogCandidate>? Candidates { get; set; }
        public IEnumerable<string>? Attempts { get; set; }
        public string? Error { get; set; }
    }

    public class ResponseExportDeviceAml : ResponseMessage
    {
        public string? DeviceName { get; set; }
        public string? FilePath { get; set; }
        public bool Success { get; set; }
        public string? State { get; set; }
        public int ErrorCount { get; set; }
        public int WarningCount { get; set; }
        public IEnumerable<string>? Messages { get; set; }
    }
}

// Application service doubles only: invalid inputs must never cross this boundary.
namespace TiaMcpServer.Siemens.Services
{
    internal sealed class DeviceUserPortService
    {
        internal int Calls;
        internal ResponseMessage Next() { Calls++; return new ResponseMessage { Message = "fixture", Meta = new JsonObject { ["success"] = true } }; }
        public ResponseMessage ReadIoSystems(string subnetName, string devicePathJson, string itemPathJson, int offset, int limit) => Next();
        public ResponseMessage ManageIoSystem(string devicePathJson, string itemPathJson, string action, string name, string subnetName, string ioSystemName, string propertiesJson, string attributesJson, bool confirmDelete, bool dryRun) => Next();
        public ResponseMessage ReadNetworkDomains(string subnetName, int offset, int limit) => Next();
        public ResponseMessage ManageNetworkDomain(string subnetName, string kind, string action, string name, string propertiesJson, string attributesJson, string participantDevicePathJson, string participantItemPathJson, bool confirmDelete, bool dryRun) => Next();
        public ResponseMessage ReadTransferAreas(string devicePathJson, string itemPathJson, int positionNumber, int extendedPositionNumber, int offset, int limit) => Next();
        public ResponseMessage ManageTransferArea(string devicePathJson, string itemPathJson, string action, string kind, string name, string type, int positionNumber, int extendedPositionNumber, string partnerDevicePathJson, string partnerItemPathJson, string senderName, int length, string propertiesJson, string attributesJson, int ruleIndex, string targetDevicePathJson, string targetItemPathJson, bool confirmDelete, bool dryRun) => Next();
        public ResponseMessage ReadDeviceItemChannels(string devicePathJson, string itemPathJson, string channelType, string channelIoType, int channelNumber, string attributeNamesJson, int offset, int limit, bool includeLinkedTags) => Next();
        public ResponseMessage UpdateDeviceItemChannel(string devicePathJson, string itemPathJson, string channelType, string channelIoType, int channelNumber, string attributesJson, bool dryRun) => Next();
        public ResponseMessage ManageDeviceUserGroup(string groupPath, string action, string newName, bool dryRun) => Next();
        public ResponseMessage ManageDeviceUsers(string devicePathJson, string itemPathJson, string family, string action, string userName, string password, string permissionsJson, bool active, string newName, bool confirmDelete, bool dryRun) => Next();
        public ResponseMessage ManagePortInterconnection(string devicePathJson, string itemPathJson, string action, string partnerDevicePathJson, string partnerItemPathJson, bool dryRun) => Next();
        public List<NetworkAttribute>? GetDeviceItemNetworkInfo(string path) { Calls++; return new List<NetworkAttribute>(); }
        public string ProbeConnectDeviceNodesToSubnet(string first, string second, string subnet) { Calls++; return "ConnectToSubnet: OK"; }
        public ResponseMessage EnsureSubnet(string anchor, string type, string subnet) => Next();
        public ResponseMessage AttachDeviceNodeToSubnet(string path, int index, string subnet, string anchor) => Next();
        public List<string> ProbeHardwareHmiConnectionOwnerCandidates(string plc, string hmi, bool deep) { Calls++; return new List<string>(); }
        public List<string> ProbeHardwareHmiConnectionWhitelistedServices(string plc, string hmi, bool deep) { Calls++; return new List<string>(); }
        public JsonObject GetProjectTopology() { Calls++; return new JsonObject { ["devices"] = new JsonArray(), ["deviceCount"] = 0 }; }
    }
    internal sealed class HardwareSecurityPortService
    {
        internal int Calls;
        internal ResponseMessage Next() { Calls++; return new ResponseMessage { Message = "fixture", Meta = new JsonObject { ["success"] = true } }; }
        public ResponseMessage ReadCommunicationConnections(string devicePathJson, string itemPathJson, int offset, int limit) => Next();
        public ResponseMessage ManageCommunicationConnection(string devicePathJson, string itemPathJson, string action, string connectionType, string connectionName, string localInterfaceItemPathJson, string localNodeName, string partnerDevicePathJson, string partnerItemPathJson, string partnerInterfaceItemPathJson, string partnerNodeName, bool confirmDelete, bool dryRun) => Next();
        public ResponseMessage ManageWatchForceTableWebAccess(string devicePathJson, string itemPathJson, string action, string softwarePath, string tableKind, string tablePath, string access, bool confirmChange, bool dryRun) => Next();
        public ResponseMessage ExchangeSystemDiagnosticsSettings(string action, string filePath, string devicePathJson, string itemPathJson, bool confirmImport, bool dryRun) => Next();
        public ResponseMessage ReadHardwareFeatures(string devicePathJson, string itemPathJson, int offset, int limit) => Next();
        public ResponseMessage ManageDeviceServiceObjects(string devicePathJson, string itemPathJson, string family, string action, string name, string propertiesJson, string filePath, bool confirmChange, bool dryRun) => Next();
        public ResponseMessage ManagePlcProtection(string devicePathJson, string itemPathJson, string action, string accessLevel, string password, string newPassword, bool confirmChange, bool dryRun) => Next();
        public ResponseMessage CompileDevice(string devicePathJson, string itemPathJson) => Next();
        public ResponseMessage ManageHardwareUtilities(string action, string typeIdentifier, string devicePathJson, string itemPathJson, string filePath, string password, bool dryRun) => Next();
        public JsonObject GetPutGetAccess(string path) { Calls++; return new JsonObject { ["found"] = true, ["enabled"] = false }; }
        public JsonObject SetPutGetAccess(string path, bool enabled) { Calls++; return new JsonObject { ["ok"] = true, ["mayHaveChanged"] = true, ["writeOutcomeKnown"] = true }; }
    }
}

