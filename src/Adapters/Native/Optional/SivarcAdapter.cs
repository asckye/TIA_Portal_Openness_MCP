#if OPTIONAL_V20_PLUS

using static TiaMcp.Adapters.Native.Optional.OptionalEvidence;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Native.Hmi;
using EngineeringGroupOperations = TiaMcp.Adapters.Native.Plc.PlcGroupOperations;
using EngineeringScalarProperties = TiaMcp.Adapters.Native.Optional.OptionalScalars;
using EngineeringObjectAddress = TiaMcp.Adapters.Native.Optional.OptionalObjectAddress;
using NativeFileOutput = TiaMcp.Adapters.Native.Safety.SafetyFileOutput;
using PlcBlockServicesLogic = TiaMcp.Adapters.Native.Safety.SafetyPrimitives;
using HardwareServicesLogic = TiaMcp.Adapters.Native.Optional.OptionalRules;
using ArgumentRules = TiaMcp.Adapters.Native.Optional.OptionalRules;
using EngineeringPath = TiaMcp.Adapters.Hardware.HardwarePath;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.Library.MasterCopies;
using Siemens.Engineering.Library.Types;
using Siemens.Engineering.SiVArc;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using TiaMcpServer.ModelContextProtocol;
using Logic = TiaMcp.Adapters.Native.Optional.SivarcPolicy;
using CreateOptions = Siemens.Engineering.SiVArc.CreateOptions;
using SivarcFamily = TiaMcp.Adapters.Native.Optional.OptionalSession.SivarcFamily;

namespace TiaMcp.Adapters.Native.Optional
{
    // The SiVArc option package (Siemens.Engineering.SiVArc). Official SiVArc manual, "SiVArc Openness"
    // chapter: rule tables / folders, rule objects and groups, copying rules from master copies, library objects as rule
    // references, PLC / HMI device columns, tag / text definitions and tag member settings on blocks, the expression resolver,
    // layout data export / import on screens, upgrading SiVArc definitions and generation. The project service is
    // project.GetService<Sivarc>(); SivarcDataProvider hangs on a CodeBlock, SivarcDefinitionsUpgrader on the PlcSoftware and
    // LayoutData on a classic Screen or Unified HmiScreen. The VM has no SiVArc licence: shape checks only, and every tool
    // answers NotSupported when the service is absent. Writes need the SiVArc licence (TIA raises "License not found").
    // Native observations: TIA V21; original observation date not recorded. See docs/reference/real-machine-ledger.md.
    internal sealed partial class SivarcAdapter
    {
        private readonly OptionalSession _session;

        public SivarcAdapter(OptionalSession session) => _session = session;

        private static string SivarcName(object node) => EngineeringGroupOperations.Get(node, "Name").ToString()!;
        private void SivarcDelete(object node)
        {
            switch (node)
            {
                case ScreenRuleFolder f: f.Delete(); break; case ScreenRuleTable t: t.Delete(); break; case ScreenRuleGroup g: g.Delete(); break;
                case ScreenRule r: r.Delete(); break;
                case TagRuleFolder f: f.Delete(); break; case TagRuleTable t: t.Delete(); break; case TagRuleGroup g: g.Delete(); break; case TagRule r: r
                    .Delete(); break;
                case AdvancedTagRuleFolder f: f.Delete(); break; case AdvancedTagRuleTable t: t.Delete(); break; case AdvancedTagRuleGroup g: g.Delete(); break;
                case AdvancedTagRule r: r.Delete(); break;
                case AlarmRuleFolder f: f.Delete(); break; case AlarmRuleTable t: t.Delete(); break; case AlarmRuleGroup g: g.Delete(); break; case AlarmRule r
                    : r.Delete(); break;
                case CopyRuleFolder f: f.Delete(); break; case CopyRuleTable t: t.Delete(); break; case CopyRuleGroup g: g.Delete(); break; case CopyRule r: r
                    .Delete(); break;
                case TextlistRuleFolder f: f.Delete(); break; case TextlistRuleTable t: t.Delete(); break; case TextlistRuleGroup g: g.Delete(); break;
                case TextlistRule r: r.Delete(); break;
                default: throw OptionalSession.SivarcShape(node);
            }
        }

        // ---- navigation ----------------------------------------------------------------------------------------------------------
        // folderPath "" = the anchor (the family's system folder); "A/B" = nested user folders.
        private object SivarcFolder(SivarcFamily family, Sivarc sivarc, string folderPath)
        {
            object current = family.Anchor(sivarc);
            foreach (var part in EngineeringGroupOperations.Parts(folderPath, true))
                current = EngineeringGroupOperations.Find(family.Folders(current), part)
                    ?? throw new PlcSoftwareException("NotFound", "SiVArc " + family.Category + " rule folder not found: " + folderPath);
            return current;
        }
        // tablePath "Folder/Sub/Table" (last segment = table name).
        private object SivarcTable(SivarcFamily family, Sivarc sivarc, string tablePath)
        {
            var parts = EngineeringGroupOperations.Parts(tablePath);
            var folder = SivarcFolder(family, sivarc, string.Join("/", parts.Take(parts.Length - 1)));
            return EngineeringGroupOperations.Find(family.Tables(folder), parts.Last())
                ?? throw new PlcSoftwareException("NotFound", "SiVArc " + family.Category + " rule table not found: " + tablePath);
        }
        // groupPath "" = the table; "G/Sub" = nested rule groups under the table.
        private static object SivarcGroup(SivarcFamily family, object table, string groupPath)
        {
            object current = table;
            foreach (var part in EngineeringGroupOperations.Parts(groupPath, true))
                current = EngineeringGroupOperations.Find(family.Groups(current), part)
                    ?? throw new PlcSoftwareException("NotFound", "SiVArc rule group not found: " + groupPath);
            return current;
        }

