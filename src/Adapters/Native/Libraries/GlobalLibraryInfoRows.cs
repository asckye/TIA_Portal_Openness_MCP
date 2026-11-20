using System;
using System.Collections.Generic;
using Siemens.Engineering.Library;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters.Libraries;

internal static class GlobalLibraryInfoRows
{
    internal static void Read(IList<GlobalLibraryInfo> infos, IDictionary<string, object?> meta)
    {
        var rows = new List<object?>();
        var unavailable = new List<string>();
        int count = infos.Count;
        for (int index = 0; index < count; index++)
        {
            var row = new Dictionary<string, object?> { ["elementType"] = null };
            rows.Add(row);
            var errors = new Dictionary<string, string>();
            void Error(string field, string reason)
            {
                unavailable.Add("infos[" + index + "]" + (field == "entry" ? "" : "." + field));
                errors[field] = reason;
                row["readErrors"] = errors;
            }
            object? Field(string field, Func<object?> read)
            {
                try { return read(); }
                catch (Exception error) when (!HmiReadPolicy.ConnectionUnavailable(error))
                {
                    meta["nativeExceptionType"] = error.GetType().FullName;
                    Error(field, error.GetType().Name + ": " + error.Message);
                    return null;
                }
            }

            var info = (GlobalLibraryInfo?)Field("entry", () => infos[index]);
            if (info == null)
            {
                if (errors.Count == 0) Error("entry", "GetGlobalLibraryInfos returned a null entry.");
                continue;
            }
            row["elementType"] = info.GetType().FullName;
            row["name"] = Field("name", () => info.Name);
            row["path"] = Field("path", () => info.Path?.FullName);
            row["libraryType"] = Field("libraryType", () => info.LibraryType.ToString());
            row["isOpen"] = Field("isOpen", () => info.IsOpen);
        }
        meta["records"] = rows;
        meta["actualCount"] = rows.Count;
        meta["truncated"] = false;
        meta["dataComplete"] = unavailable.Count == 0;
        meta["apiCallSuccess"] = true;
        if (unavailable.Count > 0) meta["unavailableFields"] = unavailable;
    }
}
