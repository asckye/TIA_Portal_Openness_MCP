#if TIA_SHARED_ADAPTER_PATHS
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using Newtonsoft.Json;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Contracts.Rpc;
using TiaOpenness.Core.Abstractions;
using TiaOpenness.Core.Adapters;
using TiaOpenness.Core.Inspection;
using TiaOpenness.Core.Rpc;
using Xunit;
using A = TiaMcp.Adapters.Contracts;
using D = TiaMcp.Adapters.Contracts.Studio;

namespace TiaOpenness.Core.Tests;

public sealed class AdapterTiaSessionTests
{
    private static MethodInfo Method(string surface, string name) =>
        (surface == "session" ? typeof(ITiaSession) : typeof(IVersionControl)).GetMethods()
        .Concat(surface == "session" ? typeof(IDisposable).GetMethods() : Array.Empty<MethodInfo>()).Single(m => m.Name == name);

    public static IEnumerable<object[]> Methods()
    {
        foreach (var surface in new[] { "session", "vci" })
        foreach (var method in (surface == "session" ? typeof(ITiaSession) : typeof(IVersionControl)).GetMethods()
            .Concat(surface == "session" ? typeof(IDisposable).GetMethods() : Array.Empty<MethodInfo>())
            .Where(m => m.Name != "Inspect" && m.Name != "get_VersionControl"))
        foreach (bool populated in new[] { false, true })
            yield return new object[] { surface, method.Name, populated };
    }

    [Theory]
    [MemberData(nameof(Methods))]
    public void Every_method_preserves_legacy_DTOs_arguments_order_and_synchronous_progress(string surface, string name, bool populated)
    {
        var oldState = new FakeState { Populated = populated };
        var newState = new FakeState { Populated = populated };
        var legacy = FakeSurface.Create<ITiaSession>(oldState);
        var current = new AdapterTiaSession(FakeSurface.Create<A.IOpennessAdapter>(newState));
        object oldTarget = surface == "session" ? legacy : legacy.VersionControl;
        object newTarget = surface == "session" ? current : current.VersionControl;
        oldState.Calls.Clear();
        newState.Calls.Clear();
        var method = Method(surface, name);
        var oldProgress = new List<string>();
        var newProgress = new List<string>();
        var owner = System.Environment.CurrentManagedThreadId;
        object?[] Arguments(List<string> progress) => method.GetParameters().Select(p =>
        {
            if (p.ParameterType == typeof(ProgressCallback)) return !populated ? null : (object)new ProgressCallback((operation, count, total, message) =>
            {
                Assert.Equal(owner, System.Environment.CurrentManagedThreadId);
                Assert.False(newState.Returned && progress == newProgress);
                progress.Add(BridgeJson.Serialize(new object[] { operation, count, total, message }));
            });
            if (p.ParameterType == typeof(bool)) return populated;
            if (p.ParameterType.IsEnum) return Enum.ToObject(p.ParameterType, populated ? 1 : 0);
            if (p.ParameterType == typeof(string)) return populated ? p.Name + " /轴\\\"\r\n" : null;
            if (p.ParameterType == typeof(IReadOnlyList<string>)) return populated ? new[] { "Z/轴", null, "", "a" } : null;
            throw new InvalidOperationException(p.ToString());
        }).ToArray();
        var arguments = Arguments(newProgress);
        var expected = method.Invoke(oldTarget, Arguments(oldProgress));
        var actual = method.Invoke(newTarget, arguments);
        Assert.Equal(BridgeJson.Serialize(expected), BridgeJson.Serialize(actual));
        Assert.Equal(oldState.Calls, newState.Calls);
        Assert.Single(newState.Calls);
        Assert.Equal(oldProgress, newProgress);
        // Lists are passed straight to the native profile, without enumeration or copying.
        for (int i = 0; i < arguments.Length; i++)
            if (arguments[i] is IReadOnlyList<string>) Assert.Same(arguments[i], newState.LastArguments![i]);
    }

