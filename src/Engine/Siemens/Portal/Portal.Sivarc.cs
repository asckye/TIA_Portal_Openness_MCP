using static TiaMcpServer.Siemens.EngineeringSessionHelpers;
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
    public partial class Portal
    {
        // ---- family table (typed per rule family; the six families share the shape but no base class) ------------------------------------
        internal sealed class SivarcFamily
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
