using System.Xml.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Units;
using Siemens.Engineering.HW;
using TiaMcp.Adapters.Native.Plc;
using Xunit;

namespace Siemens.Engineering
{
    public enum ProjectTextResultState { Info, Error }
}

namespace TiaMcp.Adapters.Contracts.Tests
{
    public sealed class ScopedImportOutcomeTests
    {
        [Theory, InlineData(ProjectTextResultState.Info, true), InlineData(ProjectTextResultState.Error, false)]
        public void Project_text_verdict_uses_the_typed_result(ProjectTextResultState state, bool success)
        {
            var meta = new Dictionary<string, object?>();
            PlcSoftwareValues.RecordNativeResult(meta, state, Array.Empty<string>());
            Assert.Equal(NativeResultStates.ProjectTexts, meta["nativeStateType"]);
            Assert.Equal(state.ToString(), meta["nativeState"]);
            Assert.Equal(success, NativeResultStates.Succeeded(state));
        }

        public static XDocument Document(string kind) => XDocument.Parse($"""
            <Document>
              <Engineering version="V18"/>
              <{kind} ID="0"><AttributeList>
                <Interface><Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5">
                  <Section Name="{(kind == "SW.Types.PlcStruct" ? "None" : "Input")}"><Member Name="Enabled" Datatype="Bool"/></Section>
                </Sections></Interface>
                <Name>Motor</Name>
              </AttributeList></{kind}>
            </Document>
            """);

        public static bool ImportNewType(string path, Func<bool> import, Func<string, bool> exists)
        {
            var name = PlcFoundationEngine.SingleNewImportName(XDocument.Load(path), false, "SW.Types.PlcStruct");
            return PlcFoundationEngine.ImportWithReadback(import, name, true, exists);
        }

        [Theory]
        [InlineData("SW.Types.PlcStruct", "Target", "", false)]
        [InlineData("SW.Types.PlcStruct", "Target", "", true)]
        [InlineData("SW.Types.PlcStruct", "", "Other", true)]
        [InlineData("SW.Types.PlcStruct", "Target", "Other", true)]
        [InlineData("SW.Blocks.FC", "Target", "", false)]
        [InlineData("SW.Blocks.FC", "Target", "", true)]
        [InlineData("SW.Blocks.FC", "", "Other", false)]
        [InlineData("SW.Blocks.FC", "Target", "Safety", true)]
        [InlineData("SW.Types.PlcStruct", "Target", "Target", false)]
        [InlineData("SW.Types.PlcStruct", "Target", "Target", true)]
        [InlineData("SW.Blocks.FC", "Target", "", false, "Target", true)]
        [InlineData("SW.Types.PlcStruct", "Target", "Other", false, "Target", true)]
        [InlineData("SW.Blocks.FC", "", "Other", false, "Other", true)]
        [InlineData("SW.Types.PlcStruct", "Target", "Safety", false, "Safety", true)]
        [InlineData("SW.Blocks.FC", "Target", "Other", true, "Safety", false)]
        [InlineData("SW.Blocks.FC", "Target", "Target", false, "Target", false)]
        public void Preview_checks_clashes_outside_the_overwrite_scope(string kind, string targetName, string sourceName, bool overwrite,
            string namespacedUnit = "", bool crossScopeAllowed = false)
        {
            var plc = new PlcSoftware();
            var target = new PlcUnit { Name = "Target" };
            var other = new PlcUnit { Name = "Other" };
            var safety = new PlcSafetyUnit { Name = "Safety" };
            foreach (var unit in new PlcUnitBase[] { target, other, safety })
                if (unit.Name == namespacedUnit) unit.NamespacePreset = "Motion";
            plc.UnitProvider.UnitGroup.Units.Add(target);
            plc.UnitProvider.UnitGroup.Units.Add(other);
            plc.UnitProvider.UnitGroup.SafetyUnits.Add(safety);
            object? source = sourceName switch { "" => null, "Other" => other, "Safety" => safety, _ => target };
            bool type = kind == "SW.Types.PlcStruct";
            if (type) PlcUnitScope.Types(plc, source).Types.Add(new HardwareObject { Name = "Motor" });
            else PlcUnitScope.Blocks(plc, source).Blocks.Add(new OB { Name = "Motor" });

            void Preview() => PlcFoundationEngine.RequireScopedImportAvailable(Document(kind), type ? "SW.Types." : "SW.Blocks.",
                overwrite, plc, targetName, targetName == "" ? null : target, (scope, name) =>
                    type ? PlcUnitScope.Types(plc, scope).Types.Find(name) != null : PlcUnitScope.Blocks(plc, scope).Blocks.Find(name) != null);
            if (crossScopeAllowed || (sourceName == targetName && overwrite)) Preview();
            else
            {
                var refusal = Assert.Throws<AdapterPreconditionException>(Preview);
                Assert.Equal(sourceName == targetName ? "overwrite" : "unitName", refusal.ParamName);
                Assert.Equal(sourceName == targetName, refusal.IsArgument);
                Assert.Contains("Motor", refusal.Message);
            }
        }

