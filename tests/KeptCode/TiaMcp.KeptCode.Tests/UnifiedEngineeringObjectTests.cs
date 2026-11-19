using System;
using System.Collections.Generic;
using System.Linq;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Engineering;
using TiaMcp.Adapters.Native.Hmi;
using Xunit;

namespace TiaMcp.Engine.Tests
{
    public sealed class UnifiedEngineeringObjectTests
    {
        [Theory]
        [InlineData("connections", "HMI_Connection_1")]
        [InlineData("tagTables", "/Diagnostic/Diag%2FInfo")]
        public void Delete_previews_contents_and_verifies_absence_with_fresh_navigation(string category, string name)
        {
            var root = new Siemens.Engineering.HmiUnified.HmiSoftware();
            root.Connections.Add(new Entry("HMI_Connection_1", root.Connections));
            var group = new Folder { Name = "Diagnostic" }; root.TagTableGroups.Add(group);
            var table = new Entry("Diag/Info", group.TagTables); table.Tags.Add(new object()); table.Tags.Add(new object()); group.TagTables.Add(table);
            using var session = new Session(root); var adapter = new UnifiedEngineeringAdapter(session);
            var preview = adapter.ManageUnifiedEngineeringObject("HMI_RT_1", category, name, "delete").Meta;
            Assert.Equal(false, preview["mayHaveChanged"]);
            Assert.NotNull(preview["before"]);
            if (category == "tagTables") Assert.Equal(2, preview["tagCount"]);
            Assert.Equal(1, session.Resolves); Assert.Equal(0, session.EditAccess);
            var applied = adapter.ManageUnifiedEngineeringObject("HMI_RT_1", category, name, "delete", dryRun: false).Meta;
            Assert.Equal(true, applied["verifiedAbsent"]);
            Assert.Equal(3, session.Resolves); Assert.Equal(1, session.EditAccess);
            if (category == "connections") Assert.Empty(root.Connections); else Assert.Empty(group.TagTables);
        }

        [Fact]
        public void Alarm_preview_checks_class_and_culture_before_creating_and_reads_texts_and_trigger_address()
        {
            var root = new Siemens.Engineering.HmiUnified.HmiSoftware();
            root.AlarmClasses.Add(new Entry("Errors", root.AlarmClasses));
            using var session = new Session(root); var adapter = new UnifiedEngineeringAdapter(session);
            const string properties = "{\"AlarmClass\":\"Errors\",\"Priority\":8,\"RaisedStateTag\":\"AlarmTrigger\",\"RaisedStateTagBitNumber\":2}";
            const string texts = "{\"EventText\":{\"en-US\":\"Alarm\",\"zh-CN\":\"报警\"}}";
            var previous = UnifiedRequestValues.Inputs;
            UnifiedRequestValues.Inputs = new() { [properties] = new HmiObject { ["AlarmClass"] = "Errors", ["Priority"] = 8,
                ["RaisedStateTag"] = "AlarmTrigger", ["RaisedStateTagBitNumber"] = 2 },
                [texts] = new HmiObject { ["EventText"] = new HmiObject { ["en-US"] = "Alarm", ["zh-CN"] = "报警" } } };
            try
            {
                adapter.ManageUnifiedEngineeringObject("HMI_RT_1", "discreteAlarms", "Alarm", "create", properties, textsJson: texts);
                Assert.Empty(root.DiscreteAlarms);
                var result = adapter.ManageUnifiedEngineeringObject("HMI_RT_1", "discreteAlarms", "Alarm", "create", properties, false, texts).Meta;
                var alarm = Assert.Single(root.DiscreteAlarms);
                Assert.Equal("Errors", alarm.AlarmClass); Assert.Equal("AlarmTrigger", alarm.RaisedStateTag); Assert.Equal(2u, alarm.RaisedStateTagBitNumber);
                var after = (HmiObject)result["after"]!;
                Assert.Equal("read-only address", ((HmiObject)after["values"]!)["TriggerBitAddress"]);
                Assert.Equal(2, ((HmiArray)((HmiObject)after["texts"]!)["EventText"]!).Count);
                Assert.Equal("<body><p>报警</p></body>", alarm.EventText.Items[1].Text);
                ((HmiObject)UnifiedRequestValues.Inputs[properties]!)["AlarmClass"] = "Missing";
                Assert.Throws<ArgumentException>(() => adapter.ManageUnifiedEngineeringObject("HMI_RT_1", "discreteAlarms", "Other", "create", properties));
                Assert.Single(root.DiscreteAlarms);
            }
            finally { UnifiedRequestValues.Inputs = previous; }
        }

