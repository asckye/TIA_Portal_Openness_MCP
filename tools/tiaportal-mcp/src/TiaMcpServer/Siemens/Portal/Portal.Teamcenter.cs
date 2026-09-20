using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.Library;
using Siemens.Engineering.TeamcenterGateway;
using TiaMcpServer.ModelContextProtocol;
using Logic = TiaMcpServer.Siemens.TeamcenterLogic;

namespace TiaMcpServer.Siemens
{
    // Phase 6 ⑥-③ (2.7.42): typed Teamcenter Gateway option package (Siemens.Engineering.TeamcenterGateway, identical on V20 / V21).
    // Official entry: TiaPortal.GetService<TeamcenterConnectionProvider>() -> Connect / ConnectSSO answer an encrypted
    // TcGatewayConnectionInfo that every other call must present; TcGatewayLockProvider and TcGatewaySearchAndDownloadProvider are
    // services of the TiaPortal, TcGatewayWorkflowProvider is a service of the open Project or GlobalLibrary. The engine keeps the
    // one active connection info in memory (never serialised - only the session token's SHA-256 prefix is reported).
    public partial class Portal
    {
        private TcGatewayConnectionInfo? _teamcenterConnection;
        private JsonObject? _teamcenterConnectionLabel;

        // ---- resolution ----------------------------------------------------------------------------------------------------------
        private TeamcenterConnectionProvider RequireTeamcenterConnectionProvider()
            => _portal!.GetService<TeamcenterConnectionProvider>() ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "TeamcenterConnectionProvider is not provided by this TIA Portal (Teamcenter Gateway not installed).");
        private TcGatewayConnectionInfo RequireTeamcenterConnection()
            => _teamcenterConnection ?? throw new PortalException(PortalErrorCode.InvalidState, "No active Teamcenter connection in this engine session; run ManageTeamcenterConnection connect / connectSso first.");
        private IEngineeringServiceProvider ExactWorkflowOwner(string target, string libraryName)
            => target == "globalLibrary" ? (IEngineeringServiceProvider)(EngineeringGroupOperations.Find(_portal!.GlobalLibraries, libraryName) as GlobalLibrary ?? throw new PortalException(PortalErrorCode.NotFound, "Exact open global library not found: " + libraryName + " (open it in TIA first).")) : _project!;
        private static TcGatewayWorkflowProvider RequireWorkflowProvider(IEngineeringServiceProvider owner)
            => owner.GetService<TcGatewayWorkflowProvider>() ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "TcGatewayWorkflowProvider is not provided by " + owner.GetType().Name + " (Teamcenter Gateway not installed, or the object was not opened from Teamcenter).");
        private static string TokenFingerprint(string? token)
        {
            if (string.IsNullOrEmpty(token)) return "";
            using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(token))).Replace("-", "").Substring(0, 16).ToLowerInvariant();
        }

        // ---- rows ----------------------------------------------------------------------------------------------------------------
        private JsonObject ConnectionRow()
        {
            var row = new JsonObject { ["connected"] = _teamcenterConnection != null };
            if (_teamcenterConnection == null) return row;
            var info = _teamcenterConnection;
            Safe(row, "group", () => info.Group); Safe(row, "role", () => info.Role); Safe(row, "sessionTokenSha256Prefix", () => TokenFingerprint(info.SessionToken));
            if (_teamcenterConnectionLabel != null) foreach (var pair in _teamcenterConnectionLabel) row[pair.Key] = pair.Value?.DeepClone();
            return row;
        }
        private static JsonObject ItemInfoRow(ItemInfo info)
        {
            var row = new JsonObject();
            Safe(row, "itemId", () => info.ItemId); Safe(row, "revisionId", () => info.RevisionId); Safe(row, "itemName", () => info.ItemName); Safe(row, "itemType", () => info.ItemType.ToString());
            return row;
        }
        private static JsonObject SearchResultRow(SearchResult result)
        {
            var row = new JsonObject();
            Safe(row, "itemId", () => result.ItemId); Safe(row, "revisionIds", () => new JsonArray(result.RevisionId.Take(500).Select(r => (JsonNode)r).ToArray()));
            return row;
        }
        private static JsonObject TeamcenterPropertyRow(TeamcenterProperty p)
        {
            var row = new JsonObject { ["name"] = p.Name };
            Safe(row, "dataType", () => p.DataType.ToString()); Safe(row, "defaultValue", () => p.DefaultValue); Safe(row, "isRequired", () => p.IsRequired); Safe(row, "mappingLevel", () => p.MappingLevel);
            Safe(row, "maxFieldLength", () => p.MaxFieldLength); Safe(row, "lowerBound", () => p.LowerBound); Safe(row, "upperBound", () => p.UpperBound);
            Safe(row, "listOfValues", () => { TcPropertyListOfValueInfo? lov = p.ListOfValueInfo; if (lov == null) return null; var o = new JsonObject(); Safe(o, "usageType", () => lov.LovUsageType.ToString()); Safe(o, "lowerLimit", () => lov.LowerLimit); Safe(o, "upperLimit", () => lov.UpperLimit); Safe(o, "values", () => new JsonArray(lov.Values.Take(500).Select(v => (JsonNode)v).ToArray())); return o; });
            return row;
        }

        // ---- tools ---------------------------------------------------------------------------------------------------------------
        public ResponseMessage ManageTeamcenterConnection(string action = "read", string userName = "", string password = "", string group = "", string role = "", string hostUrl = "", string instance = "", string loginUrl = "", string applicationId = "", bool dryRun = true)
            => RunHmiStepTool("ManageTeamcenterConnection", meta =>
            {
                var r = Logic.ValidateConnectionRequest(action, userName, password, group, role, hostUrl, instance, loginUrl, applicationId, dryRun);
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                meta["before"] = ConnectionRow();
                if (action == "read") { Safe(meta, "providerAvailable", () => _portal!.GetService<TeamcenterConnectionProvider>() != null); return "Teamcenter connection state of this engine session read; nothing changed."; }
                var provider = RequireTeamcenterConnectionProvider();
                if (action == "disconnect") { RequireTeamcenterConnection(); }
                else if (_teamcenterConnection != null) throw new PortalException(PortalErrorCode.InvalidState, "A Teamcenter connection is already active in this session; disconnect it first (one TcGatewayConnectionInfo per session).");
                if (dryRun) return "Teamcenter " + action + " preview; no call to the gateway. The password / SSO session is only used on execution and never logged.";
                meta["mayHaveChanged"] = true;
                switch (action)
                {
                    case "connect":
                        meta["nativeSignature"] = "TeamcenterConnectionProvider.Connect(userName, SecureString, group, role, hostURL, instance)";
                        using (var secure = PlcBlockServicesLogic.ToSecureString(password)) _teamcenterConnection = provider.Connect(userName, secure, group, role, hostUrl, instance);
                        _teamcenterConnectionLabel = new JsonObject { ["mode"] = "user", ["userName"] = userName, ["hostUrl"] = hostUrl, ["instance"] = instance, ["connectedAt"] = DateTime.Now };
                        break;
                    case "connectSso":
                        meta["nativeSignature"] = "TeamcenterConnectionProvider.ConnectSSO(hostURL, instance, loginURL, applicationID)";
                        _teamcenterConnection = provider.ConnectSSO(hostUrl, instance, loginUrl, applicationId);
                        _teamcenterConnectionLabel = new JsonObject { ["mode"] = "sso", ["hostUrl"] = hostUrl, ["instance"] = instance, ["loginUrl"] = loginUrl, ["applicationId"] = applicationId, ["connectedAt"] = DateTime.Now };
                        break;
                    case "disconnect":
                        meta["nativeSignature"] = "TeamcenterConnectionProvider.Disconnect(TcGatewayConnectionInfo)";
                        provider.Disconnect(_teamcenterConnection!); _teamcenterConnection = null; _teamcenterConnectionLabel = null;
                        break;
                }
                meta["after"] = ConnectionRow();
                return "Teamcenter " + action + " executed (connection info kept in engine memory; only group / role / token fingerprint reported); the project is unchanged.";
            });

        public ResponseMessage ManageTeamcenterDataset(string action, string itemId = "", string revisionId = "", string datasetType = "", string datasetName = "", string itemType = "", string tiaObjectName = "", string itemName = "", string localCacheOption = "", bool dryRun = true)
            => RunHmiStepTool("ManageTeamcenterDataset", meta =>
            {
                var r = Logic.ValidateDatasetRequest(action, itemId, revisionId, datasetType, datasetName, itemType, tiaObjectName, itemName, localCacheOption, dryRun);
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["connection"] = ConnectionRow();
                var info = RequireTeamcenterConnection();
                if (action == "search")
                {
                    var search = _portal!.GetService<TcGatewaySearchAndDownloadProvider>() ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "TcGatewaySearchAndDownloadProvider is not provided by this TIA Portal.");
                    var type = (ItemType)Enum.Parse(typeof(ItemType), r.ItemType); meta["nativeSignature"] = "TcGatewaySearchAndDownloadProvider.Search(info, ItemType." + type + ", tiaObjectName, itemId, itemName, revisionId)";
                    var results = search.Search(info, type, tiaObjectName, itemId, itemName, revisionId);
                    meta["records"] = new JsonArray(results.Take(500).Select(x => (JsonNode)SearchResultRow(x)).ToArray()); meta["expectedCount"] = results.Count; meta["actualCount"] = Math.Min(results.Count, 500);
                    return "Teamcenter search returned " + results.Count + " item(s) (ItemId with revision ids); nothing changed.";
                }
                if (dryRun) return "Teamcenter " + action + " preview; no call to the gateway.";
                meta["mayHaveChanged"] = true;
                if (action == "download")
                {
                    var search = _portal!.GetService<TcGatewaySearchAndDownloadProvider>() ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "TcGatewaySearchAndDownloadProvider is not provided by this TIA Portal.");
                    var type = (ItemType)Enum.Parse(typeof(ItemType), r.ItemType); var cache = (LocalCacheOption)Enum.Parse(typeof(LocalCacheOption), r.LocalCacheOption);
                    meta["nativeSignature"] = "TcGatewaySearchAndDownloadProvider.Download(info, itemId, revisionId, ItemType." + type + ", LocalCacheOption." + cache + ") -> FileInfo";
                    FileInfo starter = search.Download(info, itemId, revisionId, type, cache);
                    starter.Refresh(); meta["starterFile"] = new JsonObject { ["path"] = starter.FullName, ["exists"] = starter.Exists, ["bytes"] = starter.Exists ? starter.Length : (long?)null };
                    return "Project / global library downloaded into the Teamcenter cache; open the starter file with OpenProject / ManageGlobalLibrary (nothing was opened automatically).";
                }
                var locks = _portal!.GetService<TcGatewayLockProvider>() ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "TcGatewayLockProvider is not provided by this TIA Portal.");
                var dataset = (DatasetType)Enum.Parse(typeof(DatasetType), r.DatasetType);
                switch (action)
                {
                    case "checkout": meta["nativeSignature"] = "TcGatewayLockProvider.CheckoutDataset(info, itemId, revisionId, DatasetType." + dataset + ", datasetName)"; locks.CheckoutDataset(info, itemId, revisionId, dataset, datasetName); break;
                    case "checkin": meta["nativeSignature"] = "TcGatewayLockProvider.CheckinDataset(info, itemId, revisionId, DatasetType." + dataset + ", datasetName)"; locks.CheckinDataset(info, itemId, revisionId, dataset, datasetName); break;
                    default: meta["nativeSignature"] = "TcGatewayLockProvider.CancelCheckoutDataset(info, itemId, revisionId, DatasetType." + dataset + ", datasetName)"; locks.CancelCheckoutDataset(info, itemId, revisionId, dataset, datasetName); break;
                }
                meta["dataset"] = new JsonObject { ["itemId"] = itemId, ["revisionId"] = revisionId, ["datasetType"] = r.DatasetType, ["datasetName"] = datasetName };
                return "Teamcenter dataset " + action + " returned without exception (the gateway reports failures as TcGatewayException); the local project is unchanged.";
            });

        public ResponseMessage ManageTeamcenterWorkflow(string action, string target = "project", string libraryName = "", string itemType = "", string itemId = "", string revisionId = "", string localCacheOption = "", string itemDetailsJson = "{}", string revisionDetailsJson = "{}", string customAttributesJson = "{}", bool confirmSave = false, bool dryRun = true)
            => RunHmiStepTool("ManageTeamcenterWorkflow", meta =>
            {
                var r = Logic.ValidateWorkflowRequest(action, target, libraryName, itemType, itemId, revisionId, localCacheOption, itemDetailsJson, revisionDetailsJson, customAttributesJson, confirmSave, dryRun);
                meta["action"] = action; meta["target"] = r.Target; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["connection"] = ConnectionRow();
                var info = RequireTeamcenterConnection();
                var owner = ExactWorkflowOwner(r.Target, libraryName); meta["ownerClass"] = owner.GetType().Name;
                var workflow = RequireWorkflowProvider(owner);
                if (action == "readCustomAttributes")
                {
                    meta["nativeSignature"] = "TcGatewayWorkflowProvider.GetTeamcenterCustomAttributes(info, itemType)";
                    var attributes = workflow.GetTeamcenterCustomAttributes(info, itemType);
                    meta["records"] = new JsonArray(attributes.Take(500).Select(a => (JsonNode)TeamcenterPropertyRow(a)).ToArray()); meta["expectedCount"] = attributes.Count; meta["actualCount"] = Math.Min(attributes.Count, 500);
                    return "Teamcenter custom attributes for item type " + itemType + " read (" + attributes.Count + "); nothing changed.";
                }
                if (r.ItemDetails != null) meta["itemDetails"] = new JsonObject { ["itemId"] = r.ItemDetails.ItemId, ["itemName"] = r.ItemDetails.ItemName, ["revisionId"] = r.ItemDetails.RevisionId, ["teamcenterItemType"] = r.ItemDetails.TeamcenterItemType, ["comment"] = r.ItemDetails.Comment, ["teamcenterFolder"] = r.ItemDetails.TeamcenterFolder, ["teamcenterProject"] = new JsonArray(r.ItemDetails.TeamcenterProject.Select(p => (JsonNode)p).ToArray()) };
                if (r.RevisionDetails != null) meta["revisionDetails"] = new JsonObject { ["revisionId"] = r.RevisionDetails.RevisionId, ["comment"] = r.RevisionDetails.Comment };
                if (dryRun) return "Teamcenter " + action + " preview: the " + r.Target + " would be saved to Teamcenter (this saves the open object). No call to the gateway.";
                meta["mayHaveChanged"] = true; meta["projectSaved"] = true;
                // saveAsNew*: fetch the mapped custom attributes for the item type, set the requested values (SetValue with the official
                // ErrorCallback, errors collected), pass the list (or null when nothing was requested).
                IList<TeamcenterProperty>? attributesToSave = null;
                if (r.NeedsAttributes && r.CustomAttributes.Count > 0)
                {
                    var typeName = r.ItemDetails?.TeamcenterItemType ?? itemType;
                    var available = workflow.GetTeamcenterCustomAttributes(info, typeName);
                    var errors = new JsonArray(); var applied = new JsonArray();
                    foreach (var pair in r.CustomAttributes)
                    {
                        var property = available.FirstOrDefault(a => a.Name == pair.Key) ?? throw new PortalException(PortalErrorCode.NotFound, "Custom attribute '" + pair.Key + "' is not mapped for item type " + typeName + " (readCustomAttributes lists the mapped names).");
                        property.SetValue(pair.Value, message => errors.Add(new JsonObject { ["attribute"] = pair.Key, ["error"] = message }));
                        applied.Add(pair.Key);
                    }
                    meta["customAttributesApplied"] = applied; meta["customAttributeErrors"] = errors;
                    if (errors.Count > 0) throw new PortalException(PortalErrorCode.InvalidState, "Teamcenter rejected " + errors.Count + " custom attribute value(s); nothing saved (see customAttributeErrors).");
                    attributesToSave = available;
                }
                ItemInfo result;
                switch (action)
                {
                    case "save": var c1 = (LocalCacheOption)Enum.Parse(typeof(LocalCacheOption), r.LocalCacheOption); meta["nativeSignature"] = "TcGatewayWorkflowProvider.Save(info, LocalCacheOption." + c1 + ")"; result = workflow.Save(info, c1); break;
                    case "saveWithProxyObject": var c2 = (LocalCacheOption)Enum.Parse(typeof(LocalCacheOption), r.LocalCacheOption); meta["nativeSignature"] = "TcGatewayWorkflowProvider.SaveWithProxyObject(info, LocalCacheOption." + c2 + ")"; result = workflow.SaveWithProxyObject(info, c2); break;
                    case "saveToItem": var c3 = (LocalCacheOption)Enum.Parse(typeof(LocalCacheOption), r.LocalCacheOption); meta["nativeSignature"] = "TcGatewayWorkflowProvider.SaveToItem(info, itemId, revisionId, LocalCacheOption." + c3 + ")"; result = workflow.SaveToItem(info, itemId, revisionId, c3); break;
                    case "saveToItemWithProxyObject": var c4 = (LocalCacheOption)Enum.Parse(typeof(LocalCacheOption), r.LocalCacheOption); meta["nativeSignature"] = "TcGatewayWorkflowProvider.SaveToItemWithProxyObject(info, itemId, revisionId, LocalCacheOption." + c4 + ")"; result = workflow.SaveToItemWithProxyObject(info, itemId, revisionId, c4); break;
                    case "saveAsNewItem":
                    case "saveAsNewItemWithProxyObject":
                        var details = r.ItemDetails!;
                        ItemDetailsDelegate itemDelegate = d => { if (!string.IsNullOrEmpty(details.ItemId)) d.ItemId = details.ItemId; d.ItemName = details.ItemName; if (!string.IsNullOrEmpty(details.RevisionId)) d.RevisionId = details.RevisionId; d.TeamcenterItemType = details.TeamcenterItemType; if (!string.IsNullOrEmpty(details.Comment)) d.Comment = details.Comment; if (!string.IsNullOrEmpty(details.TeamcenterFolder)) d.TeamcenterFolder = details.TeamcenterFolder; if (details.TeamcenterProject.Length > 0) d.TeamcenterProject = details.TeamcenterProject; };
                        meta["nativeSignature"] = "TcGatewayWorkflowProvider." + (action == "saveAsNewItem" ? "SaveAsNewItem" : "SaveAsNewItemWithProxyObject") + "(info, ItemDetailsDelegate, IEnumerable<TeamcenterProperty>" + (attributesToSave == null ? " = null" : "") + ")";
                        result = action == "saveAsNewItem" ? workflow.SaveAsNewItem(info, itemDelegate, attributesToSave) : workflow.SaveAsNewItemWithProxyObject(info, itemDelegate, attributesToSave); break;
                    default:
                        var revision = r.RevisionDetails!;
                        RevisionDetailsDelegate revisionDelegate = d => { if (!string.IsNullOrEmpty(revision.RevisionId)) d.RevisionId = revision.RevisionId; if (!string.IsNullOrEmpty(revision.Comment)) d.Comment = revision.Comment; };
                        meta["nativeSignature"] = "TcGatewayWorkflowProvider." + (action == "saveAsNewRevision" ? "SaveAsNewRevision" : "SaveAsNewRevisionWithProxyObject") + "(info, RevisionDetailsDelegate, IEnumerable<TeamcenterProperty>" + (attributesToSave == null ? " = null" : "") + ")";
                        result = action == "saveAsNewRevision" ? workflow.SaveAsNewRevision(info, revisionDelegate, attributesToSave) : workflow.SaveAsNewRevisionWithProxyObject(info, revisionDelegate, attributesToSave); break;
                }
                meta["itemInfo"] = ItemInfoRow(result);
                return "Teamcenter " + action + " returned ItemInfo (item / revision / name / type); the open " + r.Target + " was saved by the gateway as part of the operation.";
            });
    }
}
