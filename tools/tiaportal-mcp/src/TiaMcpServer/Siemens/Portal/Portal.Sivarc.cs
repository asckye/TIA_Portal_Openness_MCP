using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.Library.MasterCopies;
using Siemens.Engineering.Library.Types;
using Siemens.Engineering.SiVArc;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using TiaMcpServer.ModelContextProtocol;
using Logic = TiaMcpServer.Siemens.SivarcLogic;
using CreateOptions = Siemens.Engineering.SiVArc.CreateOptions;

namespace TiaMcpServer.Siemens
{
    // Phase 6 ⑥-① (2.7.38): the SiVArc option package (Siemens.Engineering.SiVArc). Official SiVArc manual, "SiVArc Openness"
    // chapter: rule tables / folders, rule objects and groups, copying rules from master copies, library objects as rule
    // references, PLC / HMI device columns, tag / text definitions and tag member settings on blocks, the expression resolver,
    // layout data export / import on screens, upgrading SiVArc definitions and generation. The project service is
    // project.GetService<Sivarc>(); SivarcDataProvider hangs on a CodeBlock, SivarcDefinitionsUpgrader on the PlcSoftware and
    // LayoutData on a classic Screen or Unified HmiScreen. The VM has no SiVArc licence: shape checks only, and every tool
    // answers NotSupported when the service is absent. Writes need the SiVArc licence (TIA raises "License not found").
    public partial class Portal
    {
        // ---- family table (typed per rule family; the six families share the shape but no base class) ------------------------------------
        private sealed class SivarcFamily
        {
            public string Category = ""; public Type TypeVersionType = typeof(object);
            public Func<Sivarc, object> Anchor = _ => throw new NotSupportedException();
            public Func<object, object> Folders = _ => throw new NotSupportedException();   // anchor / folder -> *RuleFolderComposition
            public Func<object, object> Tables = _ => throw new NotSupportedException();    // anchor / folder -> *RuleTableComposition
            public Func<object, object> Groups = _ => throw new NotSupportedException();    // table / group -> *RuleGroupComposition
            public Func<object, object> Rules = _ => throw new NotSupportedException();     // table / group -> *RuleComposition
            public Func<object, string, object> CreateFolder = (_, _) => throw new NotSupportedException();
            public Func<object, string, object> CreateTable = (_, _) => throw new NotSupportedException();
            public Func<object, LibraryTypeVersion, object> CreateTableFromType = (_, _) => throw new NotSupportedException();
            public Func<object, string, object> CreateGroup = (_, _) => throw new NotSupportedException();
            public Func<object, MasterCopy, CreateOptions, object> CreateGroupFrom = (_, _, _) => throw new NotSupportedException();
            public Func<object, string, object> CreateRule = (_, _) => throw new NotSupportedException();
            public Func<object, MasterCopy, CreateOptions, object> CreateRuleFrom = (_, _, _) => throw new NotSupportedException();
        }
        private static readonly SivarcFamily[] SivarcFamilies =
        {
            new SivarcFamily
            {
                Category = "screens", TypeVersionType = typeof(ScreenRuleTableTypeVersion), Anchor = s => s.ScreenRules,
                Folders = x => x switch { ScreenRules a => a.Folders, ScreenRuleFolder f => f.Folders, _ => throw SivarcShape(x) },
                Tables = x => x switch { ScreenRules a => a.Tables, ScreenRuleFolder f => f.Tables, _ => throw SivarcShape(x) },
                Groups = x => x switch { ScreenRuleTable t => t.Groups, ScreenRuleGroup g => g.Groups, _ => throw SivarcShape(x) },
                Rules = x => x switch { ScreenRuleTable t => t.Rules, ScreenRuleGroup g => g.Rules, _ => throw SivarcShape(x) },
                CreateFolder = (c, n) => ((ScreenRuleFolderComposition)c).Create(n), CreateTable = (c, n) => ((ScreenRuleTableComposition)c).Create(n),
                CreateTableFromType = (c, v) => ((ScreenRuleTableComposition)c).CreateFrom((ScreenRuleTableTypeVersion)v),
                CreateGroup = (c, n) => ((ScreenRuleGroupComposition)c).Create(n), CreateGroupFrom = (c, m, o) => ((ScreenRuleGroupComposition)c).CreateFrom(m, o),
                CreateRule = (c, n) => ((ScreenRuleComposition)c).Create(n), CreateRuleFrom = (c, m, o) => ((ScreenRuleComposition)c).CreateFrom(m, o)
            },
            new SivarcFamily
            {
                Category = "tags", TypeVersionType = typeof(TagRuleTableTypeVersion), Anchor = s => s.TagRules,
                Folders = x => x switch { TagRules a => a.Folders, TagRuleFolder f => f.Folders, _ => throw SivarcShape(x) },
                Tables = x => x switch { TagRules a => a.Tables, TagRuleFolder f => f.Tables, _ => throw SivarcShape(x) },
                Groups = x => x switch { TagRuleTable t => t.Groups, TagRuleGroup g => g.Groups, _ => throw SivarcShape(x) },
                Rules = x => x switch { TagRuleTable t => t.Rules, TagRuleGroup g => g.Rules, _ => throw SivarcShape(x) },
                CreateFolder = (c, n) => ((TagRuleFolderComposition)c).Create(n), CreateTable = (c, n) => ((TagRuleTableComposition)c).Create(n),
                CreateTableFromType = (c, v) => ((TagRuleTableComposition)c).CreateFrom((TagRuleTableTypeVersion)v),
                CreateGroup = (c, n) => ((TagRuleGroupComposition)c).Create(n), CreateGroupFrom = (c, m, o) => ((TagRuleGroupComposition)c).CreateFrom(m, o),
                CreateRule = (c, n) => ((TagRuleComposition)c).Create(n), CreateRuleFrom = (c, m, o) => ((TagRuleComposition)c).CreateFrom(m, o)
            },
            new SivarcFamily
            {
                Category = "advancedTags", TypeVersionType = typeof(AdvancedTagRuleTableTypeVersion), Anchor = s => s.AdvancedTagRules,
                Folders = x => x switch { AdvancedTagRules a => a.Folders, AdvancedTagRuleFolder f => f.Folders, _ => throw SivarcShape(x) },
                Tables = x => x switch { AdvancedTagRules a => a.Tables, AdvancedTagRuleFolder f => f.Tables, _ => throw SivarcShape(x) },
                Groups = x => x switch { AdvancedTagRuleTable t => t.Groups, AdvancedTagRuleGroup g => g.Groups, _ => throw SivarcShape(x) },
                Rules = x => x switch { AdvancedTagRuleTable t => t.Rules, AdvancedTagRuleGroup g => g.Rules, _ => throw SivarcShape(x) },
                CreateFolder = (c, n) => ((AdvancedTagRuleFolderComposition)c).Create(n), CreateTable = (c, n) => ((AdvancedTagRuleTableComposition)c).Create(n),
                CreateTableFromType = (c, v) => ((AdvancedTagRuleTableComposition)c).CreateFrom((AdvancedTagRuleTableTypeVersion)v),
                CreateGroup = (c, n) => ((AdvancedTagRuleGroupComposition)c).Create(n), CreateGroupFrom = (c, m, o) => ((AdvancedTagRuleGroupComposition)c).CreateFrom(m, o),
                CreateRule = (c, n) => ((AdvancedTagRuleComposition)c).Create(n), CreateRuleFrom = (c, m, o) => ((AdvancedTagRuleComposition)c).CreateFrom(m, o)
            },
            new SivarcFamily
            {
                Category = "alarms", TypeVersionType = typeof(AlarmRuleTableTypeVersion), Anchor = s => s.AlarmRules,
                Folders = x => x switch { AlarmRules a => a.Folders, AlarmRuleFolder f => f.Folders, _ => throw SivarcShape(x) },
                Tables = x => x switch { AlarmRules a => a.Tables, AlarmRuleFolder f => f.Tables, _ => throw SivarcShape(x) },
                Groups = x => x switch { AlarmRuleTable t => t.Groups, AlarmRuleGroup g => g.Groups, _ => throw SivarcShape(x) },
                Rules = x => x switch { AlarmRuleTable t => t.Rules, AlarmRuleGroup g => g.Rules, _ => throw SivarcShape(x) },
                CreateFolder = (c, n) => ((AlarmRuleFolderComposition)c).Create(n), CreateTable = (c, n) => ((AlarmRuleTableComposition)c).Create(n),
                CreateTableFromType = (c, v) => ((AlarmRuleTableComposition)c).CreateFrom((AlarmRuleTableTypeVersion)v),
                CreateGroup = (c, n) => ((AlarmRuleGroupComposition)c).Create(n), CreateGroupFrom = (c, m, o) => ((AlarmRuleGroupComposition)c).CreateFrom(m, o),
                CreateRule = (c, n) => ((AlarmRuleComposition)c).Create(n), CreateRuleFrom = (c, m, o) => ((AlarmRuleComposition)c).CreateFrom(m, o)
            },
            new SivarcFamily
            {
                Category = "copies", TypeVersionType = typeof(CopyRuleTableTypeVersion), Anchor = s => s.CopyRules,
                Folders = x => x switch { CopyRules a => a.Folders, CopyRuleFolder f => f.Folders, _ => throw SivarcShape(x) },
                Tables = x => x switch { CopyRules a => a.Tables, CopyRuleFolder f => f.Tables, _ => throw SivarcShape(x) },
                Groups = x => x switch { CopyRuleTable t => t.Groups, CopyRuleGroup g => g.Groups, _ => throw SivarcShape(x) },
                Rules = x => x switch { CopyRuleTable t => t.Rules, CopyRuleGroup g => g.Rules, _ => throw SivarcShape(x) },
                CreateFolder = (c, n) => ((CopyRuleFolderComposition)c).Create(n), CreateTable = (c, n) => ((CopyRuleTableComposition)c).Create(n),
                CreateTableFromType = (c, v) => ((CopyRuleTableComposition)c).CreateFrom((CopyRuleTableTypeVersion)v),
                CreateGroup = (c, n) => ((CopyRuleGroupComposition)c).Create(n), CreateGroupFrom = (c, m, o) => ((CopyRuleGroupComposition)c).CreateFrom(m, o),
                CreateRule = (c, n) => ((CopyRuleComposition)c).Create(n), CreateRuleFrom = (c, m, o) => ((CopyRuleComposition)c).CreateFrom(m, o)
            },
            new SivarcFamily
            {
                Category = "textLists", TypeVersionType = typeof(TextlistRuleTableTypeVersion), Anchor = s => s.TextlistRules,
                Folders = x => x switch { TextlistRules a => a.Folders, TextlistRuleFolder f => f.Folders, _ => throw SivarcShape(x) },
                Tables = x => x switch { TextlistRules a => a.Tables, TextlistRuleFolder f => f.Tables, _ => throw SivarcShape(x) },
                Groups = x => x switch { TextlistRuleTable t => t.Groups, TextlistRuleGroup g => g.Groups, _ => throw SivarcShape(x) },
                Rules = x => x switch { TextlistRuleTable t => t.Rules, TextlistRuleGroup g => g.Rules, _ => throw SivarcShape(x) },
                CreateFolder = (c, n) => ((TextlistRuleFolderComposition)c).Create(n), CreateTable = (c, n) => ((TextlistRuleTableComposition)c).Create(n),
                CreateTableFromType = (c, v) => ((TextlistRuleTableComposition)c).CreateFrom((TextlistRuleTableTypeVersion)v),
                CreateGroup = (c, n) => ((TextlistRuleGroupComposition)c).Create(n), CreateGroupFrom = (c, m, o) => ((TextlistRuleGroupComposition)c).CreateFrom(m, o),
                CreateRule = (c, n) => ((TextlistRuleComposition)c).Create(n), CreateRuleFrom = (c, m, o) => ((TextlistRuleComposition)c).CreateFrom(m, o)
            }
        };
        private static Exception SivarcShape(object x) => new NotSupportedException("Unexpected SiVArc object " + x.GetType().FullName + " at this tree position.");
        private static SivarcFamily Family(string category) { Logic.AnchorProperty(category); return SivarcFamilies.Single(f => f.Category == category); }

