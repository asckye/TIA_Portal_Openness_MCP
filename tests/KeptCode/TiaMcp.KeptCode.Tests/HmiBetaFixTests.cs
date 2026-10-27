using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests
{
    public sealed class HmiBetaFixTests
    {
        [Flags] public enum WindowFlag { None = 0, Title = 1, Close = 2 }
        [Flags] public enum VisibleAlarms : ulong { None = 0, Pending = 1, Acknowledged = 1UL << 63 }
        public enum PlainEnum { None = 0, Title = 1, Close = 2, Both = 3 }
        public sealed class Window
        {
            public WindowFlag Flags { get; set; }
            public VisibleAlarms Alarms { get; set; }
            public string Caption { get; set; } = "keep";
        }
        public sealed class IgnoredWindow { public WindowFlag Flags { get => WindowFlag.None; set { } } }

        [Fact]
        public void Flags_combinations_write_and_compare_the_combined_value()
        {
            var value = HmiScalarPolicy.ConvertValue(new HardwareScalar { Kind = "string", Text = "Title, Close" }, typeof(WindowFlag));
            Assert.Equal(WindowFlag.Title | WindowFlag.Close, value);
            Assert.False(Enum.IsDefined(typeof(WindowFlag), value!));
            var target = new Window();
            int writes = 0;
            HmiExchangePolicy.WriteScalars(target, new Dictionary<string, object?> {
                ["Flags"] = "Title, Close", ["Alarms"] = "Pending, Acknowledged" }, () => writes++);
            Assert.Equal(WindowFlag.Title | WindowFlag.Close, target.Flags);
            Assert.Equal(VisibleAlarms.Pending | VisibleAlarms.Acknowledged, target.Alarms);
            Assert.Equal(2, writes);
            Assert.True(HmiScalar.SameValue(target.Flags, value));
            Assert.Throws<InvalidOperationException>(() => HmiExchangePolicy.WriteScalars(new IgnoredWindow(),
                new Dictionary<string, object?> { ["Flags"] = "Title, Close" }, () => { }));
        }

        [Theory]
        [InlineData("3")]
        [InlineData("Title, 2")]
        [InlineData("Title, Missing")]
        [InlineData("Title,")]
        [InlineData("title, Close")]
        [InlineData("")]
        public void Invalid_flag_names_refuse_before_any_write(string text)
        {
            Assert.Throws<ArgumentException>(() => HmiScalarPolicy.ConvertValue(new HardwareScalar { Kind = "string", Text = text }, typeof(WindowFlag)));
            var target = new Window();
            int writes = 0;
            Assert.Throws<NotSupportedException>(() => HmiExchangePolicy.WriteScalars(target,
                new Dictionary<string, object?> { ["Caption"] = "changed", ["Flags"] = text }, () => writes++));
            Assert.Equal(0, writes);
            Assert.Equal("keep", target.Caption);
        }

        [Fact]
        public void Nonflags_combinations_are_refused_even_when_the_result_is_defined()
        {
            Assert.Throws<ArgumentException>(() => HmiScalarPolicy.ParseEnum(typeof(PlainEnum), "Title, Close"));
            Assert.Equal(PlainEnum.Both, HmiScalarPolicy.ParseEnum(typeof(PlainEnum), "Both"));
        }

        [Theory]
        [InlineData("HmiFaceplateContainer")]
        [InlineData("Base.HmiCustomWebControlContainer")]
        [InlineData("Siemens.Engineering.HmiUnified.UI.Base.HmiCustomWidgetContainer")]
        public void All_three_containers_require_a_verbatim_type_before_native_access(string itemType)
        {
            foreach (bool dryRun in new[] { true, false })
            {
                foreach (string value in new[] { "", " ", "bad\nvalue", new string('x', 4097) })
                {
                    var session = new HmiSession();
                    Assert.Throws<ArgumentException>(() => new UnifiedScreenItemsAdapter(session).ManageUnifiedScreenItem(
                        "HMI_RT_1", "/Overview", "create", "Container_1", itemType, dryRun: dryRun, containedType: value));
                    Assert.Equal(0, session.NativeCalls);
                }
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Other_types_refuse_containedType_before_native_access(bool dryRun)
        {
            var session = new HmiSession();
            Assert.Throws<ArgumentException>(() => new UnifiedScreenItemsAdapter(session).ManageUnifiedScreenItem(
                "HMI_RT_1", "/Overview", "create", "Button_1", "HmiButton", dryRun: dryRun, containedType: "verbatim"));
            Assert.Equal(0, session.NativeCalls);
        }

        [Fact]
        public void Base_namespace_admission_is_an_explicit_container_allow_list()
        {
            var types = UnifiedScreenItemLogic.ItemTypes(typeof(HmiBetaFixTests).Assembly);
            Assert.Contains(typeof(Siemens.Engineering.HmiUnified.UI.Base.HmiCustomWebControlContainer), types);
            Assert.Contains(typeof(Siemens.Engineering.HmiUnified.UI.Base.HmiCustomWidgetContainer), types);
            Assert.DoesNotContain(typeof(Siemens.Engineering.HmiUnified.UI.Base.HmiUnlistedItem), types);
            var schema = UnifiedScreenItemLogic.PropertySchema(typeof(Window).GetProperty("Flags")!, 0);
            Assert.Equal(true, schema["flags"]);
            Assert.Equal(false, UnifiedScreenItemLogic.PropertySchema(typeof(PlainProperty).GetProperty("Value")!, 0)["flags"]);
            var description = UnifiedScreenItemLogic.TypeDescription(typeof(Siemens.Engineering.HmiUnified.UI.Base.HmiCustomWidgetContainer));
            Assert.Equal("System.String", description["eventTypeType"]);
            Assert.Equal(true, description["eventTypesRequireBinding"]);
            Assert.Empty(Assert.IsType<HmiArray>(description["eventTypes"]));
        }
        public sealed class PlainProperty { public PlainEnum Value { get; set; } }

        [Theory]
        [InlineData("HmiFaceplateContainer")]
        [InlineData("HmiCustomWebControlContainer")]
        [InlineData("HmiCustomWidgetContainer")]
        public void Create_passes_containedType_unchanged_and_checks_the_native_readback(string itemType)
        {
            const string value = "  exact opaque type:1.2 {original}  ";
            var preview = new HmiSession();
            new UnifiedScreenItemsAdapter(preview).ManageUnifiedScreenItem(
                "HMI_RT_1", "/Overview", "create", "Container_1", itemType, dryRun: true, containedType: value);
            Assert.Empty(preview.Screen.ScreenItems);
            Assert.Empty(preview.Screen.ScreenItems.ContainedTypes);
            var session = new HmiSession();
            var reply = new UnifiedScreenItemsAdapter(session).ManageUnifiedScreenItem(
                "HMI_RT_1", "/Overview", "create", "Container_1", itemType, dryRun: false, containedType: value);
            Assert.Equal(new[] { value }, session.Screen.ScreenItems.ContainedTypes);
            Assert.Equal(value, reply.Meta["containedTypeReadback"]);
            var mismatch = new HmiSession();
            mismatch.Screen.ScreenItems.WrongReadback = true;
            Assert.Throws<InvalidOperationException>(() => new UnifiedScreenItemsAdapter(mismatch).ManageUnifiedScreenItem(
                "HMI_RT_1", "/Overview", "create", "Container_1", itemType, dryRun: false, containedType: value));
            Assert.Single(mismatch.Screen.ScreenItems);
        }

        [Theory]
        [InlineData("", "ExactTag")]
        [InlineData("ExactPLC", "")]
        [InlineData(" ", " ")]
        public void Missing_PLC_binding_is_refused_before_dispatch(string plcName, string plcTag)
        {
            int nativeCalls = 0;
            var request = new UnifiedHmiRequest { Operation = "EnsureUnifiedHmiTag", PlcName = plcName, PlcTag = plcTag };
            var service = new ProbeService(() => nativeCalls++);
            Assert.Throws<ArgumentException>(() => service.Call(request));
            Assert.Equal(0, nativeCalls);
            UnifiedHmiInputValidation.PlcBinding("ExactPLC", "DifferentTag");
        }

        private sealed class ProbeService(Action native) : TiaMcpServer.Siemens.Services.UnifiedHmiPortService(
            (_, _) => { native(); throw new InvalidOperationException("Must refuse before dispatch."); },
            () => { native(); return "project"; })
        {
            internal void Call(UnifiedHmiRequest request) => Invoke<object>(request);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Container_admission_also_refuses_before_host_dispatch(bool dryRun)
        {
            int nativeCalls = 0;
            var service = new ProbeService(() => nativeCalls++);
            foreach (var type in new[] { "HmiFaceplateContainer", "HmiCustomWebControlContainer", "HmiCustomWidgetContainer" })
                Assert.Throws<ArgumentException>(() => service.Call(new UnifiedHmiRequest {
                    Operation = "ManageUnifiedScreenItem", ScreenPath = "/Overview", Action = "create", ItemType = type, DryRun = dryRun }));
            Assert.Throws<ArgumentException>(() => service.Call(new UnifiedHmiRequest {
                Operation = "ManageUnifiedScreenItem", ScreenPath = "/Overview", Action = "create", ItemType = "HmiButton", ContainedType = "unexpected", DryRun = dryRun }));
            Assert.Equal(0, nativeCalls);
        }

        public class Named { public string Name { get; set; } = ""; }
        public sealed class Table : Named { public object[] Tags { get; set; } = []; }
        public sealed class Group : Named
        {
            public object[] TagTables { get; set; } = [];
            public object[] TagTableGroups { get; set; } = [];
            public object[] Groups { get; set; } = [];
            public object[] Folders { get; set; } = [];
        }
        public sealed class Classic { public Group TagFolder { get; set; } = new(); }

        [Fact]
        public void Unified_tables_walk_Groups_and_preserve_same_names_with_absolute_escaped_paths()
        {
            var root = new Group { TagTables = [new Table { Name = "Shared" }], TagTableGroups = [new Group {
                Name = "Group A", TagTables = [new Table { Name = "Shared" }], Groups = [new Group {
                    Name = "Sub", TagTables = [new Table { Name = "Shared" }] }] }] };
            Assert.Equal(new[] { "/Shared", "/Group%20A/Shared", "/Group%20A/Sub/Shared" }, HmiExchangePolicy.TagTables(root).Select(t => t.Path));
            Assert.Throws<PlcSoftwareException>(() => HmiExchangePolicy.TagTable(root, "Shared"));
            Assert.Same(root.TagTables[0], HmiExchangePolicy.TagTable(root, "/Shared"));
        }

        [Fact]
        public void Classic_without_table_lists_tags_in_every_table_including_user_folders()
        {
            var classic = new Classic { TagFolder = new Group { TagTables = [new Table { Name = "Root", Tags = [new Named { Name = "A" }] }],
                Folders = [new Group { Name = "Nested", TagTables = [new Table { Name = "Other", Tags = [new Named { Name = "B" }] }] }] } };
            Assert.Equal(new[] { "A", "B" }, HmiExchangePolicy.TagNames(classic, ""));
            Assert.Equal(new[] { "B" }, HmiExchangePolicy.TagNames(classic, "/Nested/Other"));
            Assert.Throws<PlcSoftwareException>(() => HmiExchangePolicy.TagNames(classic, "/Missing"));
        }

        [Fact]
        public void Lookup_and_listing_faults_cannot_turn_into_missing_or_empty_results()
        {
            int creates = 0;
            Assert.Throws<InvalidOperationException>(() => { if (HmiExchangePolicy.Find(new BrokenCollection(), "A") == null) creates++; });
            Assert.Equal(0, creates);
            Assert.Throws<InvalidOperationException>(() => HmiExchangePolicy.Names(new BrokenCollection(), [], "Tags"));
            Assert.Throws<TargetInvocationException>(() => HmiExchangePolicy.Names(new BrokenGetter(), ["Tags"], "Tags"));
            Assert.Throws<PlcSoftwareException>(() => HmiExchangePolicy.Find(new[] { new Named { Name = "A" }, new Named { Name = "a" } }, "A"));
        }
        public sealed class BrokenCollection : IEnumerable { public IEnumerator GetEnumerator() => throw new InvalidOperationException("native enumeration failed"); }
        public sealed class BrokenGetter { public object Tags => throw new InvalidOperationException("native getter failed"); }

        [Fact]
        public void Exact_screen_paths_refuse_ambiguity_and_never_scan_unrelated_groups()
        {
            var first = new Siemens.Engineering.HmiUnified.UI.Screens.HmiScreen { Name = "Overview" };
            var second = new Siemens.Engineering.HmiUnified.UI.Screens.HmiScreen { Name = "Overview" };
            var root = new ScreenGroup { Screens = [first], ScreenGroups = [new ScreenGroup { Name = "Nested", Screens = [second] }] };
            Assert.Same(first, HmiExactAccess.Screen(root, "/Overview"));
            Assert.Same(second, HmiExactAccess.Screen(root, "/Nested/Overview"));
            Assert.Throws<PlcSoftwareException>(() => HmiExactAccess.Screen(root, "Overview"));
            foreach (string path in new[] { "Nested/Overview", "Nested\\Overview" })
            {
                var error = Assert.Throws<PlcSoftwareException>(() => HmiExactAccess.Screen(root, path));
                Assert.Equal("InvalidParams", error.Code);
                Assert.Contains("/Nested/Overview", error.Message);
            }
        }

        [Theory]
        [InlineData("Group/Overview")]
        [InlineData("Group\\Overview")]
        [InlineData("/Group\\Overview")]
        public void Migration_screen_paths_refuse_with_a_correction_before_reading_the_root(string path)
        {
            var root = new UnreadableScreenRoot();
            var error = Assert.Throws<ArgumentException>(() => MigrationRead.Screen(root, path));
            Assert.Contains("/Group/Overview", error.Message);
            Assert.Equal(0, root.Reads);
        }
        public sealed class UnreadableScreenRoot
        {
            public int Reads;
            public object ScreenGroups { get { Reads++; throw new InvalidOperationException("Must validate first."); } }
        }
        public sealed class ScreenGroup : Named
        {
            public object[] Screens { get; set; } = [];
            public object[] ScreenGroups { get; set; } = [];
            public object[] Groups { get; set; } = [];
        }

        [Fact]
        public void Both_example_profiles_use_Unified_roots_real_types_and_bound_faceplate_values()
        {
            string root = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
            var profiles = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "reference/tool-examples/calls.json")))!["profiles"]!.AsObject();
            foreach (var profile in profiles)
            {
                var tools = profile.Value!.AsObject();
                foreach (var tool in tools)
                    Visit(tool.Value!);
                Assert.Equal("HmiButton", (string?)tools["DescribeUnifiedScreenItemType"]!["parameters"]!["itemType"]!["exampleValue"]);
                var example = tools["ManageUnifiedScreenItem"]!["operations"]!["createFaceplateContainer"]!;
                Assert.True((bool)example["requiresBinding"]!);
                Assert.True((bool)example["parameters"]!["containedType"]!["requiresBinding"]!);
                Assert.Equal("HmiFaceplateContainer", (string?)example["arguments"]!["itemType"]);
                Assert.Equal("/Overview", (string?)tools["GetUnifiedFaceplateInstance"]!["arguments"]!["screenPath"]);
                Assert.Equal("MotorFaceplate_1", (string?)tools["GetUnifiedFaceplateInstance"]!["arguments"]!["itemName"]);
            }
            void Visit(JsonNode node)
            {
                if (node is JsonObject fields)
                    foreach (var field in fields)
                    {
                        if (field.Key == "objectPath" && field.Value is JsonArray steps && steps.Count > 0)
                            Assert.NotEqual("ScreenFolder", (string?)steps[0]?["property"]);
                        if (field.Value != null) Visit(field.Value);
                    }
                else if (node is JsonArray values) foreach (var value in values) if (value != null) Visit(value);
            }
        }
    }
}

