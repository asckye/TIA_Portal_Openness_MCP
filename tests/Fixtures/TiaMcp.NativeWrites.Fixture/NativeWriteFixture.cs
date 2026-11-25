using Siemens.Engineering.HW.Features;
using TiaMcp.Adapters;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Engineering;
using TiaMcp.Adapters.Native.Hmi;
using TiaMcp.Adapters.Native.Plc;

namespace TiaMcp.Tests.NativeWrites;

public static class NativeWriteFixture
{
    public static object Run(string scenario, string input)
    {
        if (scenario.StartsWith("mapping-", StringComparison.Ordinal) || scenario == "target-refused") return Mapping(scenario);
        if (scenario.StartsWith("dyn-", StringComparison.Ordinal)) return Dynamization(scenario);
        var interfaces = new Interfaces(scenario);
        var session = new ImportSession { ServerInterfaces = scenario == "opc-no-create" ? Array.Empty<object>() : interfaces };
        var adapter = new PlcB5Adapter(session);
        var reply = scenario switch {
            "alarm-class" or "alarm-class-missing" => adapter.ImportAlarmClasses("PLC", input),
            "alarm-text" or "alarm-text-missing" => adapter.ImportTextLists("PLC", input),
            _ => adapter.ImportOpcUaInterface("PLC", input)
        };
        return new { reply, remaining = interfaces.Count, calls = session.Classes.Calls + session.Texts.Calls };
    }

    private static object Mapping(string scenario)
    {
        var area = new TransferArea();
        bool update = scenario is "mapping-update" or "target-refused";
        if (update) area.TransferAreaMappingRules.Create().RefuseTarget = scenario == "target-refused";
        area.TransferAreaMappingRules.RefuseCreate = scenario == "mapping-refused";
        var engine = new PlcFoundationEngine();
        var meta = new Dictionary<string, object?>();
        string message = EngineeringWriteJournal.RunReported(meta, () => engine.ManageMappingRule(area,
            update ? "updateMappingRule" : "createMappingRule", 0,
            new() { ["Begin"] = new HardwareScalar { Kind = "number", Text = "2" } }, new[] { "Device" }, new[] { "module" }, false, meta),
            error => error.Message);
        var rule = area.TransferAreaMappingRules.SingleOrDefault();
        return new { meta, message, remaining = area.TransferAreaMappingRules.Count, begin = rule?.Begin, target = rule?.Target?.Name };
    }

    private static object Dynamization(string scenario)
    {
        bool update = scenario == "dyn-entry";
        var session = new DynSession();
        session.Dynamizations.RefuseCreate = scenario == "dyn-refused";
        if (update) session.Dynamizations.Create<Siemens.Engineering.HmiUnified.UI.Dynamization.TagDynamization>("Position");
        string properties = update ? """{"Offset":2}""" : """{"Offset":7}""";
        string entries = update ? """[{"kind":"Simple","Offset":3}]""" : "[]";
        UnifiedRequestValues.Inputs = new() { ["[]"] = new HmiArray(), [properties] = new HmiObject { ["Offset"] = update ? 2 : 7 } };
        var entryInput = new UnifiedHmiInput { Kind = "array" };
        if (update) entryInput.Items.Add(new UnifiedHmiInput { Kind = "object", Properties = {
            ["kind"] = new UnifiedHmiInput { Kind = "string", Text = "Simple" }, ["Offset"] = new UnifiedHmiInput { Kind = "number", Text = "3" }
        } });
        UnifiedHmiAccess.Inputs = new() { [entries] = entryInput };
        var reply = new UnifiedUiModelAdapter(session).ManageUnifiedDynamization("HMI", "[]", "Position", update ? "update" : "create",
            "Tag", properties, entries, dryRun: false);
        var dyn = session.Dynamizations.SingleOrDefault();
        return new { meta = reply.Meta, message = reply.Message, remaining = session.Dynamizations.Count, offset = dyn?.Offset,
            entries = dyn?.ValueConverter.MappingTable.Entries.Count };
    }
}
