using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.Siemens.Services;
using Xunit;

namespace TiaMcp.Engine.Tests;

public sealed class HmiB3Tests
{
    public sealed class InterfaceItem
    {
        public string PropertyName => "Speed";
        private object? value = (short)12;
        public bool Ignore;
        public object? Value { get => value; set { if (!Ignore) this.value = value; } }
        public object[] Dynamizations { get; } = [new object()];
        public object[] PropertyEventHandlers { get; } = [new object()];
    }
    public sealed class Container { public InterfaceItem[] Interface { get; } = [new()]; }

    private static object Resolve(object root, string path)
    {
        var previous = UnifiedRequestValues.Decoder;
        UnifiedRequestValues.Decoder = text => { using var doc = JsonDocument.Parse(text); return UnifiedInputCodec.Decode(doc.RootElement); };
        try { return UnifiedObjectAddress.Resolve(root, path); }
        finally { UnifiedRequestValues.Decoder = previous; }
    }

    [Theory]
    [InlineData("Owner")]
    [InlineData("propertyName")]
    [InlineData("")]
    public void Invalid_selector_keys_are_refused_before_getters(string key)
    {
        var path = JsonSerializer.Serialize(new[] { new { property = "Interface", name = "Speed", key } });
        Assert.Throws<ArgumentException>(() => Resolve(new Unreadable(), path));
        Assert.ThrowsAny<Exception>(() => V4Json.Deserialize<PropertyStep[]>(path));
    }
    public sealed class Unreadable { public object Interface => throw new InvalidOperationException("Native getter must not run."); }

    [Fact]
    public void PropertyName_selector_reaches_interface_value_dynamizations_and_events()
    {
        var root = new Container();
        const string path = "[{\"property\":\"Interface\",\"key\":\"PropertyName\",\"name\":\"Speed\"}]";
        Assert.Same(root.Interface[0], Resolve(root, path));
        Assert.Same(root.Interface[0].Dynamizations, Resolve(root, path[..^1] + ",{\"property\":\"Dynamizations\"}]"));
        Assert.Same(root.Interface[0].PropertyEventHandlers, Resolve(root, path[..^1] + ",{\"property\":\"PropertyEventHandlers\"}]"));
        Assert.Equal("PropertyName", V4Json.Deserialize<PropertyStep[]>(path)[0].Key);
        Assert.Contains("PropertyName", V4Json.Serialize(V4Json.Deserialize<PropertyStep[]>(path)));
    }

    [Fact]
    public void Interface_values_keep_the_current_runtime_type_and_verify_readback()
    {
        var item = new InterfaceItem();
        var edits = UnifiedUiModelLogic.PrepareNested(item.GetType(), new HmiObject { ["Value"] = 42 }, target: item);
        Assert.IsType<short>(edits.Single().Value);
        UnifiedUiModelLogic.ApplyNested(item, edits, new HmiObject());
        Assert.Equal((short)42, Assert.IsType<short>(item.Value));
        item.Ignore = true;
        var mismatch = UnifiedUiModelLogic.PrepareNested(item.GetType(), new HmiObject { ["Value"] = 43 }, target: item);
        Assert.Throws<InvalidOperationException>(() => UnifiedUiModelLogic.ApplyNested(item, mismatch, new HmiObject()));
        var evidence = new HmiObject();
        var error = Assert.Throws<ArgumentException>(() => UnifiedUiModelLogic.PrepareNested(item.GetType(), new HmiObject { ["Value"] = "wrong" }, target: item, evidence: evidence));
        Assert.Contains("System.Int16", error.Message);
        Assert.Equal(error.Message, evidence["conversionRefusal"]);
        item.Ignore = false; item.Value = null;
        Assert.Contains("current Value is null", Assert.Throws<ArgumentException>(() => UnifiedUiModelLogic.PrepareNested(item.GetType(), new HmiObject { ["Value"] = 1 }, target: item)).Message);
        item.Value = new object();
        Assert.Contains("System.Object", Assert.Throws<ArgumentException>(() => UnifiedUiModelLogic.PrepareNested(item.GetType(), new HmiObject { ["Value"] = 1 }, target: item)).Message);
    }

