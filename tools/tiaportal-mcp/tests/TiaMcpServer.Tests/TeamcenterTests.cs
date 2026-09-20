using System;
using System.Linq;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    // Phase 6 ⑥-③ (2.7.42) pure logic: Teamcenter Gateway connection / dataset / workflow requests, item and revision details,
    // custom attribute values.
    internal static class TeamcenterTests
    {
        private static bool Fails<T>(Action a) where T : Exception { try { a(); return false; } catch (T) { return true; } }

        internal static void Run(Action<bool, string> check)
        {
            // ---- catalogues (pinned member by member by TeamcenterShapeChecks) ----
            check(TeamcenterLogic.DatasetTypes.SequenceEqual(new[] { "T4TiaProjectDataset", "T4TiaLibraryDataset" }) && TeamcenterLogic.ItemTypes.SequenceEqual(new[] { "Project", "GlobalLibrary" }) && TeamcenterLogic.LocalCacheOptions.SequenceEqual(new[] { "Overwrite", "DoNotOverwrite" })
                && TeamcenterLogic.MappedCustomAttributeTypes.Length == 8 && TeamcenterLogic.ListOfValuesUsageTypes.Length == 3 && TeamcenterLogic.ConnectionActions.Length == 4 && TeamcenterLogic.DatasetActions.Length == 5 && TeamcenterLogic.WorkflowActions.Length == 9, "teamcenter: catalogues");

            // ---- connection ----
            TeamcenterLogic.ConnectionRequest C(string action, string user = "", string pw = "", string group = "", string role = "", string host = "", string instance = "", string login = "", string app = "", bool dryRun = true)
                => TeamcenterLogic.ValidateConnectionRequest(action, user, pw, group, role, host, instance, login, app, dryRun);
            check(!C("read").Writes && !C("connect", "user", "pw", host: "https://tc:7001", instance: "TC").Writes && C("connect", "user", "pw", "grp", "role", "https://tc:7001", "TC", dryRun: false).Writes, "teamcenter connection: connect");
            check(C("connectSso", host: "https://tc:7001", instance: "TC", login: "https://sso/login", app: "TIA", dryRun: false).Writes && C("disconnect", dryRun: false).Writes, "teamcenter connection: sso / disconnect");
            check(Fails<ArgumentException>(() => C("connect", "user", host: "https://tc", instance: "TC")) && Fails<ArgumentException>(() => C("connect", "user", "pw", instance: "TC")) && Fails<ArgumentException>(() => C("connect", "user", "pw", host: "https://tc", instance: "TC", login: "x")), "teamcenter connection: connect gates");
            check(Fails<ArgumentException>(() => C("connectSso", host: "https://tc", instance: "TC", login: "https://sso")) && Fails<ArgumentException>(() => C("connectSso", "user", host: "https://tc", instance: "TC", login: "https://sso", app: "A")) && Fails<ArgumentException>(() => C("read", host: "https://tc")) && Fails<ArgumentException>(() => C("disconnect", pw: "x")), "teamcenter connection: sso / stray gates");

            // ---- datasets ----
            TeamcenterLogic.DatasetRequest D(string action, string item = "", string rev = "", string dataset = "", string dsName = "", string type = "", string tia = "", string name = "", string cache = "", bool dryRun = true)
                => TeamcenterLogic.ValidateDatasetRequest(action, item, rev, dataset, dsName, type, tia, name, cache, dryRun);
            check(D("checkout", "000495", "A", "T4TiaProjectDataset", "Project30", dryRun: false).Writes && D("checkin", "000495", "A", "T4TiaLibraryDataset", "Lib7", dryRun: false).DatasetType == "T4TiaLibraryDataset" && D("cancelCheckout", "000495", "A", "T4TiaProjectDataset", "Project30", dryRun: false).Writes, "teamcenter datasets: lock actions");
            check(!D("search", type: "Project", tia: "P*").Writes && !D("search", type: "GlobalLibrary", item: "0004*", dryRun: false).Writes && D("download", "000495", "A", type: "Project", cache: "Overwrite", dryRun: false).LocalCacheOption == "Overwrite", "teamcenter datasets: search never writes, download");
            check(Fails<ArgumentException>(() => D("checkout", "000495", "A", "Dataset", "P")) && Fails<ArgumentException>(() => D("checkout", "000495", "A", "T4TiaProjectDataset")) && Fails<ArgumentException>(() => D("search", type: "Project")) && Fails<ArgumentException>(() => D("search", type: "Item", tia: "P*")) && Fails<ArgumentException>(() => D("download", "000495", "A", type: "Project")), "teamcenter datasets: missing arguments refused");
            check(Fails<ArgumentException>(() => D("checkout", "000495", "A", "T4TiaProjectDataset", "P", type: "Project")) && Fails<ArgumentException>(() => D("search", type: "Project", tia: "P*", cache: "Overwrite")) && Fails<ArgumentException>(() => D("download", "000495", "A", type: "Project", cache: "Overwrite", dsName: "x")), "teamcenter datasets: stray arguments refused");

            // ---- item / revision details, custom attributes ----
            var item = TeamcenterLogic.ParseItemDetails("{\"itemName\":\"Proj_Object\",\"teamcenterItemType\":\"T4TiaProject\",\"comment\":\"c\",\"teamcenterFolder\":\"Home\\\\TIA\",\"teamcenterProject\":[\"P1\",\"P2\"]}");
            check(item.ItemName == "Proj_Object" && item.TeamcenterItemType == "T4TiaProject" && item.TeamcenterProject.SequenceEqual(new[] { "P1", "P2" }) && item.ItemId == "", "teamcenter details: item details");
            check(Fails<ArgumentException>(() => TeamcenterLogic.ParseItemDetails("{\"itemName\":\"x\"}")) && Fails<ArgumentException>(() => TeamcenterLogic.ParseItemDetails("{\"itemName\":\"x\",\"teamcenterItemType\":\"T\",\"folder\":\"f\"}")) && Fails<ArgumentException>(() => TeamcenterLogic.ParseItemDetails("{\"itemName\":\"x\",\"teamcenterItemType\":\"T\",\"teamcenterProject\":\"P\"}")), "teamcenter details: required / unknown keys");
            var revision = TeamcenterLogic.ParseRevisionDetails("{\"revisionId\":\"B\",\"comment\":\"Revised\"}");
            check(revision.RevisionId == "B" && revision.Comment == "Revised" && TeamcenterLogic.ParseRevisionDetails("{}").RevisionId == "" && Fails<ArgumentException>(() => TeamcenterLogic.ParseRevisionDetails("{\"itemId\":\"x\"}")), "teamcenter details: revision details");
            var attributes = TeamcenterLogic.ParseCustomAttributes("{\"Owner\":\"me\",\"Count\":232,\"Flag\":true}");
            check(attributes["Owner"] == "me" && attributes["Count"] == "232" && attributes["Flag"] == "true" && TeamcenterLogic.ParseCustomAttributes("").Count == 0 && Fails<ArgumentException>(() => TeamcenterLogic.ParseCustomAttributes("{\"List\":[1]}")), "teamcenter details: custom attribute values as text");

            // ---- workflow ----
            TeamcenterLogic.WorkflowRequest W(string action, string target = "project", string lib = "", string type = "", string item = "", string rev = "", string cache = "", string details = "{}", string revDetails = "{}", string attrs = "{}", bool confirm = false, bool dryRun = true)
                => TeamcenterLogic.ValidateWorkflowRequest(action, target, lib, type, item, rev, cache, details, revDetails, attrs, confirm, dryRun);
            check(!W("readCustomAttributes", type: "T4TiaProject").Writes && !W("readCustomAttributes", type: "T4TiaProject", dryRun: false).Writes && W("readCustomAttributes", "globalLibrary", "Lib7", "T4TiaLibrary").Target == "globalLibrary", "teamcenter workflow: attributes read never writes");
            check(!W("save", cache: "Overwrite").Writes && W("save", cache: "Overwrite", confirm: true, dryRun: false).Writes && W("saveWithProxyObject", cache: "DoNotOverwrite", confirm: true, dryRun: false).LocalCacheOption == "DoNotOverwrite", "teamcenter workflow: save needs confirmSave");
            check(W("saveToItem", item: "000495", rev: "A", cache: "Overwrite", confirm: true, dryRun: false).Writes && W("saveToItemWithProxyObject", item: "000495", rev: "B", cache: "Overwrite", confirm: true, dryRun: false).Writes, "teamcenter workflow: save to item");
            var newItem = W("saveAsNewItem", details: "{\"itemName\":\"P\",\"teamcenterItemType\":\"T4TiaProject\"}", attrs: "{\"Owner\":\"me\"}", confirm: true, dryRun: false);
            check(newItem.Writes && newItem.ItemDetails!.ItemName == "P" && newItem.CustomAttributes["Owner"] == "me" && newItem.NeedsAttributes, "teamcenter workflow: new item with attributes");
            var newRevision = W("saveAsNewRevisionWithProxyObject", revDetails: "{\"revisionId\":\"B\"}", confirm: true, dryRun: false);
            check(newRevision.Writes && newRevision.RevisionDetails!.RevisionId == "B" && newRevision.CustomAttributes.Count == 0 && W("saveAsNewRevision", type: "T4TiaProject", revDetails: "{}", attrs: "{\"Owner\":\"me\"}", confirm: true, dryRun: false).CustomAttributes.Count == 1, "teamcenter workflow: new revision");
            check(Fails<ArgumentException>(() => W("save", cache: "Overwrite", dryRun: false)) && Fails<ArgumentException>(() => W("save")) && Fails<ArgumentException>(() => W("saveToItem", item: "000495", cache: "Overwrite")) && Fails<ArgumentException>(() => W("saveAsNewItem", details: "{\"itemName\":\"P\"}")) && Fails<ArgumentException>(() => W("saveAsNewRevision", attrs: "{\"Owner\":\"me\"}")), "teamcenter workflow: missing arguments refused");
            check(Fails<ArgumentException>(() => W("readCustomAttributes")) && Fails<ArgumentException>(() => W("save", cache: "Overwrite", item: "000495")) && Fails<ArgumentException>(() => W("save", cache: "Overwrite", details: "{\"itemName\":\"P\",\"teamcenterItemType\":\"T\"}")) && Fails<ArgumentException>(() => W("saveAsNewItem", details: "{\"itemName\":\"P\",\"teamcenterItemType\":\"T\"}", type: "T")) && Fails<ArgumentException>(() => W("save", "globalLibrary", cache: "Overwrite")) && Fails<ArgumentException>(() => W("save", lib: "Lib7", cache: "Overwrite")), "teamcenter workflow: stray arguments refused");
        }
    }
}