        public sealed class Entry(string name, List<Entry> owner)
        {
            public string Name => name;
            public List<object> Tags { get; } = new();
            public void Delete() { Tags.Clear(); owner.Remove(this); }
        }
        public class Folder
        {
            public string Name { get; set; } = "";
            public List<Entry> TagTables { get; } = new();
            public List<Folder> Groups { get; } = new();
        }
        public sealed class Alarms : List<Alarm>
        {
            public Alarm Create(string name) { var alarm = new Alarm(name, this); Add(alarm); return alarm; }
            public Alarm? Find(string name) => this.SingleOrDefault(alarm => alarm.Name == name);
        }
        public sealed class Alarm(string name, Alarms owner)
        {
            public string Name => name;
            public string AlarmClass { get; set; } = "";
            public int Priority { get; set; }
            public string RaisedStateTag { get; set; } = "";
            public uint RaisedStateTagBitNumber { get; set; }
            public string TriggerBitAddress => "read-only address";
            public WriteRollbackTests.MultilingualText EventText { get; } = new();
            public void Delete() => owner.Remove(this);
        }
        [Theory]
        [InlineData("bad", "Tapped", "{}", "action")]
        [InlineData("read", "", "{}", "eventType")]
        [InlineData("update", "Tapped", "{}", "scriptProperties")]
        [InlineData("update", "Tapped", "[]", "scriptProperties")]
        public void Invalid_event_edit_names_the_argument_before_target_resolution(string action, string eventType, string properties, string parameter)
        {
            const string path = "[{\"property\":\"Screens\",\"name\":\"Main\"}]";
            var previous = UnifiedRequestValues.Inputs;
            UnifiedRequestValues.Inputs = new Dictionary<string, object?> {
                [path] = new HmiArray(new HmiObject { ["property"] = "Screens", ["name"] = "Main" }) };
            try
            {
                using var session = new Session(new Siemens.Engineering.HmiUnified.HmiSoftware());
                var error = Assert.Throws<ArgumentException>(() =>
                    new UnifiedEventsAdapter(session).ManageUnifiedEvent("HMI", path, eventType, action, scriptProperties: properties));
                Assert.Equal(parameter, error.ParamName);
                Assert.Equal(0, session.Resolves);
                Assert.Equal(0, session.EditAccess);
            }
            finally { UnifiedRequestValues.Inputs = previous; }
        }

        private sealed class Session(Siemens.Engineering.HmiUnified.HmiSoftware root) : IUnifiedSession, IDisposable
        {
            public int Resolves, EditAccess;
            public object CurrentProject => new WriteRollbackTests.AlarmProject();
            public object? HmiReadFault => null;
            public MigrationPages MigrationPages => throw new NotSupportedException();
            public object ExactUnifiedRoot(string path) => ResolveHmiSoftwareOrThrow(path);
            public object ExactPlantViews() => throw new NotSupportedException();
            public object ResolveHmiSoftwareOrThrow(string path) { Resolves++; return root; }
            public string UnifiedCollection(string category) => category switch {
                "connections" => "Connections", "tagTables" => "TagTables", "discreteAlarms" => "DiscreteAlarms",
                _ => throw new ArgumentException("Unknown fixture category.", nameof(category)) };
            public IDisposable AcquireHmiEditAccess() { EditAccess++; return this; }
            public void RecordHmiReadFault(HmiObject meta) => throw new NotSupportedException();
            public HmiMessage RunHmiStepTool(string tool, Func<HmiObject, string> action, bool requiresProject = true)
            {
                var meta = new HmiObject();
                return new HmiMessage { Message = EngineeringWriteJournal.Run(meta, () => action(meta)), Meta = meta };
            }
            public void Dispose() { }
        }
    }
}

namespace Siemens.Engineering.HmiUnified
{
    public sealed partial class HmiSoftware : TiaMcp.Engine.Tests.UnifiedEngineeringObjectTests.Folder
    {
        public List<TiaMcp.Engine.Tests.UnifiedEngineeringObjectTests.Entry> Connections { get; } = new();
        public List<TiaMcp.Engine.Tests.UnifiedEngineeringObjectTests.Entry> AlarmClasses { get; } = new();
        public List<TiaMcp.Engine.Tests.UnifiedEngineeringObjectTests.Folder> TagTableGroups { get; } = new();
        public TiaMcp.Engine.Tests.UnifiedEngineeringObjectTests.Alarms DiscreteAlarms { get; } = new();
    }
}
