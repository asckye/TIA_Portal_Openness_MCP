using TiaMcp.Adapters.Contracts;
using System;
using System.Collections.Generic;
using System.Collections;
using System.Reflection;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;

namespace TiaMcp.Adapters.Native.Engineering
{
    // Scoped to the synchronous owner-thread call. Undo uses only public setters
    // and Delete methods; an unavailable or failed undo remains an explicit task.
    internal sealed class EngineeringWriteJournal : IDisposable
    {
        [ThreadStatic] private static EngineeringWriteJournal? current;
        private readonly EngineeringWriteJournal? parent;
        private readonly List<(string Target, Action Undo, object? Previous, Action? Probe)> entries = new();
        private readonly Dictionary<(object Target, string Member), (string Path, string Owner, object? Previous, Action Verify)> checks =
            new(MemberComparer.Instance);
        private readonly HashSet<string> deleted = new(StringComparer.Ordinal);
        private readonly HashSet<object> deletedObjects = new(MemberComparer.Instance);
        private readonly Dictionary<(object Target, string Member), object?> snapshots = new(MemberComparer.Instance);
        private readonly List<object?> pendingCleanup = new();
        private readonly List<object?> unrestoredEffects = new();
        private bool pendingRefused = true;
        private bool unrestoredWrite;
        private bool disconnected;
        private bool finished;
        private bool undoing;

        internal EngineeringWriteJournal() { parent = current; current = this; }
        internal static bool Active => current != null && !current.undoing;
        internal static string RunReported(IDictionary<string, object?> meta, Func<string> action, Func<Exception, string> failure)
        {
            try { return Run(meta, action); }
            catch (Exception error)
            {
                meta["operationSuccess"] = false;
                meta["nativeExceptionType"] = error.GetBaseException().GetType().FullName;
                meta["nativeMessage"] = error.GetBaseException().Message;
                return failure(error);
            }
        }

        internal static string Run(IDictionary<string, object?> meta, Func<string> action, Func<IDisposable>? acquireUndo = null, bool inspectVerdict = true)
        {
            using var journal = new EngineeringWriteJournal();
            try
            {
                string message = action();
                if (journal.pendingCleanup.Count > 0)
                {
                    meta["operationSuccess"] = false;
                    journal.Undo(meta, acquireUndo);
                }
                else if (inspectVerdict && meta.TryGetValue("operationSuccess", out var success) && success is false)
                    journal.Undo(meta, acquireUndo);
                else journal.Commit();
                return message;
            }
            catch (Exception error)
            {
                ObserveFailure(error);
                journal.Undo(meta, acquireUndo);
                throw;
            }
        }

        internal static void ObserveFailure(Exception error)
        {
            if (current != null) current.disconnected |= HmiReadPolicy.ConnectionUnavailable(error);
        }

        private sealed class RestoreMismatchException : InvalidOperationException
        {
            internal RestoreMismatchException(string message) : base(message) { }
        }

        internal static void Record(string target, Action undo, object? previous = null, Action? probe = null)
        {
            if (current != null && !current.undoing) current.entries.Add((target, undo, previous, probe));
        }

        internal static void Property(object target, PropertyInfo property)
        {
            if (current == null || current.undoing) return;
            string name = Identity(target) + "." + property.Name;
            if (property.GetMethod?.IsPublic != true || property.SetMethod?.IsPublic != true)
            {
                Record(name + ": restore previous value (public getter/setter unavailable)",
                    () => throw new NotSupportedException("Public getter/setter unavailable."));
                return;
            }
            var previous = current.snapshots.TryGetValue((target, "." + property.Name), out var captured) ? captured : property.GetValue(target);
            Check(target, "." + property.Name, name, Identity(target), previous, () => {
                if (!Equals(property.GetValue(target), previous)) throw new RestoreMismatchException("Final restore readback differs.");
            });
            Record(name + ": restore previous value", () => {
                if (Equals(property.GetValue(target), previous)) return;
                property.SetValue(target, previous);
                if (!Equals(property.GetValue(target), previous)) throw new InvalidOperationException("Restore readback differs.");
            }, previous, () => { property.GetValue(target); });
        }