        private Sivarc RequireSivarc()
            => _project!.GetService<Sivarc>() ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "SiVArc service unavailable on this project (SiVArc option package not installed).");
        private static string SivarcName(object node) => EngineeringGroupOperations.Get(node, "Name").ToString()!;
        private static void SivarcDelete(object node)
        {
            switch (node)
            {
                case ScreenRuleFolder f: f.Delete(); break; case ScreenRuleTable t: t.Delete(); break; case ScreenRuleGroup g: g.Delete(); break; case ScreenRule r: r.Delete(); break;
                case TagRuleFolder f: f.Delete(); break; case TagRuleTable t: t.Delete(); break; case TagRuleGroup g: g.Delete(); break; case TagRule r: r.Delete(); break;
                case AdvancedTagRuleFolder f: f.Delete(); break; case AdvancedTagRuleTable t: t.Delete(); break; case AdvancedTagRuleGroup g: g.Delete(); break; case AdvancedTagRule r: r.Delete(); break;
                case AlarmRuleFolder f: f.Delete(); break; case AlarmRuleTable t: t.Delete(); break; case AlarmRuleGroup g: g.Delete(); break; case AlarmRule r: r.Delete(); break;
                case CopyRuleFolder f: f.Delete(); break; case CopyRuleTable t: t.Delete(); break; case CopyRuleGroup g: g.Delete(); break; case CopyRule r: r.Delete(); break;
                case TextlistRuleFolder f: f.Delete(); break; case TextlistRuleTable t: t.Delete(); break; case TextlistRuleGroup g: g.Delete(); break; case TextlistRule r: r.Delete(); break;
                default: throw SivarcShape(node);
            }
        }

        // ---- navigation ----------------------------------------------------------------------------------------------------------
        // folderPath "" = the anchor (the family's system folder); "A/B" = nested user folders.
        private object SivarcFolder(SivarcFamily family, Sivarc sivarc, string folderPath)
        {
            object current = family.Anchor(sivarc);
            foreach (var part in EngineeringGroupOperations.Parts(folderPath, true))
                current = EngineeringGroupOperations.Find(family.Folders(current), part) ?? throw new PortalException(PortalErrorCode.NotFound, "SiVArc " + family.Category + " rule folder not found: " + folderPath);
            return current;
        }
        // tablePath "Folder/Sub/Table" (last segment = table name).
        private object SivarcTable(SivarcFamily family, Sivarc sivarc, string tablePath)
        {
            var parts = EngineeringGroupOperations.Parts(tablePath);
            var folder = SivarcFolder(family, sivarc, string.Join("/", parts.Take(parts.Length - 1)));
            return EngineeringGroupOperations.Find(family.Tables(folder), parts.Last()) ?? throw new PortalException(PortalErrorCode.NotFound, "SiVArc " + family.Category + " rule table not found: " + tablePath);
        }
        // groupPath "" = the table; "G/Sub" = nested rule groups under the table.
        private static object SivarcGroup(SivarcFamily family, object table, string groupPath)
        {
            object current = table;
            foreach (var part in EngineeringGroupOperations.Parts(groupPath, true))
                current = EngineeringGroupOperations.Find(family.Groups(current), part) ?? throw new PortalException(PortalErrorCode.NotFound, "SiVArc rule group not found: " + groupPath);
            return current;
        }

