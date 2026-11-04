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
    private string DccObject(HmiObject meta)
    {
        OptionalObjectAddress.Parse(_request.ObjectPathJson);
        OptionalRules.ValidatePagination(_request.Offset, _request.Limit);
        var target = OptionalObjectAddress.Resolve(Dcc(), _request.ObjectPathJson);
        Page(target is IEnumerable && target is not string ? Items(target) : new[] { target }, meta, true);
        meta["dataComplete"] = false;
        return "DCC scalar properties and schema read from an exact property path.";
    }

    private object Chart(object container, string path)
    {
        var parts = DccPolicy.ChartParts(path);
        object current = Named(Get(container, "Charts"), parts[0]);
        foreach (var part in parts.Skip(1)) current = Named(Get(current, "Subcharts"), part);
        return current;
    }

    private string DccCharts(HmiObject meta)
    {
        if (_request.MaxDepth < 1 || _request.MaxDepth > 8) throw new ArgumentException("maxDepth 1..8 required.");
        var container = Dcc();
        var charts = string.IsNullOrEmpty(_request.ChartPath) ? Items(Get(container, "Charts")) : new[] { Chart(container, _request.ChartPath) };
        if (_request.IncludeLibraries) meta["dcbLibraries"] = new HmiArray(Items(Get(container, "DcbLibraries")).Select(v => (object?)Row(v)).ToArray());
        meta["records"] = new HmiArray(charts.Select(v => (object?)ChartRow(v, 1)).ToArray());
        meta["expectedCount"] = charts.Length;
        meta["actualCount"] = charts.Length;
        meta["dataComplete"] = false;
        return "Available DCC chart properties read; no modification.";
    }

    private static HmiObject FunctionRow(object functions)
    {
        var row = new HmiObject();
        OptionalEvidence.Safe(row, "driveObjectType", () => {
            var handler = Optional(Get(functions, "DriveObjectFunctions"), "DriveObjectTypeHandler");
            return handler == null ? null : new HmiObject { ["current"] = Optional(handler, "CurrentDriveObjectType") is object current ? Row(current) : null,
                ["possible"] = new HmiArray(Items(Get(handler, "PossibleDriveObjectTypes")).Select(v => (object?)Row(v)).ToArray()) };
        });
        OptionalEvidence.Safe(row, "activation", () => {
            var activation = Optional(Get(functions, "DriveObjectFunctions"), "DriveObjectActivation");
            return activation == null ? null : new HmiObject {
                ["activationState"] = Get(activation, "ActivationState").ToString(), ["isActive"] = Get(activation, "IsActive") };
        });
        foreach (var field in new[] { "FunctionInUse", "Commissioning", "SafetyCommissioning", "HardwareProjection" })
            OptionalEvidence.Safe(row, char.ToLowerInvariant(field[0])+field.Substring(1)+"Available", () => Optional(functions, field) != null);
        return row;
    }

    private HmiObject ChartRow(object chart, int depth)
    {
        var row = Row(chart);
        if (_request.IncludeBlocks) OptionalEvidence.Safe(row, "blocks", () => new HmiArray(Items(Get(chart, "Blocks")).Take(500).Select(block => {
            var value = Row(block);
            if (_request.IncludePins) value["pins"] = new HmiArray(Items(Get(block, "Pins")).Take(500).Select(pin => (object?)Row(pin)).ToArray());
            return (object?)value;
        }).ToArray()));
        OptionalEvidence
            .Safe(row, "chartInterfaces", () => new HmiArray(Items(Get(chart, "ChartInterfaces")).Take(200).Select(v => (object?)Row(v)).ToArray()));
        OptionalEvidence.Safe(row, "partitions", () => new HmiArray(Items(Get(chart, "Partitions")).Select(v => (object?)Row(v)).ToArray()));
        if (depth < _request.MaxDepth) OptionalEvidence
            .Safe(row, "subcharts", () => new HmiArray(Items(Get(chart, "Subcharts")).Take(200).Select(v => (object?)ChartRow(v, depth+1)).ToArray()));
        return row;
    }

    private string DcbLibraries(HmiObject meta)
    {
        bool writing = DccPolicy.ValidateLibraryRequest(_request.Action, _request.FilePath, _request.DryRun);
        if (_request.Action == "import")
        {
            var file = OptionalRules.RequireExistingInputFile(_request.FilePath, "filePath");
            meta["file"] = file.FullName; meta["action"] = _request.Action; meta["dryRun"] = _request.DryRun;
#if LIBRARY_DCB_IMPORT
            if (!writing) return "DCB extension library import preview; nothing imported into the project library.";
            using var access = Edit(true);
            meta["mayHaveChanged"] = true; meta["nativeSignature"] = "ProjectLibrary.ImportDcbLibrary(string)";
            _engine.PlcB5Session.CurrentProject!.ProjectLibrary.ImportDcbLibrary(file.FullName);
            return "DCB extension library imported into the project library; no save.";
#else
            throw new NotSupportedException("ProjectLibrary.ImportDcbLibrary requires V16 or later; V21 uses DcbLibraryImporter.");
#endif
        }
        Page(Items(Get(Dcc(), "DcbLibraries")), meta);
        return "DCB libraries of the selected drive object read; no import.";
    }

    private string DccEdit(HmiObject meta)
    {
        bool writing = _request.IsWrite;
        object plan;
        switch (_request.Operation)
        {
            case "ManageDccChart": plan = DccPolicy.ValidateChartRequest(_request.ChartName, _request.Action, _request.FilePath, _request.ImportOptions,
                _request.PropertiesJson, _request.ConfirmDelete, _request.DryRun, _request.SequenceIndex); break;
            case "ManageDccBlock": plan = DccPolicy.ValidateBlockRequest(_request.ChartPath, _request.BlockName, _request.Action, _request.BlockType,
                _request.LibraryName, _request.PropertiesJson, _request.ConfirmDelete, _request.DryRun, _request.SequenceIndex); break;
            case "ManageDccPin": plan = DccPolicy.ValidatePinRequest(_request.ChartPath, _request.BlockName, _request.PinName, _request.Action,
                _request.PropertiesJson, _request.PartnerJson, _request.SetAsSignal, _request.ParameterNumber, _request.ArrayIndex, _request.DryRun); break;
            case "ManageDccChartInterface": plan = DccPolicy.ValidateInterfaceRequest(_request.ChartPath, _request.InterfaceName, _request.Action,
                _request.SourceBlock, _request.SourcePin, _request.PropertiesJson, _request.ConfirmDelete, _request.DryRun); break;
            default: plan = DccPolicy.ValidatePartitionRequest(_request.ChartPath, _request.PartitionName, _request.Action,
                _request.PropertiesJson, _request.ConfirmDelete, _request.DryRun); break;
        }
        using var access = Edit(writing);
        var container = Dcc();
        if (_request.Operation == "ManageDccChart" && _request.Action == "readSequence")
        {
            var sequence = string.IsNullOrEmpty(_request.ChartName) ? Call(Get(container, "Charts"), "GetChartSequence")
                : Call(Chart(container, _request.ChartName), "GetRunSequence");
            meta["sequence"] = new HmiArray(Items(sequence!).Take(1000).Select(v => (object?)Row(v)).ToArray());
            return "DCC execution sequence read; nothing changed.";
        }
        if (_request.Operation == "ManageDccChart" && _request.Action is "optimizeSequence" or "showEditor")
        {
            var selected = Chart(container, _request.ChartName);
            if (_request.DryRun) return "DCC sequence/editor preview; nothing changed.";
            meta["mayHaveChanged"] = _request.Action == "optimizeSequence";
            Call(selected, _request.Action == "showEditor" ? "ShowDccEditor" : "OptimizeRunSequence");
            return "DCC sequence/editor action returned; no save.";
        }
        if (_request.Operation == "ManageDccChart" && _request.Action is "exportAll" or "import")
        {
            var file = _request.Action == "import" ? OptionalRules.RequireExistingInputFile(_request.FilePath, "filePath") : SafetyFileOutput
                .Plan(_request.FilePath);
            if (_request.DryRun) return "DCC chart exchange preview; nothing changed or written.";
            if (_request.Action == "import")
            {
                meta["mayHaveChanged"] = true;
                meta["result"] = Row(Call(Get(container, "Charts"), "Import", file.FullName,
                    EnumValue("Siemens.Engineering.MC.Drives.Dcc.DccImportOptions", ((DccPolicy.ChartRequest)plan).ImportOption))!);
            }
            else
            {
                meta["mayHaveWrittenFiles"] = true;
                Call(Get(container, "Charts"), "Export", file.FullName);
                meta["output"] = SafetyFileOutput.Verify(file);
            }
            return "DCC chart exchange returned; no save.";
        }
        string path = _request.Operation == "ManageDccChart" ? _request.ChartName : _request.ChartPath;
        object? chart = null;
        object collection;
        string name;
        if (_request.Operation == "ManageDccChart")
        {
            collection = Get(container, "Charts");
            name = path;
        }
        else
        {
            chart = Chart(container, path);
            collection =
                Get(chart, _request.Operation == "ManageDccBlock" ? "Blocks" : _request.Operation == "ManageDccChartInterface" ? "ChartInterfaces" : _request
                    .Operation == "ManageDccPin" ? "Blocks" : "Partitions");
            name = _request.Operation == "ManageDccBlock" ? _request.BlockName : _request.Operation == "ManageDccChartInterface" ? _request.InterfaceName
                : _request.PartitionName;
        }
        object? target;
        if (_request.Operation == "ManageDccPin")
        {
            var block = Named(Get(chart!, "Blocks"), _request.BlockName);
            target = Named(Get(block, "Pins"), _request.PinName);
        }
        else target = string.IsNullOrEmpty(name) ? null : Find(collection, name);
        if (_request.Action == "read")
        {
            if (target == null) Page(Items(collection), meta); else meta["before"] = Row(target);
            return "DCC object read; nothing changed.";
        }
        if (target != null) meta["before"] = Row(target);
        var properties = Object(_request.PropertiesJson, "propertiesJson");
        var scalarProperties = new HmiObject();
        foreach (var pair in properties) if (pair.Key != "Partition") scalarProperties[pair.Key] = pair.Value;
        var partition = properties.ContainsKey("Partition")
            ? Named(Get(chart ?? Chart(container, string.Join("/", DccPolicy.ChartParts(path).Take(DccPolicy.ChartParts(path).Length - 1))), "Partitions"),
                properties["Partition"]!.OptionalValue<string>())
            : null;
        var changes = target == null ? null : OptionalScalars
            .Prepare((_request.Action == "updateParameter" ? Get(target, "Parameter") : target).GetType(), scalarProperties);
        if (_request.Action == "export")
        {
            var file = SafetyFileOutput.Plan(_request.FilePath);
            if (_request.DryRun) return "DCC export preview; nothing written.";
            meta["mayHaveWrittenFiles"] = true;
            Call(target!, "Export", file.FullName);
            meta["output"] = SafetyFileOutput.Verify(file);
            return "DCC object exported and hashed; content semantics not verified.";
        }
        if (_request.DryRun) return "DCC object edit preview; nothing changed.";
        meta["mayHaveChanged"] = true;
        if (_request.Action == "delete")
        {
            Call(target!, "Delete");
            meta["verifiedAbsent"] = Find(collection, name) == null;
            return "DCC object deletion returned; no save.";
        }
        if (_request.Action == "create")
        {
            if (_request.Operation == "ManageDccBlock")
            {
                target = string.IsNullOrEmpty(name) ? Call(collection, "Create", _request.BlockType)
                    : string.IsNullOrEmpty(_request.LibraryName) ? Call(collection, "Create", name, _request.BlockType)
                    : Call(collection, "Create", name, _request.BlockType, _request.LibraryName);
            }
            else if (_request.Operation == "ManageDccChartInterface")
            {
                var pin = Named(Get(Named(Get(chart!, "Blocks"), _request.SourceBlock), "Pins"), _request.SourcePin);
                target = Call(collection, "Create", pin);
            }
            else target = _request.Operation == "ManageDccChart" && ((DccPolicy.ChartRequest)plan).AutoName ? Call(collection, "Create")
                : Call(collection, "Create", name);
        }
        else if (_request.Action == "setAsPredecessor") Call(target!, "SetAsPredecessor");
        else if (_request.Operation == "ManageDccPin" && _request.Action is "publish" or "unpublish")
        {
            var published = _request.Action == "unpublish" ? Call(target!, "Unpublish")
                : _request.ParameterNumber >= 0 ? Call(target!, "Publish", _request.SetAsSignal, (uint)_request.ParameterNumber)
                : Call(target!, "Publish", _request.SetAsSignal);
            meta["result"] = published == null ? null : Row(published);
        }
        else if (_request.Operation == "ManageDccPin" && _request.Action is "connect" or "disconnect")
        {
            var pinPlan = (DccPolicy.PinRequest)plan;
            var partnerChart = chart!;
            var partner = string.IsNullOrEmpty(pinPlan.PartnerInterface)
                ? Named(Get(Named(Get(partnerChart, "Blocks"), pinPlan.PartnerBlock), "Pins"), pinPlan.PartnerPin)
                : Named(Get(partnerChart, "ChartInterfaces"), pinPlan.PartnerInterface);
            if (_request.Action == "connect") meta["connection"] = Row(Call(target!, "Connect", partner)!);
            else
            {
                var connection = Items(Get(target!, "Connections"))
                    .FirstOrDefault(v => MatchesEndpoint(Get(v, "Sink"), pinPlan) || MatchesEndpoint(Get(v, "Source"), pinPlan))
                    ?? throw new PlcSoftwareException("NotFound", "No connection of pin " + _request.PinName + " to the given partner.");
                Call(connection, "Delete");
            }
        }
        else
        {
            if (_request.Action == "updateParameter") target = Get(target!, "Parameter");
            OptionalScalars.Apply(target!, changes ?? OptionalScalars.Prepare(target!.GetType(), scalarProperties), meta);
        }
        if (_request.Action == "create" && scalarProperties.Count != 0) OptionalScalars
            .Apply(target!, OptionalScalars.Prepare(target!.GetType(), scalarProperties), meta);
        if (partition != null) target!.GetType().GetProperty("Partition")!.SetValue(target, partition);
        if (_request.SequenceIndex >= 0)
        {
            Call(target!, "MoveInRuntimeSequence", (uint)_request.SequenceIndex);
        }
        meta["after"] = Row(target!);
        return "DCC native object operation returned; no save or download.";
    }

    private static bool MatchesEndpoint(object endpoint, DccPolicy.PinRequest request)
    {
        if (!string.IsNullOrEmpty(request.PartnerInterface)) return endpoint.GetType().Name == "DccChartInterface" && Name(endpoint) == request
            .PartnerInterface;
        return endpoint.GetType().Name == "DccPin" && Name(endpoint) == request.PartnerPin && Name(Get(endpoint, "Parent")) == request.PartnerBlock;
    }
}
