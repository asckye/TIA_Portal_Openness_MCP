using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW.Blocks;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Native.Hmi;
using TiaMcp.Adapters.Native.Plc;
using TiaMcp.Adapters.Native.Safety;
using Group = TiaMcp.Adapters.Native.Plc.PlcGroupOperations;

namespace TiaMcp.Adapters.Native.Optional;

internal sealed partial class LegacyOptionalAdapter
{
    private readonly PlcFoundationEngine _engine;
    private readonly OptionalEngineeringRequest _request;
    private readonly string _release;

    internal LegacyOptionalAdapter(PlcFoundationEngine engine, OptionalEngineeringRequest request)
    {
        _engine = engine;
        _request = request;
        _release = engine.ReleaseKey;
    }

    private static object Get(object target, string name) => Group.Get(target, name);
    private static object? Optional(object target, string name) => target.GetType().GetProperty(name)?.GetValue(target);
    private static object[] Items(object target) => Group.Items(target).ToArray();
    private static object? Find(object target, string name) => Group.Find(target, name);
    private static object Named(object target, string name) => Find(target, name)
        ?? throw new PlcSoftwareException("NotFound", "Exact object not found: " + name);
    private static string Name(object target) => Convert.ToString(Get(target, "Name"), CultureInfo.InvariantCulture)!;
    private static HmiObject Object(string raw, string parameter) => OptionalRules.ParseObject(raw, parameter);
    private static HmiObject Row(object value) => HmiScalar.Read(value);

    private static object? Call(object target, string method, params object?[] arguments)
        => OptionalNativeOperations.Call(target, method, arguments);

    private static Type ApiType(string name) => typeof(IEngineeringObject).Assembly.GetType(name)
        ?? throw new NotSupportedException("Official API type unavailable on this release: " + name);
    private static object EnumValue(string name, string value) => Enum.Parse(ApiType(name), value, false);
    private static object Service(object owner, string name) => HardwareOfficialService.Require(owner, "Siemens.Engineering." + name, "Siemens.Engineering");
    private IDisposable? Edit(bool writing) => writing ? _engine.PlcB5Session.AcquireHmiEditAccess() : null;
    private DeviceItem DriveItem() => _engine.PlcB5Session.ExactEngineeringHardware(
        SivarcPolicy.ParseNames(_request.DevicePathJson, "devicePathJson"), SivarcPolicy.ParseNames(_request.ItemPathJson, "itemPathJson")) as DeviceItem
        ?? throw new ArgumentException("itemPathJson must select a drive device item.");
    private object Drive()
    {
        var selector = StartdrivePolicy.ParseDriveSelector(_request.DriveObjectNumber, _request.DriveObjectIndex);
        var objects = Drives(DriveItem());
        if (selector.Index >= objects.Length) throw new PlcSoftwareException("NotFound", "driveObjectIndex is out of range.");
        return objects[selector.Index];
    }
    private object[] Drives(DeviceItem item) => _release == "14sp1"
        ? new[] { Service(item, "Startdrive.DriveObject") }
        : Items(Get(Service(item, "MC.Drives.DriveObjectContainer"), "DriveObjects"));
    private object Dcc() => Service(Drive(), "MC.Drives.Dcc.DriveControlChartContainer");
    private object Sivarc() => Service(_engine.PlcB5Session.CurrentProject!, "SiVArc.Sivarc");
    private object Rules() => Get(Sivarc(), SivarcPolicy.AnchorProperty(_request.Category));

    internal HardwareAddressingReply Execute()
    {
        if (OptionalEngineeringCapabilities.Unsupported(_release, _request) is string unavailable)
            throw new NotSupportedException(unavailable);
        var previous = OptionalInputs.Current;
        OptionalInputs.Current = _request;
        try
        {
            return _engine.RunHmiStep(_request.Operation, values => {
                var meta = new HmiObject(values);
                meta["mayHaveChanged"] = false;
                meta["mayHaveWrittenFiles"] = false;
                try
                {
                    return Dispatch(meta);
                }
                catch (Exception error)
                {
                    OptionalEvidence.RecordFailure(meta, error);
                    throw;
                }
                finally
                {
                    foreach (var pair in meta) values[pair.Key] = pair.Value;
                }
            }, requiresProject: !(_request.Operation == "ManageTechnologyExtensions" && StartdrivePolicy.IsPortalScopedExtensionAction(_request.Action)));
        }
        finally { OptionalInputs.Current = previous; }
    }

    private void Page(object[] rows, HmiObject meta, bool schema = false)
    {
        int limit = _request.Limit == 0 ? 500 : _request.Limit;
        OptionalRules.ValidatePagination(_request.Offset, limit);
        var records = rows.Skip(_request.Offset).Take(limit).Select(value => (object?)(schema ? OptionalObjectAddress.Read(value) : Row(value))).ToArray();
        meta["records"] = new HmiArray(records);
        foreach (var pair in OptionalRules.PageMeta(rows.Length, _request.Offset, limit, records.Length)) meta[pair.Key] = pair.Value;
    }

    private string Dispatch(HmiObject meta) => _request.Operation switch {
        "GenerateSivarc" => Generate(meta),
        "GetSivarcRuleTree" => SivarcTree(meta),
        "ListSivarcRules" or "ManageSivarcRule" => GenericSivarc(meta),
        "ManageSivarcRuleContainer" => SivarcContainer(meta),
        "ManageSivarcTableRule" => SivarcRule(meta),
        "ListSivarcBlockDefinitions" or "ManageSivarcBlockDefinition" => Definitions(meta),
        "ListDriveObjects" => DriveObjects(meta),
        "GetDriveParameters" or "GetOnlineDriveParameters" or "ManageStartdriveParameter" => Parameters(meta),
        "ManageDriveTelegrams" => Telegrams(meta),
        "ManageDriveFunctions" or "ManageOnlineDriveFunctions" => Functions(meta),
        "ManageDriveSecurity" => Security(meta),
        "ManageDriveHardwareModule" => HardwareModule(meta),
        "ManageTechnologyExtensions" => Extensions(meta),
        "GetDccObject" => DccObject(meta),
        "ListDccCharts" => DccCharts(meta),
        "ManageDcbLibraries" => DcbLibraries(meta),
        "ManageDccChart" or "ManageDccBlock" or "ManageDccPin" or "ManageDccChartInterface" or "ManageDccChartPartition" => DccEdit(meta),
        _ => throw new NotSupportedException("The optional-product operation is unavailable on this release.")
    };

}