        [Theory, InlineData("absent"), InlineData("present"), InlineData("unreadable"), InlineData("overwrite"),
            InlineData("options"), InlineData("batch")]
        public void Type_import_failure_requires_a_single_new_type_and_a_readable_absence(string scenario)
        {
            var document = Document("SW.Types.PlcStruct");
            if (scenario == "batch") document.Root!.Add(new XElement(document.Root.Elements("SW.Types.PlcStruct").Single()));
            string? name = PlcFoundationEngine.SingleNewImportName(document, scenario == "overwrite", "SW.Types.PlcStruct");
            var native = new EngineeringException("Type import failed.");
            native.MessageData.DetailText = "Destination refused the PLC type.";
            var calls = new List<string>();
            var failure = Assert.Throws<EngineeringException>(() => PlcFoundationEngine.ImportWithReadback<bool>(
                () => { calls.Add("import"); throw native; }, name, scenario != "options", selected => {
                    calls.Add("read:" + selected);
                    return scenario == "unreadable" ? throw new InvalidOperationException("Readback lost.") : scenario == "present";
                }));
            Assert.Equal(scenario is "absent" or "present" or "unreadable" ? new[] { "import", "read:Motor" } : new[] { "import" }, calls);
            Assert.Same(native, failure);
            Assert.Equal("import", failure.Data["phase"]);
            Assert.Contains("Destination refused the PLC type.", Assert.IsType<string>(failure.Data["nativeMessage"]));
            Assert.Equal(scenario == "absent", failure.Data["verifiedAbsent"] is true);
            Assert.Equal(scenario == "absent", failure.Data["foundationKnownNoMutation"] is true);
            Assert.Equal(scenario == "absent" ? TiaOpenness.Shared.HostFailureKind.Precondition : TiaOpenness.Shared.HostFailureKind.Unknown,
                TiaOpenness.Shared.HostFailurePolicy.Classify(failure, dispatched: true, readOnly: false));
        }

        [Fact]
        public void Version_import_cannot_prove_unchanged_when_a_dependency_version_is_replaced()
        {
            var versions = new List<object> { new { VersionNumber = new Version(1, 0) } };
            var dependency = new { Name = "Motor", Versions = versions };
            var root = new { Types = new[] { new { Name = "Valve", Versions = Array.Empty<object>() } },
                Folders = new[] { new { Name = "Library", Types = new[] { dependency }, Folders = Array.Empty<object>() } } };
            var proof = PlcFoundationEngine.CaptureLibraryImportEnvironment(null, "None", "None", () => PlcFoundationEngine.LibraryVersionSet(root));
            Assert.NotNull(proof);
            Assert.True(proof());
            versions[0] = new { VersionNumber = new Version(2, 0) };
            Assert.False(proof());
        }

        [Fact]
        public void Unreadable_library_version_snapshot_keeps_an_eligible_count_proof_uncertain()
        {
            var proof = PlcFoundationEngine.CaptureLibraryImportEnvironment(null, "None", "None", () => throw new InvalidOperationException());
            var meta = new Dictionary<string, object?> { ["mayHaveChanged"] = true };
            PlcFoundationEngine.RecordUnchangedLibraryImport(meta, proof, 1, () => 1, true);
            Assert.Equal(true, meta["mayHaveChanged"]);
        }

        [Theory, InlineData("unchanged"), InlineData("type-renamed"), InlineData("block-added"), InlineData("count"),
            InlineData("library-type-count"), InlineData("before-unreadable"), InlineData("after-unreadable"),
            InlineData("override"), InlineData("cultures")]
        public void Library_import_requires_unchanged_library_counts_and_both_complete_PLC_trees(string scenario)
        {
            var plc = new PlcSoftware();
            var group = new Siemens.Engineering.SW.Types.PlcTypeGroup { Name = "User types" };
            var nested = new Siemens.Engineering.SW.Types.PlcTypeGroup { Name = "Dependencies" };
            plc.TypeGroup.Groups.Add(group);
            group.Groups.Add(nested);
            nested.Types.Add(new HardwareObject { Name = "Motor" });
            int libraryTypes = 2;
            bool unreadable = scenario == "before-unreadable";
            var proof = PlcFoundationEngine.CaptureLibraryImportEnvironment(plc, scenario == "cultures" ? "ActivateInactiveCultures" : "None",
                scenario == "override" ? "Override" : "None", () => unreadable ? throw new InvalidOperationException() : libraryTypes);
            if (scenario == "type-renamed") nested.Types[0].Name = "Replaced";
            if (scenario == "block-added") plc.BlockGroup.Groups.Create("Sibling").Blocks.Add(new OB { Name = "Motor" });
            if (scenario == "library-type-count") libraryTypes++;
            unreadable = scenario == "after-unreadable";
            var meta = new Dictionary<string, object?> { ["mayHaveChanged"] = true };
            PlcFoundationEngine.RecordUnchangedLibraryImport(meta, proof, 1, () => scenario == "count" ? 2 : 1, false);
            Assert.Equal(scenario != "unchanged", meta["mayHaveChanged"]);
        }
    }
}
