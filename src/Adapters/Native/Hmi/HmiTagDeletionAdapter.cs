using Siemens.Engineering;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Hmi.Globalization;
using Siemens.Engineering.Hmi.RuntimeScripting;
using Siemens.Engineering.Hmi.Screen;
using Siemens.Engineering.Hmi.Tag;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Plc;
using TiaMcp.Adapters.Reflection;
using TiaMcpServer.ModelContextProtocol;
using static TiaMcp.Adapters.Native.Hmi.HmiAccessHelpers;
#if HMI_UNIFIED
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HmiUnified.HmiTags;
#endif
using System;

namespace TiaMcp.Adapters.Native.Hmi
{
    internal sealed class HmiTagDeletionAdapter
    {
        private readonly HmiSession _session;

        public HmiTagDeletionAdapter(HmiSession session) => _session = session;

        public HmiMessage DeleteHmiTag(string softwarePath, string tagTablePath, string tagName, bool dryRun = true, bool confirmDelete = false)
            => _session.RunHmiStepTool("DeleteHmiTag", meta => {
                string[] parts = HmiTagDeletion.Validate(tagTablePath, tagName, dryRun, confirmDelete);
                if (string.IsNullOrWhiteSpace(softwarePath)) throw new ArgumentException("softwarePath is required.");
                using var access = dryRun ? null : _session.AcquireHmiEditAccess();
                meta["softwarePath"] = softwarePath; meta["tagTablePath"] = tagTablePath; meta["tagName"] = tagName;
                meta["binding"] = _session.GetBindingIdentity();
                string address = softwarePath + tagTablePath + "/" + tagName;
                meta["readStarted"] = true;
                var sw = InvocationJournal.Native("DeleteHmiTag.ResolveSoftware", () => _session.ResolveHmiSoftwareOrThrow(softwarePath), "HmiSoftware/HmiTarget", softwarePath);
                if (sw is HmiTarget classic)
                {
                    if (parts.Length == 0) throw new ArgumentException("Classic HMI requires an explicit /Folder/Table path, including its default table name.");
                    TagFolder folder = InvocationJournal.Native("Classic.TagFolder", () => classic.TagFolder, "HmiTarget", softwarePath);
                    for (int i = 0; i < parts.Length - 1; i++)
                        folder = InvocationJournal.Native("Classic.TagFolder.Find", () => folder.Folders.Find(parts[i]), "TagFolder", tagTablePath) ?? throw new PlcSoftwareException("NotFound", "Tag folder not found: " + parts[i]);
                    var table = InvocationJournal.Native("Classic.TagTable.Find", () => folder.TagTables.Find(parts[parts.Length - 1]), "TagFolder", tagTablePath);
                    if (table == null && parts.Length == 1)
                    {
                        var defaultTable = InvocationJournal.Native("Classic.DefaultTagTable", () => classic.TagFolder.DefaultTagTable, "TagSystemFolder", tagTablePath);
                        if (defaultTable != null && string.Equals(defaultTable.Name, parts[0], StringComparison.Ordinal)) table = defaultTable;
                    }
                    var tags = InvocationJournal.Native("Classic.TagTable.Tags", () => (table ?? throw new PlcSoftwareException("NotFound", "Tag table not found.")).Tags, "TagTable", tagTablePath);
                    meta["family"] = "Classic";
                    return HmiTagDeletion.Execute(meta, dryRun,
                        () => InvocationJournal.Native("Hmi.Tag.Find", () => (object?)tags.Find(tagName), "Siemens.Engineering.Hmi.Tag.Tag", address),
                        tag => InvocationJournal.Native("Hmi.Tag.Delete", () => ((Tag)tag).Delete(), "Siemens.Engineering.Hmi.Tag.Tag", address));
                }
#if HMI_UNIFIED
                if (sw is HmiSoftware unified)
                {
                    HmiTagComposition tags;
                    if (parts.Length == 0) tags = InvocationJournal.Native("Unified.RootTags", () => unified.Tags, "HmiSoftware", softwarePath);
                    else
                    {
                        HmiTagTableGroup? group = null;
                        for (int i = 0; i < parts.Length - 1; i++)
                            group = InvocationJournal.Native("Unified.TagGroup.Find", () => (group == null ? unified.TagTableGroups : group.Groups).Find(parts[i]), "HmiTagTableGroup", tagTablePath) ?? throw new PlcSoftwareException("NotFound", "Tag group not found: " + parts[i]);
                        tags = InvocationJournal.Native("Unified.TagTable.Tags", () => ((group == null ? unified.TagTables : group.TagTables).Find(parts[parts.Length - 1]) ?? throw new PlcSoftwareException("NotFound", "Tag table not found.")).Tags, "HmiTagTable", tagTablePath);
                    }
                    meta["family"] = "Unified";
                    return HmiTagDeletion.Execute(meta, dryRun,
                        () => InvocationJournal.Native("Unified.HmiTag.Find", () => (object?)tags.Find(tagName), "Siemens.Engineering.HmiUnified.HmiTags.HmiTag", address),
                        tag => InvocationJournal.Native("Unified.HmiTag.Delete", () => ((HmiTag)tag).Delete(), "Siemens.Engineering.HmiUnified.HmiTags.HmiTag", address));
                }
#endif
                throw new PlcSoftwareException("NotSupportedOnVersion", "Only Classic and Unified HMI tags are supported.");
            });
    }
}
