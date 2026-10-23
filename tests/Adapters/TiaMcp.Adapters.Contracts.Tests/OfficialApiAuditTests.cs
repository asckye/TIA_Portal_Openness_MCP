using System.Reflection;
using System.Reflection.Emit;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Libraries;
using TiaMcp.Adapters.Reflection;
using TiaOpenness.Shared;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests
{
    public sealed class OfficialApiAuditTests
    {
        private static OpennessReflection Reflection(object target)
            => new(new ReflectionSession((_, _, _, _) => target, () => null));
        private static HardwareScalar Text(string value) => new() { Kind = "string", Text = value };

        [Fact]
        public void Overloads_bind_by_convertibility_and_skip_open_generics()
        {
            var target = new Overloads();
            var api = Reflection(target);
            var result = api.InvokeObject("project", "", "Select", new[] { Text("true") }, allowWrite: true);
            Assert.Equal("bool", result.Value);
            Assert.Equal(true, result.Meta["invocationAttempted"]);
            Assert.Equal(1, target.Calls);
            result = api.InvokeObject("project", "", "OnlyGeneric", new[] { Text("value") }, allowWrite: true);
            Assert.Equal(false, result.Meta["success"]);
            Assert.False(result.Meta.TryGetValue("invocationAttempted", out var attempted) && Equals(true, attempted));
            Assert.Equal(1, target.Calls);
        }

        [Theory]
        [InlineData("Ambiguous", "42")]
        [InlineData("Select", "unconvertible")]
        [InlineData("NeedsObject", "not-an-object")]
        public void Ambiguous_and_unconvertible_methods_do_not_attempt_invocation(string method, string value)
        {
            var target = new Overloads();
            var result = Reflection(target).InvokeObject("project", "", method, new[] { Text(value) }, allowWrite: true);
            Assert.Equal(false, result.Meta["success"]);
            Assert.False(result.Meta.TryGetValue("invocationAttempted", out var attempted) && Equals(true, attempted));
            Assert.Equal(0, target.Calls);
        }

        [Fact]
        public void Null_cannot_bind_nonnullable_value_parameters()
        {
            var target = new Overloads();
            var result = Reflection(target).InvokeObject("project", "", "Select", new[] { new HardwareScalar { Kind = "null" } }, allowWrite: true);
            Assert.Equal(false, result.Meta["success"]);
            Assert.Equal(0, target.Calls);
        }

        [Fact]
        public void An_exception_inside_the_bound_method_records_a_real_attempt()
        {
            var target = new Overloads();
            var result = Reflection(target).InvokeObject("project", "", "Fail", allowWrite: true);
            Assert.Equal(false, result.Meta["success"]);
            Assert.Equal(true, result.Meta["invocationAttempted"]);
            Assert.Equal("native-sentinel", result.Message);
            Assert.Equal(1, target.Calls);
        }

        [Theory]
        [InlineData("Siemens.Engineering.Safety.SafetyAdministration", "SetSafetyOfflineProgramPassword", "ManagePlcSafety")]
        [InlineData("Siemens.Engineering.Safety.RuntimeGroup", "SetAttribute", "ManagePlcSafety")]
        [InlineData("Siemens.Engineering.Safety.GlobalSettings", "SafetyModificationsPossible", "ManageSafetyGlobalSettings")]
        [InlineData("Siemens.Engineering.SafetyValidation.TraceConfiguration", "set_IsTraced", "ManageSafetyFunction")]
        [InlineData("Siemens.Engineering.HW.Features.PlcAccessLevelProvider", "SetPassword", "ManagePlcProtection")]
        [InlineData("Siemens.Engineering.HW.Features.PlcMasterSecretConfigurator", "ProtectAllConfiguration", "ManagePlcProtection")]
        [InlineData("Siemens.Engineering.Umac.UmacConfigurator", "Activate", "ManageProjectUserManagement")]
        [InlineData("Siemens.Engineering.SW.Blocks.PlcBlockProtectionProvider", "Protect", "ManagePlcBlockProtection")]
        [InlineData("Siemens.Engineering.HmiUnified.Library.ScriptModuleType", "SetAttribute", "ManageLibraryType")]
        public void Guarded_write_surfaces_point_to_the_dedicated_tool(string type, string method, string tool)
        {
            Assert.Contains(tool, OpennessReflectionPolicy.GuardedWriteRefusal(type, method, 1));
            Assert.Null(OpennessReflectionPolicy.GuardedWriteRefusal(type, "GetAttribute", 1));
        }

        [Fact]
        public void InvokeService_refuses_a_guarded_service_without_entering_its_method()
        {
            var type = ServiceTypes.Safety;
            var target = Activator.CreateInstance(type)!;
            var api = Reflection(new ServiceOwner(target));
            var result = api.InvokeService("deviceitem", "CPU", type.FullName!, "SetSafetyOfflineProgramPassword", new[] { Text("secret") }, allowWrite: true);
            Assert.Equal(false, result.Meta["success"]);
            Assert.Contains("ManagePlcSafety", result.Message);
            Assert.Contains("ManagePlcSafety", (string)result.Meta["policyRefusal"]!);
            Assert.Equal(0, type.GetField("Calls")!.GetValue(target));
            Assert.False(result.Meta.TryGetValue("invocationAttempted", out var attempted) && Equals(true, attempted));
            result = Reflection(target).InvokeObject("deviceitem", "CPU", "SetSafetyOfflineProgramPassword", new[] { Text("secret") }, allowWrite: true);
            Assert.Equal(false, result.Meta["success"]);
            Assert.Equal(0, type.GetField("Calls")!.GetValue(target));
        }

        [Fact]
        public void Service_resolution_rejects_ambiguous_suffixes_and_ignores_foreign_assemblies()
        {
            var session = new ReflectionSession((_, _, _, _) => null, () => null);
            Assert.NotNull(ServiceTypes.First);
            Assert.Throws<PlcSoftwareException>(() => session.FindTypeBySuffix("P881fUniqueProvider"));
            Assert.Equal(ServiceTypes.First, session.FindTypeBySuffix(ServiceTypes.First.FullName!));
            Assert.Null(session.FindTypeBySuffix(nameof(ForeignOnlyService)));
        }

        [Fact]
        public void Classic_tag_tables_are_found_through_nested_folders_and_tags_stay_reachable()
        {
            var tag = new Named("Start");
            var table = new Table { Name = "Nested", Tags = new[] { tag } };
            var hmi = new Siemens.Engineering.Hmi.HmiTarget { TagFolder = new Folder {
                Folders = new[] { new Folder { Folders = new[] { new Folder { TagTables = new[] { table } } } } } } };
            var found = ReflectionScreenTraversal.FindTagTable(hmi, "Nested");
            Assert.Same(table, found);
            Assert.Same(tag, ((Table)found!).Tags.Single());
            Assert.Null(ReflectionScreenTraversal.FindTagTable(hmi, "missing"));
            Assert.Throws<NotSupportedException>(() => ReflectionScreenTraversal.RequireScreenItemsApi(hmi));
            Assert.Same(table, ReflectionScreenTraversal.FindTagTable(new { TagTables = new[] { table } }, "Nested"));
        }

        [Fact]
        public void Existing_screen_item_name_never_becomes_master_copy_import_evidence()
        {
            var result = LibraryPolicy.RefuseScreenItemMasterCopyImport("library.al21", "ExistingButton", "HMI", "Main", "ExistingButton");
            Assert.Equal(false, result.Ok);
            Assert.Contains("no Openness API for screen-item instantiation from a master copy", result.Error);
            Assert.Empty(result.Attempts!);
            Assert.Empty(result.ReadbackItems!);
            var reply = new LibraryReply { Import = result };
            Assert.False(reply.MayHaveChanged);
            Assert.False(reply.RequiresSessionReset);
        }

        [Fact]
        public void Master_copy_screen_entry_has_no_heuristic_invocation_or_existing_item_write_path()
        {
            var root = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
            var source = File.ReadAllText(Path.Combine(root, "src/Adapters/Native/Libraries/LibraryService.cs"));
            foreach (var forbidden in new[] { "TryImportMasterCopyIntoScreen", "TryImportMasterCopyViaExtensionMethods",
                "IsMasterCopyImportMethodName", "TryBuildMasterCopyImportArgs", "ResolveImportedReadbackName", "LibraryTrySetProperty" })
                Assert.DoesNotContain(forbidden, source);
        }

        [Fact]
        public void Guarded_reflection_reads_preserve_the_existing_read_surfaces()
        {
            Assert.Null(OpennessReflectionPolicy.GuardedWriteRefusal("Siemens.Engineering.SafetyValidation.TestValidity", "CheckValidity", 0));
            Assert.Null(OpennessReflectionPolicy.GuardedWriteRefusal("Siemens.Engineering.SafetyValidation.TestValidity", "checkvalidity", 0));
            Assert.Null(OpennessReflectionPolicy.GuardedWriteRefusal("Siemens.Engineering.Safety.SafetyAdministration", "tostring", 0));
            Assert.Null(OpennessReflectionPolicy.GuardedWriteRefusal("Siemens.Engineering.Safety.GlobalSettings", "safetymodificationspossible", 0));
            Assert.Null(OpennessReflectionPolicy.GuardedWriteRefusal("Siemens.Engineering.Safety.GlobalSettings", "SafetyModificationsPossible", 0));
            Assert.Contains("GetProjectProtection", OpennessReflectionPolicy.GuardedWriteRefusal("Siemens.Engineering.Project", "ProtectProject", 2));
        }

        [Fact]
        public void Project_library_update_rejects_unapplied_modes()
        {
            Assert.Throws<NotSupportedException>(() => LibraryPolicy.ValidateProjectUpdateModes(true, "NoDefaultVersionChange", "RetainStructure"));
            Assert.Throws<NotSupportedException>(() => LibraryPolicy.ValidateProjectUpdateModes(true, "SetOnlyHigherUpdatedVersionAsDefault", "UpdateStructure"));
            LibraryPolicy.ValidateProjectUpdateModes(true, "SetOnlyHigherUpdatedVersionAsDefault", "RetainStructure");
            LibraryPolicy.ValidateProjectUpdateModes(false, "NoDefaultVersionChange", "UpdateStructure");
        }

        [Theory]
        [InlineData(1, true)] [InlineData(30, true)] [InlineData(31, false)] [InlineData(256, false)]
        public void Safety_new_password_has_the_documented_length_limit(int length, bool allowed)
        {
            if (allowed) Assert.True(SafetyPolicy.ValidateRequest("setPassword", "", new string('x', length), true, false));
            else Assert.Throws<ArgumentException>(() => SafetyPolicy.ValidateRequest("setPassword", "", new string('x', length), true, false));
        }

        [Fact]
        public void Safety_password_administration_requires_offline_and_confirmation()
        {
            foreach (var action in new[] { "setPassword", "revokePassword" })
            {
                Assert.True(SafetyPolicy.RequiresOffline(action));
                Assert.Throws<ArgumentException>(() => SafetyPolicy.ValidateRequest(action, "", "secret", false, false));
                Assert.Throws<ArgumentException>(() => SafetyPolicy.ValidateRequest(action, "", "", true, false));
            }
            Assert.False(SafetyPolicy.RequiresOffline("login"));
        }

        [Theory]
        [InlineData(200, 300, "ToDB,FromDB")]
        [InlineData(20, 50, "FromDB,ToDB")]
        [InlineData(100, 180, "ToDB,FromDB")]
        public void Range_setter_order_remains_valid_at_every_intermediate_step(int from, int to, string expected)
        {
            var range = new Dictionary<string, int> { ["FromDB"] = 100, ["ToDB"] = 150 };
            var requested = new Dictionary<string, int> { ["FromDB"] = from, ["ToDB"] = to };
            var order = SafetyPolicy.OrderBlockNumberChanges(requested, key => range[key]);
            Assert.Equal(expected, string.Join(",", order));
            foreach (var key in order)
            {
                range[key] = requested[key];
                Assert.True(range["FromDB"] <= range["ToDB"]);
            }
        }

        [Fact]
        public void All_ranges_are_validated_before_any_setter_and_single_bounds_use_current_partner()
        {
            var current = new Dictionary<string, int> { ["FromDB"] = 100, ["ToDB"] = 150, ["FromFC"] = 100, ["ToFC"] = 150 };
            Assert.Throws<ArgumentException>(() => SafetyPolicy.OrderBlockNumberChanges(new Dictionary<string, int> {
                ["FromDB"] = 200, ["ToDB"] = 300, ["FromFC"] = 160 }, key => current[key]));
            Assert.Throws<ArgumentException>(() => SafetyPolicy.OrderBlockNumberChanges(new Dictionary<string, int> { ["ToDB"] = 99 }, key => current[key]));
            Assert.Equal(new[] { "ToDB" }, SafetyPolicy.OrderBlockNumberChanges(new Dictionary<string, int> { ["ToDB"] = 300 }, key => current[key]));
            Assert.Equal(100, current["FromDB"]);
        }

        [Theory]
        [InlineData("16")] [InlineData("17")] [InlineData("18")] [InlineData("19")] [InlineData("20")] [InlineData("21")]
        public void Dcb_import_is_admitted_on_every_documented_release(string release)
        {
            Assert.True(PortedFamilies.Available(release, "ManageDcbLibraries"));
            Assert.Null(OptionalEngineeringCapabilities.Unsupported(release, new OptionalEngineeringRequest { Operation = "ManageDcbLibraries", Action = "import" }));
            if (release is "16" or "17") Assert.NotNull(OptionalEngineeringCapabilities.Unsupported(release, new OptionalEngineeringRequest { Operation = "ManageDcbLibraries", Action = "read" }));
        }

        public sealed class Overloads
        {
            public int Calls;
            public string Select(int value) { Calls++; return "int"; }
            public string Select(bool value) { Calls++; return "bool"; }
            public string Select<T>(string value) { Calls++; return "generic"; }
            public void OnlyGeneric<T>(string value) { Calls++; }
            public void Ambiguous(int value) { Calls++; }
            public void Ambiguous(long value) { Calls++; }
            public void NeedsObject(Overloads value) { Calls++; }
            public void Fail() { Calls++; throw new InvalidOperationException("native-sentinel"); }
        }
        public sealed class ForeignOnlyService { }
        public sealed record Named(string Name);
        public sealed class Table { public string Name { get; set; } = ""; public Named[] Tags { get; set; } = Array.Empty<Named>(); }
        public sealed class Folder { public Table[] TagTables { get; set; } = Array.Empty<Table>(); public Folder[] Folders { get; set; } = Array.Empty<Folder>(); }
        public sealed class ServiceOwner(object service) { public T? GetService<T>() where T : class => service as T; }

        private static class ServiceTypes
        {
            private static readonly ModuleBuilder Module = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Siemens.Engineering.P881fFixtures"), AssemblyBuilderAccess.Run).DefineDynamicModule("fixtures");
            internal static readonly Type First = Define("Siemens.Engineering.First.P881fUniqueProvider");
            private static readonly Type Second = Define("Siemens.Engineering.Second.P881fUniqueProvider");
            internal static readonly Type Safety = Define("Siemens.Engineering.Safety.SafetyAdministration");
            private static Type Define(string name)
            {
                var type = Module.DefineType(name, TypeAttributes.Public);
                type.DefineDefaultConstructor(MethodAttributes.Public);
                var calls = type.DefineField("Calls", typeof(int), FieldAttributes.Public);
                var method = type.DefineMethod("SetSafetyOfflineProgramPassword", MethodAttributes.Public, typeof(void), new[] { typeof(string) });
                var il = method.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, calls); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stfld, calls); il.Emit(OpCodes.Ret);
                return type.CreateType()!;
            }
        }
    }
}

namespace Siemens.Engineering.Hmi
{
    public sealed class HmiTarget
    {
        public TiaMcp.Adapters.Contracts.Tests.OfficialApiAuditTests.Folder TagFolder { get; set; } = new();
    }
}
