using System;
using System.IO;
using System.Linq;
using TiaMcp.Adapters.Native.Hmi;

namespace TiaMcp.Adapters.Native.Safety;

internal static class SafetyValidationPrimitives
{
    internal static void Page(object[] all, int offset, int limit, HmiObject meta)
    {
        var rows = all.Skip(offset).Take(limit).ToArray();
        meta["records"] = new HmiArray(rows);
        foreach (var pair in TiaMcp.Adapters.Native.Optional.OptionalRules.PageMeta(all.Length, offset, limit, rows.Length)) meta[pair.Key] = pair.Value;
    }
    internal static void Safe(HmiObject row, string key, Func<object?> read)
    {
        try { row[key] = read(); }
        catch (Exception error) { row[key] = null; row[key + "Error"] = error.GetBaseException().Message; }
    }
    internal static void ValidatePagination(int offset, int limit)
    {
        if (offset < 0 || limit < 1 || limit > 500) throw new ArgumentException("offset>=0, limit 1..500 required.");
    }
    internal static FileInfo InputFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) throw new ArgumentException("filePath must be an absolute file path.");
        var file = new FileInfo(path);
        if (!file.Exists || file.Length == 0) throw new FileNotFoundException("filePath does not exist or is empty.", path);
        return file;
    }
    internal static (bool? Flag, string? EnumName) ParseConditionValue(object? value, string key)
    {
        if (value is bool flag) return (flag, null);
        if (value is string text && new[] { "FALSE", "TRUE", "NotRelevant" }.Contains(text, StringComparer.Ordinal)) return (null, text);
        throw new ArgumentException("propertiesJson." + key + " must be true/false or one of FALSE/TRUE/NotRelevant.");
    }
    internal static void ValidateConditionValues(string usage, HmiObject properties)
    {
        string[] keys = usage == "OperatingMode" ? new[] { "executedInput" } : usage == "InputCondition" ? new[] { "initialInput", "executedInput" }
            : usage == "Response" ? new[] { "response" } : Array.Empty<string>();
        foreach (var key in keys)
            if (properties.ContainsKey(key) && ParseConditionValue(properties[key], key).EnumName == "NotRelevant")
                throw new ArgumentException("propertiesJson." + key + " cannot be NotRelevant while the signal usage is " + usage + " (TIA refuses it natively; use true/false).");
    }
}
