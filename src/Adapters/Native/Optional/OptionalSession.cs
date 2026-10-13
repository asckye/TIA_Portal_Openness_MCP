#if OPTIONAL_V20_PLUS
using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Library.MasterCopies;
using Siemens.Engineering.Library.Types;
using Siemens.Engineering.SW;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Native.Hmi;

namespace TiaMcp.Adapters.Native.Optional;

internal sealed partial class OptionalSession
{
    private readonly PlcFoundationEngine _engine;
    internal OptionalSession(PlcFoundationEngine engine) { _engine = engine; }
    internal Project? CurrentProject => _engine.PlcB5Session.CurrentProject;
    internal TiaPortal? CurrentPortal => _engine.PlcB5Session.CurrentPortal;
    internal IDisposable AcquireHmiEditAccess() => _engine.PlcB5Session.AcquireHmiEditAccess();
    internal PlcSoftware ExactPlcForEngineering(string path, bool writing) => _engine.PlcB5Session.ExactPlcForEngineering(path, writing);
    internal SoftwareContainer? ResolveSoftwareContainerUncached(string path) => _engine.PlcB5Session.ResolveSoftwareContainerUncached(path);
    internal object ExactObjectUnder(object root, string path, string collection, string label) => _engine.PlcB5Session.ExactObjectUnder(root, path, collection, label);
    internal MasterCopy ExactMasterCopy(string library, string path) => _engine.PlcB5Session.ExactMasterCopy(library, path);
    internal object ExactOpenEngineeringLibrary(string library) => _engine.ExactOpenEngineeringLibrary(library);
    internal object EngineeringLibraryFolder(object library, string path, string root) => _engine.HmiLibraryFolder(library, path, root);
    internal LibraryType ExactLibraryType(object library, string path) => _engine.OptionalLibraryType(library, path);
    internal LibraryTypeVersion ExactTypeVersion(LibraryType type, string version) => _engine.OptionalTypeVersion(type, version);
    internal string LibraryPathOf(object value, string root) => _engine.OptionalLibraryPath(value, root);
    internal object? MultilingualJson(MultilingualText text) => _engine.OptionalMultilingual(text);
    internal object ResolveHmiScreenOrThrow(string software, string screen) => _engine.OptionalHmiScreen(software, screen);
    internal DeviceItem ExactDeviceItem(string[] devices, string[] items) => _engine.PlcB5Session.ExactEngineeringHardware(devices, items) as DeviceItem
        ?? throw new ArgumentException("itemPathJson must select a device item.");
    internal HardwareObject ExactEngineeringHardware(string devices, string items) => _engine.PlcB5Session.ExactEngineeringHardware(SivarcPolicy.ParseNames(devices, "devicePathJson"), SivarcPolicy.ParseNames(items, "itemPathJson"));
    internal Device ExactEngineeringDevice(string[] devices) => _engine.PlcB5Session.ExactEngineeringHardware(devices, Array.Empty<string>()) as Device
        ?? throw new ArgumentException("devicePathJson must select a device.");
    internal static string[] ExactNameList(string raw, string parameter = "namesJson")
    {
        if (raw.Length > 65536) throw new ArgumentException("Name list too large.");
        var names = (OptionalInputs.Parse(raw) as HmiArray ?? throw new ArgumentException("Expected a JSON array of exact paths.")).Select(n => n!.OptionalValue<string>()).ToArray();
        if (names.Length < 1 || names.Length > 500 || names.Any(string.IsNullOrWhiteSpace) || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
            throw new ArgumentException("Provide 1..500 unique nonempty paths.");
        return names;
    }
    internal object ExactSiVArcRoot(string category) => Family(category).Anchor(RequireSivarc());
    internal SivarcFamily GetSivarcFamily(string category) => Family(category);
    internal object ExactTechnology(string software, string path, bool writing) => _engine.OptionalTechnology(software, path, writing);
    internal HmiObject InterfaceRow(object value) => new HmiObject(TiaMcp.Adapters.Native.Plc.PlcOrganisationAdapter.ReadTechnologyInterfaceEvidence(value));
    internal HmiObject AddressRow(Address value) => new HmiObject(HardwareAddressPrimitives.AddressRow(value));
    internal HardwareAddressingReply RunHmiStepTool(string tool, Func<HmiObject, string> action, bool requiresProject = true)
        => _engine.RunHmiStep(tool, values => {
            var meta = new HmiObject(values);
            try { return action(meta); }
            catch (Exception error) { OptionalEvidence.RecordFailure(meta, error); throw; }
            finally { foreach (var pair in meta) values[pair.Key] = pair.Value; }
        }, requiresProject);
}
#endif
