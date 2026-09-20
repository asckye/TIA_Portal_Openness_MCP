using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    // Phase 6 ⑥-③ (2.7.42): pure logic (no Siemens dependency) for the TIA Portal Test Suite option package
    // (Siemens.Engineering.TestSuite, identical on V20 / V21): style guide rule sets, application test cases and test sets,
    // system test cases; scope, execution, file exchange and library master copies.
    internal static class TestSuiteLogic
    {
        internal static string RequireOneOf(string value, string[] allowed, string parameter) => HardwareServicesLogic.RequireOneOf(value, allowed, parameter);
        private static void RequireText(string value, string parameter, int max = 256)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Trim() != value) throw new ArgumentException("Exact nonempty " + parameter + " required (max " + max + " chars, no surrounding whitespace).");
        }
        private static void Refuse(string value, string parameter, string reason)
        {
            if (!string.IsNullOrEmpty(value)) throw new ArgumentException(parameter + " " + reason);
        }

        // ---- official names (pinned member by member by TestSuiteShapeChecks) ----------------------------------------------------
        internal static readonly string[] Categories = { "styleGuide", "application", "system" };
        internal static readonly string[] Kinds = { "case", "testSet" };                                                // testSet: application only (ApplicationTestSet)
        internal static readonly string[] ImportOptions = { "None", "Override", "SkipInactiveCultures", "ActivateInactiveCultures" }; // Siemens.Engineering.ImportOptions (LoadFromFile loadOptions)
        internal static readonly string[] RSLoadOptions = { "None", "IgnorePropertyErrors", "IgnoreMissingAttributes", "SkipInvalidObjects" }; // StyleGuide.RSLoadOptions (flags)
        internal static readonly string[] TCLoadOptions = { "None", "IgnoreInvalidObject" };                            // ApplicationTest.TCLoadOptions and SystemTest.TCLoadOptions
        internal static readonly string[] TSLoadOptions = { "None", "IgnoreInvalidObject" };                            // ApplicationTest.TSLoadOptions (test set import)
        internal static readonly string[] ExecutionModes = { "SystemManagedPLCSIMInstance", "ExternallyManagedPLCSIMInstance" };
        internal static readonly string[] ServerInterfaces = { "UserDefined", "StandardSIMATIC", "SiOMECompanionSpecification" };
        internal static readonly string[] UpdateOptions = { "Add", "Override" };
        internal static readonly string[] TestResultsStates = { "Success", "Information", "Warning", "Error" };
        internal static readonly string[] ScopeKinds = { "project", "deviceGroup", "plc", "blocks", "tags", "types", "units" }; // official style-guide scope objects

        internal static readonly string[] ExchangeActions = { "import", "export", "delete", "importTestSets" };
        internal static readonly string[] ManageActions = { "read", "rename", "setScope", "copyScope", "createFromMasterCopy", "showInEditor" };

        internal static string CategoryLabel(string category) => RequireOneOf(category, Categories, "category") switch { "styleGuide" => "rule set", "application" => "test case", _ => "system test case" };
        internal static string RequireKind(string category, string kind)
        {
            var k = RequireOneOf(string.IsNullOrEmpty(kind) ? "case" : kind, Kinds, "kind");
            if (k == "testSet" && category != "application") throw new ArgumentException("kind testSet (ApplicationTestSet) exists in the application category only.");
            return k;
        }
        // Style-guide RSLoadOptions are flags (IgnoreMissingAttributes | SkipInvalidObjects); the two TCLoadOptions and TSLoadOptions are single values.
        internal static string[] ParseLoadOptions(string category, string action, string loadOptions)
        {
            var allowed = action == "importTestSets" ? TSLoadOptions : category == "styleGuide" ? RSLoadOptions : TCLoadOptions;
            var parts = (string.IsNullOrWhiteSpace(loadOptions) ? "None" : loadOptions).Split(new[] { '|', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToArray();
            if (parts.Length == 0 || parts.Distinct(StringComparer.Ordinal).Count() != parts.Length) throw new ArgumentException("loadOptions must list distinct " + string.Join("/", allowed) + " names.");
            if (parts.Length > 1 && category != "styleGuide") throw new ArgumentException("loadOptions takes a single value for " + category + " (" + string.Join("/", allowed) + "); only style-guide RSLoadOptions combine with |.");
            foreach (var part in parts) RequireOneOf(part, allowed, "loadOptions");
            return parts;
        }

        // ---- read ----------------------------------------------------------------------------------------------------------------
        internal static string ValidateReadRequest(string category, string kind, int offset, int limit)
        {
            RequireOneOf(category, Categories, "category"); HardwareServicesLogic.ValidatePagination(offset, limit);
            return RequireKind(category, kind);
        }

        // ---- exchange (import / export / delete) ---------------------------------------------------------------------------------
        internal sealed class ExchangeRequest { public string Action = ""; public string Kind = "case"; public bool Writes; public bool WritesFile; public string ImportOption = "None"; public string[] LoadFlags = Array.Empty<string>(); }
        internal static ExchangeRequest ValidateExchangeRequest(string category, string action, string name, string filePath, string importOptions, string loadOptions, string kind, bool dryRun)
        {
            RequireOneOf(category, Categories, "category"); RequireOneOf(action, ExchangeActions, "action");
            var r = new ExchangeRequest { Action = action, Kind = RequireKind(category, kind) };
            if (action == "importTestSets" && category != "application") throw new ArgumentException("importTestSets (ApplicationTestSystemGroup.LoadFromFile) exists in the application category only.");
            if (action == "import" || action == "importTestSets") { if (!string.IsNullOrEmpty(name)) RequireText(name, "name"); }
            else RequireText(name, "name");
            if (action == "export" && r.Kind == "testSet") throw new ArgumentException("ApplicationTestSet has no SaveToFile; export applies to kind case (test sets are imported with importTestSets).");
            if (action == "import" && r.Kind == "testSet") throw new ArgumentException("Use importTestSets for test sets (ApplicationTestSystemGroup.LoadFromFile).");
            if (action != "delete") RequireText(filePath, "filePath", 1024); else Refuse(filePath, "filePath", "does not apply to delete.");
            if (action == "import" || action == "importTestSets") { r.ImportOption = RequireOneOf(string.IsNullOrEmpty(importOptions) ? "None" : importOptions, ImportOptions, "importOptions"); r.LoadFlags = ParseLoadOptions(category, action, loadOptions); }
            else { if (importOptions != "None") Refuse(importOptions, "importOptions", "applies to import / importTestSets only (the default None is tolerated)."); Refuse(loadOptions, "loadOptions", "applies to import / importTestSets only."); }
            r.WritesFile = action == "export" && !dryRun; r.Writes = action != "export" && !dryRun;
            return r;
        }

        // ---- execution -----------------------------------------------------------------------------------------------------------
        internal sealed class RunRequest { public string Kind = "case"; public string[] Names = Array.Empty<string>(); public bool RunAll; public bool External; }
        internal static RunRequest ValidateRunRequest(string category, string name, string namesJson, bool runAll, string kind, bool confirmExternalExecution, bool dryRun)
        {
            RequireOneOf(category, Categories, "category");
            var r = new RunRequest { Kind = RequireKind(category, kind), RunAll = runAll };
            var names = SivarcLogic.ParseNames(namesJson, "namesJson");
            if (!string.IsNullOrEmpty(name)) { RequireText(name, "name"); names = new[] { name }.Concat(names).Distinct(StringComparer.Ordinal).ToArray(); }
            if (runAll && names.Length > 0) throw new ArgumentException("runAll executes the whole system group; do not combine it with name / namesJson.");
            if (!runAll && names.Length == 0) throw new ArgumentException("name, namesJson or runAll=true is required; unbounded execution without a selection is refused.");
            if (runAll && r.Kind == "testSet") throw new ArgumentException("runAll runs the ApplicationTestSystemGroup (its test cases); select test sets by name / namesJson.");
            r.Names = names; r.External = category != "styleGuide";
            if (r.External) HardwareServicesLogic.RequireConfirmation(confirmExternalExecution, "confirmExternalExecution", dryRun);
            return r;
        }

        // ---- manage (scope, rename, master copies, editor) -------------------------------------------------------------------------
        internal sealed class ScopeEntry { public string Kind = ""; public string SoftwarePath = ""; public string GroupPath = ""; public string Name = ""; }
        internal static ScopeEntry[] ParseScopeEntries(string json)
        {
            var array = (string.IsNullOrWhiteSpace(json) ? new JsonArray() : JsonNode.Parse(json) as JsonArray) ?? throw new ArgumentException("scopeJson must be a JSON array of scope entries {kind, softwarePath, groupPath, name}.");
            var entries = new List<ScopeEntry>();
            foreach (var node in array)
            {
                var o = node as JsonObject ?? throw new ArgumentException("scopeJson entries must be objects.");
                var unknown = o.Select(p => p.Key).Where(k => k != "kind" && k != "softwarePath" && k != "groupPath" && k != "name").ToArray();
                if (unknown.Length > 0) throw new ArgumentException("scopeJson entry keys not allowed: " + string.Join(", ", unknown) + " (allowed: kind, softwarePath, groupPath, name).");
                var e = new ScopeEntry { Kind = RequireOneOf(o["kind"]?.GetValue<string>() ?? "", ScopeKinds, "scopeJson.kind"), SoftwarePath = o["softwarePath"]?.GetValue<string>() ?? "", GroupPath = o["groupPath"]?.GetValue<string>() ?? "", Name = o["name"]?.GetValue<string>() ?? "" };
                bool needsSoftware = e.Kind == "plc" || e.Kind == "blocks" || e.Kind == "tags" || e.Kind == "types" || e.Kind == "units";
                if (needsSoftware) RequireText(e.SoftwarePath, "scopeJson.softwarePath", 1024); else Refuse(e.SoftwarePath, "scopeJson.softwarePath", "applies to plc / blocks / tags / types / units entries only.");
                if (e.Kind == "deviceGroup") RequireText(e.Name, "scopeJson.name"); else Refuse(e.Name, "scopeJson.name", "applies to deviceGroup entries only.");
                if (e.Kind != "blocks" && e.Kind != "tags" && e.Kind != "types") Refuse(e.GroupPath, "scopeJson.groupPath", "applies to blocks / tags / types entries only (a user group below the system group).");
                entries.Add(e);
            }
            return entries.ToArray();
        }
        internal sealed class ManageRequest
        {
            public string Action = ""; public string Kind = "case"; public bool Writes; public string ExecutionMode = ""; public string ServerInterface = ""; public string UpdateOption = ""; public ScopeEntry[] Scope = Array.Empty<ScopeEntry>();
        }
        internal static ManageRequest ValidateManageRequest(string category, string name, string action, string kind, string newName, string softwarePath, string instanceName, string executionMode, string opcUaServerAddress, string serverInterfaceType, string interfaceFolderPath, string updateOptions, string scopeJson, string targetName, string masterCopyPath, bool dryRun)
        {
            RequireOneOf(category, Categories, "category"); RequireOneOf(action, ManageActions, "action");
            var r = new ManageRequest { Action = action, Kind = RequireKind(category, kind) };
            if (action != "createFromMasterCopy" || !string.IsNullOrEmpty(name)) RequireText(name, "name");
            if (action == "rename") RequireText(newName, "newName"); else Refuse(newName, "newName", "applies to rename only.");
            if (action == "createFromMasterCopy") { RequireText(masterCopyPath, "masterCopyPath", 1024); if (r.Kind == "testSet") throw new ArgumentException("ApplicationTestSetComposition has no CreateFrom(MasterCopy); kind case only."); }
            else Refuse(masterCopyPath, "masterCopyPath", "applies to createFromMasterCopy only.");
            if (action == "copyScope") { if (category != "styleGuide") throw new ArgumentException("copyScope (RuleSet.CopyScope) exists for style-guide rule sets only."); RequireText(targetName, "targetName"); }
            else Refuse(targetName, "targetName", "applies to copyScope only.");
            if (action == "showInEditor" && category == "system") throw new ArgumentException("SystemTestCase has no ShowInEditor (rule sets, application test cases and test sets do).");
            if ((action == "setScope" || action == "copyScope") && r.Kind == "testSet") throw new ArgumentException("Test sets carry no scope (ApplicationTestSet has Name / Delete / ShowInEditor only).");
            bool scopeStyle = action == "setScope" && category == "styleGuide", scopeApplication = action == "setScope" && category == "application", scopeSystem = action == "setScope" && category == "system";
            if (scopeStyle) { r.UpdateOption = RequireOneOf(string.IsNullOrEmpty(updateOptions) ? "Override" : updateOptions, UpdateOptions, "updateOptions"); r.Scope = ParseScopeEntries(scopeJson); if (r.Scope.Length == 0) throw new ArgumentException("scopeJson must list at least one scope entry for a style-guide setScope."); }
            else { Refuse(updateOptions, "updateOptions", "applies to style-guide setScope only."); if (!string.IsNullOrWhiteSpace(scopeJson) && scopeJson.Trim() != "[]") throw new ArgumentException("scopeJson applies to style-guide setScope only."); }
            if (scopeApplication)
            {
                RequireText(softwarePath, "softwarePath", 1024);
                if (!string.IsNullOrEmpty(instanceName) || !string.IsNullOrEmpty(executionMode)) { RequireText(instanceName, "instanceName"); r.ExecutionMode = RequireOneOf(executionMode, ExecutionModes, "executionMode"); }
            }
            else { Refuse(softwarePath, "softwarePath", "applies to application setScope only."); Refuse(instanceName, "instanceName", "applies to application setScope only."); Refuse(executionMode, "executionMode", "applies to application setScope only."); }
            if (scopeSystem)
            {
                RequireText(opcUaServerAddress, "opcUaServerAddress", 1024);
                if (!opcUaServerAddress.StartsWith("opc.tcp://", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("opcUaServerAddress must use the format opc.tcp://server:port/path (official).");
                r.ServerInterface = RequireOneOf(serverInterfaceType, ServerInterfaces, "serverInterfaceType");
                if (!string.IsNullOrEmpty(interfaceFolderPath) && !System.IO.Path.IsPathRooted(interfaceFolderPath)) throw new ArgumentException("interfaceFolderPath must be an absolute directory path (official: relative paths are refused).");
            }
            else { Refuse(opcUaServerAddress, "opcUaServerAddress", "applies to system setScope only."); Refuse(serverInterfaceType, "serverInterfaceType", "applies to system setScope only."); Refuse(interfaceFolderPath, "interfaceFolderPath", "applies to system setScope only."); }
            r.Writes = action != "read" && action != "showInEditor" && !dryRun;
            return r;
        }
    }
}