        // ---- rows ------------------------------------------------------------------------------------------------------------------
        private object? SivarcReferenceJson(object? reference)
        {
            if (reference == null) return null;
            var row = new HmiObject { ["refClass"] = reference.GetType().Name };
            Safe(row, "name", () => reference is IEngineeringObject ? EngineeringGroupOperations.Get(reference, "Name").ToString() : null);
            if (reference is MasterCopy || reference is LibraryType || reference is MasterCopyFolder || reference is LibraryTypeFolder)
                Safe(row, "libraryPath", () => _session
                    .LibraryPathOf(reference, reference is MasterCopy || reference is MasterCopyFolder ? "MasterCopySystemFolder" : "LibraryTypeSystemFolder"));
            return row;
        }
        private HmiObject SivarcRuleRow(object rule, bool includeLayoutFields)
        {
            var row = new HmiObject { ["name"] = SivarcName(rule), ["ruleClass"] = rule.GetType().Name };
            void Common(string comment, string condition, ConditionOperator op, bool enabled) {
                row["comment"] = comment; row["condition"] = condition; row["conditionOperator"] = op.ToString(); row["enabled"] = enabled; }
            switch (rule)
            {
                case ScreenRule r:
                    Common(r.Comment, r.Condition, r.ConditionOperator, r.Enabled); row["layoutField"] = r.LayoutField; row["loopCount"] = r.LoopCount;
                    Safe(row, "programBlock", () => SivarcReferenceJson(r.ProgramBlock));
                    Safe(row, "libraryScreen", () => SivarcReferenceJson(r.LibraryScreen));
                    Safe(row, "screenObjectLibraryItem", () => SivarcReferenceJson(r.ScreenObjectLibraryItem));
                    if (includeLayoutFields) Safe(row, "layoutFields", () => new HmiArray(r.GetLayoutFields().Take(200).Select(f => (object)f).ToArray()));
                    break;
                case ScreenRuleGroup g:
                    Common(g.Comment, g.Condition, g.ConditionOperator, g.Enabled); row["layoutField"] = g.LayoutField; row["loopCount"] = g.LoopCount;
                    Safe(row, "programBlock", () => SivarcReferenceJson(g.ProgramBlock));
                    Safe(row, "libraryScreen", () => SivarcReferenceJson(g.LibraryScreen));
                    Safe(row, "screenObjectLibraryItem", () => SivarcReferenceJson(g.ScreenObjectLibraryItem));
                    if (includeLayoutFields) Safe(row, "layoutFields", () => new HmiArray(g.GetLayoutFields().Take(200).Select(f => (object)f).ToArray()));
                    break;
                case TagRule r: Common(r.Comment, r.Condition, r.ConditionOperator, r.Enabled); row["tagGroupHierarchy"] = r.TagGroupHierarchy;
                row["tagTable"] = r.TagTable; break;
                case TagRuleGroup g: Common(g.Comment, g.Condition, g.ConditionOperator, g.Enabled); break;
                case AdvancedTagRule r:
                    Common(r.Comment, r.Condition, r.ConditionOperator, r.Enabled); row["tagGroupHierarchy"] = r.TagGroupHierarchy; row["tagTable"] = r
                        .TagTable;
                    Safe(row, "programBlock", () => SivarcReferenceJson(r.ProgramBlock));
                    Safe(row, "tagLibraryItem", () => SivarcReferenceJson(r.TagLibraryItem)); break;
                case AdvancedTagRuleGroup g: Common(g.Comment, g.Condition, g.ConditionOperator, g.Enabled); break;
                case AlarmRule r: Common(r.Comment, r.Condition, r.ConditionOperator, r.Enabled);
                Safe(row, "programBlock", () => SivarcReferenceJson(r.ProgramBlock));
                Safe(row, "alarmLibraryItem", () => SivarcReferenceJson(r.AlarmLibraryItem)); break;
                case AlarmRuleGroup g: Common(g.Comment, g.Condition, g.ConditionOperator, g.Enabled); break;
                case CopyRule r: Common(r.Comment, r.Condition, r.ConditionOperator, r.Enabled); row["folderStructure"] = r.FolderStructure;
                Safe(row, "libraryObject", () => SivarcReferenceJson(r.LibraryObject)); break;
                case CopyRuleGroup g: Common(g.Comment, g.Condition, g.ConditionOperator, g.Enabled); break;
                case TextlistRule r: Common(r.Comment, r.Condition, r.ConditionOperator, r.Enabled);
                Safe(row, "programBlock", () => SivarcReferenceJson(r.ProgramBlock));
                Safe(row, "textlistLibraryItem", () => SivarcReferenceJson(r.TextlistLibraryItem)); break;
                case TextlistRuleGroup g: Common(g.Comment, g.Condition, g.ConditionOperator, g.Enabled); break;
                default: throw OptionalSession.SivarcShape(rule);
            }
            return row;
        }
        private HmiObject SivarcGroupRow(SivarcFamily family, object group, int depth, int maxDepth, bool includeRules)
        {
            var row = SivarcRuleRow(group, false);
            object groups = family.Groups(group), rules = family.Rules(group);
            row["groupCount"] = EngineeringGroupOperations.Items(groups).Count(); row["ruleCount"] = EngineeringGroupOperations.Items(rules).Count();
            if (includeRules) row["rules"] =
                new HmiArray(EngineeringGroupOperations.Items(rules).Take(200).Select(r => (object)SivarcRuleRow(r, false)).ToArray());
            if (depth < maxDepth) row["groups"] =
                new HmiArray(EngineeringGroupOperations.Items(groups).Select(g => (object)SivarcGroupRow(family, g, depth + 1, maxDepth, includeRules))
                    .ToArray());
            else row["groupsTruncated"] = row["groupCount"]!.OptionalValue<int>() > 0;
            return row;
        }
        private static HmiObject SivarcTableRow(SivarcFamily family, object table)
        {
            var row = new HmiObject { ["name"] = SivarcName(table), ["tableClass"] = table.GetType().Name };
            Safe(row, "isDefault", () => (bool)EngineeringGroupOperations.Get(table, "IsDefault"));
            Safe(row, "groupCount", () => EngineeringGroupOperations.Items(family.Groups(table)).Count());
            Safe(row, "ruleCount", () => EngineeringGroupOperations.Items(family.Rules(table)).Count());
            // rule tables instantiated from a *RuleTableType carry the connected version (official "Version details of instantiated Rule Table")
            Safe(row, "libraryTypeVersion", () =>
            {
                var info = ((IEngineeringServiceProvider)table).GetService<LibraryTypeInstanceInfo>();
                var version = info?.LibraryTypeVersion; if (version == null) return null;
                return new HmiObject {
                    ["typeName"] = version.Parent is LibraryType t ? t.Name : null, ["version"] = version.VersionNumber?.ToString(), ["typeClass"] = version
                        .Parent?.GetType().Name
                };
            });
            return row;
        }
        private HmiObject SivarcFolderRow(SivarcFamily family, object folder, int depth, int maxDepth)
        {
            object folders = family.Folders(folder), tables = family.Tables(folder);
            var row = new HmiObject {
                ["name"] = folder.GetType().GetProperty("Name") == null ? Logic.AnchorProperty(family.Category) : SivarcName(folder), ["folderClass"] = folder
                    .GetType().Name
            };
            row["tables"] = new HmiArray(EngineeringGroupOperations.Items(tables).Select(t => (object)SivarcTableRow(family, t)).ToArray());
            row["folderCount"] = EngineeringGroupOperations.Items(folders).Count();
            if (depth < maxDepth) row["folders"] =
                new HmiArray(EngineeringGroupOperations.Items(folders).Select(f => (object)SivarcFolderRow(family, f, depth + 1, maxDepth)).ToArray());
            else row["foldersTruncated"] = row["folderCount"]!.OptionalValue<int>() > 0;
            return row;
        }
        private static HmiArray SivarcMessageRows(SivarcFeedbackMessageComposition messages, int depth = 0)
            => new HmiArray(EngineeringGroupOperations.Items(messages).Cast<SivarcFeedbackMessage>().Take(2000).Select(m =>
            {
                var row = new HmiObject {
                    ["path"] = m.Path, ["description"] = m.Description, ["messageType"] = m.MessageType.ToString(), ["dateTime"] = EngineeringScalarProperties
                        .Json(m.DateTime), ["errorCount"] = m.ErrorCount, ["warningCount"] = m.WarningCount
                };
                if (depth < 8) Safe(row, "messages", () => SivarcMessageRows(m.Messages, depth + 1));
                return (object)row;
            }).ToArray());