        // ---- rows ------------------------------------------------------------------------------------------------------------------
        private static JsonNode? SivarcReferenceJson(object? reference)
        {
            if (reference == null) return null;
            var row = new JsonObject { ["refClass"] = reference.GetType().Name };
            Safe(row, "name", () => reference is IEngineeringObject ? EngineeringGroupOperations.Get(reference, "Name").ToString() : null);
            if (reference is MasterCopy || reference is LibraryType || reference is MasterCopyFolder || reference is LibraryTypeFolder) Safe(row, "libraryPath", () => LibraryPathOf(reference, reference is MasterCopy || reference is MasterCopyFolder ? "MasterCopySystemFolder" : "LibraryTypeSystemFolder"));
            return row;
        }
        private static JsonObject SivarcRuleRow(object rule, bool includeLayoutFields)
        {
            var row = new JsonObject { ["name"] = SivarcName(rule), ["ruleClass"] = rule.GetType().Name };
            void Common(string comment, string condition, ConditionOperator op, bool enabled) { row["comment"] = comment; row["condition"] = condition; row["conditionOperator"] = op.ToString(); row["enabled"] = enabled; }
            switch (rule)
            {
                case ScreenRule r:
                    Common(r.Comment, r.Condition, r.ConditionOperator, r.Enabled); row["layoutField"] = r.LayoutField; row["loopCount"] = r.LoopCount;
                    Safe(row, "programBlock", () => SivarcReferenceJson(r.ProgramBlock)); Safe(row, "libraryScreen", () => SivarcReferenceJson(r.LibraryScreen)); Safe(row, "screenObjectLibraryItem", () => SivarcReferenceJson(r.ScreenObjectLibraryItem));
                    if (includeLayoutFields) Safe(row, "layoutFields", () => new JsonArray(r.GetLayoutFields().Take(200).Select(f => (JsonNode)f).ToArray()));
                    break;
                case ScreenRuleGroup g:
                    Common(g.Comment, g.Condition, g.ConditionOperator, g.Enabled); row["layoutField"] = g.LayoutField; row["loopCount"] = g.LoopCount;
                    Safe(row, "programBlock", () => SivarcReferenceJson(g.ProgramBlock)); Safe(row, "libraryScreen", () => SivarcReferenceJson(g.LibraryScreen)); Safe(row, "screenObjectLibraryItem", () => SivarcReferenceJson(g.ScreenObjectLibraryItem));
                    if (includeLayoutFields) Safe(row, "layoutFields", () => new JsonArray(g.GetLayoutFields().Take(200).Select(f => (JsonNode)f).ToArray()));
                    break;
                case TagRule r: Common(r.Comment, r.Condition, r.ConditionOperator, r.Enabled); row["tagGroupHierarchy"] = r.TagGroupHierarchy; row["tagTable"] = r.TagTable; break;
                case TagRuleGroup g: Common(g.Comment, g.Condition, g.ConditionOperator, g.Enabled); break;
                case AdvancedTagRule r:
                    Common(r.Comment, r.Condition, r.ConditionOperator, r.Enabled); row["tagGroupHierarchy"] = r.TagGroupHierarchy; row["tagTable"] = r.TagTable;
                    Safe(row, "programBlock", () => SivarcReferenceJson(r.ProgramBlock)); Safe(row, "tagLibraryItem", () => SivarcReferenceJson(r.TagLibraryItem)); break;
                case AdvancedTagRuleGroup g: Common(g.Comment, g.Condition, g.ConditionOperator, g.Enabled); break;
                case AlarmRule r: Common(r.Comment, r.Condition, r.ConditionOperator, r.Enabled); Safe(row, "programBlock", () => SivarcReferenceJson(r.ProgramBlock)); Safe(row, "alarmLibraryItem", () => SivarcReferenceJson(r.AlarmLibraryItem)); break;
                case AlarmRuleGroup g: Common(g.Comment, g.Condition, g.ConditionOperator, g.Enabled); break;
                case CopyRule r: Common(r.Comment, r.Condition, r.ConditionOperator, r.Enabled); row["folderStructure"] = r.FolderStructure; Safe(row, "libraryObject", () => SivarcReferenceJson(r.LibraryObject)); break;
                case CopyRuleGroup g: Common(g.Comment, g.Condition, g.ConditionOperator, g.Enabled); break;
                case TextlistRule r: Common(r.Comment, r.Condition, r.ConditionOperator, r.Enabled); Safe(row, "programBlock", () => SivarcReferenceJson(r.ProgramBlock)); Safe(row, "textlistLibraryItem", () => SivarcReferenceJson(r.TextlistLibraryItem)); break;
                case TextlistRuleGroup g: Common(g.Comment, g.Condition, g.ConditionOperator, g.Enabled); break;
                default: throw SivarcShape(rule);
            }
            return row;
        }
        private static JsonObject SivarcGroupRow(SivarcFamily family, object group, int depth, int maxDepth, bool includeRules)
        {
            var row = SivarcRuleRow(group, false);
            object groups = family.Groups(group), rules = family.Rules(group);
            row["groupCount"] = EngineeringGroupOperations.Items(groups).Count(); row["ruleCount"] = EngineeringGroupOperations.Items(rules).Count();
            if (includeRules) row["rules"] = new JsonArray(EngineeringGroupOperations.Items(rules).Take(200).Select(r => (JsonNode)SivarcRuleRow(r, false)).ToArray());
            if (depth < maxDepth) row["groups"] = new JsonArray(EngineeringGroupOperations.Items(groups).Select(g => (JsonNode)SivarcGroupRow(family, g, depth + 1, maxDepth, includeRules)).ToArray());
            else row["groupsTruncated"] = row["groupCount"]!.GetValue<int>() > 0;
            return row;
        }
        private static JsonObject SivarcTableRow(SivarcFamily family, object table)
        {
            var row = new JsonObject { ["name"] = SivarcName(table), ["tableClass"] = table.GetType().Name };
            Safe(row, "isDefault", () => (bool)EngineeringGroupOperations.Get(table, "IsDefault"));
            Safe(row, "groupCount", () => EngineeringGroupOperations.Items(family.Groups(table)).Count()); Safe(row, "ruleCount", () => EngineeringGroupOperations.Items(family.Rules(table)).Count());
            // rule tables instantiated from a *RuleTableType carry the connected version (official "Version details of instantiated Rule Table")
            Safe(row, "libraryTypeVersion", () =>
            {
                var info = ((IEngineeringServiceProvider)table).GetService<LibraryTypeInstanceInfo>();
                var version = info?.LibraryTypeVersion; if (version == null) return null;
                return new JsonObject { ["typeName"] = version.Parent is LibraryType t ? t.Name : null, ["version"] = version.VersionNumber?.ToString(), ["typeClass"] = version.Parent?.GetType().Name };
            });
            return row;
        }
        private JsonObject SivarcFolderRow(SivarcFamily family, object folder, int depth, int maxDepth)
        {
            object folders = family.Folders(folder), tables = family.Tables(folder);
            var row = new JsonObject { ["name"] = folder.GetType().GetProperty("Name") == null ? Logic.AnchorProperty(family.Category) : SivarcName(folder), ["folderClass"] = folder.GetType().Name };
            row["tables"] = new JsonArray(EngineeringGroupOperations.Items(tables).Select(t => (JsonNode)SivarcTableRow(family, t)).ToArray());
            row["folderCount"] = EngineeringGroupOperations.Items(folders).Count();
            if (depth < maxDepth) row["folders"] = new JsonArray(EngineeringGroupOperations.Items(folders).Select(f => (JsonNode)SivarcFolderRow(family, f, depth + 1, maxDepth)).ToArray());
            else row["foldersTruncated"] = row["folderCount"]!.GetValue<int>() > 0;
            return row;
        }
        private static JsonArray SivarcMessageRows(SivarcFeedbackMessageComposition messages, int depth = 0)
            => new JsonArray(EngineeringGroupOperations.Items(messages).Cast<SivarcFeedbackMessage>().Take(2000).Select(m =>
            {
                var row = new JsonObject { ["path"] = m.Path, ["description"] = m.Description, ["messageType"] = m.MessageType.ToString(), ["dateTime"] = EngineeringScalarProperties.Json(m.DateTime), ["errorCount"] = m.ErrorCount, ["warningCount"] = m.WarningCount };
                if (depth < 8) Safe(row, "messages", () => SivarcMessageRows(m.Messages, depth + 1));
                return (JsonNode)row;
            }).ToArray());

        // ---- tools: rule tree ----------------------------------------------------------------------------------------------------
        public ResponseMessage ReadSivarcRuleTree(string category, string folderPath = "", string tablePath = "", bool includeRules = true, int maxDepth = 4, int offset = 0, int limit = 200)
            => RunHmiStepTool("ReadSivarcRuleTree", meta =>
            {
                Logic.ValidateTreeRequest(category, folderPath, tablePath, maxDepth, offset, limit);
                var family = Family(category); var sivarc = RequireSivarc();
                meta["category"] = category; meta["anchor"] = Logic.AnchorProperty(category);
                if (string.IsNullOrEmpty(tablePath))
                {
                    meta["folderPath"] = folderPath; meta["tree"] = SivarcFolderRow(family, SivarcFolder(family, sivarc, folderPath), 0, maxDepth);
                    return "SiVArc " + category + " rule folders and tables read (typed " + Logic.AnchorProperty(category) + " anchor); give tablePath for one table's groups and rules. No generation.";
                }
                var table = SivarcTable(family, sivarc, tablePath);
                meta["tablePath"] = tablePath; meta["table"] = SivarcTableRow(family, table);
                var rules = EngineeringGroupOperations.Items(family.Rules(table)).Select(r => (JsonNode)SivarcRuleRow(r, false)).ToArray();
                Page(rules, offset, limit, meta);
                meta["groups"] = new JsonArray(EngineeringGroupOperations.Items(family.Groups(table)).Select(g => (JsonNode)SivarcGroupRow(family, g, 1, maxDepth, includeRules)).ToArray());
                return "SiVArc rule table read: paged top-level rules (records) and nested groups (up to maxDepth, 200 rules per group). Device columns need ManageSivarcRule read with deviceNamesJson. No generation.";
            });

