using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters.Native.Optional;

internal static class OptionalNativeOperations
{
    internal static object? Call(object target, string method, params object?[] arguments)
    {
        var candidates = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == method && !m.ContainsGenericParameters && m.GetParameters().Length == arguments.Length)
            .Where(m => m.GetParameters().Select((p, i) => arguments[i] == null ? !p.ParameterType.IsValueType
                : p.ParameterType.IsInstanceOfType(arguments[i])).All(match => match)).ToArray();
        if (candidates.Length != 1) throw new NotSupportedException("Exact official overload unavailable: " + target.GetType().FullName + "." + method);
        try { return candidates[0].Invoke(target, arguments); }
        catch (TargetInvocationException error)
        {
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
        foreach (var row in prepared) row.property.SetValue(row.entry, row.value);
    }
}
