using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters.Native.Hmi
{
    // Shared preflight and exact lookup rules; no native writes or fallback names.
    internal static class HmiExchangePolicy
    {
        internal static string ExportOption(string value)
        {
            if (value != "None" && value != "WithDefaults" && value != "WithReadOnly")
                throw new ArgumentException("exportOptions must be None, WithDefaults or WithReadOnly.");
            return value;
        }

        internal static void RequireClassic(object software)
        {
            if (software.GetType().FullName == "Siemens.Engineering.HmiUnified.HmiSoftware")
                throw new PlcSoftwareException("NotSupportedOnVersion", "Classic WinCC XML exchange is unavailable on Unified software. Use ExchangeUnifiedTags or ExchangeUnifiedScriptModules for supported Unified directory exchange; Unified screen/connection XML exchange is not exposed by the SDK.");
        }

        internal static object? Find(object collection, string name)
        {
            var matches = Items(collection).Where(x => string.Equals(Name(x), name,
                StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length > 1) throw new PlcSoftwareException("InvalidParams", "Ambiguous object: " + name + "; use a complete path.");
            return matches.SingleOrDefault();
        }

        private static string Name(object value) => Get(value, "Name") as string
            ?? throw new InvalidOperationException("HMI object has no readable Name: " + value.GetType().FullName);

        internal static List<string> Names(object root, string[] propertyHints, string finalCollectionName)
        {
            object? collection = propertyHints.Length == 0 ? root : null;
            foreach (var hint in propertyHints)
            {
                var value = Get(root, hint);
                if (value == null) continue;
                collection = hint.EndsWith("Folder", StringComparison.OrdinalIgnoreCase) ? Get(value, finalCollectionName) : value;
                if (collection != null) break;
            }
            if (collection == null) throw new NotSupportedException("HMI collection unavailable: " + finalCollectionName);
            return Items(collection).Select(Name).ToList();
        }

        internal static List<(string Path, object Value)> TagTables(object software, int initialDepth = 0)
        {
            var root = Get(software, "TagFolder") ?? software;
            var result = new List<(string, object)>();
            var ancestors = new HashSet<object>();
            Walk(root, "", ReferenceEquals(root, software) && root.GetType().GetProperty("TagTableGroups") != null ? "TagTableGroups" : "Folders", initialDepth);
            return result;

            void Walk(object folder, string path, string childProperty, int depth)
            {
                if (depth > 64 || !ancestors.Add(folder)) throw new InvalidOperationException("HMI folder cycle or traversal limit.");
                var tables = Get(folder, "TagTables") ?? throw new NotSupportedException("TagTables unavailable.");
                foreach (var table in Items(tables))
                {
                    if (result.Count >= 10000) throw new InvalidOperationException("HMI tag table traversal limit.");
                    result.Add((path + "/" + Uri.EscapeDataString(Name(table)), table));
                }
                var groups = Get(folder, childProperty);
                if (groups != null)
                    foreach (var child in Items(groups))
                        Walk(child, path + "/" + Uri.EscapeDataString(Name(child)), childProperty == "Folders" ? "Folders" : "Groups", depth + 1);
                ancestors.Remove(folder);
            }
        }

        internal static List<string> TagNames(object software, string tagTableName)
        {
            if (!string.IsNullOrWhiteSpace(tagTableName))
            {
                var table = TagTable(software, tagTableName)
                    ?? throw new PlcSoftwareException("NotFound", "HMI tag table not found: " + tagTableName);
                return Names(table, new[] { "Tags" }, "Tags");
            }
            // Classic TagFolder has no Tags; both APIs document Tags on each tag table.
            return TagTables(software).SelectMany(table => Names(table.Value, new[] { "Tags" }, "Tags")).ToList();
        }

        internal static object Folder(object root, string path, string rootGroups = "Folders")
        {
            if (path == "") return root;
            var parts = EngineeringInputValidation.AbsoluteHmiPath(path, rootAllowed: true, maximumSegments: 64);
            object current = root;
            for (int i = 0; i < parts.Length; i++)
            {
                var groups = Get(current, i == 0 ? rootGroups : rootGroups == "Folders" ? "Folders" : "Groups")
                    ?? throw new PlcSoftwareException("NotFound", "HMI folder collection unavailable: " + path);
                current = Find(groups, Uri.UnescapeDataString(parts[i]))
                    ?? throw new PlcSoftwareException("NotFound", "HMI folder not found: " + path);
            }
            return current;
        }

        internal static object? TagTable(object software, string nameOrPath)
        {
            EngineeringInputValidation.NameOrAbsolutePath(nameOrPath);
            if (string.IsNullOrWhiteSpace(nameOrPath)) throw new PlcSoftwareException("InvalidParams", "Tag table name/path is required.");
            object root = Get(software, "TagFolder") ?? software;
            string groupsName = ReferenceEquals(root, software) ? "TagTableGroups" : "Folders";
            if (nameOrPath.StartsWith("/", StringComparison.Ordinal))
            {
                var parts = nameOrPath.Substring(1).Split('/');
                if (parts.Length > 65 || parts.Any(string.IsNullOrEmpty)) throw new PlcSoftwareException("InvalidParams", "Invalid absolute tag table path.");
                int slash = nameOrPath.LastIndexOf('/');
                var folder = Folder(root, nameOrPath.Substring(0, slash), groupsName);
                return Find(Get(folder, "TagTables") ?? throw new PlcSoftwareException("NotFound", "TagTables unavailable."),
                    Uri.UnescapeDataString(nameOrPath.Substring(slash + 1)));
            }
            var found = new List<object>();
            Walk(root, groupsName, 0, new HashSet<object>());
            if (found.Count > 1) throw new PlcSoftwareException("InvalidParams", "Ambiguous tag table: " + nameOrPath + "; use a complete path.");
            return found.SingleOrDefault();
            void Walk(object folder, string childProperty, int depth, HashSet<object> ancestors)
            {
                if (depth > 64 || !ancestors.Add(folder)) throw new InvalidOperationException("HMI folder cycle or traversal limit.");
                var tables = Get(folder, "TagTables");
                if (tables != null) found.AddRange(Items(tables).Where(x => string.Equals(Get(x, "Name") as string,
                    nameOrPath, StringComparison.OrdinalIgnoreCase)));
                var groups = Get(folder, childProperty);
                if (groups != null) foreach (var child in Items(groups)) Walk(child, childProperty == "Folders" ? "Folders" : "Groups", depth + 1, ancestors);
                ancestors.Remove(folder);
            }
        }

        internal static object EnsureTagTable(object software, string nameOrPath, Action beforeWrite)
        {
            EngineeringInputValidation.NameOrAbsolutePath(nameOrPath);
            object root = Get(software, "TagFolder") ?? software;
            var existing = TagTable(software, nameOrPath);
            if (existing != null) return existing;
            var tables = Get(Get(software, "TagFolder") ?? software, "TagTables")
                ?? throw new PlcSoftwareException("NotFound", "TagTables unavailable.");
            string name = nameOrPath;
            if (nameOrPath.StartsWith("/", StringComparison.Ordinal))
            {
                int slash = nameOrPath.LastIndexOf('/');
                root = Folder(root, nameOrPath.Substring(0, slash), ReferenceEquals(root, software) ? "TagTableGroups" : "Folders");
                tables = Get(root, "TagTables") ?? throw new PlcSoftwareException("NotFound", "TagTables unavailable.");
                name = Uri.UnescapeDataString(nameOrPath.Substring(slash + 1));
            }
            var create = tables.GetType().GetMethod("Create", new[] { typeof(string) })
                ?? throw new NotSupportedException("Documented TagTables.Create(string) unavailable.");
            beforeWrite();
            try { return create.Invoke(tables, new object[] { name }) ?? throw new InvalidOperationException("Tag table creation returned null."); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            { throw new InvalidOperationException(error.InnerException.GetType().FullName + ": " + error.InnerException.Message, error.InnerException); }
        }

        internal static void BeforeImport(object collection, string path, string objectType, bool overwrite)
        {
            // Read the exported identities, never infer object names from the filename.
            var document = XDocument.Load(path);
            var names = document.Descendants().Where(e => e.Name.LocalName == objectType)
                .Select(e => e.Elements().SingleOrDefault(a => a.Name.LocalName == "AttributeList")?
                    .Elements().SingleOrDefault(a => a.Name.LocalName == "Name")?.Value).ToArray();
            if (names.Length == 0 || names.Any(string.IsNullOrWhiteSpace))
                throw new PlcSoftwareException("InvalidParams", "Cannot verify the import target identities for " + objectType + ". Use an official exported XML file.");
            if (names.Any(n => n!.IndexOfAny(new[] { '/', '\\' }) >= 0))
                throw new PlcSoftwareException("InvalidParams", "Imported HMI object names must be single names without path separators. Select the existing destination with absolute folderPath /Group/Sub; do not put /Group/Sub/Table in the XML Name.");
            if (!overwrite)
                foreach (var name in names)
                    if (Find(collection, name!) != null)
                        throw new PlcSoftwareException("InvalidState", "HMI import target already exists: " + name + ". Set overwrite=true explicitly to replace it.");
        }

        internal static void WriteScalars(object target, IDictionary<string, object?> changes, Action beforeWrite)
        {
            var prepared = new List<(PropertyInfo Property, object? Value)>();
            foreach (var change in changes)
            {
                var property = target.GetType().GetProperty(change.Key);
                if (property?.SetMethod?.IsPublic != true || property.GetMethod?.IsPublic != true || property.GetIndexParameters().Length != 0)
                    throw new NotSupportedException("Public writable property unavailable: " + target.GetType().FullName + "." + change.Key);
                object? value = change.Value;
                if (property.PropertyType.IsEnum && value is string text)
                {
                    try { value = HmiScalarPolicy.ParseEnum(property.PropertyType, text); }
                    catch (ArgumentException error)
                    { throw new NotSupportedException("Documented enum value unavailable: " + change.Key + "." + text, error); }
                }
                else if (value != null && !property.PropertyType.IsInstanceOfType(value))
                    throw new NotSupportedException("Property type does not match the documented value: " + change.Key);
                prepared.Add((property!, value));
            }
            foreach (var change in prepared)
            {
                beforeWrite();
                change.Property.SetValue(target, change.Value);
                if (!Equals(change.Property.GetValue(target), change.Value))
                    throw new InvalidOperationException("Property readback differs: " + change.Property.Name + ". Changes are not rolled back.");
            }
        }

        internal static void UpdateScript(object script, string code, string? globals, bool? async, Action beforeWrite)
        {
            var changes = new Dictionary<string, object?> { ["ScriptCode"] = code };
            if (globals != null) changes["GlobalDefinitionAreaScriptCode"] = globals;
            if (async.HasValue) changes["Async"] = async.Value;
            WriteScalars(script, changes, beforeWrite);
        }

        internal static void RequireLegacyText(string text)
        {
            if (text.Length != 0)
                throw new NotSupportedException("EnsureUnifiedHmiScreenItem cannot choose a text language. Omit text and use SetUnifiedMultilingualProperty with an explicit culture; the requested text was not written.");
        }

        internal static object EnsureTag(object software, object table, string name, Action beforeWrite)
        {
            var tableName = Get(table, "Name") as string ?? throw new InvalidOperationException("Tag table name unavailable.");
            var tags = Get(software, "Tags") ?? throw new NotSupportedException("Unified Tags unavailable.");
            var tag = Find(tags, name);
            if (tag == null)
            {
                var create = tags.GetType().GetMethod("Create", new[] { typeof(string), typeof(string) })
                    ?? throw new NotSupportedException("Documented Tags.Create(name, tagTableName) unavailable.");
                beforeWrite();
                tag = create.Invoke(tags, new object[] { name, tableName })
                    ?? throw new InvalidOperationException("Tag creation returned null.");
            }
            var owner = Get(tag, "TagTableName") as string;
            if (!string.Equals(owner, tableName, StringComparison.Ordinal))
                throw new PlcSoftwareException("InvalidState", "Unified tag '" + name + "' belongs to table '" + owner + "', requested '" + tableName + "'. Tag names are device-wide; no alternative Create overload was attempted.");
            return tag;
        }

        internal static void RequireConnection(object connection, string expectedPartner)
        {
            var partner = Get(connection, "Partner") as string;
            var driver = Get(connection, "CommunicationDriver") as string;
            var address = Get(connection, "InitialAddress") as string;
            if (!string.Equals(partner, expectedPartner, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(expectedPartner) || string.IsNullOrWhiteSpace(driver) || string.IsNullOrWhiteSpace(address))
                throw new NotSupportedException("Existing Unified connection partner/driver/address could not be verified for '" + expectedPartner
                    + "'. Configure it through TIA or ManageUnifiedEngineeringObject and confirm the readback. No guessed properties were written.");
        }

        internal static void BeforeOpaqueImport(object existing, bool overwrite, IDictionary<string, object?> evidence)
        {
            evidence["overwrite"] = overwrite;
            if (!overwrite && Items(existing).Any())
            {
                // Expected-name lists are readback hints, not proof that a native package contains no other objects.
                evidence["v4Rejection"] = "PRECONDITION_FAILED";
                evidence["importOverwriteRefused"] = true;
                throw new PlcSoftwareException("InvalidState", "HMI import targets already exist and the native package identities cannot all be verified offline. Set overwrite=true explicitly to permit replacement, or import into an empty target. No native Import was attempted.");
            }
        }

        internal static object? Get(object value, string property) => value.GetType().GetProperty(property)?.GetValue(value);
        internal static IEnumerable<object> Items(object value)
        {
            if (value is not IEnumerable items || value is string) throw new InvalidOperationException("Expected HMI collection.");
            int count = 0;
            foreach (var item in items)
            {
                if (++count > 10000) throw new InvalidOperationException("HMI collection limit exceeded.");
                if (item != null) yield return item;
            }
        }
    }
}
