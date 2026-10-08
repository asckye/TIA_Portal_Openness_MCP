#nullable disable
#if TIA_ENGINE_LOCAL_PRIMITIVES
#define PLC_WATCH_READ
#define PLC_TECH_GROUP_READ
#define PLC_DOCUMENT_EXPORT
#define PLC_WATCH_EXPORT
namespace TiaMcpServer.Siemens.LocalWatchTechnology
#else
namespace TiaMcp.Adapters
#endif
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using global::Siemens.Engineering;
    using global::Siemens.Engineering.SW;
    using global::Siemens.Engineering.SW.TechnologicalObjects;
#if PLC_WATCH_READ
    using global::Siemens.Engineering.SW.WatchAndForceTables;
#endif

    // Raw native operations only. Hosts retain lookup, traversal, import options,
    // reflection fallbacks, readback, error handling and their existing threads.
    public static class WatchTechnologyPrimitives
    {
        public static TechnologicalInstanceDBGroup TechnologyGroup(PlcSoftware software) => software.TechnologicalObjectGroup;
        public static TechnologicalInstanceDBComposition Objects(TechnologicalInstanceDBGroup group) => group.TechnologicalObjects;
        public static string Name(TechnologicalInstanceDB item) => item.Name;
        public static string Name(TechnologicalInstanceDBGroup group) => group.Name;
#if PLC_TECH_GROUP_READ
        public static TechnologicalInstanceDBUserGroupComposition Groups(TechnologicalInstanceDBGroup group) => group.Groups;
        public static string Name(TechnologicalInstanceDBUserGroup group) => group.Name;
#endif
#if PLC_WATCH_READ
        public static PlcWatchAndForceTableSystemGroup WatchGroup(PlcSoftware software) => software.WatchAndForceTableGroup;
        public static PlcWatchTableComposition WatchTables(PlcWatchAndForceTableGroup group) => group.WatchTables;
        public static PlcWatchAndForceTableUserGroupComposition Groups(PlcWatchAndForceTableGroup group) => group.Groups;
        public static string Name(PlcWatchAndForceTableUserGroup group) => group.Name;
        public static string Name(PlcWatchTable table) => table.Name;
        public static bool IsConsistent(PlcWatchTable table) => table.IsConsistent;
#if PLC_SPECIAL_EXPORT || PLC_WATCH_EXPORT
        public static void Export(PlcWatchTable table, FileInfo file, ExportOptions options) => table.Export(file, options);
#endif
#endif

        // This editing surface is used by the V20/V21 engines only.
#if PLC_DOCUMENT_EXPORT
        public static object Attribute(IEngineeringObject target, string name) => target.GetAttribute(name);
        public static void SetAttribute(IEngineeringObject target, string name, object value) => target.SetAttribute(name, value);
        public static TechnologicalInstanceDB Create(TechnologicalInstanceDBComposition objects, string name, string typeIdentifier, Version version) => objects.Create(name, typeIdentifier, version);
        public static TechnologicalInstanceDB Find(TechnologicalInstanceDBComposition objects, string name) => objects.Find(name);
        public static TechnologicalParameter Find(TechnologicalParameterComposition parameters, string name) => parameters.Find(name);
        public static TechnologicalParameterComposition Parameters(TechnologicalInstanceDB item) => item.Parameters;
        public static int Count(TechnologicalInstanceDBComposition objects) => objects.Count;
        public static int Count(TechnologicalInstanceDBUserGroupComposition groups) => groups.Count;
        public static PlcWatchTable Find(PlcWatchTableComposition tables, string name) => tables.Find(name);
        public static IList<PlcWatchTable> Import(PlcWatchTableComposition tables, FileInfo file, ImportOptions options) => tables.Import(file, options);
        public static PlcTableCommentEntryComposition Entries(PlcWatchTable table) => table.Entries;
        public static PlcTableCommentEntryComposition Entries(PlcForceTable table) => table.Entries;
        public static string Name(PlcForceTable table) => table.Name;
        public static bool IsConsistent(PlcForceTable table) => table.IsConsistent;
        public static int Count(PlcTableCommentEntryComposition entries) => entries.Count;
        public static PlcTableCommentEntry Create(PlcTableCommentEntryComposition entries) => entries.Create();
        public static void Delete(PlcWatchTable table) => table.Delete();
        public static void Delete(PlcTableCommentEntry entry) => entry.Delete();
        public static string Name(PlcWatchTableEntry entry) => entry.Name;
        public static string Address(PlcWatchTableEntry entry) => entry.Address;
        public static PlcWatchAndForceTableDisplayFormat DisplayFormat(PlcWatchTableEntry entry) => entry.DisplayFormat;
        public static PlcWatchAndForceTablePreDefinedTrigger MonitorTrigger(PlcWatchTableEntry entry) => entry.MonitorTrigger;
        public static PlcWatchAndForceTablePreDefinedTrigger ModifyTrigger(PlcWatchTableEntry entry) => entry.ModifyTrigger;
        public static string ModifyValue(PlcWatchTableEntry entry) => entry.ModifyValue;
        public static bool ModifyIntention(PlcWatchTableEntry entry) => entry.ModifyIntention;
        public static string Name(PlcForceTableEntry entry) => entry.Name;
        public static string Address(PlcForceTableEntry entry) => entry.Address;
        public static PlcWatchAndForceTableDisplayFormat DisplayFormat(PlcForceTableEntry entry) => entry.DisplayFormat;
        public static PlcWatchAndForceTablePreDefinedTrigger MonitorTrigger(PlcForceTableEntry entry) => entry.MonitorTrigger;
        public static string ForceValue(PlcForceTableEntry entry) => entry.ForceValue;
        public static bool ForceIntention(PlcForceTableEntry entry) => entry.ForceIntention;
#endif
    }
}
