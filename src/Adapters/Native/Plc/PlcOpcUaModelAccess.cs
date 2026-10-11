using System.Collections.Generic;
using System.IO;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
#if PLC_UNITS
using Siemens.Engineering.SW.Units;
#endif

namespace TiaMcp.Adapters.Native.Plc
{
#if PLC_OPC_UA
    // Native primitives for the Siemens modelled XML pipeline. The worker owns
    // its XML phases; each primitive runs synchronously on the existing lane.
    public static class PlcOpcUaModelAccess
    {
        public static bool HasProvider(PlcSoftware plc) => plc.GetService<Siemens.Engineering.SW.OpcUa.OpcUaProvider>() != null;
        public static PlcTagTableSystemGroup Tags(PlcSoftware plc) => plc.TagTableGroup;
        public static PlcTypeSystemGroup Types(PlcSoftware plc) => plc.TypeGroup;
        public static PlcBlockSystemGroup Blocks(PlcSoftware plc) => plc.BlockGroup;
#if PLC_UNITS
        public static PlcUnit? Unit(PlcSoftware plc, string name) => plc.GetService<PlcUnitProvider>()?.UnitGroup.Units.Find(name);
        public static PlcTagTableSystemGroup Tags(PlcUnit unit) => unit.TagTableGroup;
        public static PlcTypeSystemGroup Types(PlcUnit unit) => unit.TypeGroup;
        public static PlcBlockSystemGroup Blocks(PlcUnit unit) => unit.BlockGroup;
        public static string Name(PlcUnit unit) => unit.Name;
#endif
        public static IEnumerable<PlcTagTable> Tables(PlcTagTableGroup group) => Enumerate(group.TagTables);
        public static IEnumerable<PlcTagTableUserGroup> Groups(PlcTagTableGroup group) => Enumerate(group.Groups);
        public static IEnumerable<PlcUserConstant> Constants(PlcTagTable table) => Enumerate(table.UserConstants);
        public static IEnumerable<PlcTag> Tags(PlcTagTable table) => Enumerate(table.Tags);
        public static IEnumerable<PlcSystemTypeGroup> SystemGroups(PlcTypeSystemGroup group) => Enumerate(group.SystemTypeGroups);
        public static IEnumerable<PlcType> Types(PlcSystemTypeGroup group) => Enumerate(group.Types);
        public static IEnumerable<PlcType> Types(PlcTypeGroup group) => Enumerate(group.Types);
        public static IEnumerable<PlcTypeUserGroup> Groups(PlcTypeGroup group) => Enumerate(group.Groups);
        public static IEnumerable<PlcBlock> Blocks(PlcBlockGroup group) => Enumerate(group.Blocks);
        public static IEnumerable<PlcBlockUserGroup> Groups(PlcBlockGroup group) => Enumerate(group.Groups);
        public static string Name(PlcUserConstant value) => value.Name;
        public static string Value(PlcUserConstant value) => value.Value;
        public static string Name(PlcTag value) => value.Name;
        public static string DataType(PlcTag value) => value.DataTypeName;
        public static string Address(PlcTag value) => value.LogicalAddress;
        public static bool Accessible(PlcTag value) => value.ExternalAccessible;
        public static bool Writable(PlcTag value) => value.ExternalWritable;
        public static string Name(PlcType value) => value.Name;
        public static object Attribute(PlcType value, string name) => value.GetAttribute(name);
        public static void Export(PlcType value, FileInfo file, ExportOptions options) => value.Export(file, options);
        public static string Name(PlcBlock value) => value.Name;
        public static string Name(PlcBlockGroup value) => value.Name;
        public static ProgrammingLanguage Language(PlcBlock value) => value.ProgrammingLanguage;
        public static object Attribute(PlcBlock value, string name) => value.GetAttribute(name);
        public static void Export(PlcBlock value, FileInfo file, ExportOptions options) => value.Export(file, options);
        private static IEnumerable<PlcTagTable> Enumerate(PlcTagTableComposition values)
        {
            foreach (var value in values) yield return value;
        }
        private static IEnumerable<PlcTagTableUserGroup> Enumerate(PlcTagTableUserGroupComposition values)
        {
            foreach (var value in values) yield return value;
        }
        private static IEnumerable<PlcUserConstant> Enumerate(PlcUserConstantComposition values)
        {
            foreach (var value in values) yield return value;
        }
        private static IEnumerable<PlcTag> Enumerate(PlcTagComposition values)
        {
            foreach (var value in values) yield return value;
        }
        private static IEnumerable<PlcSystemTypeGroup> Enumerate(PlcSystemTypeGroupComposition values)
        {
            foreach (var value in values) yield return value;
        }
        private static IEnumerable<PlcType> Enumerate(PlcTypeComposition values)
        {
            foreach (var value in values) yield return value;
        }
        private static IEnumerable<PlcTypeUserGroup> Enumerate(PlcTypeUserGroupComposition values)
        {
            foreach (var value in values) yield return value;
        }
        private static IEnumerable<PlcBlock> Enumerate(PlcBlockComposition values)
        {
            foreach (var value in values) yield return value;
        }
        private static IEnumerable<PlcBlockUserGroup> Enumerate(PlcBlockUserGroupComposition values)
        {
            foreach (var value in values) yield return value;
        }
    }
#endif
}
