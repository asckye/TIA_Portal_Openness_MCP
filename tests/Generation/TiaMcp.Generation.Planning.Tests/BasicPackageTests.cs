using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using TiaMcp.Logic.Generation;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

namespace TiaMcp.Generation.Planning.Tests
{
    public sealed class BasicPackageTests
    {
        public static IEnumerable<object[]> CulturesAndReleases => BasicPackageFixture.Releases.SelectMany(r =>
            new[] { "en-US", "fr-FR", "tr-TR", "zh-CN" }.Select(c => new object[] { r, c }));
        public static IEnumerable<object[]> Releases => BasicPackageFixture.Releases.Select(r => new object[] { r });
        public static IEnumerable<object[]> Sources => Directory.GetFiles(Path.Combine(BasicPackageFixture.DirectoryPath, "sources", "plc"), "*.scl")
            .OrderBy(p => p, StringComparer.Ordinal).Select(p => new object[] { Path.GetFileName(p) });

        [Theory]
        [MemberData(nameof(CulturesAndReleases))]
        public void BasicGoldenPlansAndCallSourceAreStable(string release, string culture)
        {
            var previous = CultureInfo.CurrentCulture; var previousUi = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
                var first = BasicPackageFixture.Build(release); var second = BasicPackageFixture.Build(release);
                Assert.Equal(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", "Basic", "plan." + release + ".json")), Encoding.UTF8.GetBytes(first.CanonicalPlan));
                Assert.Equal(first.CanonicalPlan, second.CanonicalPlan);
                Assert.Equal(first.Plan.PlanHash, GenerationDocuments.PlanHash(first.CanonicalPlan));
                Assert.Equal(0, first.Plan.SelfCheck.Errors); Assert.Empty(first.Plan.Conflicts);
                var call = first.Artifacts.Single(a => a.Content.StartsWith("FUNCTION \"FC_U01_Calls\"", StringComparison.Ordinal));
                Assert.Equal(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", "Basic", "FC_U01_Calls.scl")), Encoding.UTF8.GetBytes(call.Content));
                Assert.All(first.Artifacts, a => Assert.Equal(a.Sha256, CanonicalJson.HashBytes(Encoding.UTF8.GetBytes(a.Content))));
            }
            finally { CultureInfo.CurrentCulture = previous; CultureInfo.CurrentUICulture = previousUi; }
        }

        [Theory]
        [MemberData(nameof(Releases))]
        public void EveryReleaseRetainsCompleteExampleAndHonestAvailability(string release)
        {
            var result = BasicPackageFixture.Build(release);
            Assert.Equal(8, result.Expected.Blocks.Count(b => b.InstanceType == "FB_MotorDol"));
            Assert.Equal(4, result.Expected.Blocks.Count(b => b.InstanceType == "FB_Valve2Pos"));
            Assert.Equal(6, result.Expected.Blocks.Count(b => b.InstanceType == "FB_DigitalSensor"));
            Assert.Equal(2, result.Expected.Blocks.Count(b => b.InstanceType == "FB_AnalogIn"));
            Assert.Single(result.Expected.Blocks, b => b.InstanceType == "FB_ModeManager");
            Assert.Equal(48, result.Expected.Tags.Count);
            Assert.Equal(15, result.Expected.Alarms.Count);
            Assert.Equal(15, result.Expected.Blocks.Count(b => b.InstanceType == "FB_BasicProgramAlarm"));
            Assert.Equal(21, Assert.Single(result.Expected.Screens).Widgets.Count);
            Assert.Contains(result.Plan.Unavailable, u => u.Phase == "alarms");
            Assert.Contains(result.Plan.Unavailable, u => u.Phase == "hmi");
            Assert.Equal(release != "20" && release != "21", result.Plan.Unavailable.Any(u => u.Phase == "plcStructure"));
            Assert.Contains(result.Plan.Steps, s => s.Tool == "GenerateBlocksFromExternalSource" && s.Expect.Contains == "FC_U01_Calls");
            Assert.All(result.Plan.Steps, s => Assert.Equal("NOT RUN", s.Availability.Native));
            var call = result.Artifacts.Single(a => a.Content.StartsWith("FUNCTION \"FC_U01_Calls\"", StringComparison.Ordinal)).Content;
            Assert.True(call.IndexOf("\"IDB_U01-00U01\"(", StringComparison.Ordinal) < call.IndexOf("\"IDB_U01-M01\"(", StringComparison.Ordinal));
            Assert.True(call.IndexOf("\"IDB_U01-V04\"(", StringComparison.Ordinal) < call.IndexOf("\"IDB_U01-Z00Unit\"(", StringComparison.Ordinal));
        }

        [Theory]
        [MemberData(nameof(Releases))]
        public void S71200PlanningExcludesTheProgramAlarmAdapter(string release)
        {
            var result = BasicPackageFixture.Build(release, programAlarm: false);
            Assert.DoesNotContain(result.Artifacts, a => a.Content.Contains("Program_Alarm", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Expected.Blocks, b => b.Name == "FB_BasicProgramAlarm");
            Assert.Equal(0, result.Plan.SelfCheck.Errors);
        }

        [Theory]
        [MemberData(nameof(Sources))]
        public void AllPackageSclPassesTheRepositoryAnalysisCore(string file)
        {
            var source = File.ReadAllText(Path.Combine(BasicPackageFixture.DirectoryPath, "sources", "plc", file));
            var findings = PlcDocumentationLogic.LintScl(source);
            Assert.DoesNotContain(findings, f => f.Severity == "error");
            Assert.DoesNotContain('\r', source);
            Assert.False(File.ReadAllBytes(Path.Combine(BasicPackageFixture.DirectoryPath, "sources", "plc", file)).Take(3).SequenceEqual(new byte[] { 239, 187, 191 }));
        }

        [Theory]
        [MemberData(nameof(Releases))]
        public void GeneratedSclAlsoPassesTheRepositoryAnalysisCore(string release)
        {
            foreach (var artifact in BasicPackageFixture.Build(release).Artifacts)
                Assert.DoesNotContain(PlcDocumentationLogic.LintScl(artifact.Content), f => f.Severity == "error");
        }

        [Fact]
        public void MultipleUnitsHaveDistinctControllersAndPermitBindings()
        {
            var package = StandardPackageLoader.LoadDirectory(BasicPackageFixture.DirectoryPath);
            var machine = GenerationDocuments.Load<MachineDescription>(File.ReadAllText(Path.Combine(BasicPackageFixture.DirectoryPath, "examples", "machine.json")));
            var unit = GenerationDocuments.Load<MachineDescription>(GenerationDocuments.Canonical(machine)).Devices.Single(d => d.Type == "unit");
            var motor = GenerationDocuments.Load<MachineDescription>(GenerationDocuments.Canonical(machine)).Devices.First(d => d.Type == "motor.dol");
            unit.Id = "00U02"; unit.Parent = "U02"; unit.Hmi!.Screen = "U02";
            motor.Id = "M09"; motor.Parent = "U02"; motor.Hmi!.Screen = "U02";
            machine.Devices.Add(unit); machine.Devices.Add(motor);
            machine.Topology.Add(new MachineDescriptionTopology { Id = "U02", Kind = "unit", Name = new Dictionary<string, string> { ["en-US"] = "Second unit", ["zh-CN"] = "第二单元" } });
            var observed = new ProjectModel { ProjectIdentity = machine.Target.Project.ProjectIdentity!, Devices = new List<ProjectDevice>
                { new ProjectDevice { Station = "PLC1", Name = "PLC1", Article = machine.Stations[0].Article!, Firmware = machine.Stations[0].Firmware! } } };
            var result = GenerationPlanner.Build(package, GenerationDocuments.Canonical(machine), observed, new GenerationPlanningOptions { ArtifactRoot = "C:/tiamcp-generation" });
            Assert.Equal(0, result.Plan.SelfCheck.Errors);
            Assert.Equal(2, result.Expected.Blocks.Count(b => b.InstanceType == "FB_ModeManager"));
            var call = result.Artifacts.Single(a => a.Content.StartsWith("FUNCTION \"FC_U02_Calls\"", StringComparison.Ordinal)).Content;
            Assert.Contains("\"IDB_U02-00U02\".\"Hmi\".\"Status\".\"Permit\"", call);
            Assert.DoesNotContain("\"IDB_U01-00U01\"", call);
            Assert.True(call.IndexOf("\"IDB_U02-00U02\"(", StringComparison.Ordinal) < call.IndexOf("\"IDB_U02-M09\"(", StringComparison.Ordinal));
        }

        [Fact]
        public void WidgetInterfacesResolveToGeneratedIdbMembers()
        {
            var package = StandardPackageLoader.LoadDirectory(BasicPackageFixture.DirectoryPath);
            var result = BasicPackageFixture.Build("21", package);
            var hmi = package.GetPart<HmiPart>("hmi.json");
            Assert.Equal(5, hmi.Widgets.Count); Assert.All(hmi.Widgets, w => Assert.Equal("elementGroup", w.Kind));
            foreach (var widget in hmi.Widgets)
            {
                using var source = JsonDocument.Parse(package.ReadFile(widget.Template));
                var udt = result.Expected.Types.Single(t => t.Name == source.RootElement.GetProperty("interfaceType").GetString());
                foreach (var field in widget.InterfaceMap!.Keys)
                {
                    var members = udt.Members;
                    foreach (var segment in field.Split('.'))
                    {
                        Assert.True(members.TryGetValue(segment, out var type), widget.Id + "/" + field);
                        members = result.Expected.Types.SingleOrDefault(t => t.Name == type)?.Members ?? new Dictionary<string, string>();
                    }
                }
            }
        }

        [Fact]
        public void UnifiedAlarmClassesAreExplicitlyLimitedTo20And21()
        {
            var package = StandardPackageLoader.LoadDirectory(BasicPackageFixture.DirectoryPath);
            using var source = JsonDocument.Parse(package.ReadFile("sources/hmi/unified-alarm-classes.json"));
            Assert.Equal(new[] { "20", "21" }, source.RootElement.GetProperty("releases").EnumerateArray().Select(r => r.GetString()));
            Assert.Equal(2, source.RootElement.GetProperty("classes").GetArrayLength());
            Assert.All(package.GetPart<AlarmsPart>("alarms.json").Templates, t => Assert.Equal("Program_Alarm", t.Backend));
        }
    }
}