        // ---- tools: rule tree ----------------------------------------------------------------------------------------------------
        public HardwareAddressingReply ReadSivarcRuleTree(string category, string folderPath = "", string tablePath = "", bool includeRules = true,
                int maxDepth = 4, int offset = 0, int limit = 200)
            => _session.RunHmiStepTool("GetSivarcRuleTree", meta =>
            {
                Logic.ValidateTreeRequest(category, folderPath, tablePath, maxDepth, offset, limit);
                var family = _session.GetSivarcFamily(category); var sivarc = _session.RequireSivarc();
                meta["category"] = category; meta["anchor"] = Logic.AnchorProperty(category);
                if (string.IsNullOrEmpty(tablePath))
                {
                    meta["folderPath"] = folderPath; meta["tree"] = SivarcFolderRow(family, SivarcFolder(family, sivarc, folderPath), 0, maxDepth);
                    return "SiVArc " + category + " rule folders and tables read (typed " + Logic.AnchorProperty(category)
                        + " anchor); give tablePath for one table's groups and rules. No generation.";
                }
                var table = SivarcTable(family, sivarc, tablePath);
                meta["tablePath"] = tablePath; meta["table"] = SivarcTableRow(family, table);
                var rules = EngineeringGroupOperations.Items(family.Rules(table)).Select(r => (object)SivarcRuleRow(r, false)).ToArray();
                Page(rules, offset, limit, meta);
                meta["groups"] =
                    new HmiArray(EngineeringGroupOperations.Items(family.Groups(table))
                        .Select(g => (object)SivarcGroupRow(family, g, 1, maxDepth, includeRules)).ToArray());
                return "SiVArc rule table read: paged top-level rules (records) and nested groups (up to maxDepth, 200 rules per group). Device columns need " +
                    "ManageSivarcTableRule read with deviceNamesJson. No generation.";
            });