namespace TiaMcp.Adapters.Native.Hmi
{
    // All native access in the screen-item adapter passes through this counted session.
    internal sealed class HmiSession
    {
        internal int NativeCalls;
        internal Siemens.Engineering.HmiUnified.UI.Screens.HmiScreen Screen { get; } = new() { Name = "Overview" };
        internal Assembly UnifiedAssembly => typeof(HmiSession).Assembly;
        internal HmiMessage RunHmiStepTool(string tool, Func<HmiObject, string> action, bool requiresProject = true)
        {
            var meta = new HmiObject();
            UnifiedHmiAccess.Inputs = new() { ["{}"] = new UnifiedHmiInput { Kind = "object" } };
            try { return new HmiMessage { Message = action(meta), Meta = meta }; }
            finally { UnifiedHmiAccess.Inputs = null; }
        }
        internal IDisposable AcquireHmiEditAccess() { NativeCalls++; return new Access(); }
        internal object ExactUnifiedRoot(string path) { NativeCalls++; return new Root(Screen); }
        internal HmiObject? ValidateUnifiedProperties(Type type, string properties) => null;
        private sealed class Access : IDisposable { public void Dispose() { } }
        private sealed class Root(object screen) { public object[] Screens { get; } = [screen]; }
    }
}

namespace Siemens.Engineering.HmiUnified.UI.Base
{
    public class HmiScreenItemBase { public string Name { get; set; } = ""; }
    public class HmiContainerBase : HmiScreenItemBase { public string ContainedType { get; set; } = ""; }
    public sealed class StringEvents { public object Create(string eventType) => throw new InvalidOperationException("Metadata only."); }
    public sealed class HmiCustomWebControlContainer : HmiContainerBase { public StringEvents EventHandlers { get; } = new(); }
    public sealed class HmiCustomWidgetContainer : HmiContainerBase { public StringEvents EventHandlers { get; } = new(); }
    public sealed class HmiUnlistedItem : HmiScreenItemBase { }
}
namespace Siemens.Engineering.HmiUnified.UI.Controls { public sealed class HmiFaceplateContainer : Base.HmiContainerBase { } }
namespace Siemens.Engineering.HmiUnified.UI.Widgets { public sealed class HmiButton : Base.HmiScreenItemBase { } }
namespace Siemens.Engineering.HmiUnified.UI.Screens
{
    public sealed class HmiScreen { public string Name { get; set; } = ""; public Items ScreenItems { get; } = new(); }
    public sealed class Items : List<Base.HmiScreenItemBase>
    {
        public bool WrongReadback;
        public List<string> ContainedTypes { get; } = [];
        public T Create<T>(string name) where T : Base.HmiScreenItemBase, new() { var item = new T { Name = name }; Add(item); return item; }
        public T Create<T>(string name, string containedType) where T : Base.HmiScreenItemBase, new()
        {
            ContainedTypes.Add(containedType);
            var item = Create<T>(name);
            ((Base.HmiContainerBase)(object)item).ContainedType = WrongReadback ? "changed" : containedType;
            return item;
        }
    }
}
