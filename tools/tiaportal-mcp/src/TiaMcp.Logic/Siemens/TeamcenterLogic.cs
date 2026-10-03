using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    // Phase 6 ⑥-③ (2.7.42): pure logic (no Siemens dependency) for the Teamcenter Gateway option package
    // (Siemens.Engineering.TeamcenterGateway, identical on V20 / V21): connection (user / SSO), dataset locks, search and download,
    // and the workflow provider that saves the open project / global library to Teamcenter (as is, to an item, as new item / revision,
    // with or without proxy objects) plus its custom attributes.
    internal static class TeamcenterLogic
    {
        internal static string RequireOneOf(string value, string[] allowed, string parameter) => HardwareServicesLogic.RequireOneOf(value, allowed, parameter);
        private static void RequireText(string value, string parameter, int max = 1024)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Trim() != value) throw new ArgumentException("Exact nonempty " + parameter + " required (max " + max + " chars, no surrounding whitespace).");
        }
        private static void Refuse(string value, string parameter, string reason)
        {
            if (!string.IsNullOrEmpty(value)) throw new ArgumentException(parameter + " " + reason);
        }
        internal static JsonObject ParseObject(string json, string parameter) => SivarcLogic.ParseObject(json, parameter);

        // ---- official names (pinned member by member by TeamcenterShapeChecks) ---------------------------------------------------
        internal static readonly string[] DatasetTypes = { "T4TiaProjectDataset", "T4TiaLibraryDataset" };
        internal static readonly string[] ItemTypes = { "Project", "GlobalLibrary" };
        internal static readonly string[] LocalCacheOptions = { "Overwrite", "DoNotOverwrite" };
        internal static readonly string[] MappedCustomAttributeTypes = { "Char", "Date", "Double", "Float", "Integer", "Boolean", "Short", "String" };
        internal static readonly string[] ListOfValuesUsageTypes = { "Exhaustive", "Suggestive", "Range" };
        internal static readonly string[] Targets = { "project", "globalLibrary" };

        internal static readonly string[] ConnectionActions = { "read", "connect", "connectSso", "disconnect" };
        internal static readonly string[] DatasetActions = { "checkout", "checkin", "cancelCheckout", "search", "download" };
        internal static readonly string[] WorkflowActions = { "readCustomAttributes", "save", "saveWithProxyObject", "saveToItem", "saveToItemWithProxyObject", "saveAsNewItem", "saveAsNewItemWithProxyObject", "saveAsNewRevision", "saveAsNewRevisionWithProxyObject" };

        // ---- connection ----------------------------------------------------------------------------------------------------------
        internal sealed class ConnectionRequest { public string Action = ""; public bool Writes; }
        internal static ConnectionRequest ValidateConnectionRequest(string action, string userName, string password, string group, string role, string hostUrl, string instance, string loginUrl, string applicationId, bool dryRun)
        {
            RequireOneOf(action, ConnectionActions, "action");
            if (action == "connect")
            {
                RequireText(userName, "userName", 256); if (string.IsNullOrEmpty(password)) throw new ArgumentException("password is required for connect (official: TeamcenterConnectionProvider.Connect takes a SecureString; it is never logged).");
                RequireText(hostUrl, "hostUrl"); RequireText(instance, "instance", 256);
                Refuse(loginUrl, "loginUrl", "applies to connectSso only."); Refuse(applicationId, "applicationId", "applies to connectSso only.");
            }
            else if (action == "connectSso")
            {
                RequireText(hostUrl, "hostUrl"); RequireText(instance, "instance", 256); RequireText(loginUrl, "loginUrl"); RequireText(applicationId, "applicationId", 256);
                Refuse(userName, "userName", "applies to connect only (ConnectSSO uses the active SSO session)."); Refuse(password, "password", "applies to connect only."); Refuse(group, "group", "applies to connect only."); Refuse(role, "role", "applies to connect only.");
            }
            else
            {
                foreach (var pair in new[] { (userName, "userName"), (password, "password"), (group, "group"), (role, "role"), (hostUrl, "hostUrl"), (instance, "instance"), (loginUrl, "loginUrl"), (applicationId, "applicationId") })
                    Refuse(pair.Item1, pair.Item2, "applies to connect / connectSso only.");
            }
            return new ConnectionRequest { Action = action, Writes = action != "read" && !dryRun };
        }

        // ---- datasets (lock provider, search and download) -----------------------------------------------------------------------
        internal sealed class DatasetRequest { public string Action = ""; public bool Writes; public string DatasetType = ""; public string ItemType = ""; public string LocalCacheOption = ""; }
        internal static DatasetRequest ValidateDatasetRequest(string action, string itemId, string revisionId, string datasetType, string datasetName, string itemType, string tiaObjectName, string itemName, string localCacheOption, bool dryRun)
        {
            RequireOneOf(action, DatasetActions, "action");
            var r = new DatasetRequest { Action = action };
            bool lockAction = action == "checkout" || action == "checkin" || action == "cancelCheckout";
            if (lockAction)
            {
                RequireText(itemId, "itemId", 256); RequireText(revisionId, "revisionId", 256); r.DatasetType = RequireOneOf(datasetType, DatasetTypes, "datasetType"); RequireText(datasetName, "datasetName", 256);
                Refuse(itemType, "itemType", "applies to search / download only."); Refuse(tiaObjectName, "tiaObjectName", "applies to search only."); Refuse(itemName, "itemName", "applies to search only."); Refuse(localCacheOption, "localCacheOption", "applies to download only.");
            }
            else
            {
                r.ItemType = RequireOneOf(itemType, ItemTypes, "itemType");
                Refuse(datasetType, "datasetType", "applies to checkout / checkin / cancelCheckout only."); Refuse(datasetName, "datasetName", "applies to checkout / checkin / cancelCheckout only.");
                if (action == "search")
                {
                    if (string.IsNullOrEmpty(tiaObjectName) && string.IsNullOrEmpty(itemId) && string.IsNullOrEmpty(itemName)) throw new ArgumentException("search needs at least one of tiaObjectName / itemId / itemName (wildcards allowed, official).");
                    Refuse(localCacheOption, "localCacheOption", "applies to download only.");
                }
                else
                {
                    RequireText(itemId, "itemId", 256); RequireText(revisionId, "revisionId", 256); r.LocalCacheOption = RequireOneOf(localCacheOption, LocalCacheOptions, "localCacheOption");
                    Refuse(tiaObjectName, "tiaObjectName", "applies to search only."); Refuse(itemName, "itemName", "applies to search only.");
                }
            }
            r.Writes = action != "search" && !dryRun;
            return r;
        }

        // ---- workflow (save to Teamcenter) ---------------------------------------------------------------------------------------
        internal static readonly string[] ItemDetailKeys = { "itemId", "itemName", "revisionId", "teamcenterItemType", "comment", "teamcenterFolder", "teamcenterProject" };
        internal static readonly string[] RevisionDetailKeys = { "revisionId", "comment" };
        internal sealed class ItemDetailsRequest { public string ItemId = ""; public string ItemName = ""; public string RevisionId = ""; public string TeamcenterItemType = ""; public string Comment = ""; public string TeamcenterFolder = ""; public string[] TeamcenterProject = Array.Empty<string>(); }
        internal sealed class RevisionDetailsRequest { public string RevisionId = ""; public string Comment = ""; }
        internal static ItemDetailsRequest ParseItemDetails(string json)
        {
            var o = ParseObject(json, "itemDetailsJson");
            var unknown = o.Select(p => p.Key).Where(k => !ItemDetailKeys.Contains(k, StringComparer.Ordinal)).ToArray();
            if (unknown.Length > 0) throw new ArgumentException("itemDetailsJson keys not allowed: " + string.Join(", ", unknown) + " (allowed: " + string.Join(", ", ItemDetailKeys) + ").");
            string Text(string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var t) ? t : o[key] == null ? "" : throw new ArgumentException("itemDetailsJson." + key + " must be a string.");
            var r = new ItemDetailsRequest { ItemId = Text("itemId"), ItemName = Text("itemName"), RevisionId = Text("revisionId"), TeamcenterItemType = Text("teamcenterItemType"), Comment = Text("comment"), TeamcenterFolder = Text("teamcenterFolder") };
            RequireText(r.ItemName, "itemDetailsJson.itemName", 256); RequireText(r.TeamcenterItemType, "itemDetailsJson.teamcenterItemType", 256);   // official: ItemName and TeamcenterItemType are required
            if (o["teamcenterProject"] is JsonArray projects) r.TeamcenterProject = projects.Select(p => p?.GetValue<string>() ?? throw new ArgumentException("itemDetailsJson.teamcenterProject must list strings.")).ToArray();
            else if (o["teamcenterProject"] != null) throw new ArgumentException("itemDetailsJson.teamcenterProject must be a JSON array of Teamcenter project names.");
            return r;
        }
        internal static RevisionDetailsRequest ParseRevisionDetails(string json)
        {
            var o = ParseObject(json, "revisionDetailsJson");
            var unknown = o.Select(p => p.Key).Where(k => !RevisionDetailKeys.Contains(k, StringComparer.Ordinal)).ToArray();
            if (unknown.Length > 0) throw new ArgumentException("revisionDetailsJson keys not allowed: " + string.Join(", ", unknown) + " (allowed: " + string.Join(", ", RevisionDetailKeys) + ").");
            string Text(string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var t) ? t : o[key] == null ? "" : throw new ArgumentException("revisionDetailsJson." + key + " must be a string.");
            return new RevisionDetailsRequest { RevisionId = Text("revisionId"), Comment = Text("comment") };
        }
        // customAttributesJson {"AttributeName": "value"} - values are strings (TeamcenterProperty.SetValue(string, ErrorCallback) converts per DataType).
        internal static Dictionary<string, string> ParseCustomAttributes(string json)
        {
            var o = ParseObject(json, "customAttributesJson");
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in o)
            {
                if (string.IsNullOrWhiteSpace(pair.Key)) throw new ArgumentException("customAttributesJson keys must be attribute names.");
                result[pair.Key] = pair.Value is JsonValue v ? (v.TryGetValue<string>(out var text) ? text : v.ToJsonString()) : throw new ArgumentException("customAttributesJson." + pair.Key + " must be a scalar (sent to SetValue as text).");
            }
            return result;
        }
        internal sealed class WorkflowRequest
        {
            public string Action = ""; public string Target = "project"; public bool Writes; public string LocalCacheOption = ""; public ItemDetailsRequest? ItemDetails; public RevisionDetailsRequest? RevisionDetails; public Dictionary<string, string> CustomAttributes = new Dictionary<string, string>(StringComparer.Ordinal);
            public bool NeedsAttributes => Action == "readCustomAttributes" || Action.StartsWith("saveAsNew", StringComparison.Ordinal);
        }
        internal static WorkflowRequest ValidateWorkflowRequest(string action, string target, string libraryName, string itemType, string itemId, string revisionId, string localCacheOption, string itemDetailsJson, string revisionDetailsJson, string customAttributesJson, bool confirmSave, bool dryRun)
        {
            RequireOneOf(action, WorkflowActions, "action");
            var r = new WorkflowRequest { Action = action, Target = RequireOneOf(string.IsNullOrEmpty(target) ? "project" : target, Targets, "target") };
            if (r.Target == "globalLibrary") RequireText(libraryName, "libraryName", 256); else Refuse(libraryName, "libraryName", "applies to target globalLibrary only.");
            bool toItem = action == "saveToItem" || action == "saveToItemWithProxyObject";
            bool plainSave = action == "save" || action == "saveWithProxyObject";
            bool newItem = action == "saveAsNewItem" || action == "saveAsNewItemWithProxyObject";
            bool newRevision = action == "saveAsNewRevision" || action == "saveAsNewRevisionWithProxyObject";
            if (action == "readCustomAttributes") RequireText(itemType, "itemType", 256);
            else if (newRevision) { if (!string.IsNullOrEmpty(itemType)) RequireText(itemType, "itemType", 256); }
            else Refuse(itemType, "itemType", "applies to readCustomAttributes (and saveAsNewRevision* with customAttributesJson) only; saveAsNewItem* take the type from itemDetailsJson.teamcenterItemType.");
            if (toItem) { RequireText(itemId, "itemId", 256); RequireText(revisionId, "revisionId", 256); } else { Refuse(itemId, "itemId", "applies to saveToItem* only."); Refuse(revisionId, "revisionId", "applies to saveToItem* only."); }
            if (toItem || plainSave) r.LocalCacheOption = RequireOneOf(localCacheOption, LocalCacheOptions, "localCacheOption"); else Refuse(localCacheOption, "localCacheOption", "applies to save* / saveToItem* only.");
            if (newItem) r.ItemDetails = ParseItemDetails(itemDetailsJson); else if (!string.IsNullOrWhiteSpace(itemDetailsJson) && itemDetailsJson.Trim() != "{}") throw new ArgumentException("itemDetailsJson applies to saveAsNewItem* only.");
            if (newRevision) r.RevisionDetails = ParseRevisionDetails(revisionDetailsJson); else if (!string.IsNullOrWhiteSpace(revisionDetailsJson) && revisionDetailsJson.Trim() != "{}") throw new ArgumentException("revisionDetailsJson applies to saveAsNewRevision* only.");
            if (newItem || newRevision) r.CustomAttributes = ParseCustomAttributes(customAttributesJson); else if (!string.IsNullOrWhiteSpace(customAttributesJson) && customAttributesJson.Trim() != "{}") throw new ArgumentException("customAttributesJson applies to saveAsNewItem* / saveAsNewRevision* only.");
            if (newRevision && r.CustomAttributes.Count > 0 && string.IsNullOrEmpty(itemType)) throw new ArgumentException("saveAsNewRevision* with customAttributesJson needs itemType (the Teamcenter item type whose mapped attributes are set, e.g. T4TiaProject).");
            if (action != "readCustomAttributes") HardwareServicesLogic.RequireConfirmation(confirmSave, "confirmSave", dryRun);
            r.Writes = action != "readCustomAttributes" && !dryRun;
            return r;
        }
    }
}