    [Theory]
    [MemberData(nameof(Methods))]
    public void Every_method_preserves_the_original_exception_without_retry(string surface, string name, bool populated)
    {
        var state = new FakeState { Populated = populated };
        var current = new AdapterTiaSession(FakeSurface.Create<A.IOpennessAdapter>(state));
        object target = surface == "session" ? current : current.VersionControl;
        state.Calls.Clear();
        state.Failure = new InvalidOperationException("original native failure /轴");
        var method = Method(surface, name);
        var args = method.GetParameters().Select(p => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
        var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(target, args));
        Assert.Same(state.Failure, error.InnerException);
        Assert.Single(state.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Inspection_matches_legacy_PLC_resolution_and_skips_non_PLC_blocks(bool plc)
    {
        var state = new FakeState { PlcDeviceId = plc ? "Canonical PLC" : null };
        using var current = new AdapterTiaSession(FakeSurface.Create<A.IOpennessAdapter>(state));
        state.Calls.Clear();
        var options = new InspectionOptions { BlockNamePattern = "^FB_", FindUnusedBlocks = true };
        var actual = current.Inspect("alias", options);
        // Frozen legacy Inspect policy: TryGetPlc, ListBlocks(alias, false), then the
        // shared rules with the canonical ID and unknown cross references.
        var expected = plc ? InspectionEngine.Run("Canonical PLC", (IReadOnlyList<BlockInfo>)Sample(typeof(IReadOnlyList<BlockInfo>), true)!, options, null)
            : new InspectionReport { DeviceId = "alias" };
        expected.TimestampUtc = actual.TimestampUtc;
        Assert.Equal(BridgeJson.Serialize(expected), BridgeJson.Serialize(actual));
        Assert.Equal(plc ? new[] { "FindPlcDeviceId:[\"alias\"]", "ListBlocks:[\"alias\",false]" }
            : new[] { "FindPlcDeviceId:[\"alias\"]" }, state.Calls);
        Assert.DoesNotContain(actual.Findings, f => f.RuleId == "DEAD-001");
    }

    [Fact]
    public void Inspection_lookup_failure_does_not_list_blocks()
    {
        var state = new FakeState();
        var current = new AdapterTiaSession(FakeSurface.Create<A.IOpennessAdapter>(state));
        state.Calls.Clear();
        state.Failure = new KeyNotFoundException("unknown device");
        Assert.Same(state.Failure, Assert.Throws<KeyNotFoundException>(() => current.Inspect("missing", null)));
        Assert.Single(state.Calls);
    }

    [Fact]
    public void Version_control_tracks_the_current_project_capability()
    {
        var state = new FakeState { VciAvailable = false };
        using var current = new AdapterTiaSession(FakeSurface.Create<A.IOpennessAdapter>(state));
        Assert.Null(current.VersionControl);
        state.VciAvailable = true;
        Assert.NotNull(current.VersionControl);
        current.CloseProject();
        state.VciAvailable = false;
        Assert.Null(current.VersionControl);
        current.OpenProject("next.ap21");
        state.VciAvailable = true;
        Assert.NotNull(current.VersionControl);
    }

    [Fact]
    public void Callback_exception_is_not_wrapped_or_retried()
    {
        var state = new FakeState();
        using var current = new AdapterTiaSession(FakeSurface.Create<A.IOpennessAdapter>(state));
        state.Calls.Clear();
        var failure = new IOException("progress observer failed");
        Assert.Same(failure, Assert.Throws<IOException>(() => current.ExportBlocks("PLC", null, "out", ExportFormat.Source, true,
            (op, count, total, text) => throw failure)));
        Assert.Single(state.Calls);
        Assert.False(state.Returned);
    }

    public static IEnumerable<object[]> Dtos() => typeof(D.SessionState).Assembly.GetExportedTypes()
        .Where(t => t.Namespace == typeof(D.SessionState).Namespace && t.IsClass && t != typeof(D.ProgressCallback))
        .Select(t => new object[] { t.Name });

    [Theory]
    [InlineData("Abstractions/SessionFactoryLoader.cs", 2)]
    [InlineData("Rpc/BridgeChannel.cs", 1)]
    public void Switched_loading_sources_only_differ_inside_the_explicit_build_switch(string relative, int branches)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "AGENTS.md"))) root = root.Parent;
        Assert.NotNull(root);
        var core = Path.Combine(root!.FullName, "src/Studio/Core");
        var original = File.ReadAllText(Path.Combine(core, relative)).Replace("\r\n", "\n");
        var switched = File.ReadAllLines(Path.Combine(core, "Adapters", Path.GetFileName(relative)));
        var retained = new List<string>();
        bool include = true;
        int count = 0;
        foreach (var line in switched)
        {
            if (line == "#if TIA_SHARED_ADAPTER_PATHS") { include = false; count++; }
            else if (line == "#else" || line == "#endif") include = true;
            else if (include) retained.Add(line);
        }
        Assert.Equal(branches, count);
        Assert.Equal(original, string.Join("\n", retained) + "\n");
    }

    [Theory]
    [MemberData(nameof(Dtos))]
    public void Mapping_covers_every_DTO_and_preserves_all_fields_nulls_lists_and_enums(string name)
    {
        var type = typeof(D.SessionState).Assembly.GetType(typeof(D.SessionState).Namespace + "." + name)!;
        var map = typeof(StudioDtoMap).GetMethod("Map", BindingFlags.Static | BindingFlags.NonPublic, new[] { type })!;
        Assert.NotNull(map);
        void Equal(object? value) => Assert.Equal(BridgeJson.Serialize(value), BridgeJson.Serialize(map.Invoke(null, new[] { value })));
        Equal(null);
        Equal(Activator.CreateInstance(type));
        Equal(Sample(type, true));
        foreach (var property in type.GetProperties().Where(p => p.CanWrite))
        {
            var value = Sample(type, true)!;
            if (!property.PropertyType.IsValueType || Nullable.GetUnderlyingType(property.PropertyType) != null)
            {
                property.SetValue(value, null);
                Equal(value);
            }
            if (property.PropertyType.IsEnum)
                foreach (var enumValue in Enum.GetValues(property.PropertyType))
                {
                    property.SetValue(value, enumValue);
                    Equal(value);
                }
            if (property.GetValue(Sample(type, true)) is IList)
            {
                var list = (IList)Activator.CreateInstance(property.PropertyType)!;
                list.Add(null);
                property.SetValue(value, list);
                Equal(value);
            }
        }
    }

    [Theory]
    [InlineData("14sp1")]
    [InlineData("15.1")]
    [InlineData("16")]
    [InlineData("17")]
    [InlineData("18")]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public void Loader_and_hello_use_the_same_selected_adapter_and_mock_uses_Core(string key)
    {
        var root = Path.Combine(Path.GetTempPath(), "studio-adapter-path-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = SessionFactoryLoader.AdapterPath(root, key);
            Assert.Equal(Path.Combine(root, "adapters", "v" + key, "TiaMcp.Adapter." + key + ".dll"), path);
            Assert.Equal(Path.Combine(root, "TiaOpenness.Core.dll"), BridgeChannel.AdapterPath(root, key, false));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "fixture");
            Assert.Equal(path, BridgeChannel.AdapterPath(root, key, false));
            Assert.Equal(Path.Combine(root, "TiaOpenness.Core.dll"), BridgeChannel.AdapterPath(root, key, true));
            var factory = new AdapterSessionFactory(path, key);
            Assert.Throws<InvalidOperationException>(() => factory.Create());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static object? Sample(Type type, bool populated)
    {
        if (type == typeof(void)) return null;
        if (type == typeof(bool)) return populated;
        if (type.IsEnum) return Enum.ToObject(type, populated ? 1 : 0);
        if (!populated) return type.IsGenericType ? null : Activator.CreateInstance(type);
        var legacyType = type.IsGenericType ? typeof(IReadOnlyList<>).MakeGenericType(typeof(SessionState).Assembly.GetType(
            typeof(SessionState).Namespace + "." + type.GetGenericArguments()[0].Name)!)
            : typeof(SessionState).Assembly.GetType(typeof(SessionState).Namespace + "." + type.Name)!;
        return JsonConvert.DeserializeObject(BridgeJsonGoldenTests.OldJson(BridgeJsonGoldenTests.Sample(legacyType)), type, Legacy.BridgeJson.Settings);
    }

    public sealed class FakeState
    {
        public readonly List<string> Calls = new();
        public object?[]? LastArguments;
        public bool Populated = true;
        public bool Returned;
        public bool VciAvailable = true;
        public string? PlcDeviceId = "PLC";
        public Exception? Failure;
    }

    // Both contract profiles receive the same frozen legacy DTO samples. No production
    // mapper is used to create expected results or the adapter fake's inputs.
    public class FakeSurface : DispatchProxy
    {
        private FakeState state = null!;
        public static T Create<T>(FakeState state) where T : class
        {
            var result = Create<T, FakeSurface>();
            ((FakeSurface)(object)result).state = state;
            return result;
        }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var method = targetMethod!;
            var type = method.ReturnType;
            if (type == typeof(A.IStudioSession)) return Create<A.IStudioSession>(state);
            if (type == typeof(A.IHardware)) return Create<A.IHardware>(state);
            if (type == typeof(A.IVersionControl)) return state.VciAvailable ? Create<A.IVersionControl>(state) : null;
            if (type == typeof(IVersionControl)) return state.VciAvailable ? Create<IVersionControl>(state) : null;
            state.Returned = false;
            state.LastArguments = args;
            state.Calls.Add(method.Name + ":" + BridgeJson.Serialize(args!.Where(a => a is not Delegate).ToArray()));
            if (state.Failure != null) ExceptionDispatchInfo.Capture(state.Failure).Throw();
            foreach (var callback in args!.OfType<Delegate>())
            {
                try
                {
                    callback.DynamicInvoke("export", 0, 2, null);
                    callback.DynamicInvoke("export", 1, 2, "轴 <>&\"\r\n");
                }
                catch (TargetInvocationException ex) { ExceptionDispatchInfo.Capture(ex.InnerException!).Throw(); }
            }
            state.Returned = true;
            return method.Name == "FindPlcDeviceId" ? state.PlcDeviceId : Sample(type, state.Populated);
        }
    }
}
#endif