        internal static void CaptureProperties(object target, IEnumerable<PropertyInfo> properties)
        {
            if (!Active) return;
            foreach (var property in properties)
            {
                var key = (target, "." + property.Name);
                if (current!.snapshots.ContainsKey(key)) continue;
                if (property.GetMethod?.IsPublic != true) throw new NotSupportedException("Previous property value unavailable: " + key);
                current.snapshots.Add(key, property.GetValue(target));
            }
        }

        internal static void CaptureAttributes(object target, IEnumerable<string> names, Func<string, object?> read)
        {
            if (!Active) return;
            foreach (string name in names)
            {
                var key = (target, "/@" + name);
                if (!current!.snapshots.ContainsKey(key)) current.snapshots.Add(key, read(name));
            }
        }

        internal static void ConnectedNode(string path, Func<bool> connected, Action disconnect)
        {
            Record(path + ": disconnect the node attached by this call", () => {
                if (connected()) disconnect();
                if (connected()) throw new InvalidOperationException("Node remains connected after undo.");
            }, probe: () => { connected(); });
        }

        internal static void BeforeCall(object target, MethodInfo method, object?[] arguments)
        {
            if (!Active) return;
            var parameters = method.GetParameters();
            if (method.Name == "MoveInRuntimeSequence") Unrestored(Identity(target) + "/" + method.Name);
            if (method.Name == "SetAttribute" && arguments.Length == 2 && arguments[0] is string attribute)
            {
                var getter = target.GetType().GetMethod("GetAttribute", new[] { typeof(string) })
                    ?? method.DeclaringType?.GetMethod("GetAttribute", new[] { typeof(string) });
                Attribute(target, attribute,
                    () => getter == null ? throw new NotSupportedException("Public GetAttribute unavailable.") : getter
                        .Invoke(target, new object[] { attribute }),
                    value => method.Invoke(target, new[] { (object)attribute, value! }));
            }
            if (method.Name == "SetAttributes" && arguments.Length == 1
                && arguments[0] is IEnumerable<KeyValuePair<string, object>> values)
            {
                var getter = target.GetType().GetMethod("GetAttribute", new[] { typeof(string) })
                    ?? method.DeclaringType?.GetMethod("GetAttribute", new[] { typeof(string) });
                var setter = target.GetType().GetMethod("SetAttribute", new[] { typeof(string), typeof(object) })
                    ?? method.DeclaringType?.GetMethod("SetAttribute", new[] { typeof(string), typeof(object) });
                if (getter == null || setter == null) Unrestored(Identity(target) + "/SetAttributes");
                else
                {
                    foreach (var pair in values)
                    {
                        string name = pair.Key;
                        Attribute(target, name, () => getter.Invoke(target, new object[] { name }),
                            previous => setter.Invoke(target, new[] { (object)name, previous! }));
                    }
                }
            }
            if (method.Name.StartsWith("Import", StringComparison.Ordinal))
                Record(Identity(target) + "/" + method.Name + "(" + ImportPaths(parameters, arguments)
                        + "): reconcile the imported objects with retained before/after evidence; native import has no verified inverse",
                    () => throw new NotSupportedException("Native import has no verified inverse."));
        }

        internal static void Unrestored(string target)
        {
            if (!Active) return;
            current!.unrestoredWrite = true;
            current.unrestoredEffects.Add(Cleanup(target, null, "No verified public inverse; inspect the retained native state."));
        }

        internal static void CreatedResult(MethodInfo method, object? result, object? collection = null)
        {
            if (!Active || (method.Name != "Create" && method.Name != "CreateFrom") || result == null) return;
            Created(result, Identity(result), collection: collection);
        }

        internal static void Attribute(object target, string name, Func<object?> read, Action<object?> write)
        {
            if (!Active) return;
            string path = Identity(target) + "/@" + name;
            object? previous;
            try { previous = current!.snapshots.TryGetValue((target, "/@" + name), out var captured) ? captured : read(); }
            catch (Exception error)
            {
                Record(path + ": reconcile attribute; previous value could not be read (" + error.GetBaseException().GetType().Name + ")",
                    () => throw new NotSupportedException("Previous attribute value unavailable."));
                return;
            }
            Record(path + ": restore previous attribute", () => {
                if (Equals(read(), previous)) return;
                write(previous);
                if (!Equals(read(), previous)) throw new InvalidOperationException("Attribute restore readback differs.");
            }, previous, () => { read(); });
            Check(target, "/@" + name, path, Identity(target), previous, () => {
                if (!Equals(read(), previous)) throw new RestoreMismatchException("Final attribute restore readback differs.");
            });
        }