        // ---- tools: folders / tables ----------------------------------------------------------------------------------------------
        public ResponseMessage ManageSivarcRuleContainer(string category, string kind, string path, string action = "read", string libraryName = "", string typePath = "", string typeVersion = "", bool confirmDelete = false, bool dryRun = true)
            => RunHmiStepTool("ManageSivarcRuleContainer", meta =>
            {
                bool write = Logic.ValidateContainerRequest(category, kind, path, action, typePath, typeVersion, confirmDelete, dryRun);
                using var access = write ? AcquireHmiEditAccess() : null;
                var family = Family(category); var sivarc = RequireSivarc();
                var parts = EngineeringGroupOperations.Parts(path); var parentPath = string.Join("/", parts.Take(parts.Length - 1));
                meta["category"] = category; meta["kind"] = kind; meta["path"] = path; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                var parent = SivarcFolder(family, sivarc, parentPath);
                object collection = kind == "folder" ? family.Folders(parent) : family.Tables(parent);
                var existing = EngineeringGroupOperations.Find(collection, parts.Last());
                if (action == "read")
                {
                    if (existing == null) throw new PortalException(PortalErrorCode.NotFound, "SiVArc " + kind + " not found: " + path);
                    meta[kind] = kind == "folder" ? SivarcFolderRow(family, existing, 0, 1) : SivarcTableRow(family, existing);
                    return "SiVArc rule " + kind + " read; no modification.";
                }
                if (action == "delete")
                {
                    if (existing == null) throw new PortalException(PortalErrorCode.NotFound, "SiVArc " + kind + " not found: " + path);
                    meta["before"] = kind == "folder" ? SivarcFolderRow(family, existing, 0, 1) : SivarcTableRow(family, existing);
                    if (kind == "table" && meta["before"]!["isDefault"]?.GetValue<bool>() == true) throw new InvalidOperationException("The default rule table cannot be deleted.");
                    if (kind == "folder" && (meta["before"]!["tables"]!.AsArray().Count > 0 || meta["before"]!["folderCount"]!.GetValue<int>() > 0)) throw new InvalidOperationException("Nonempty rule folder deletion refused; delete its tables and sub-folders first.");
                    if (!write) return "Delete preview of an empty SiVArc rule " + kind + "; nothing changed.";
                    meta["mayHaveChanged"] = true; SivarcDelete(existing);
                    meta["verifiedAbsent"] = EngineeringGroupOperations.Find(kind == "folder" ? family.Folders(SivarcFolder(family, sivarc, parentPath)) : family.Tables(SivarcFolder(family, sivarc, parentPath)), parts.Last()) == null;
                    if (meta["verifiedAbsent"]!.GetValue<bool>() != true) throw new InvalidOperationException("SiVArc " + kind + " still present after Delete().");
                    return "SiVArc rule " + kind + " deleted and verified absent; project not saved.";
                }
                if (existing != null) throw new InvalidOperationException("SiVArc " + kind + " already exists: " + path);
                LibraryTypeVersion? version = null;
                if (action == "createFromType")
                {
                    var type = ExactLibraryType(ExactOpenEngineeringLibrary(libraryName), typePath);
                    version = ExactTypeVersion(type, typeVersion);
                    if (!family.TypeVersionType.IsInstanceOfType(version)) throw new ArgumentException("Type version is " + version.GetType().Name + ", not a " + family.TypeVersionType.Name + " (category " + category + ").");
                    meta["source"] = new JsonObject { ["typePath"] = typePath, ["typeClass"] = type.GetType().Name, ["version"] = version.VersionNumber?.ToString() };
                }
                meta["nativeSignature"] = action == "createFromType" ? collection.GetType().Name + ".CreateFrom(" + family.TypeVersionType.Name + ")" : collection.GetType().Name + ".Create(string)";
                if (!write) return "SiVArc rule " + kind + " create preview; nothing changed (real creation needs the SiVArc licence).";
                meta["mayHaveChanged"] = true;
                object created = kind == "folder" ? family.CreateFolder(collection, parts.Last()) : version != null ? family.CreateTableFromType(collection, version) : family.CreateTable(collection, parts.Last());
                var fresh = EngineeringGroupOperations.Find(kind == "folder" ? family.Folders(SivarcFolder(family, sivarc, parentPath)) : family.Tables(SivarcFolder(family, sivarc, parentPath)), SivarcName(created)) ?? throw new InvalidOperationException("Created SiVArc " + kind + " not found on re-navigation.");
                meta["created"] = kind == "folder" ? SivarcFolderRow(family, fresh, 0, 1) : SivarcTableRow(family, fresh);
                return "SiVArc rule " + kind + " created and read back; project not saved.";
            });