        // ---- tools: folders / tables ----------------------------------------------------------------------------------------------
        public HardwareAddressingReply ManageSivarcRuleContainer(string category, string kind, string path, string action = "read", string libraryName = "",
                string typePath = "", string typeVersion = "", bool confirmDelete = false, bool dryRun = true)
            => _session.RunHmiStepTool("ManageSivarcRuleContainer", meta =>
            {
                bool write = Logic.ValidateContainerRequest(category, kind, path, action, typePath, typeVersion, confirmDelete, dryRun);
                using var access = write ? _session.AcquireHmiEditAccess() : null;
                var family = _session.GetSivarcFamily(category); var sivarc = _session.RequireSivarc();
                var parts = EngineeringGroupOperations.Parts(path); var parentPath = string.Join("/", parts.Take(parts.Length - 1));
                meta["category"] = category; meta["kind"] = kind; meta["path"] = path; meta["action"] = action; meta["dryRun"] = dryRun;
                meta["mayHaveChanged"] = false;
                var parent = SivarcFolder(family, sivarc, parentPath);
                object collection = kind == "folder" ? family.Folders(parent) : family.Tables(parent);
                var existing = EngineeringGroupOperations.Find(collection, parts.Last());
                if (action == "read")
                {
                    if (existing == null) throw new PlcSoftwareException("NotFound", "SiVArc " + kind + " not found: " + path);
                    meta[kind] = kind == "folder" ? SivarcFolderRow(family, existing, 0, 1) : SivarcTableRow(family, existing);
                    return "SiVArc rule " + kind + " read; no modification.";
                }
                if (action == "delete")
                {
                    if (existing == null) throw new PlcSoftwareException("NotFound", "SiVArc " + kind + " not found: " + path);
                    meta["before"] = kind == "folder" ? SivarcFolderRow(family, existing, 0, 1) : SivarcTableRow(family, existing);
                    if (kind == "table" && meta["before"]!.Field("isDefault")?.OptionalValue<bool>() == true)
                        throw new InvalidOperationException("The default rule table cannot be deleted.");
                    if (kind == "folder"
                            && (meta["before"]!.Field("tables")!.Array().Count > 0 || meta["before"]!.Field("folderCount")!.OptionalValue<int>() > 0))
                        throw new InvalidOperationException("Nonempty rule folder deletion refused; delete its tables and sub-folders first.");
                    if (!write) return "Delete preview of an empty SiVArc rule " + kind + "; nothing changed.";
                    meta["mayHaveChanged"] = true; SivarcDelete(existing);
                    meta["verifiedAbsent"] = EngineeringGroupOperations
                        .Find(kind == "folder" ? family.Folders(SivarcFolder(family, sivarc, parentPath)) : family
                            .Tables(SivarcFolder(family, sivarc, parentPath)), parts.Last()) == null;
                    if (meta["verifiedAbsent"]!.OptionalValue<bool>() != true)
                        throw new InvalidOperationException("SiVArc " + kind + " still present after Delete().");
                    return "SiVArc rule " + kind + " deleted and verified absent; project not saved.";
                }
                if (existing != null) throw new InvalidOperationException("SiVArc " + kind + " already exists: " + path);
                LibraryTypeVersion? version = null;
                if (action == "createFromType")
                {
                    var type = _session.ExactLibraryType(_session.ExactOpenEngineeringLibrary(libraryName), typePath);
                    version = _session.ExactTypeVersion(type, typeVersion);
                    if (!family.TypeVersionType.IsInstanceOfType(version))
                        throw new ArgumentException("Type version is " + version.GetType().Name + ", not a " + family.TypeVersionType.Name + " (category "
                            + category + ").");
                    meta["source"] = new HmiObject {
                        ["typePath"] = typePath, ["typeClass"] = type.GetType().Name, ["version"] = version.VersionNumber?.ToString() };
                }
                meta["nativeSignature"] = action == "createFromType" ? collection.GetType().Name + ".CreateFrom(" + family.TypeVersionType.Name + ")"
                    : collection.GetType().Name + ".Create(string)";
                if (!write) return "SiVArc rule " + kind + " create preview; nothing changed (real creation needs the SiVArc licence).";
                meta["mayHaveChanged"] = true;
                object created = kind == "folder" ? family.CreateFolder(collection, parts.Last()) : version != null ? family
                    .CreateTableFromType(collection, version) : family.CreateTable(collection, parts.Last());
                var fresh = EngineeringGroupOperations
                    .Find(kind == "folder" ? family.Folders(SivarcFolder(family, sivarc, parentPath)) : family.Tables(SivarcFolder(family, sivarc, parentPath)),
                        SivarcName(created))
                    ?? throw new InvalidOperationException("Created SiVArc " + kind + " not found on re-navigation.");
                meta["created"] = kind == "folder" ? SivarcFolderRow(family, fresh, 0, 1) : SivarcTableRow(family, fresh);
                return "SiVArc rule " + kind + " created and read back; project not saved.";
            });