        private static void Check(object target, string member, string path, string owner, object? previous, Action verify)
        {
            if (current != null && !current.checks.ContainsKey((target, member))) current.checks.Add((target, member), (path, owner, previous, verify));
        }

        internal static void Created(object? target, string path, Func<bool>? absent = null, object? collection = null)
        {
            if (target == null) return;
            if (absent == null && collection != null)
            {
                string identity = Identity(target);
                string? name = (ReadableProperty(target.GetType(), "Name") ?? ReadableProperty(target.GetType(), "PropertyName"))?.GetValue(target) as string;
                var find = collection.GetType().GetMethod("Find", new[] { typeof(string) });
                absent = find != null && name != null ? () => find.Invoke(collection, new object[] { name }) == null
                    : collection is IEnumerable items
                        ? () => { foreach (var item in items) if (item != null && Identity(item) == identity) return false; return true; }
                        : null;
            }
            Record(path + ": delete object created by this call", () => {
                var delete = target.GetType().GetMethod("Delete", Type.EmptyTypes);
                if (delete?.IsPublic != true) throw new NotSupportedException("Public Delete unavailable.");
                string identity = Identity(target);
                bool named = ReadableProperty(target.GetType(), "Name") != null || ReadableProperty(target.GetType(), "PropertyName") != null
                    || ReadableProperty(target.GetType(), "Language") != null;
                delete.Invoke(target, Array.Empty<object>());
                if (absent == null) throw new NotSupportedException("Deletion returned, but absence cannot be verified.");
                if (!absent()) throw new InvalidOperationException("Deleted object remains in its collection.");
                if (current != null) { current.deletedObjects.Add(target); if (named) current.deleted.Add(identity); }
            });
        }

        internal static void Creating(Func<object?> find, string path)
        {
            // Register before dispatch: a Create can throw after inserting its object.
            // Callers have already established that this exact name did not exist.
            Record(path + ": delete object created by this call if present", () => {
                var target = find();
                if (target == null) return;
                var delete = target.GetType().GetMethod("Delete", Type.EmptyTypes);
                if (delete?.IsPublic != true) throw new NotSupportedException("Public Delete unavailable.");
                string identity = Identity(target);
                bool named = ReadableProperty(target.GetType(), "Name") != null || ReadableProperty(target.GetType(), "PropertyName") != null
                    || ReadableProperty(target.GetType(), "Language") != null;
                delete.Invoke(target, Array.Empty<object>());
                if (find() != null) throw new InvalidOperationException("Deleted object remains in its collection.");
                if (current != null) { current.deletedObjects.Add(target); if (named) current.deleted.Add(identity); }
            });
        }

        internal void Commit()
        {
            if (parent != null)
            {
                parent.entries.AddRange(entries);
                parent.unrestoredWrite |= unrestoredWrite;
                parent.disconnected |= disconnected;
                parent.pendingCleanup.AddRange(pendingCleanup);
                parent.pendingRefused &= pendingRefused;
                parent.unrestoredEffects.AddRange(unrestoredEffects);
                foreach (var check in checks) if (!parent.checks.ContainsKey(check.Key)) parent.checks.Add(check.Key, check.Value);
            }
            finished = true;
        }