        // ---- tools: rules / groups ------------------------------------------------------------------------------------------------
        private object ResolveSivarcReference(Logic.Reference reference)
        {
            switch (reference.Kind)
            {
                case "plcBlock":
                    var plc = ExactPlcForEngineering(reference.SoftwarePath, false);
                    return ExactObjectUnder(plc.BlockGroup, reference.Path, "Blocks", "PLC block") is CodeBlock block ? block : throw new ArgumentException("Referenced block is not a CodeBlock: " + reference.Path);
                case "masterCopy": return ExactMasterCopy(reference.LibraryName, reference.Path);
                case "libraryType": return ExactLibraryType(ExactOpenEngineeringLibrary(reference.LibraryName), reference.Path);
                case "masterCopyFolder": return EngineeringLibraryFolder(ExactOpenEngineeringLibrary(reference.LibraryName), reference.Path, "MasterCopyFolder");
                default: return EngineeringLibraryFolder(ExactOpenEngineeringLibrary(reference.LibraryName), reference.Path, "TypeFolder");
            }
        }
        // Typed setters for the reference properties; V20 declares them with SiVArc marker interfaces, V21 as IEngineeringObject.
        private static void SetSivarcReference(object rule, string property, object? value)
        {
#if TIA_V20
            ISivarcProgramBlockSource? Program() => value == null ? null : value as ISivarcProgramBlockSource ?? throw new ArgumentException(property + " needs a PLC block, block type or block master copy (ISivarcProgramBlockSource).");
            ISivarcDataBlockSource? DataBlock() => value == null ? null : value as ISivarcDataBlockSource ?? throw new ArgumentException(property + " needs a data block source (ISivarcDataBlockSource).");
            ISivarcLibraryItem? Item() => value == null ? null : value as ISivarcLibraryItem ?? throw new ArgumentException(property + " needs a library master copy, type or folder (ISivarcLibraryItem).");
            ISivarcLibraryMasterCopy? Copy() => value == null ? null : value as ISivarcLibraryMasterCopy ?? throw new ArgumentException(property + " needs a library master copy (ISivarcLibraryMasterCopy).");
            switch (rule, property)
            {
                case (ScreenRule r, "ProgramBlock"): r.ProgramBlock = Program(); break; case (ScreenRule r, "LibraryScreen"): r.LibraryScreen = Item(); break; case (ScreenRule r, "ScreenObjectLibraryItem"): r.ScreenObjectLibraryItem = Item(); break;
                case (ScreenRuleGroup g, "ProgramBlock"): g.ProgramBlock = Program(); break; case (ScreenRuleGroup g, "LibraryScreen"): g.LibraryScreen = Item(); break; case (ScreenRuleGroup g, "ScreenObjectLibraryItem"): g.ScreenObjectLibraryItem = Item(); break;
                case (AdvancedTagRule r, "ProgramBlock"): r.ProgramBlock = DataBlock(); break; case (AdvancedTagRule r, "TagLibraryItem"): r.TagLibraryItem = Copy(); break;
                case (AlarmRule r, "ProgramBlock"): r.ProgramBlock = Program(); break; case (AlarmRule r, "AlarmLibraryItem"): r.AlarmLibraryItem = Copy(); break;
                case (CopyRule r, "LibraryObject"): r.LibraryObject = Item(); break;
                case (TextlistRule r, "ProgramBlock"): r.ProgramBlock = Program(); break; case (TextlistRule r, "TextlistLibraryItem"): r.TextlistLibraryItem = Copy(); break;
                default: throw new ArgumentException(property + " is not a reference property of " + rule.GetType().Name + ".");
            }
#else
            IEngineeringObject? Obj() => value == null ? null : value as IEngineeringObject ?? throw new ArgumentException(property + " needs an engineering object.");
            switch (rule, property)
            {
                case (ScreenRule r, "ProgramBlock"): r.ProgramBlock = Obj(); break; case (ScreenRule r, "LibraryScreen"): r.LibraryScreen = Obj(); break; case (ScreenRule r, "ScreenObjectLibraryItem"): r.ScreenObjectLibraryItem = Obj(); break;
                case (ScreenRuleGroup g, "ProgramBlock"): g.ProgramBlock = Obj(); break; case (ScreenRuleGroup g, "LibraryScreen"): g.LibraryScreen = Obj(); break; case (ScreenRuleGroup g, "ScreenObjectLibraryItem"): g.ScreenObjectLibraryItem = Obj(); break;
                case (AdvancedTagRule r, "ProgramBlock"): r.ProgramBlock = Obj(); break; case (AdvancedTagRule r, "TagLibraryItem"): r.TagLibraryItem = Obj(); break;
                case (AlarmRule r, "ProgramBlock"): r.ProgramBlock = Obj(); break; case (AlarmRule r, "AlarmLibraryItem"): r.AlarmLibraryItem = Obj(); break;
                case (CopyRule r, "LibraryObject"): r.LibraryObject = Obj(); break;
                case (TextlistRule r, "ProgramBlock"): r.ProgramBlock = Obj(); break; case (TextlistRule r, "TextlistLibraryItem"): r.TextlistLibraryItem = Obj(); break;
                default: throw new ArgumentException(property + " is not a reference property of " + rule.GetType().Name + ".");
            }
#endif
        }
        // Typed setters for the scalar rule / group properties (ConditionOperator parsed from the official enum names).
        private static void SetSivarcScalar(object rule, string property, JsonNode? value)
        {
            string S() => value?.GetValue<string>() ?? "";
            bool B() => value?.GetValue<bool>() ?? throw new ArgumentException(property + " needs true / false.");
            ConditionOperator Op() => (ConditionOperator)Enum.Parse(typeof(ConditionOperator), Logic.RequireOneOf(S(), Logic.ConditionOperators, "ConditionOperator"));
            switch (rule)
            {
                case ScreenRule r: switch (property) { case "Name": r.Name = S(); break; case "Comment": r.Comment = S(); break; case "Condition": r.Condition = S(); break; case "ConditionOperator": r.ConditionOperator = Op(); break; case "Enabled": r.Enabled = B(); break; case "LayoutField": r.LayoutField = S(); break; case "LoopCount": r.LoopCount = S(); break; default: throw Unknown(); } break;
                case ScreenRuleGroup g: switch (property) { case "Name": g.Name = S(); break; case "Comment": g.Comment = S(); break; case "Condition": g.Condition = S(); break; case "ConditionOperator": g.ConditionOperator = Op(); break; case "Enabled": g.Enabled = B(); break; case "LayoutField": g.LayoutField = S(); break; case "LoopCount": g.LoopCount = S(); break; default: throw Unknown(); } break;
                case TagRule r: switch (property) { case "Name": r.Name = S(); break; case "Comment": r.Comment = S(); break; case "Condition": r.Condition = S(); break; case "ConditionOperator": r.ConditionOperator = Op(); break; case "Enabled": r.Enabled = B(); break; case "TagGroupHierarchy": r.TagGroupHierarchy = S(); break; case "TagTable": r.TagTable = S(); break; default: throw Unknown(); } break;
                case TagRuleGroup g: switch (property) { case "Name": g.Name = S(); break; case "Comment": g.Comment = S(); break; case "Condition": g.Condition = S(); break; case "ConditionOperator": g.ConditionOperator = Op(); break; case "Enabled": g.Enabled = B(); break; default: throw Unknown(); } break;
                case AdvancedTagRule r: switch (property) { case "Name": r.Name = S(); break; case "Comment": r.Comment = S(); break; case "Condition": r.Condition = S(); break; case "ConditionOperator": r.ConditionOperator = Op(); break; case "Enabled": r.Enabled = B(); break; case "TagGroupHierarchy": r.TagGroupHierarchy = S(); break; case "TagTable": r.TagTable = S(); break; default: throw Unknown(); } break;
                case AdvancedTagRuleGroup g: switch (property) { case "Name": g.Name = S(); break; case "Comment": g.Comment = S(); break; case "Condition": g.Condition = S(); break; case "ConditionOperator": g.ConditionOperator = Op(); break; case "Enabled": g.Enabled = B(); break; default: throw Unknown(); } break;
                case AlarmRule r: switch (property) { case "Name": r.Name = S(); break; case "Comment": r.Comment = S(); break; case "Condition": r.Condition = S(); break; case "ConditionOperator": r.ConditionOperator = Op(); break; case "Enabled": r.Enabled = B(); break; default: throw Unknown(); } break;
                case AlarmRuleGroup g: switch (property) { case "Name": g.Name = S(); break; case "Comment": g.Comment = S(); break; case "Condition": g.Condition = S(); break; case "ConditionOperator": g.ConditionOperator = Op(); break; case "Enabled": g.Enabled = B(); break; default: throw Unknown(); } break;
                case CopyRule r: switch (property) { case "Name": r.Name = S(); break; case "Comment": r.Comment = S(); break; case "Condition": r.Condition = S(); break; case "ConditionOperator": r.ConditionOperator = Op(); break; case "Enabled": r.Enabled = B(); break; case "FolderStructure": r.FolderStructure = S(); break; default: throw Unknown(); } break;
                case CopyRuleGroup g: switch (property) { case "Name": g.Name = S(); break; case "Comment": g.Comment = S(); break; case "Condition": g.Condition = S(); break; case "ConditionOperator": g.ConditionOperator = Op(); break; case "Enabled": g.Enabled = B(); break; default: throw Unknown(); } break;
                case TextlistRule r: switch (property) { case "Name": r.Name = S(); break; case "Comment": r.Comment = S(); break; case "Condition": r.Condition = S(); break; case "ConditionOperator": r.ConditionOperator = Op(); break; case "Enabled": r.Enabled = B(); break; default: throw Unknown(); } break;
                case TextlistRuleGroup g: switch (property) { case "Name": g.Name = S(); break; case "Comment": g.Comment = S(); break; case "Condition": g.Condition = S(); break; case "ConditionOperator": g.ConditionOperator = Op(); break; case "Enabled": g.Enabled = B(); break; default: throw Unknown(); } break;
                default: throw SivarcShape(rule);
            }
            Exception Unknown() => new ArgumentException(property + " is not a scalar property of " + rule.GetType().Name + ".");
        }
        // PLC / HMI device columns are dynamic attributes named after the PLC / HMI runtime (official "Configuring PLC and HMI device").
        private static JsonObject SivarcDeviceColumns(IEngineeringObject rule, string[] names)
        {
            var row = new JsonObject();
            foreach (var name in names) { try { row[name] = EngineeringScalarProperties.Json(rule.GetAttribute(name)); } catch (Exception ex) { row[name] = null; row[name + "Error"] = ex.GetBaseException().Message; } }
            return row;
        }