        // ---- tools: rules / groups ------------------------------------------------------------------------------------------------
        private object ResolveSivarcReference(Logic.Reference reference)
        {
            switch (reference.Kind)
            {
                case "plcBlock":
                    var plc = _session.ExactPlcForEngineering(reference.SoftwarePath, false);
                    return _session.ExactObjectUnder(plc.BlockGroup, reference.Path, "Blocks", "PLC block") is CodeBlock block ? block
                        : throw new ArgumentException("Referenced block is not a CodeBlock: " + reference.Path);
                case "masterCopy": return _session.ExactMasterCopy(reference.LibraryName, reference.Path);
                case "libraryType": return _session.ExactLibraryType(_session.ExactOpenEngineeringLibrary(reference.LibraryName), reference.Path);
                case "masterCopyFolder": return _session
                    .EngineeringLibraryFolder(_session.ExactOpenEngineeringLibrary(reference.LibraryName), reference.Path, "MasterCopyFolder");
                default: return _session.EngineeringLibraryFolder(_session.ExactOpenEngineeringLibrary(reference.LibraryName), reference.Path, "TypeFolder");
            }
        }
        // Typed setters for the reference properties; V20 declares them with SiVArc marker interfaces, V21 as IEngineeringObject.
        private static void SetSivarcReference(object rule, string property, object? value)
        {
#if !OPTIONAL_V21
            ISivarcProgramBlockSource? Program() => value == null ? null : value as ISivarcProgramBlockSource
                ?? throw new ArgumentException(property + " needs a PLC block, block type or block master copy (ISivarcProgramBlockSource).");
            ISivarcDataBlockSource? DataBlock() => value == null ? null : value as ISivarcDataBlockSource
                ?? throw new ArgumentException(property + " needs a data block source (ISivarcDataBlockSource).");
            ISivarcLibraryItem? Item() => value == null ? null : value as ISivarcLibraryItem
                ?? throw new ArgumentException(property + " needs a library master copy, type or folder (ISivarcLibraryItem).");
            ISivarcLibraryMasterCopy? Copy() => value == null ? null : value as ISivarcLibraryMasterCopy
                ?? throw new ArgumentException(property + " needs a library master copy (ISivarcLibraryMasterCopy).");
            switch (rule, property)
            {
                case (ScreenRule r, "ProgramBlock"): r.ProgramBlock = Program(); break; case (ScreenRule r, "LibraryScreen"): r.LibraryScreen = Item(); break;
                case (ScreenRule r, "ScreenObjectLibraryItem"): r.ScreenObjectLibraryItem = Item(); break;
                case (ScreenRuleGroup g, "ProgramBlock"): g.ProgramBlock = Program(); break; case (ScreenRuleGroup g, "LibraryScreen"): g.LibraryScreen =
                    Item(); break; case (ScreenRuleGroup g, "ScreenObjectLibraryItem"): g.ScreenObjectLibraryItem = Item(); break;
                case (AdvancedTagRule r, "ProgramBlock"): r.ProgramBlock = DataBlock(); break; case (AdvancedTagRule r, "TagLibraryItem"): r.TagLibraryItem =
                    Copy(); break;
                case (AlarmRule r, "ProgramBlock"): r.ProgramBlock = Program(); break; case (AlarmRule r, "AlarmLibraryItem"): r.AlarmLibraryItem = Copy();
                break;
                case (CopyRule r, "LibraryObject"): r.LibraryObject = Item(); break;
                case (TextlistRule r, "ProgramBlock"): r.ProgramBlock = Program(); break; case (TextlistRule r, "TextlistLibraryItem"): r.TextlistLibraryItem =
                    Copy(); break;
                default: throw new ArgumentException(property + " is not a reference property of " + rule.GetType().Name + ".");
            }
#else
            IEngineeringObject? Obj() => value == null ? null : value as IEngineeringObject
                ?? throw new ArgumentException(property + " needs an engineering object.");
            switch (rule, property)
            {
                case (ScreenRule r, "ProgramBlock"): r.ProgramBlock = Obj(); break; case (ScreenRule r, "LibraryScreen"): r.LibraryScreen = Obj(); break;
                case (ScreenRule r, "ScreenObjectLibraryItem"): r.ScreenObjectLibraryItem = Obj(); break;
                case (ScreenRuleGroup g, "ProgramBlock"): g.ProgramBlock = Obj(); break; case (ScreenRuleGroup g, "LibraryScreen"): g.LibraryScreen = Obj();
                break; case (ScreenRuleGroup g, "ScreenObjectLibraryItem"): g.ScreenObjectLibraryItem = Obj(); break;
                case (AdvancedTagRule r, "ProgramBlock"): r.ProgramBlock = Obj(); break; case (AdvancedTagRule r, "TagLibraryItem"): r.TagLibraryItem = Obj();
                break;
                case (AlarmRule r, "ProgramBlock"): r.ProgramBlock = Obj(); break; case (AlarmRule r, "AlarmLibraryItem"): r.AlarmLibraryItem = Obj(); break;
                case (CopyRule r, "LibraryObject"): r.LibraryObject = Obj(); break;
                case (TextlistRule r, "ProgramBlock"): r.ProgramBlock = Obj(); break; case (TextlistRule r, "TextlistLibraryItem"): r.TextlistLibraryItem =
                    Obj(); break;
                default: throw new ArgumentException(property + " is not a reference property of " + rule.GetType().Name + ".");
            }
#endif
        }
        // Typed setters for the scalar rule / group properties (ConditionOperator parsed from the official enum names).
        private void SetSivarcScalar(object rule, string property, object? value)
        {
            string S() => value?.OptionalValue<string>() ?? "";
            bool B() => value?.OptionalValue<bool>() ?? throw new ArgumentException(property + " needs true / false.");
            ConditionOperator Op() => (ConditionOperator)Enum
                .Parse(typeof(ConditionOperator), Logic.RequireOneOf(S(), Logic.ConditionOperators, "ConditionOperator"));
            switch (rule)
            {
                case ScreenRule r: switch (property) {
                    case "Name": r.Name = S(); break; case "Comment": r.Comment = S(); break; case "Condition": r.Condition = S(); break;
                    case "ConditionOperator": r.ConditionOperator = Op(); break; case "Enabled": r.Enabled = B(); break; case "LayoutField": r.LayoutField =
                        S(); break; case "LoopCount": r.LoopCount = S(); break; default: throw Unknown();
                } break;
                case ScreenRuleGroup g: switch (property) {
                    case "Name": g.Name = S(); break; case "Comment": g.Comment = S(); break; case "Condition": g.Condition = S(); break;
                    case "ConditionOperator": g.ConditionOperator = Op(); break; case "Enabled": g.Enabled = B(); break; case "LayoutField": g.LayoutField =
                        S(); break; case "LoopCount": g.LoopCount = S(); break; default: throw Unknown();
                } break;
                case TagRule r: switch (property) {
                    case "Name": r.Name = S(); break; case "Comment": r.Comment = S(); break; case "Condition": r.Condition = S(); break;
                    case "ConditionOperator": r.ConditionOperator = Op(); break; case "Enabled": r.Enabled = B(); break; case "TagGroupHierarchy": r
                        .TagGroupHierarchy = S(); break; case "TagTable": r.TagTable = S(); break; default: throw Unknown();
                } break;
                case TagRuleGroup g: switch (property) {
                    case "Name": g.Name = S(); break; case "Comment": g.Comment = S(); break; case "Condition": g.Condition = S(); break;
                    case "ConditionOperator": g.ConditionOperator = Op(); break; case "Enabled": g.Enabled = B(); break; default: throw Unknown();
                } break;
                case AdvancedTagRule r: switch (property) {
                    case "Name": r.Name = S(); break; case "Comment": r.Comment = S(); break; case "Condition": r.Condition = S(); break;
                    case "ConditionOperator": r.ConditionOperator = Op(); break; case "Enabled": r.Enabled = B(); break; case "TagGroupHierarchy": r
                        .TagGroupHierarchy = S(); break; case "TagTable": r.TagTable = S(); break; default: throw Unknown();
                } break;
                case AdvancedTagRuleGroup g: switch (property) {
                    case "Name": g.Name = S(); break; case "Comment": g.Comment = S(); break; case "Condition": g.Condition = S(); break;
                    case "ConditionOperator": g.ConditionOperator = Op(); break; case "Enabled": g.Enabled = B(); break; default: throw Unknown();
                } break;
                case AlarmRule r: switch (property) {
                    case "Name": r.Name = S(); break; case "Comment": r.Comment = S(); break; case "Condition": r.Condition = S(); break;
                    case "ConditionOperator": r.ConditionOperator = Op(); break; case "Enabled": r.Enabled = B(); break; default: throw Unknown();
                } break;
                case AlarmRuleGroup g: switch (property) {
                    case "Name": g.Name = S(); break; case "Comment": g.Comment = S(); break; case "Condition": g.Condition = S(); break;
                    case "ConditionOperator": g.ConditionOperator = Op(); break; case "Enabled": g.Enabled = B(); break; default: throw Unknown();
                } break;
                case CopyRule r: switch (property) {
                    case "Name": r.Name = S(); break; case "Comment": r.Comment = S(); break; case "Condition": r.Condition = S(); break;
                    case "ConditionOperator": r.ConditionOperator = Op(); break; case "Enabled": r.Enabled = B(); break; case "FolderStructure": r
                        .FolderStructure = S(); break; default: throw Unknown();
                } break;
                case CopyRuleGroup g: switch (property) {
                    case "Name": g.Name = S(); break; case "Comment": g.Comment = S(); break; case "Condition": g.Condition = S(); break;
                    case "ConditionOperator": g.ConditionOperator = Op(); break; case "Enabled": g.Enabled = B(); break; default: throw Unknown();
                } break;
                case TextlistRule r: switch (property) {
                    case "Name": r.Name = S(); break; case "Comment": r.Comment = S(); break; case "Condition": r.Condition = S(); break;
                    case "ConditionOperator": r.ConditionOperator = Op(); break; case "Enabled": r.Enabled = B(); break; default: throw Unknown();
                } break;
                case TextlistRuleGroup g: switch (property) {
                    case "Name": g.Name = S(); break; case "Comment": g.Comment = S(); break; case "Condition": g.Condition = S(); break;
                    case "ConditionOperator": g.ConditionOperator = Op(); break; case "Enabled": g.Enabled = B(); break; default: throw Unknown();
                } break;
                default: throw OptionalSession.SivarcShape(rule);
            }
            Exception Unknown() => new ArgumentException(property + " is not a scalar property of " + rule.GetType().Name + ".");
        }
        // PLC / HMI device columns are dynamic attributes named after the PLC / HMI runtime (official "Configuring PLC and HMI device").
        private static HmiObject SivarcDeviceColumns(IEngineeringObject rule, string[] names)
        {
            var row = new HmiObject();
            foreach (var name in names) {
                try { row[name] = EngineeringScalarProperties.Json(rule.GetAttribute(name)); } catch (Exception ex) {
                    row[name] = null; row[name + "Error"] = ex.GetBaseException().Message; }
            }
            return row;
        }