    public sealed class Geometry { public int Left { get; set; } = 4; }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Each_try_set_result_and_readback_is_retained(bool returned, bool assigned)
    {
        var target = new Geometry();
        var row = UnifiedUiModelLogic.VerifiedWrite(target, "Left", 7, () => { if (assigned) target.Left = 7; return returned; });
        Assert.Equal(returned, row["trySetResult"]);
        Assert.Equal(target.Left, row["readback"]);
        Assert.Equal(returned && assigned, row["verified"]);
    }

    [Theory]
    [InlineData("GetUnifiedAlarmCommon")]
    [InlineData("GetUnifiedAuditSettings")]
    [InlineData("ListUnifiedEngineeringObjects")]
    [InlineData("ManageUnifiedEngineeringObject")]
    [InlineData("ManageUnifiedListEntries")]
    public void Invalid_categories_refuse_before_host_dispatch(string operation)
    {
        int native = 0;
        var service = new ProbeService(() => native++);
        Assert.Throws<ArgumentException>(() => service.Call(new UnifiedRequest { Operation = operation, Category = "AlarmClasses", Action = "read", Limit = 10 }));
        Assert.Equal(0, native);
    }
    private sealed class ProbeService(Action native) : UnifiedPortService((_, _) => { native(); throw new Exception("Must validate first."); }, () => { native(); return "project"; })
    { internal void Call(UnifiedRequest request) => Invoke(request, true); }

    public sealed class UserGroup
    {
        public string Name { get; set; } = "";
        public UserGroups Groups { get; } = new();
        public object[] Screens { get; } = [];
        internal UserGroups? Owner;
        public void Delete() => Owner!.Remove(this);
    }
    public sealed class UserGroups : List<UserGroup>
    {
        public UserGroup Create(string name) { var group = new UserGroup { Name = name, Owner = this }; Add(group); return group; }
    }
    [Fact]
    public void Escaped_group_segments_remain_exact_during_create_rename_and_empty_delete()
    {
        Assert.Throws<ArgumentException>(() => EngineeringInputValidation.HmiGroupPath("/" + string.Join("/", Enumerable.Repeat("Group", 65))));
        Assert.Equal(64, EngineeringInputValidation.HmiGroupPath("/" + string.Join("/", Enumerable.Repeat("Group", 64))).Length);
        Assert.Throws<ArgumentException>(() => EngineeringInputValidation.HmiGroupPath("/Group/%2E%2E"));
        foreach (var name in new[] { ".", ".." })
            Assert.Throws<ArgumentException>(() => UnifiedHmiInputValidation.Request(new UnifiedHmiRequest { Operation = "ManageUnifiedHmiGroup", Family = "screens", GroupPath = "/Group", Action = "rename", NewName = name }));
        var root = new UserGroup();
        string[] Parts(string value) => EngineeringInputValidation.HmiGroupPath(value, rootAllowed: true);
        var created = TiaMcp.Adapters.Native.Plc.PlcGroupOperations.Manage(root, "/A%2FB/Sub", "create", "", false, "Screens", pathParts: Parts);
        Assert.Equal(2, created["createdCount"]);
        Assert.Equal("/A%2FB/Sub", created["groupPath"]);
        Assert.Equal(new[] { "/A%2FB", "/A%2FB/Sub" }, ((IEnumerable<object?>)created["createdPaths"]!).Cast<string>());
        var parent = Assert.Single(root.Groups);
        Assert.Equal("A/B", parent.Name);
        Assert.Equal("Sub", Assert.Single(parent.Groups).Name);
        TiaMcp.Adapters.Native.Plc.PlcGroupOperations.Manage(root, "/A%2FB/Sub", "rename", "Changed", false, "Screens", pathParts: Parts);
        Assert.Equal("Changed", Assert.Single(parent.Groups).Name);
        TiaMcp.Adapters.Native.Plc.PlcGroupOperations.Manage(root, "/A%2FB/Changed", "deleteEmpty", "", false, "Screens", pathParts: Parts);
        Assert.Empty(parent.Groups);
        Assert.Single(root.Groups);
    }

    [Theory]
    [InlineData("Group/Sub")]
    [InlineData("Group\\Sub")]
    public void Group_paths_refuse_before_dispatch_with_the_corrected_form(string path)
    {
        var error = Assert.Throws<ArgumentException>(() => UnifiedHmiInputValidation.Request(new UnifiedHmiRequest { Operation = "ManageUnifiedHmiGroup", Family = "screens", GroupPath = path, Action = "create" }));
        Assert.Contains("/Group/Sub", error.Message);
    }
}