        public ResponseMessage ManageSivarcRule(string category, string tablePath, string rulePath = "", string kind = "rule", string action = "read", string propertiesJson = "{}", string referencesJson = "{}",
            string deviceSelectionJson = "{}", string deviceNamesJson = "[]", string libraryName = "", string masterCopyPath = "", string createOption = "Replace", bool confirmDelete = false, bool dryRun = true)
            => RunHmiStepTool("ManageSivarcRule", meta =>
            {
                bool write = Logic.ValidateRuleRequest(category, tablePath, rulePath, kind, action, propertiesJson, referencesJson, deviceSelectionJson, masterCopyPath, createOption, confirmDelete, dryRun);
                var deviceNames = Logic.ParseNames(deviceNamesJson, "deviceNamesJson");
                using var access = write ? AcquireHmiEditAccess() : null;
                var family = Family(category); var sivarc = RequireSivarc(); var table = SivarcTable(family, sivarc, tablePath);
                meta["category"] = category; meta["tablePath"] = tablePath; meta["rulePath"] = rulePath; meta["kind"] = kind; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                var parts = EngineeringGroupOperations.Parts(rulePath, true); var groupPath = action == "createFromMasterCopy" ? rulePath : string.Join("/", parts.Take(parts.Length - 1));
                var owner = SivarcGroup(family, table, groupPath);
                object Collection(object o) => kind == "group" ? family.Groups(o) : family.Rules(o);
                object? Fresh(string name) => EngineeringGroupOperations.Find(Collection(SivarcGroup(family, SivarcTable(family, sivarc, tablePath), groupPath)), name);
                var existing = action == "createFromMasterCopy" ? null : EngineeringGroupOperations.Find(Collection(owner), parts.Last());
                if (action == "read" || action == "update" || action == "delete")
                {
                    if (existing == null) throw new PortalException(PortalErrorCode.NotFound, "SiVArc " + kind + " not found: " + rulePath + " (table " + tablePath + ")");
                    meta["before"] = kind == "group" ? SivarcGroupRow(family, existing, 0, 1, false) : SivarcRuleRow(existing, category == "screens");
                    if (deviceNames.Length > 0) meta["deviceColumns"] = SivarcDeviceColumns((IEngineeringObject)existing, deviceNames);
                }
                var properties = Logic.ParseObject(propertiesJson, "propertiesJson"); var references = Logic.ParseObject(referencesJson, "referencesJson"); var devices = Logic.ParseObject(deviceSelectionJson, "deviceSelectionJson");
                if (action == "read") return "SiVArc " + kind + " read (typed " + existing!.GetType().Name + "); no modification.";
                if (action == "delete")
                {
                    if (kind == "group" && (meta["before"]!["groupCount"]!.GetValue<int>() > 0 || meta["before"]!["ruleCount"]!.GetValue<int>() > 0)) throw new InvalidOperationException("Nonempty rule group deletion refused; delete its rules and sub-groups first.");
                    if (!write) return "Delete preview of SiVArc " + kind + "; nothing changed.";
                    meta["mayHaveChanged"] = true; SivarcDelete(existing!);
                    meta["verifiedAbsent"] = Fresh(parts.Last()) == null;
                    if (meta["verifiedAbsent"]!.GetValue<bool>() != true) throw new InvalidOperationException("SiVArc " + kind + " still present after Delete().");
                    return "SiVArc " + kind + " deleted and verified absent; project not saved.";
                }
                // resolve references up front so a preview reports what would be assigned
                var resolved = new Dictionary<string, object?>(StringComparer.Ordinal); var resolvedRows = new JsonObject();
                foreach (var pair in references)
                {
                    var referenced = pair.Value == null ? null : ResolveSivarcReference(Logic.ParseReference(pair.Value, pair.Key));
                    resolved[pair.Key] = referenced; resolvedRows[pair.Key] = SivarcReferenceJson(referenced);
                }
                if (resolvedRows.Count > 0) meta["references"] = resolvedRows;
                MasterCopy? source = null; CreateOptions option = (CreateOptions)Enum.Parse(typeof(CreateOptions), createOption);
                if (action == "createFromMasterCopy") { source = ExactMasterCopy(libraryName, masterCopyPath); meta["source"] = new JsonObject { ["masterCopyPath"] = masterCopyPath, ["name"] = source.Name, ["createOption"] = option.ToString() }; }
                else if (action == "create" && existing != null) throw new InvalidOperationException("SiVArc " + kind + " already exists: " + rulePath);
                meta["nativeSignature"] = action == "createFromMasterCopy" ? Collection(owner).GetType().Name + ".CreateFrom(MasterCopy, CreateOptions." + option + ")" : action == "create" ? Collection(owner).GetType().Name + ".Create(string)" : "typed property setters";
                if (!write) return "SiVArc " + kind + " " + action + " preview; nothing changed (real edits need the SiVArc licence).";
                meta["mayHaveChanged"] = true;
                object target;
                if (action == "createFromMasterCopy") target = kind == "group" ? family.CreateGroupFrom(Collection(owner), source!, option) : family.CreateRuleFrom(Collection(owner), source!, option);
                else if (action == "create") target = kind == "group" ? family.CreateGroup(Collection(owner), parts.Last()) : family.CreateRule(Collection(owner), parts.Last());
                else target = existing!;
                foreach (var pair in properties) SetSivarcScalar(target, pair.Key, pair.Value);
                foreach (var pair in resolved) SetSivarcReference(target, pair.Key, pair.Value);
                if (devices.Count > 0) ((IEngineeringObject)target).SetAttributes(devices.Select(d => new KeyValuePair<string, object>(d.Key, d.Value!.GetValue<bool>())).ToList());
                var fresh = Fresh(SivarcName(target)) ?? throw new InvalidOperationException("SiVArc " + kind + " not found on re-navigation after " + action + ".");
                meta["after"] = kind == "group" ? SivarcGroupRow(family, fresh, 0, 1, false) : SivarcRuleRow(fresh, category == "screens");
                if (devices.Count > 0) meta["deviceColumnsAfter"] = SivarcDeviceColumns((IEngineeringObject)fresh, devices.Select(d => d.Key).ToArray());
                return "SiVArc " + kind + " " + action + " applied and read back; project not saved, no generation.";
            });

        // ---- tools: block definitions (SivarcDataProvider on a CodeBlock) ----------------------------------------------------------------
        private (CodeBlock block, SivarcDataProvider provider) RequireSivarcBlock(string softwarePath, string blockPath, bool writing)
        {
            var plc = ExactPlcForEngineering(softwarePath, writing);
            var block = ExactObjectUnder(plc.BlockGroup, blockPath, "Blocks", "PLC block") as CodeBlock ?? throw new ArgumentException("SivarcDataProvider applies to code blocks (OB / FB / FC), not to " + blockPath + ".");
            var provider = block.GetService<SivarcDataProvider>() ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "SivarcDataProvider unavailable on this block (SiVArc option package not installed).");
            return (block, provider);
        }
        private static JsonObject TagDefinitionRow(TagDefinition d) => new JsonObject { ["name"] = d.Name, ["value"] = d.Value, ["comment"] = d.Comment };
        private static JsonObject TextDefinitionRow(TextDefinition d) => new JsonObject { ["name"] = d.Name, ["expression"] = d.Expression, ["comment"] = d.Comment, ["text"] = MultilingualJson(d.Text) };
#if !TIA_V20
        private static JsonObject TagMemberRow(TagMember m) => new JsonObject { ["name"] = m.Name, ["acquisitionCycle"] = m.AcquisitionCycle, ["acquisitionMode"] = m.AcquisitionMode.ToString(), ["comment"] = m.Comment };
        private static JsonObject TagMemberSettingRow(TagMemberSetting s, bool members)
        {
            TagMemberComposition parameters = s.BlockParameters;
            var row = new JsonObject { ["useCommonConfiguration"] = s.UseCommonConfiguration, ["blockParameterCount"] = parameters.Count };
            Safe(row, "commonParameters", () => s.CommonParameters == null ? null : TagMemberRow(s.CommonParameters));
            if (members) row["blockParameters"] = new JsonArray(EngineeringGroupOperations.Items(parameters).Cast<TagMember>().Take(500).Select(m => (JsonNode)TagMemberRow(m)).ToArray());
            return row;
        }
        private static void SetTagMember(TagMember member, string property, JsonNode? value)
        {
            switch (property)
            {
                case "AcquisitionCycle": member.AcquisitionCycle = value?.GetValue<string>() ?? ""; break;
                case "AcquisitionMode": member.AcquisitionMode = (AcquisitionMode)Enum.Parse(typeof(AcquisitionMode), Logic.RequireOneOf(value?.GetValue<string>() ?? "", Logic.AcquisitionModes, "AcquisitionMode")); break;
                default: member.Comment = value?.GetValue<string>() ?? ""; break;
            }
        }