        public HardwareAddressingReply ManageSivarcTableRule(string category, string tablePath, string rulePath = "", string kind = "rule", string action =
                "read", string propertiesJson = "{}", string referencesJson = "{}",
            string deviceSelectionJson = "{}", string deviceNamesJson = "[]", string libraryName = "", string masterCopyPath = "", string createOption =
                "Replace", bool confirmDelete = false, bool dryRun = true)
            => _session.RunHmiStepTool("ManageSivarcTableRule", meta =>
            {
                bool write = Logic
                    .ValidateRuleRequest(category, tablePath, rulePath, kind, action, propertiesJson, referencesJson, deviceSelectionJson, masterCopyPath,
                        createOption, confirmDelete, dryRun);
                var deviceNames = Logic.ParseNames(deviceNamesJson, "deviceNamesJson");
                using var access = write ? _session.AcquireHmiEditAccess() : null;
                var family = _session.GetSivarcFamily(category); var sivarc = _session.RequireSivarc(); var table = SivarcTable(family, sivarc, tablePath);
                meta["category"] = category; meta["tablePath"] = tablePath; meta["rulePath"] = rulePath; meta["kind"] = kind; meta["action"] = action;
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                var parts = EngineeringGroupOperations.Parts(rulePath, true); var groupPath = action == "createFromMasterCopy" ? rulePath : string
                    .Join("/", parts.Take(parts.Length - 1));
                var owner = SivarcGroup(family, table, groupPath);
                object Collection(object o) => kind == "group" ? family.Groups(o) : family.Rules(o);
                object? Fresh(string name) => EngineeringGroupOperations
                    .Find(Collection(SivarcGroup(family, SivarcTable(family, sivarc, tablePath), groupPath)), name);
                var existing = action == "createFromMasterCopy" ? null : EngineeringGroupOperations.Find(Collection(owner), parts.Last());
                if (action == "read" || action == "update" || action == "delete")
                {
                    if (existing == null)
                        throw new PlcSoftwareException("NotFound", "SiVArc " + kind + " not found: " + rulePath + " (table " + tablePath + ")");
                    meta["before"] = kind == "group" ? SivarcGroupRow(family, existing, 0, 1, false) : SivarcRuleRow(existing, category == "screens");
                    if (deviceNames.Length > 0) meta["deviceColumns"] = SivarcDeviceColumns((IEngineeringObject)existing, deviceNames);
                }
                var properties = Logic.ParseObject(propertiesJson, "propertiesJson"); var references = Logic.ParseObject(referencesJson, "referencesJson");
                var devices = Logic.ParseObject(deviceSelectionJson, "deviceSelectionJson");
                if (action == "read") return "SiVArc " + kind + " read (typed " + existing!.GetType().Name + "); no modification.";
                if (action == "delete")
                {
                    if (kind == "group"
                            && (meta["before"]!.Field("groupCount")!.OptionalValue<int>() > 0 || meta["before"]!.Field("ruleCount")!.OptionalValue<int>() > 0))
                        throw new InvalidOperationException("Nonempty rule group deletion refused; delete its rules and sub-groups first.");
                    if (!write) return "Delete preview of SiVArc " + kind + "; nothing changed.";
                    meta["mayHaveChanged"] = true; SivarcDelete(existing!);
                    meta["verifiedAbsent"] = Fresh(parts.Last()) == null;
                    if (meta["verifiedAbsent"]!.OptionalValue<bool>() != true)
                        throw new InvalidOperationException("SiVArc " + kind + " still present after Delete().");
                    return "SiVArc " + kind + " deleted and verified absent; project not saved.";
                }
                // resolve references up front so a preview reports what would be assigned
                var resolved = new Dictionary<string, object?>(StringComparer.Ordinal); var resolvedRows = new HmiObject();
                foreach (var pair in references)
                {
                    var referenced = pair.Value == null ? null : ResolveSivarcReference(Logic.ParseReference(pair.Value, pair.Key));
                    resolved[pair.Key] = referenced; resolvedRows[pair.Key] = SivarcReferenceJson(referenced);
                }
                if (resolvedRows.Count > 0) meta["references"] = resolvedRows;
                MasterCopy? source = null; CreateOptions option = (CreateOptions)Enum.Parse(typeof(CreateOptions), createOption);
                if (action == "createFromMasterCopy") {
                    source = _session.ExactMasterCopy(libraryName, masterCopyPath); meta["source"] = new HmiObject {
                        ["masterCopyPath"] = masterCopyPath, ["name"] = source.Name, ["createOption"] = option.ToString() };
                }
                else if (action == "create" && existing != null) throw new InvalidOperationException("SiVArc " + kind + " already exists: " + rulePath);
                meta["nativeSignature"] = action == "createFromMasterCopy" ? Collection(owner).GetType().Name + ".CreateFrom(MasterCopy, CreateOptions."
                    + option + ")" : action == "create" ? Collection(owner).GetType().Name + ".Create(string)" : "typed property setters";
                if (!write) return "SiVArc " + kind + " " + action + " preview; nothing changed (real edits need the SiVArc licence).";
                meta["mayHaveChanged"] = true;
                object target;
                if (action == "createFromMasterCopy") target = kind == "group" ? family.CreateGroupFrom(Collection(owner), source!, option) : family
                    .CreateRuleFrom(Collection(owner), source!, option);
                else if (action == "create") target = kind == "group" ? family.CreateGroup(Collection(owner), parts.Last()) : family
                    .CreateRule(Collection(owner), parts.Last());
                else target = existing!;
                foreach (var pair in properties) SetSivarcScalar(target, pair.Key, pair.Value);
                foreach (var pair in resolved) SetSivarcReference(target, pair.Key, pair.Value);
                if (devices.Count > 0) ((IEngineeringObject)target)
                    .SetAttributes(devices.Select(d => new KeyValuePair<string, object>(d.Key, d.Value!.OptionalValue<bool>())).ToList());
                var fresh = Fresh(SivarcName(target))
                    ?? throw new InvalidOperationException("SiVArc " + kind + " not found on re-navigation after " + action + ".");
                meta["after"] = kind == "group" ? SivarcGroupRow(family, fresh, 0, 1, false) : SivarcRuleRow(fresh, category == "screens");
                if (devices.Count > 0) meta["deviceColumnsAfter"] = SivarcDeviceColumns((IEngineeringObject)fresh, devices.Select(d => d.Key).ToArray());
                return "SiVArc " + kind + " " + action + " applied and read back; project not saved, no generation.";
            });

