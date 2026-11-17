using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering.HW;

namespace Siemens.Engineering.Library.Types
{
    public sealed class LibraryType { public string Name { get; set; } = "Motor"; }
    public class LibraryTypeVersion
    {
        public LibraryTypeVersionState State { get; set; } = LibraryTypeVersionState.Committed;
        public Version VersionNumber { get; set; } = new(1, 0, 0);
        public LibraryType TypeObject { get; set; } = new();
    }
    public enum UpdatePathsMode { KeepExistingPathsInTarget, ThrowIfPathsConflict, UpdatePathsInTarget }
}
namespace Siemens.Engineering.SW.Blocks
{
    public sealed class CodeBlockLibraryTypeVersion : Library.Types.LibraryTypeVersion { }
    public sealed class PlcBlockComposition : NativeNamedList<OB>
    {
        public int CreateFromCalls { get; private set; }
        public OB CreateFrom(CodeBlockLibraryTypeVersion version)
        { CreateFromCalls++; var block = new OB { Name = version.TypeObject.Name }; Add(block); return block; }
        public OB CreateFrom(CodeBlockLibraryTypeVersion version, Library.Types.UpdatePathsMode mode) => CreateFrom(version);
    }
}
namespace Siemens.Engineering.SW.Types
{
    public sealed class PlcTypeGroup
    {
        public string Name { get; set; } = "";
        public NativeNamedList<HardwareObject> Types { get; } = new();
        public NativeNamedList<PlcTypeGroup> Groups { get; } = new();
        public NativeNamedList<PlcTypeGroup> SystemTypeGroups { get; } = new();
    }
}
namespace Siemens.Engineering.SW.Tags
{
    public sealed class PlcTagTableGroup
    {
        public string Name { get; set; } = "";
        public NativeNamedList<PlcTagTable> TagTables { get; } = new();
        public NativeNamedList<PlcTagTableGroup> Groups { get; } = new();
    }
    public sealed class PlcTagTable : HardwareObject
    {
        public NativeNamedList<HardwareObject> Tags { get; } = new();
        public NativeNamedList<HardwareObject> UserConstants { get; } = new();
    }
}
namespace Siemens.Engineering.SW.Units
{
    public class PlcUnitBase
    {
        public string Name { get; set; } = "";
        public string NamespacePreset { get; set; } = "";
        public Blocks.PlcBlockGroup BlockGroup { get; } = new();
        public Types.PlcTypeGroup TypeGroup { get; } = new();
    }
    public sealed class PlcUnit : PlcUnitBase { }
    public sealed class PlcSafetyUnit : PlcUnitBase { }
    public sealed class PlcUnitSystemGroup
    {
        public NativeNamedList<PlcUnit> Units { get; } = new();
        public NativeNamedList<PlcSafetyUnit> SafetyUnits { get; } = new();
    }
    public sealed class PlcUnitProvider { public PlcUnitSystemGroup UnitGroup { get; } = new(); }
}
namespace Siemens.Engineering.Hmi.Screen
{
    public sealed class ScreenLibraryTypeVersion : Library.Types.LibraryTypeVersion { }
    public sealed class Screen : HardwareObject { }
    public sealed class ScreenComposition : NativeNamedList<Screen>
    {
        public Screen CreateFrom(ScreenLibraryTypeVersion source) { var screen = new Screen { Name = source.TypeObject.Name }; Add(screen); return screen; }
    }
    public sealed class ScreenFolder
    {
        public string Name { get; set; } = "";
        public ScreenComposition Screens { get; } = new();
        public NativeNamedList<ScreenFolder> Folders { get; } = new();
    }
}
namespace Siemens.Engineering.Hmi
{
    public sealed partial class HmiTarget { public Screen.ScreenFolder ScreenFolder { get; } = new(); }
}
namespace TiaMcp.Adapters.Native.Plc
{
    public static class PlcUnitScope
    {
        public static IEnumerable<KeyValuePair<string, object>> Units(Siemens.Engineering.SW.PlcSoftware plc)
            => plc.UnitProvider.UnitGroup.Units.Select(unit => new KeyValuePair<string, object>(unit.Name, unit));
        public static Siemens.Engineering.SW.Units.PlcUnitBase? FindSafety(Siemens.Engineering.SW.Units.PlcUnitSystemGroup group, string name)
            => group.SafetyUnits.Find(name);
        public static Siemens.Engineering.SW.Blocks.PlcBlockGroup Blocks(Siemens.Engineering.SW.PlcSoftware plc, object? unit)
            => unit == null ? plc.BlockGroup : ((Siemens.Engineering.SW.Units.PlcUnitBase)unit).BlockGroup;
        public static Siemens.Engineering.SW.Types.PlcTypeGroup Types(Siemens.Engineering.SW.PlcSoftware plc, object? unit)
            => unit == null ? plc.TypeGroup : ((Siemens.Engineering.SW.Units.PlcUnitBase)unit).TypeGroup;
    }
}
namespace TiaMcp.Adapters.Native.Hmi
{
    internal static class ClassicHmiFoldersAdapter
    {
        internal static Siemens.Engineering.Hmi.Screen.ScreenFolder ExactScreenFolder(Siemens.Engineering.Hmi.HmiTarget hmi, IEnumerable<string> parts)
        { var folder = hmi.ScreenFolder; foreach (string part in parts) folder = folder.Folders.Find(part)!; return folder; }
        internal static TiaMcp.Adapters.Contracts.Tests.OfficialApiAuditTests.Folder ExactTagFolder(Siemens.Engineering.Hmi.HmiTarget hmi,
            IEnumerable<string> parts)
        { var folder = hmi.TagFolder; foreach (string part in parts) folder = folder.Folders.Single(item => item.Name == part); return folder; }
    }
}
namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        private Siemens.Engineering.Project Project() => project!;
    }
}