        internal void Rollback(IDictionary<string, object?> meta)
        {
            if (finished) return;
            var cleanup = CleanupTasks(meta);
            if (entries.Count == 0 && cleanup.Count == 0) { finished = true; return; }
            bool refused = cleanup.Count == pendingCleanup.Count && pendingRefused, reachable = true;
            bool sawRefusal = pendingCleanup.Count > 0 && pendingRefused;
            var restored = new List<object?>();
            undoing = true;
            for (int index = entries.Count - 1; index >= 0; index--)
            {
                var entry = entries[index];
                try { entry.Undo(); restored.Add(entry.Target); }
                catch (Exception error)
                {
                    // Keep attempting independent undo steps; never hide the remainder.
                    disconnected |= HmiReadPolicy.ConnectionUnavailable(error);
                    var cause = error.GetBaseException();
                    bool typed = cause is AdapterPreconditionException or NotSupportedException or UnauthorizedAccessException or ArgumentException;
                    refused &= typed;
                    sawRefusal |= typed;
                    try { if (entry.Probe == null) reachable = false; else entry.Probe(); }
                    catch (Exception probeError) { reachable = false; disconnected |= HmiReadPolicy.ConnectionUnavailable(probeError); }
                    cleanup.Add(Cleanup(entry.Target, entry.Previous, cause.GetType().Name));
                }
            }
            foreach (var check in checks)
            {
                bool removed = deletedObjects.Contains(check.Key.Target);
                foreach (var path in deleted)
                    if (check.Value.Owner == path || check.Value.Owner.StartsWith(path + "/", StringComparison.Ordinal)) { removed = true; break; }
                if (removed) continue;
                try { check.Value.Verify(); }
                catch (Exception error) {
                    if (error is not RestoreMismatchException) { refused = false; reachable = false; }
                    disconnected |= HmiReadPolicy.ConnectionUnavailable(error);
                    cleanup.Add(Cleanup(check.Value.Path + ": restore previous value", check.Value.Previous, error.GetBaseException().GetType().Name)); }
            }
            meta["rollbackScope"] = "Hooked writes only; unhooked native effects are not established by this journal.";
            meta["rollbackAttempted"] = entries.Count > 0;
            SetOutcome(meta, entries.Count > 0 && cleanup.Count == 0, refused && sawRefusal, reachable);
            if (meta.TryGetValue("writeOutcomeUnknown", out var unknown) && unknown is true && cleanup.Count == 0)
                cleanup.Add(Cleanup("Verify unconfirmed native effects of " + (meta.TryGetValue("tool", out var tool) ? tool : "the write"),
                    null, "Captured values were restored, but the native write outcome remains unconfirmed."));
            meta["restoredChanges"] = restored;
            meta["cleanupRequired"] = cleanup;
            if (cleanup.Count > 0)
            {
                parent?.pendingCleanup.AddRange(cleanup);
                if (parent != null)
                {
                    parent.unrestoredWrite |= unrestoredWrite;
                    parent.disconnected |= disconnected;
                    parent.pendingRefused &= meta["rollbackOutcome"] as string == "partial";
                }
            }

            finished = true;
        }

        private void Undo(IDictionary<string, object?> meta, Func<IDisposable>? acquireUndo)
        {
            if (entries.Count == 0 || acquireUndo == null) { Rollback(meta); return; }
            IDisposable? access;
            try { access = acquireUndo(); }
            catch (Exception error)
            {
                meta["rollbackScope"] = "Hooked writes only; unhooked native effects are not established by this journal.";
                disconnected |= HmiReadPolicy.ConnectionUnavailable(error);
                SetOutcome(meta, false, false, false);
                var cleanup = CleanupTasks(meta);
                foreach (var entry in entries) cleanup
                    .Add(Cleanup(entry.Target, entry.Previous, "Undo access unavailable: " + error.GetBaseException().GetType().Name));
                meta["cleanupRequired"] = cleanup;
                meta["mayHaveChanged"] = true;
                parent?.pendingCleanup.AddRange(cleanup);
                if (parent != null) parent.pendingRefused = false;
                finished = true;
                return;
            }
            try { Rollback(meta); }
            finally
            {
                try { access?.Dispose(); }
                catch (Exception error)
                {
                    disconnected |= HmiReadPolicy.ConnectionUnavailable(error);
                    SetOutcome(meta, false, false, false);
                    var cleanup = meta["cleanupRequired"] as List<object?> ?? new List<object?>();
                    var task = Cleanup("Release the edit access acquired for rollback", null, error.GetBaseException().GetType().Name);
                    cleanup.Add(task);
                    parent?.pendingCleanup.Add(task);
                    if (parent != null) parent.pendingRefused = false;
                    meta["cleanupRequired"] = cleanup;
                }
            }
        }

        private void SetOutcome(IDictionary<string, object?> meta, bool restored, bool refused, bool reachable)
        {
            bool Flag(string name) => meta.TryGetValue(name, out var value) && value is true;
            string outcome = WriteRollbackPolicy.Outcome(restored, disconnected || Flag("connectionUnavailable"),
                Flag("writeOutcomeUnknown"), unrestoredWrite || Flag("unrestoredWrite"), refused, reachable);
            meta["rollbackOutcome"] = outcome;
            meta["rollbackCompleted"] = outcome == "restored";
            meta["requiresSessionReset"] = outcome == "unknown";
            meta["rollbackRefused"] = refused;
            meta["rollbackReachable"] = reachable;
            meta["unrestoredWrite"] = unrestoredWrite || Flag("unrestoredWrite");
            if (disconnected) meta["connectionUnavailable"] = true;
            meta["mayHaveChanged"] = outcome != "restored";
            if (outcome != "restored") meta["outcome"] = outcome;
            if (outcome == "unknown") { meta["writeOutcomeUnknown"] = true; meta["requiresSessionReset"] = true; }
        }