        private static HmiObject TagDefinitionRow(TagDefinition d) => new HmiObject { ["name"] = d.Name, ["value"] = d.Value, ["comment"] = d.Comment };
        private HmiObject TextDefinitionRow(TextDefinition d) => new HmiObject {
            ["name"] = d.Name, ["expression"] = d.Expression, ["comment"] = d.Comment, ["text"] = _session.MultilingualJson(d.Text) };
#if OPTIONAL_V21
        private static HmiObject TagMemberRow(TagMember m) => new HmiObject {
            ["name"] = m.Name, ["acquisitionCycle"] = m.AcquisitionCycle, ["acquisitionMode"] = m.AcquisitionMode.ToString(), ["comment"] = m.Comment };
        private static HmiObject TagMemberSettingRow(TagMemberSetting s, bool members)
        {
            TagMemberComposition parameters = s.BlockParameters;
            var row = new HmiObject { ["useCommonConfiguration"] = s.UseCommonConfiguration, ["blockParameterCount"] = parameters.Count };
            Safe(row, "commonParameters", () => s.CommonParameters == null ? null : TagMemberRow(s.CommonParameters));
            if (members) row["blockParameters"] =
                new HmiArray(EngineeringGroupOperations.Items(parameters).Cast<TagMember>().Take(500).Select(m => (object)TagMemberRow(m)).ToArray());
            return row;
        }
#endif
    }
}

#endif