#endif
        public ResponseMessage ReadSivarcBlockDefinitions(string softwarePath, string blockPath, bool includeBlockParameters = true)
            => RunHmiStepTool("ReadSivarcBlockDefinitions", meta =>
            {
                EngineeringGroupOperations.Parts(blockPath);
                var (block, provider) = RequireSivarcBlock(softwarePath, blockPath, false);
                TagDefinitionComposition tags = provider.TagDefinitions; TextDefinitionComposition texts = provider.TextDefinitions;
                meta["softwarePath"] = softwarePath; meta["blockPath"] = blockPath; meta["block"] = new JsonObject { ["name"] = block.Name, ["blockClass"] = block.GetType().Name, ["number"] = block.Number };
                meta["tagDefinitions"] = new JsonArray(EngineeringGroupOperations.Items(tags).Cast<TagDefinition>().Take(500).Select(d => (JsonNode)TagDefinitionRow(d)).ToArray()); meta["tagDefinitionCount"] = tags.Count;
                meta["textDefinitions"] = new JsonArray(EngineeringGroupOperations.Items(texts).Cast<TextDefinition>().Take(500).Select(d => (JsonNode)TextDefinitionRow(d)).ToArray()); meta["textDefinitionCount"] = texts.Count;
#if TIA_V20
                meta["tagMemberSettings"] = null; meta["tagMemberSettingsNote"] = "TagMemberSetting / TagMember are V21 additions; absent on V20.";
#else
                Safe(meta, "tagMemberSettings", () => provider.TagMemberSettings == null ? null : TagMemberSettingRow(provider.TagMemberSettings, includeBlockParameters));
#endif
                return "SiVArc tag / text definitions and tag member settings of the block read (SivarcDataProvider); no modification.";
            });

        public ResponseMessage ManageSivarcBlockDefinition(string softwarePath, string blockPath, string kind, string name = "", string action = "read", string propertiesJson = "{}", string textsJson = "{}", bool confirmDelete = false, bool dryRun = true)
            => RunHmiStepTool("ManageSivarcBlockDefinition", meta =>
            {
                bool write = Logic.ValidateDefinitionRequest(blockPath, kind, name, action, propertiesJson, textsJson, confirmDelete, dryRun);
                using var access = write ? AcquireHmiEditAccess() : null;
                var (block, provider) = RequireSivarcBlock(softwarePath, blockPath, write);
                var properties = Logic.ParseObject(propertiesJson, "propertiesJson"); var texts = Logic.ParseObject(textsJson, "textsJson");
                meta["softwarePath"] = softwarePath; meta["blockPath"] = blockPath; meta["kind"] = kind; meta["name"] = name; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                string S(string key) => properties[key]?.GetValue<string>() ?? "";
                if (kind == "tagDefinition" || kind == "textDefinition")
                {
                    bool tag = kind == "tagDefinition";
                    object Fresh() => tag ? provider.TagDefinitions : provider.TextDefinitions;
                    var existing = EngineeringGroupOperations.Find(Fresh(), name);
                    if (action != "create" && existing == null) throw new PortalException(PortalErrorCode.NotFound, kind + " not found on block: " + name);
                    if (action == "create" && existing != null) throw new InvalidOperationException(kind + " already exists on block: " + name);
                    if (existing != null) meta["before"] = tag ? TagDefinitionRow((TagDefinition)existing) : TextDefinitionRow((TextDefinition)existing);
                    if (action == "read") return kind + " read; no modification.";
                    meta["nativeSignature"] = action == "create" ? (tag ? "TagDefinitionComposition.Create(string)" : "TextDefinitionComposition.Create(string)") : action == "delete" ? kind + ".Delete()" : "typed property setters" + (texts.Count > 0 ? " + MultilingualTextItem.Text" : "");
                    if (!write) return kind + " " + action + " preview; nothing changed (real edits need the SiVArc licence).";
                    meta["mayHaveChanged"] = true;
                    if (action == "delete")
                    {
                        if (tag) ((TagDefinition)existing!).Delete(); else ((TextDefinition)existing!).Delete();
                        meta["verifiedAbsent"] = EngineeringGroupOperations.Find(Fresh(), name) == null;
                        if (meta["verifiedAbsent"]!.GetValue<bool>() != true) throw new InvalidOperationException(kind + " still present after Delete().");
                        return kind + " deleted and verified absent; project not saved.";
                    }
                    if (tag)
                    {
                        TagDefinitionComposition definitions = provider.TagDefinitions;
                        var target = action == "create" ? definitions.Create(name) : (TagDefinition)existing!;
                        foreach (var pair in properties) { switch (pair.Key) { case "Name": target.Name = S("Name"); break; case "Value": target.Value = S("Value"); break; default: target.Comment = S("Comment"); break; } }
                        var fresh = EngineeringGroupOperations.Find(Fresh(), target.Name) as TagDefinition ?? throw new InvalidOperationException("Tag definition not found on re-navigation."); meta["after"] = TagDefinitionRow(fresh);
                    }
                    else
                    {
                        TextDefinitionComposition definitions = provider.TextDefinitions;
                        var target = action == "create" ? definitions.Create(name) : (TextDefinition)existing!;
                        foreach (var pair in properties) { switch (pair.Key) { case "Name": target.Name = S("Name"); break; case "Expression": target.Expression = S("Expression"); break; default: target.Comment = S("Comment"); break; } }
                        if (texts.Count > 0)
                        {
                            var items = EngineeringGroupOperations.Items(target.Text.Items).Cast<MultilingualTextItem>().ToArray();
                            foreach (var pair in texts)
                            {
                                var item = items.FirstOrDefault(i => string.Equals(i.Language?.Culture?.Name, pair.Key, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException("Project language not found for text definition: " + pair.Key);
                                item.Text = pair.Value!.GetValue<string>();
                            }
                        }
                        var fresh = EngineeringGroupOperations.Find(Fresh(), target.Name) as TextDefinition ?? throw new InvalidOperationException("Text definition not found on re-navigation."); meta["after"] = TextDefinitionRow(fresh);
                    }
                    return kind + " " + action + " applied and read back; project not saved.";
                }
#if TIA_V20
                throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "TagMemberSetting / TagMember (kind " + kind + ") are V21 additions; absent on V20.");
#else
                var settings = provider.TagMemberSettings ?? throw new PortalException(PortalErrorCode.NotFound, "TagMemberSettings unavailable on this block.");
                if (kind == "tagMemberSettings")
                {
                    meta["before"] = TagMemberSettingRow(settings, false);
                    if (action == "read") return "Tag member settings read; no modification.";
                    if (!write) return "Tag member settings update preview; nothing changed.";
                    meta["mayHaveChanged"] = true; settings.UseCommonConfiguration = properties["UseCommonConfiguration"]?.GetValue<bool>() ?? settings.UseCommonConfiguration;
                    meta["after"] = TagMemberSettingRow(provider.TagMemberSettings!, false);
                    return "Tag member settings updated and read back; project not saved.";
                }
                TagMember member = kind == "commonParameters" ? settings.CommonParameters ?? throw new PortalException(PortalErrorCode.NotFound, "CommonParameters unavailable on this block.")
                    : EngineeringGroupOperations.Find(settings.BlockParameters, name) as TagMember ?? throw new PortalException(PortalErrorCode.NotFound, "Block parameter not found in tag member settings: " + name);
                meta["before"] = TagMemberRow(member);
                if (action == "read") return "Tag member read; no modification.";
                if (!write) return "Tag member update preview; nothing changed.";
                meta["mayHaveChanged"] = true;
                foreach (var pair in properties) SetTagMember(member, pair.Key, pair.Value);
                var again = provider.TagMemberSettings!;
                meta["after"] = TagMemberRow(kind == "commonParameters" ? again.CommonParameters : (TagMember)EngineeringGroupOperations.Find(again.BlockParameters, name)!);
                return "Tag member updated and read back; project not saved.";
#endif
            });

        // ---- tools: expression resolver --------------------------------------------------------------------------------------------------
        public ResponseMessage ResolveSivarcExpression(string softwarePath, string blockPath, string devicePathJson, string itemPathJson, string libraryItemKind, string libraryItemPath, string expression, string libraryName = "", int maxResults = 500)
            => RunHmiStepTool("ResolveSivarcExpression", meta =>
            {
                Logic.ValidateExpressionRequest(blockPath, devicePathJson, itemPathJson, libraryItemKind, libraryItemPath, expression, maxResults);
                var sivarc = RequireSivarc();
                var plc = ExactPlcForEngineering(softwarePath, false);
                var block = ExactObjectUnder(plc.BlockGroup, blockPath, "Blocks", "PLC block") as CodeBlock ?? throw new ArgumentException("The expression resolver takes a code block (FB / FC), not " + blockPath + ".");
                var deviceItem = ExactDeviceItem(Logic.ParseNames(devicePathJson, "devicePathJson"), Logic.ParseNames(itemPathJson, "itemPathJson"));
                object libraryItem = libraryItemKind == "masterCopy" ? ExactMasterCopy(libraryName, libraryItemPath) : ExactLibraryType(ExactOpenEngineeringLibrary(libraryName), libraryItemPath);
                meta["block"] = new JsonObject { ["name"] = block.Name, ["blockClass"] = block.GetType().Name }; meta["deviceItem"] = deviceItem.Name; meta["libraryItem"] = SivarcReferenceJson(libraryItem); meta["expression"] = expression;
#if TIA_V20
                ExpressionResolver resolver = sivarc.GetExpressionResolver(block, deviceItem, libraryItem as ISivarcLibraryItem ?? throw new ArgumentException("Library item is not a SiVArc library item (ISivarcLibraryItem)."));
#else
                ExpressionResolver resolver = sivarc.GetExpressionResolver(block, deviceItem, (IEngineeringObject)libraryItem);
#endif
                var results = resolver.Resolve(expression);
                var rows = EngineeringGroupOperations.Items(results).Cast<ExpressionResult>().Select(r => (JsonNode)new JsonObject { ["instanceName"] = r.InstanceName, ["callPath"] = r.CallPath, ["result"] = r.Result }).ToArray();
                Page(rows, 0, maxResults, meta);
                return "SiVArc expression resolved for every block instance in the compiled call structure (ExpressionResolver.Resolve); read-only.";
            });

        // ---- tools: screen layout data (V21 LayoutData on a classic Screen or Unified HmiScreen) -------------------------------------------
        public ResponseMessage ManageSivarcScreenLayout(string softwarePath, string screenName, string action, string filePath, bool dryRun = true)
            => RunHmiStepTool("ManageSivarcScreenLayout", meta =>
            {
                bool write = Logic.ValidateLayoutRequest(screenName, action, filePath, dryRun);
                using var access = write ? AcquireHmiEditAccess() : null;
                var screen = ResolveHmiScreenOrThrow(softwarePath, screenName);
                meta["softwarePath"] = softwarePath; meta["screenName"] = screenName; meta["screenClass"] = screen.GetType().Name; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
#if TIA_V20
                throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "SiVArc LayoutData (screen layout export / import) is a V21 addition; absent on V20.");
#else
                var provider = screen as IEngineeringServiceProvider ?? throw new NotSupportedException("Screen is not a service provider: " + screen.GetType().FullName);
                LayoutData layout = provider.GetService<LayoutData>() ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "LayoutData service unavailable on this screen (SiVArc option package not installed).");
                if (action == "export")
                {
                    var file = NativeFileOutput.Plan(filePath); meta["file"] = file.FullName;
                    layout.Export(file); meta["output"] = NativeFileOutput.Verify(file);
                    return "SiVArc screen layouts exported to the YML file (LayoutData.Export); project unchanged.";
                }
                var input = HardwareServicesLogic.RequireExistingInputFile(filePath, "filePath"); meta["file"] = input.FullName;
                if (!write) return "SiVArc layout import preview; nothing changed (LayoutData.Import would apply the file's layouts to the screen).";
                meta["mayHaveChanged"] = true;
                LayoutDataImportResult result = layout.Import(input);
                meta["result"] = new JsonObject { ["state"] = result.State.ToString(), ["numberOfLayouts"] = result.NumberOfLayouts };
                if (result.State != LayoutImportResultState.Success) meta["operationSuccess"] = false;
                return "SiVArc screen layouts imported (LayoutData.Import); inspect result.state; project not saved.";
#endif
            });

        // ---- tools: definitions upgrader --------------------------------------------------------------------------------------------------
        public ResponseMessage UpgradeSivarcDefinitions(string softwarePath, bool dryRun = true)
            => RunHmiStepTool("UpgradeSivarcDefinitions", meta =>
            {
                using var access = dryRun ? null : AcquireHmiEditAccess();
                var plc = ExactPlcForEngineering(softwarePath, !dryRun);
                var upgrader = plc.GetService<SivarcDefinitionsUpgrader>() ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "SivarcDefinitionsUpgrader unavailable on this PLC (SiVArc option package not installed).");
                meta["softwarePath"] = softwarePath; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["nativeSignature"] = "SivarcDefinitionsUpgrader.Upgrade()";
                if (dryRun) return "SiVArc definitions upgrade preview (SIVARCCOND / SIVARCTEXT -> tag / text definitions); nothing changed.";
                meta["mayHaveChanged"] = true;
                UpgradeDefinitionsResult result = upgrader.Upgrade();
                meta["result"] = new JsonObject { ["warningCount"] = result.WarningCount, ["messages"] = SivarcMessageRows(result.Messages) };
                return "SiVArc definitions upgraded on the PLC; inspect warningCount / messages; project not saved.";
            });

        // ---- typed generation (retrofit of GenerateSiVArc) ----------------------------------------------------------------------------------
        public ResponseMessage GenerateSiVArc(string hmiDeviceName, string plcSoftwarePathsJson, string generationOptions, bool dryRun = true, string additionalHmiDeviceNamesJson = "[]")
            => RunHmiStepTool("GenerateSiVArc", meta =>
            {
                using var access = dryRun ? null : AcquireHmiEditAccess();
                var device = ExactEngineeringDevice(new JsonArray(JsonValue.Create(hmiDeviceName)).ToJsonString());
                var extra = Logic.ParseNames(additionalHmiDeviceNamesJson, "additionalHmiDeviceNamesJson").Select(n => ExactEngineeringDevice(new JsonArray(JsonValue.Create(n)).ToJsonString()).Name).ToArray();
                var devices = new[] { device.Name }.Concat(extra).ToArray();
                if (devices.Distinct(StringComparer.Ordinal).Count() != devices.Length) throw new ArgumentException("HMI device names repeat.");
                var plcs = ExactNameList(plcSoftwarePathsJson).Select(p => ExactPlcForEngineering(p, !dryRun).Name).ToArray();
                if (plcs.Distinct(StringComparer.Ordinal).Count() != plcs.Length) throw new InvalidOperationException("Native PLC name aliases are ambiguous.");
                var sivarc = RequireSivarc();
                var names = Logic.ParseGenerationOptions(generationOptions);
                GenerationOptions options = names.Aggregate(GenerationOptions.None, (acc, n) => acc | (GenerationOptions)Enum.Parse(typeof(GenerationOptions), n));
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["hmiDeviceName"] = device.Name; meta["hmiDeviceNames"] = new JsonArray(devices.Select(d => (JsonNode)d).ToArray()); meta["plcNames"] = new JsonArray(plcs.Select(p => (JsonNode)p).ToArray());
                meta["generationOptions"] = options.ToString(); meta["nativeSignature"] = devices.Length > 1 ? "Sivarc.Generate(IEnumerable<string>, IEnumerable<string>, GenerationOptions)" : "Sivarc.Generate(string, IEnumerable<string>, GenerationOptions)";
                if (dryRun) return "SiVArc native generation preview; generation can create/update HMI objects according to rules and selected native options.";
                meta["mayHaveChanged"] = true;
                SivarcGenerationResult result = devices.Length > 1 ? sivarc.Generate(devices, plcs, options) : sivarc.Generate(device.Name, plcs, options);
                meta["result"] = new JsonObject { ["isGenerationSuccessful"] = result.IsGenerationSuccessful, ["errorCount"] = result.ErrorCount, ["warningCount"] = result.WarningCount, ["messages"] = SivarcMessageRows(result.Messages) };
                meta["generationPassed"] = result.IsGenerationSuccessful; meta["apiCallSuccess"] = true;
                if (!result.IsGenerationSuccessful) meta["operationSuccess"] = false;
                return "SiVArc generation returned (typed SivarcGenerationResult with recursive feedback messages). No automatic save/compile/download.";
            });

        // ---- library type kinds of the option packages (ReadLibraryType typeKind) ----------------------------------------------------------
        private static string OptionPackageLibraryTypeKind(LibraryType type) => type switch
        {
            ScreenRuleTableType => "sivarcScreenRuleTable", TagRuleTableType => "sivarcTagRuleTable", AdvancedTagRuleTableType => "sivarcAdvancedTagRuleTable",
            AlarmRuleTableType => "sivarcAlarmRuleTable", CopyRuleTableType => "sivarcCopyRuleTable", TextlistRuleTableType => "sivarcTextlistRuleTable", ExpressionTableType => "sivarcExpressionTable",
            global::Siemens.Engineering.MC.Drives.Dcc.DcbBlockTypeLibraryType => "dccBlockType",
            _ => "other"
        };
    }
}