        private List<object?> CleanupTasks(IDictionary<string, object?> meta)
        {
            var tasks = new List<object?>(pendingCleanup);
            tasks.AddRange(unrestoredEffects);
            if (meta.TryGetValue("cleanupRequired", out var existing) && existing is IEnumerable items && existing is not string)
                foreach (var item in items) if (item != null && !tasks.Contains(item)) tasks.Add(item);
            return tasks;
        }

        private static Dictionary<string, object?> Cleanup(string target, object? previous, string reason)
        {
            bool sensitive = false;
            foreach (string marker in new[] { "password", "secret", "token", "credential" })
                sensitive |= target.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0;
            return new Dictionary<string, object?> { ["target"] = target, ["reason"] = reason,
                ["previousType"] = previous?.GetType().FullName,
                ["previousValue"] = sensitive ? "(redacted; recover from retained backup)" : previous == null ? null
                    : previous is string || previous.GetType().IsPrimitive || previous.GetType().IsEnum || previous is decimal || previous is DateTime
                        || previous is TimeSpan || previous is Guid || previous is Version
                        ? Convert.ToString(previous, CultureInfo.InvariantCulture) : "(non-scalar; recover from retained before evidence)" };
        }

        private sealed class MemberComparer : IEqualityComparer<(object Target, string Member)>, IEqualityComparer<object>
        {
            internal static readonly MemberComparer Instance = new();
            bool IEqualityComparer<object>.Equals(object? left, object? right) => ReferenceEquals(left, right);
            int IEqualityComparer<object>.GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
            public bool Equals((object Target, string Member) left, (object Target, string Member) right)
                => ReferenceEquals(left.Target, right.Target) && string.Equals(left.Member, right.Member, StringComparison.Ordinal);
            public int GetHashCode((object Target, string Member) value)
                => RuntimeHelpers.GetHashCode(value.Target) ^ StringComparer.Ordinal.GetHashCode(value.Member);
        }

        private static string ImportPaths(ParameterInfo[] parameters, object?[] arguments)
        {
            var paths = new List<string>();
            for (int index = 0; index < arguments.Length; index++)
            {
                string name = parameters[index].Name ?? "";
                if (arguments[index] is FileSystemInfo file) paths.Add(name + "=" + file.FullName);
                else if (arguments[index] is string text && (name.EndsWith("Path", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("name", StringComparison.OrdinalIgnoreCase) || name.Equals("fileName", StringComparison.OrdinalIgnoreCase)))
                    paths.Add(name + "=" + text);
            }
            return string.Join(", ", paths);
        }

        private static PropertyInfo? ReadableProperty(Type type, string name)
        {
            for (Type? currentType = type; currentType != null; currentType = currentType.BaseType)
                foreach (var property in currentType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    if (property.Name == name && property.GetMethod?.IsPublic == true && property.GetIndexParameters().Length == 0) return property;
            return null;
        }

        private static string Identity(object target)
        {
            var parts = new List<string>();
            object? item = target;
            for (int count = 0; item != null && count < 32; count++)
            {
                var name = ReadableProperty(item.GetType(), "Name")?.GetValue(item) as string
                    ?? ReadableProperty(item.GetType(), "PropertyName")?.GetValue(item) as string;
                if (name == null && ReadableProperty(item.GetType(), "Language")?.GetValue(item) is object language
                    && ReadableProperty(language.GetType(), "Culture")?.GetValue(language) is CultureInfo culture) name = "language=" + culture.Name;
                parts.Add(name == null ? item.GetType().Name : Uri.EscapeDataString(name));
                var next = ReadableProperty(item.GetType(), "Parent")?.GetValue(item);
                if (ReferenceEquals(next, item)) break;
                item = next;
            }
            parts.Reverse();
            return "/" + string.Join("/", parts);
        }

        public void Dispose()
        {
            current = parent;
        }
    }
}
