using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;
using MotionLogic = TiaMcpServer.Siemens.MotionProDiagClassicHmiLogic;

namespace TiaMcp.Engine.Tests
{
    internal static class MotionProDiagClassicHmiTests
    {
        // Minimal stand-ins for the official IEngineeringObject/IEngineeringComposition metadata surface.
        private sealed class Info { public string Name { get; set; } = ""; public string AccessMode { get; set; } = ""; }
        private sealed class CreationInfo { public Type? Type { get; set; } }
        private sealed class InvocationInfo { public string Name { get; set; } = ""; public object[] ParameterInfos { get; set; } = Array.Empty<object>(); }
        private sealed class FakeEntry { public string Name { get; set; } = ""; public bool Deleted; public void Delete() { Deleted = true; } }
        private sealed class FakeObject
        {
            public readonly Dictionary<string, object?> Values = new Dictionary<string, object?>();
            public readonly List<Info> Infos = new List<Info>();
            public bool IgnoreWrites; public FakeComposition? Entries; public bool Deleted; public bool AdvertiseDelete;
            public IList<Info> GetAttributeInfos() => Infos;
            public object? GetAttribute(string name) => Values[name];
            public void SetAttribute(string name, object? value) { if (!IgnoreWrites) Values[name] = value; }
            public IList<Info> GetCompositionInfos() => new[] { new Info { Name = "Entries" } };
            public object? GetComposition(string name) => name == "Entries" ? Entries : null;
            public IList<InvocationInfo> GetInvocationInfos() => AdvertiseDelete ? new[] { new InvocationInfo { Name = "Delete" } } : Array.Empty<InvocationInfo>();
            public object? Invoke(string name, IEnumerable<KeyValuePair<Type, object>> args) { if (name == "Delete") Deleted = true; return null; }
        }
        private sealed class FakeComposition : IEnumerable
        {
            public readonly List<object> Items = new List<object>();
            public IEnumerator GetEnumerator() => Items.GetEnumerator();
            public IList<CreationInfo> GetCreationInfos() => new[] { new CreationInfo { Type = typeof(FakeEntry) } };
            public object Create(Type type, IEnumerable<KeyValuePair<string, object>> attributes)
            {
                var entry = new FakeEntry { Name = attributes.Single(a => a.Key == "Name").Value.ToString()! }; Items.Add(entry); return entry;
            }
        }
        internal static void Run(Action<bool, string> check)
        {
            bool Fails<TException>(Action action) where TException : Exception { try { action(); return false; } catch (TException) { return true; } catch { return false; } }
            // Motion action/aspect routing.
            check(MotionLogic.MotionCategory("read", "") == "read" && MotionLogic.MotionCategory("connectIdent", "") == "ident", "motion read/ident categories");
            check(MotionLogic.MotionCategory("addMasterValue", "synchronousSetPoint") == "masterValue" && MotionLogic.MotionCategory("deleteMapping", "dbMemberMapping") == "mapping" && MotionLogic.MotionCategory("connect", "encoder") == "connection", "motion aspect categories");
            check(Fails<ArgumentException>(() => MotionLogic.MotionCategory("connect", "")), "connect requires an aspect");
            check(Fails<ArgumentException>(() => MotionLogic.MotionCategory("read", "actor")), "read refuses an aspect");
            check(Fails<ArgumentException>(() => MotionLogic.MotionCategory("addMasterValue", "actor")), "master value refuses a connection aspect");
            check(Fails<ArgumentException>(() => MotionLogic.MotionCategory("drive", "actor")), "unknown motion action refused");
            check(MotionLogic.MasterValueAspects["superimposingSetPoint"].Service.EndsWith(".SuperimposingAxes") && MotionLogic.MappingAspects["toMapping"].Property == "TechnologicalObjectMapping", "aspect tables point at V21 XML types");
            // Connection target parsing: exactly one native overload.
            check(MotionLogic.ParseConnectionTarget("{\"devicePath\":[\"PLC_1\"],\"itemPath\":[\"Rack\",\"TM\"]}").Mode == "deviceItem", "device item target");
            var pair = MotionLogic.ParseConnectionTarget("{\"devicePath\":[\"D\"],\"itemPath\":[\"A\"],\"secondItemPath\":[\"B\"],\"connectOption\":\"AllowAllModules\"}");
            check(pair.Mode == "deviceItems" && pair.HasConnectOption && pair.ConnectOption == "AllowAllModules", "two device items with connect option");
            check(MotionLogic.ParseConnectionTarget("{\"devicePath\":[\"D\"],\"itemPath\":[\"A\"],\"channelIndex\":2}").Mode == "deviceItemChannel", "device item channel target");
            check(MotionLogic.ParseConnectionTarget("{\"dbMemberPath\":\"DB1.x\"}").Mode == "dbMember", "db member target");
            check(MotionLogic.ParseConnectionTarget("{\"plcTagPath\":\"Tags/Default/Tag1\"}").Mode == "plcTag", "plc tag target");
            var bits = MotionLogic.ParseConnectionTarget("{\"inputBitAddress\":0,\"outputBitAddress\":8}");
            check(bits.Mode == "addresses" && bits.OutputBitAddress == 8 && bits.ConnectOption == "Default", "bit address target defaults connect option");
            check(MotionLogic.ParseConnectionTarget("{\"address\":16}").Mode == "address", "single address target");
            check(Fails<ArgumentException>(() => MotionLogic.ParseConnectionTarget("{}")), "empty target refused");
            check(Fails<ArgumentException>(() => MotionLogic.ParseConnectionTarget("{\"dbMemberPath\":\"x\",\"address\":1}")), "two targets refused");
            check(Fails<ArgumentException>(() => MotionLogic.ParseConnectionTarget("{\"devicePath\":[\"D\"]}")), "device path without item path refused");
            check(Fails<ArgumentException>(() => MotionLogic.ParseConnectionTarget("{\"inputBitAddress\":1}")), "half bit address pair refused");
            check(Fails<ArgumentException>(() => MotionLogic.ParseConnectionTarget("{\"address\":-1}")), "negative address refused");
            check(Fails<ArgumentException>(() => MotionLogic.ParseConnectionTarget("{\"telegram\":3}")), "unknown target key refused");
            check(Fails<ArgumentException>(() => MotionLogic.ParseConnectionTarget("{\"dbMemberPath\":\"x\",\"connectOption\":\"Default\"}")), "connect option outside device/address targets refused");
            check(Fails<ArgumentException>(() => MotionLogic.ParseConnectionTarget("[1]")), "non-object target refused");
            check(Fails<ArgumentException>(() => MotionLogic.RequireConnectionMode("torque", "plcTag")), "torque has no PlcTag overload");
            check(Fails<ArgumentException>(() => MotionLogic.RequireConnectionMode("actor", "addresses")), "actor bit addresses belong to ConfigureMotionHardwareConnection");
            check(!Fails<Exception>(() => MotionLogic.RequireConnectionMode("encoder", "addresses")) && !Fails<Exception>(() => MotionLogic.RequireConnectionMode("outputCam", "plcTag")), "encoder addresses and output cam tag are native overloads");
            // Shared validation.
            check(MotionLogic.RequireAction("export", MotionLogic.ScriptActions) == "export" && Fails<ArgumentException>(() => MotionLogic.RequireAction("create", MotionLogic.CycleActions)), "action allow-lists");
            check(Fails<ArgumentException>(() => MotionLogic.RequireName("a/b", "alias")) && Fails<ArgumentException>(() => MotionLogic.RequireName(" ", "alias")) && MotionLogic.RequireName("Alias_1", "alias") == "Alias_1", "single-segment names");
            check(Fails<ArgumentException>(() => MotionLogic.RequirePagination(0, 501)) && Fails<ArgumentException>(() => MotionLogic.RequirePagination(-1, 10)) && !Fails<Exception>(() => MotionLogic.RequirePagination(0, 500)), "pagination bounds");
            check(MotionLogic.ParseAttributes("{\"CycleTime\":250}", true)["CycleTime"]!.GetValue<int>() == 250, "scalar attributes parsed");
            check(Fails<ArgumentException>(() => MotionLogic.ParseAttributes("{\"A\":{\"b\":1}}", true)) && Fails<ArgumentException>(() => MotionLogic.ParseAttributes("{}", true)) && Fails<ArgumentException>(() => MotionLogic.ParseAttributes("{\"A\":1}", false)) && Fails<ArgumentException>(() => MotionLogic.ParseAttributes("3", true)), "attribute JSON refusals");
            check(Fails<ArgumentException>(() => MotionLogic.RequireImportConfirmation("Override", false)) && !Fails<Exception>(() => MotionLogic.RequireImportConfirmation("Override", true)) && !Fails<Exception>(() => MotionLogic.RequireImportConfirmation("None", false)), "override import needs confirmation");
            var split = MotionLogic.SplitObjectPath("Folder/Sub/Script1");
            check(split.Folder.SequenceEqual(new[] { "Folder", "Sub" }) && split.Name == "Script1" && MotionLogic.SplitObjectPath("Only").Folder.Length == 0, "object path split");
            check(Fails<ArgumentException>(() => MotionLogic.SplitObjectPath("a//b")) && Fails<ArgumentException>(() => MotionLogic.SplitObjectPath("../x")), "object path refusals");
            check(MotionLogic.ShortTypeName(MotionLogic.SupervisionProvider) == "SupervisionProvider" && MotionLogic.ListKinds["graphic"].Property == "GraphicLists" && MotionLogic.LibraryTypeKinds["faceplate"].EndsWith(".FaceplateLibraryType"), "name tables");
            // Dynamic attribute access on the official metadata surface.
            var fake = new FakeObject();
            fake.Infos.AddRange(new[] { new Info { Name = "CycleTime", AccessMode = "ReadWrite" }, new Info { Name = "Unit", AccessMode = "Read" }, new Info { Name = "Secret", AccessMode = "Write" } });
            fake.Values["CycleTime"] = 100; fake.Values["Unit"] = "Millisecond"; fake.Values["Secret"] = "hidden";
            var read = EngineeringDynamicAccess.Read(fake);
            check(read["values"]!["CycleTime"]!.GetValue<int>() == 100 && read["values"]!["Unit"]!.GetValue<string>() == "Millisecond", "readable attributes read");
            check(read["values"]!["Secret"] == null && read["excludedComplexAttributes"]!.AsArray().Any(x => x!.GetValue<string>() == "Secret") && read["dataComplete"]!.GetValue<bool>(), "write-only attribute excluded, not read");
            check(read["schema"]!.AsArray().Count == 3, "attribute schema lists every advertised attribute");
            check(Fails<NotSupportedException>(() => EngineeringDynamicAccess.Prepare(fake, new JsonObject { ["Unit"] = "Second" })), "read-only attribute refused before any write");
            check(Fails<NotSupportedException>(() => EngineeringDynamicAccess.Prepare(fake, new JsonObject { ["Ghost"] = 1 })), "unadvertised attribute refused");
            var prepared = EngineeringDynamicAccess.Prepare(fake, new JsonObject { ["CycleTime"] = 250 });
            check(prepared.Count == 1 && prepared[0].Value is int converted && converted == 250, "value converted to the existing attribute type");
            var meta = new JsonObject();
            EngineeringDynamicAccess.Apply(fake, prepared, meta);
            check(Equals(fake.Values["CycleTime"], 250) && meta["mayHaveChanged"]!.GetValue<bool>() && meta["appliedAttributes"]!.AsArray().Count == 1, "attribute applied with change flag set before the write");
            fake.IgnoreWrites = true;
            check(Fails<InvalidOperationException>(() => EngineeringDynamicAccess.Apply(fake, EngineeringDynamicAccess.Prepare(fake, new JsonObject { ["CycleTime"] = 500 }), new JsonObject())), "silent native no-op detected by readback");
            check(Fails<NotSupportedException>(() => EngineeringDynamicAccess.Read(new object())), "plain object without official metadata is unsupported, not empty");
            // Advertised compositions, creation and deletion.
            fake.Entries = new FakeComposition(); fake.Entries.Items.Add(new FakeEntry { Name = "E1" }); fake.Entries.Items.Add(new FakeEntry { Name = "Dup" }); fake.Entries.Items.Add(new FakeEntry { Name = "Dup" });
            check(EngineeringDynamicAccess.CompositionNames(fake).SequenceEqual(new[] { "Entries" }), "composition names from GetCompositionInfos");
            check(ReferenceEquals(EngineeringDynamicAccess.Composition(fake, "Entries"), fake.Entries), "advertised composition resolved");
            check(Fails<NotSupportedException>(() => EngineeringDynamicAccess.Composition(fake, "Other")) && Fails<ArgumentException>(() => EngineeringDynamicAccess.Composition(fake, "")), "unadvertised composition refused");
            check(EngineeringDynamicAccess.CreationType(fake.Entries, "FakeEntry") == typeof(FakeEntry), "creation type must be advertised");
            check(Fails<NotSupportedException>(() => EngineeringDynamicAccess.CreationType(fake.Entries, "System.String")), "unadvertised creation type refused");
            var created = EngineeringDynamicAccess.Create(fake.Entries, typeof(FakeEntry), new JsonObject { ["Name"] = "E2" });
            check(EngineeringDynamicAccess.Name(created) == "E2" && fake.Entries.Items.Count == 4, "entry created through Create(Type, attributes)");
            check(ReferenceEquals(EngineeringDynamicAccess.FindByName(fake.Entries, "E1"), fake.Entries.Items[0]), "exact entry found by name");
            check(Fails<InvalidOperationException>(() => EngineeringDynamicAccess.FindByName(fake.Entries, "Dup")) && Fails<InvalidOperationException>(() => EngineeringDynamicAccess.FindByName(fake.Entries, "Missing")), "ambiguous or missing entry refused");
            var entry = (FakeEntry)created;
            check(EngineeringDynamicAccess.CanDelete(entry), "public Delete recognised");
            EngineeringDynamicAccess.Delete(entry);
            check(entry.Deleted, "public Delete invoked");
            check(!EngineeringDynamicAccess.CanDelete(fake) && Fails<NotSupportedException>(() => EngineeringDynamicAccess.Delete(fake)), "delete without advertisement is unsupported");
            fake.AdvertiseDelete = true;
            check(EngineeringDynamicAccess.CanDelete(fake), "advertised parameterless Delete invocation recognised");
            EngineeringDynamicAccess.Delete(fake);
            check(fake.Deleted, "advertised Delete invoked through Invoke");
            check(EngineeringDynamicAccess.Name(new FakeEntry { Name = "N" }) == "N" && EngineeringDynamicAccess.Name(new object()) == null, "name resolution prefers CLR property and never guesses");
        }
    }
}
