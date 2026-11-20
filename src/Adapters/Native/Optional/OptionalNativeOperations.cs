using TiaMcp.Adapters.Native.Engineering;
using TiaMcp.Adapters.Native.Plc;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters.Native.Optional;

internal static class OptionalNativeOperations
{
    internal static void RequireGenerationConnections(string hmi, string[] plcPartners, IEnumerable<string> connections,
        bool hasHmiSoftware, string hmiParameter)
    {
        if (!hasHmiSoftware)
            throw AdapterPreconditionException.Failed($"Device '{hmi}' has no Classic or Unified HMI software for SiVArc generation.",
                "hmi-software-missing", hmiParameter);
        var available = new HashSet<string>(connections.Where(name => !string.IsNullOrWhiteSpace(name)), StringComparer.OrdinalIgnoreCase);
        if (!available.Overlaps(plcPartners))
            throw AdapterPreconditionException.Failed(
                $"HMI device '{hmi}' needs a connection to at least one selected PLC CPU ({string.Join(", ", plcPartners)}). "
                + "Configure it in TIA Portal before generation.", "missing-plc-connection", "plcSoftwarePaths");
    }

    internal static object CreateLegacySivarcContainer(IDictionary<string, object?> meta, Func<object> resolveCollection, object collection,
        string name, object? version)
    {
        string method = version == null ? "Create" : "CreateFrom";
        object argument = version ?? name;
        return CreateSivarcContainer(meta, resolveCollection, () =>
            PlcGroupOperations.Call(collection, method, new[] { argument.GetType() }, argument));
    }

    internal static object CreateSivarcContainer(IDictionary<string, object?> meta, Func<object> resolveCollection, Func<object> create)
    {
        object?[]? Fingerprint()
        {
            var collection = resolveCollection();
            int count = (int)PlcGroupOperations.Get(collection, "Count");
            var names = PlcGroupOperations.Items(collection).Select(item => (string)PlcGroupOperations.Get(item, "Name"))
                .OrderBy(name => name, StringComparer.Ordinal).ToArray();
            if (names.Length != count) return null;
            return new object?[] { count }.Concat(names).ToArray();
        }
        object? created = null;
        NativeRefusalProbe.Run(meta, Fingerprint, () => created = create());
        return created!;
    }

    internal static (string Name, string Type)[]? ScreenItemRows(object screen)
    {
        try
        {
            var items = PlcGroupOperations.Get(screen, "ScreenItems");
            return PlcGroupOperations.Items(items).Select(item =>
                ((string)PlcGroupOperations.Get(item, "Name"), item.GetType().Name)).ToArray();
        }
        catch (Exception) /* swallow(probe-optional): an unreadable inventory requires explicit removal consent */
        { return null; }
    }

    internal static (string Name, string Type)[]? ScreenItemDifference(
        (string Name, string Type)[]? from, (string Name, string Type)[]? other)
    {
        if (from == null || other == null) return null;
        var counts = other.GroupBy(item => item).ToDictionary(group => group.Key, group => group.Count());
        var difference = new List<(string Name, string Type)>();
        foreach (var item in from)
        {
            if (counts.TryGetValue(item, out int count) && count > 0) counts[item] = count - 1;
            else difference.Add(item);
        }
        return difference.ToArray();
    }

    internal static object? Call(object target, string method, params object?[] arguments)
    {
        var candidates = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == method && !m.ContainsGenericParameters && m.GetParameters().Length == arguments.Length)
            .Where(m => m.GetParameters().Select((p, i) => arguments[i] == null ? !p.ParameterType.IsValueType
                : p.ParameterType.IsInstanceOfType(arguments[i])).All(match => match)).ToArray();
        if (candidates.Length != 1) throw new NotSupportedException("Exact official overload unavailable: " + target.GetType().FullName + "." + method);
        EngineeringWriteJournal.BeforeCall(target, candidates[0], arguments!);
        try { var result = candidates[0].Invoke(target, arguments); EngineeringWriteJournal.CreatedResult(candidates[0], result, target); return result; }
        catch (TargetInvocationException error)
        {
            if (method is "Create" or "CreateFrom") EngineeringWriteJournal.Unrestored(target.GetType().Name + "/" + method);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw();
            throw;
        }
    }

    internal static object ParameterNumber(string release, int number)
    {
        if (release != "14sp1") return number;
        if (number < 0 || number > ushort.MaxValue) throw new ArgumentException("V14 SP1 parameter numbers must be 0..65535.");
        return (ushort)number;
    }

    internal static void RequireCanChange(object? allowed)
    {
        if (allowed is not true) throw new PlcSoftwareException("InvalidState", "The native Can* check refused this telegram operation; nothing changed.");
    }

    internal static void ChangeTelegramNumber(object telegram, int number, bool execute, Action beforeWrite)
    {
        RequireCanChange(Call(telegram, "CanChangeTelegram", number));
        if (!execute) return;
        var property = telegram.GetType().GetProperty("TelegramNumber")
            ?? throw new NotSupportedException("Official TelegramNumber property unavailable.");
        if (property.SetMethod?.IsPublic != true) throw new NotSupportedException("Official TelegramNumber setter unavailable.");
        beforeWrite();
        EngineeringWriteJournal.Property(telegram, property);
        property.SetValue(telegram, number);
    }

    internal static string OwningDeviceName(object? current, Func<object, object?> parent, Func<object, string?> deviceName)
    {
        while (current != null)
        {
            string? name = deviceName(current);
            if (!string.IsNullOrEmpty(name)) return name!;
            current = parent(current);
        }
        throw new PlcSoftwareException("NotFound", "Owning device of PLC software not found; Sivarc.Generate requires PLC device names.");
    }

    internal static void ApplyConfigurationEntries(object[] entries, IDictionary<string, object?> requested,
        Func<object?, Type, object?> convert, Action beforeWrite)
    {
        // Prepare the entire batch before mutating any ConfigurationEntry.
        var prepared = requested.Select(pair => {
            var entry = entries.SingleOrDefault(value =>
                Convert.ToString(value.GetType().GetProperty("Name")?.GetValue(value), CultureInfo.InvariantCulture) == pair.Key
                || Convert.ToString(value.GetType().GetProperty("Number")?.GetValue(value), CultureInfo.InvariantCulture) == pair.Key)
                ?? throw new PlcSoftwareException("NotFound", "Configuration entry not found: " + pair.Key);
            var property = entry.GetType().GetProperty("Value") ?? throw new NotSupportedException("ConfigurationEntry.Value unavailable.");
            var value = convert(pair.Value, property.GetValue(entry)?.GetType() ?? typeof(object));
            return (entry, property, value);
        }).ToArray();
        beforeWrite();
        foreach (var row in prepared) EngineeringWriteJournal.CaptureProperties(row.entry, new[] { row.property });
        foreach (var row in prepared) { EngineeringWriteJournal.Property(row.entry, row.property); row.property.SetValue(row.entry, row.value); }
    }
}
