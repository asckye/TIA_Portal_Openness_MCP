using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    internal static class HmiSnapshot
    {
        internal static JsonObject Capture(object root, int maxDepth = 6, int maxNodes = 2000, int maxString = 65536,
            Action<string, string, Exception?>? trace = null)
        {
            maxDepth = Math.Max(1, Math.Min(12, maxDepth)); maxNodes = Math.Max(1, Math.Min(10000, maxNodes));
            maxString = Math.Max(1, maxString);
            var rows = new JsonArray(); var seen = new Dictionary<object, string>(ReferenceComparer.Instance);
            var timer = Stopwatch.StartNew(); bool incomplete = false, aborted = false, limited = false;
            int omitted = 0, failures = 0, quarantined = 0;
            string? lastAttempted = null, lastCompleted = null, failurePath = null;
            Walk(root, "$", 0);
            return new JsonObject { ["kind"] = "InspectionSnapshot", ["restorableBackup"] = false,
                ["coverage"] = "Public readable properties/collections within limits; Parent/indexers excluded; quarantined getters explicitly reported; no hidden attributes or library internals promised",
                ["apiCallSuccess"] = !aborted, ["dataComplete"] = !incomplete,
                ["completenessScope"] = "The declared public-property snapshot scope only; not the entire HMI project or faceplate internals",
                ["incomplete"] = incomplete, ["truncated"] = limited || aborted, ["traversalComplete"] = !limited && !aborted,
                ["connectionUnavailable"] = aborted, ["remoteInspectionStopped"] = aborted, ["failurePath"] = failurePath,
                ["readFailureCount"] = failures, ["quarantinedCount"] = quarantined,
                ["lastAttemptedPath"] = lastAttempted, ["lastCompletedPath"] = lastCompleted,
                ["omittedCount"] = omitted, ["omittedCountIsExact"] = false,
                ["expectedCount"] = null, ["actualCount"] = rows.Count, ["nodeCount"] = rows.Count, ["countUnit"] = "evidence nodes, not screens or objects",
                ["nextCursor"] = null, ["resumable"] = false,
                ["maxDepth"] = maxDepth, ["maxNodes"] = maxNodes, ["maxStringLength"] = maxString,
                ["elapsedMs"] = timer.ElapsedMilliseconds, ["timeBudgetMs"] = 30000,
                ["budgetNote"] = "Checked between synchronous reads; an in-progress Openness call cannot be interrupted.", ["nodes"] = rows };

            bool CanRead()
            {
                if (aborted) return false;
                if (rows.Count < maxNodes && timer.ElapsedMilliseconds <= 30000) return true;
                incomplete = limited = true; omitted++; return false;
            }
            object? Read(string path, Func<object?> action)
            {
                lastAttempted = path; trace?.Invoke("before", path, null);
                var value = action(); lastCompleted = path; trace?.Invoke("after", path, null); return value;
            }
            void Failed(JsonObject row, string path, Exception ex)
            {
                var cause = MigrationRead.Cause(ex);
                row["status"] = "ReadFailed"; row["error"] = cause.Message;
                row["exceptionType"] = cause.GetType().FullName; row["exception"] = ex.ToString();
                incomplete = true; failures++; trace?.Invoke("failed", path, ex);
                if (HmiReadSafety.ConnectionUnavailable(ex))
                {
                    aborted = true; failurePath = path;
                    row["connectionUnavailable"] = true;
                }
            }
            void Walk(object? value, string path, int depth)
            {
                if (!CanRead()) return;
                var row = new JsonObject { ["path"] = path, ["status"] = "Read" }; rows.Add(row);
                if (value == null) { row["value"] = null; return; }
                Type type;
                try { type = (Type)Read(path + "/$type", () => value.GetType())!; row["type"] = type.FullName; }
                catch (Exception ex) { Failed(row, path + "/$type", ex); return; }
                if (value is string str)
                {
                    row["value"] = str.Length <= maxString ? str : str.Substring(0, maxString);
                    if (str.Length > maxString) { row["status"] = "Truncated"; incomplete = limited = true; }
                    return;
                }
                if (type.IsPrimitive || type.IsEnum || value is decimal || value is DateTime || value is Guid || value is TimeSpan || value is Version)
                { row["value"] = value.ToString(); return; }
                if (seen.TryGetValue(value, out var previous)) { row["referencePath"] = previous; return; }
                seen[value] = path;
                if (depth >= maxDepth) { row["status"] = "DepthLimit"; incomplete = limited = true; return; }
                if (value is IEnumerable enumerable)
                {
                    if (!CanRead()) return;
                    IEnumerator? iterator = null; int index = 0;
                    try
                    {
                        iterator = (IEnumerator)Read(path + "/$enumerator", () => enumerable.GetEnumerator())!;
                        while (CanRead())
                        {
                            var itemPath = path + "/" + index;
                            if (!(bool)Read(itemPath + "/$moveNext", () => iterator.MoveNext())!) break;
                            if (!CanRead()) break;
                            var child = Read(itemPath + "/$current", () => iterator.Current);
                            Walk(child, itemPath, depth + 1); index++;
                        }
                    }
                    catch (Exception ex) { Failed(row, lastAttempted ?? path, ex); }
                    finally
                    {
                        // A remote enumerator is another engineering handle. Do not call it again
                        // after a connection failure, even just to Dispose it while unwinding.
                        if (!aborted && iterator is IDisposable disposable)
                            try { Read(path + "/$disposeEnumerator", () => { disposable.Dispose(); return null; }); }
                            catch (Exception ex) { Failed(row, path + "/$disposeEnumerator", ex); }
                    }
                    return;
                }
                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    if (property.Name == "Parent" || property.GetMethod == null || !property.GetMethod.IsPublic || property.GetIndexParameters().Length != 0)
                    { omitted++; continue; }
                    if (!CanRead()) break;
                    var childPath = path + "/" + Uri.EscapeDataString(property.Name);
                    var skip = HmiReadSafety.SkipReason(type, property.Name);
                    if (skip != null)
                    {
                        rows.Add(new JsonObject { ["path"] = childPath, ["type"] = property.PropertyType.FullName,
                            ["status"] = "Quarantined", ["reason"] = skip, ["readAttempted"] = false });
                        incomplete = true; quarantined++; trace?.Invoke("quarantined", childPath, null); continue;
                    }
                    try { var child = Read(childPath, () => property.GetValue(value)); Walk(child, childPath, depth + 1); }
                    catch (Exception ex)
                    {
                        var failedRow = new JsonObject { ["path"] = childPath }; rows.Add(failedRow); Failed(failedRow, childPath, ex);
                    }
                }
            }
        }
        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
    }
}
