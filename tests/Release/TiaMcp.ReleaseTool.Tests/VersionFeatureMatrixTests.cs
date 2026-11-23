using System.Reflection;
using System.Xml.Linq;
using Mono.Cecil;
using TiaMcp.Adapters.Contracts;
using System.Text.Json.Nodes;
using TiaMcp.BuildCommon;
using Xunit;
using static TiaMcp.ReleaseTool.CatalogJson;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class VersionFeatureMatrixTests
{
    private static string Root => Repository.FindRoot();
    public static IEnumerable<object[]> Releases => Strings(Read(Root, "reference/version-feature-matrix.json")["releases"])
        .Select(release => new object[] { release });

    [Theory, MemberData(nameof(Releases))]
    [Trait("Category", "RequiresSiemensSdk")]
    public void OpennessApiBoundariesMatchTheOfflineSdk(string release)
    {
        var sdk = MergeCheck.ResolvePublicApi(Root, Environment.GetEnvironmentVariable("TIA_MCP_TEST_PUBLIC_API_ROOT"),
            args => ReviewChangeSet.Git("git", args, Root));
        var (directory, core) = ReleaseCommands.PlcWorkerApi(release);
        var api = Path.Combine(sdk, directory);
        var members = Directory.GetFiles(api, "Siemens.Engineering*.xml")
            .SelectMany(path => XDocument.Load(path).Descendants("member").Attributes("name").Select(attribute => attribute.Value))
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(members);
        bool Has(string id) => members.Contains(id.Insert(2, "Siemens.Engineering."));
        var evidence = new Dictionary<OpennessApi, bool>
        {
            [OpennessApi.PlcUnits] = Has("T:SW.Units.PlcUnitProvider"),
            [OpennessApi.IgnoreUnitAttributes] = Has("F:SW.SWImportOptions.IgnoreUnitAttributes"),
            [OpennessApi.VersionControl] = Has("T:VersionControl.VersionControlInterface"),
            [OpennessApi.SafetyPasswordCompile] = Has("M:Safety.SafetyAdministration.LoginToSafetyOfflineProgram(System.Security.SecureString)"),
            [OpennessApi.WatchForceEntries] = Has("P:SW.WatchAndForceTables.PlcWatchTableEntry.Address")
                && Has("P:SW.WatchAndForceTables.PlcForceTableEntry.Address"),
            [OpennessApi.UmcServerSynchronization] = Has("M:Umac.UmcServerConfigurator.CheckConsistency")
                && Has("M:Umac.UmcServerConfigurator.Synchronize"),
            [OpennessApi.HardwareCatalog] = Has("T:HW.HardwareCatalog.HardwareCatalog"),
            [OpennessApi.UnifiedHmi] = Has("T:HmiUnified.HmiSoftware"),
            [OpennessApi.OnlineAuthentication] = Has("T:Online.Configurations.OnlineAuthenticationConfiguration"),
            [OpennessApi.CultureImportOptions] = Has("F:ImportOptions.SkipInactiveCultures"),
            [OpennessApi.TechnologyGroups] = Has("T:SW.TechnologicalObjects.TechnologicalInstanceDBUserGroup"),
            [OpennessApi.PlcDocuments] = Has("T:SW.ImportDocumentOptions"),
            [OpennessApi.OfflineUmcUsers] = Has("M:Umac.UmcUserComposition.CreateOfflineUmcUser(System.String)")
                && Has("M:Umac.UmcUserGroupComposition.CreateOfflineUmcUserGroup")
                && Has("M:Umac.UmcUser.SetName(System.String)") && Has("M:Umac.UmcUserGroup.SetName(System.String)"),
            [OpennessApi.ObjectIdentifiers] = Has("T:ObjectIdentifierProvider"),
            [OpennessApi.LibraryCopyModes] =
                Has("M:SW.Blocks.PlcBlockComposition.CreateFrom(Siemens.Engineering.Library.MasterCopies.MasterCopy,"
                    + "Siemens.Engineering.Library.MasterCopies.MasterCopyMode)"),
            [OpennessApi.LibraryVersionEditing] = Has("M:Library.Types.LibraryTypeVersion.Edit"),
            [OpennessApi.LibraryDoNotUse] = Has("P:Library.Types.LibraryType.DoNotUse"),
            [OpennessApi.LibraryDocuments] =
                Has("M:Library.Types.LibraryTypeComposition.CreateFromDocuments(System.IO.DirectoryInfo,System.String,"
                    + "Siemens.Engineering.Library.LibraryImportOptions)"),
            [OpennessApi.AlarmTextListCopyMode] =
                Has("M:SW.Alarm.TextLists.PlcAlarmUserTextlistComposition.CreateFrom(Siemens.Engineering.Library.MasterCopies.MasterCopy,"
                    + "Siemens.Engineering.Library.MasterCopies.MasterCopyMode)"),
            [OpennessApi.EncryptedPscExport] =
                Has("M:HW.Utilities.CardReaderPscProvider.Export(Siemens.Engineering.HW.Device,System.IO.FileInfo,System.Security.SecureString)"),
            [OpennessApi.TelecontrolFiles] = Has("M:HW.Features.TelecontrolManagement.ExportDataPoints(System.IO.FileInfo)"),
            [OpennessApi.UnifiedTextLists] = Has("P:HmiUnified.HmiSoftware.HmiTextLists"),
            [OpennessApi.UnifiedAlarmAudit] = Has("P:HmiUnified.HmiSoftware.HmiAlarmAuditClass"),
            [OpennessApi.UnifiedExpressionDynamization] = Has("T:HmiUnified.UI.Dynamization.ExpressionDynamization"),
            [OpennessApi.UnifiedRuntimeFontSettings] = Has("P:HmiUnified.RuntimeSettings.HmiRuntimeSetting.EnableLanguageCompatibleFontFamilies"),
            [OpennessApi.TelecontrolPoints] = Has("P:HW.Features.TelecontrolManagement.TelecontrolDataPoints"),
            [OpennessApi.SnapshotValues] = Has("T:SW.Blocks.Interface.ValueService"),
            [OpennessApi.MotionAxisMappings] = Has("T:SW.TechnologicalObjects.Motion.InterpreterMappings")
                && Has("T:SW.TechnologicalObjects.Ident.IdentTechnologicalObjectProvider")
                && Has("T:SW.TechnologicalObjects.Motion.SuperimposingAxes"),
            [OpennessApi.OpcUaAccessControl] = Has("P:SW.OpcUa.ServerInterfaceGroup.AccessControl"),
            [OpennessApi.PlcDocumentCreation] = Has("M:SW.PlcDocumentComposition.CreateFrom(Siemens.Engineering.Library.MasterCopies.MasterCopy)"),
            [OpennessApi.CommunicationConnections] = Has("T:HW.CommunicationConnections.ConnectionComposition"),
            [OpennessApi.DefaultWebPages] = Has("T:HW.Features.DefaultWebPagesFeature"),
            [OpennessApi.ProtectAllConfiguration] = Has("M:HW.Features.PlcMasterSecretConfigurator.ProtectAllPlcConfiguration"),
            [OpennessApi.SafetyBaseId] = Has("T:Safety.SafetyBaseIdProvider"),
            [OpennessApi.UnifiedGraphicLists] = Has("P:HmiUnified.HmiSoftware.HmiGraphicLists"),
            [OpennessApi.UnifiedRuntimeInputSettings] = Has("P:HmiUnified.RuntimeSettings.HmiRuntimeSetting.CentralInputHint"),
            [OpennessApi.UnifiedAlarmAndDiagnosisItems] = Has("T:HmiUnified.UI.Widgets.HmiAlarmIndicator"),
            [OpennessApi.DriveHardwareChanges] = Has("M:MC.Drives.DriveItemHardwareModule.ChangeType(System.String)"),
            [OpennessApi.SivarcTagMembers] = Has("T:SiVArc.TagMemberSetting"),
            [OpennessApi.InterfaceSectionsV5] = File.Exists(Path.Combine(release == "21" ? Directory.GetParent(api)!.FullName : api,
                "Schemas", "SW.InterfaceSections_v5.xsd"))
        };
        // The property XML has the same member ID for read-only and writable Name.
        var assemblyPath = Path.Combine(api, (release == "21" ? "Siemens.Engineering.Step7" : core) + ".dll");
        using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath);
        var group = assembly.MainModule.GetType("Siemens.Engineering.SW.ExternalSources.PlcExternalSourceUserGroup");
        evidence[OpennessApi.ExternalSourceGroupRename] = group?.Properties.SingleOrDefault(property => property.Name == "Name")?.SetMethod?.IsPublic == true;
        var fields = typeof(OpennessApi).GetFields(BindingFlags.Public | BindingFlags.Static).Where(field => field.FieldType == typeof(OpennessApi));
        int major = release == "14sp1" ? 14 : int.Parse(release.Split('.')[0], System.Globalization.CultureInfo.InvariantCulture);
        foreach (var field in fields)
        {
            var boundary = (OpennessApi)field.GetValue(null)!;
            Assert.True(evidence.TryGetValue(boundary, out var present), "Missing SDK evidence for " + field.Name);
            Assert.True(present == (major >= boundary.FirstMajor), $"{field.Name}: V{release}, FirstMajor {boundary.FirstMajor}, present {present}");
        }
    }

    [Theory, MemberData(nameof(Releases))]
    public void Technology_export_matches_advertised_release_tools_and_preserves_sdk_evidence(string release)
    {
        var matrix = Read(Root, "reference/version-feature-matrix.json");
        var feature = matrix["features"]!.AsArray().Single(row => Text(row!["id"]) == "technology-export")!;
        var roster = Strings(Read(Root, "manifest/version-tools.json")["releases"]![release]!["tools"]);
        Assert.Equal("implemented", Text(feature["implementation"]![release]));
        var tools = Strings(feature["tools"]);
        Assert.Equal(tools.Where(tool => roster.Contains(tool, StringComparer.Ordinal)), Strings(feature["advertisedTools"]![release]));
        Assert.Equal(tools, Strings(feature["advertisedTools"]![release]));
        foreach (string identifier in Strings(feature["sdkIdentifiers"]))
            Assert.Contains(identifier, Strings(feature["sdkEvidence"]![release]!["present"]));
        Assert.Empty(Strings(feature["sdkEvidence"]![release]!["absentFromInspectedXml"]));
        Assert.Contains("NOT RUN", Text(matrix["nativeAcceptance"]), StringComparison.Ordinal);
    }

    [Fact]
    public void Technology_export_coverage_cells_match_the_reviewed_feature_matrix()
    {
        var matrix = Read(Root, "reference/version-feature-matrix.json");
        var feature = matrix["features"]!.AsArray().Single(row => Text(row!["id"]) == "technology-export")!;
        var row = File.ReadLines(Path.Combine(Root, "docs/reference/openness-coverage.md"))
            .Single(line => line.StartsWith("| Export technology-object XML |", StringComparison.Ordinal));
        var cells = row.Split('|', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(cell => cell.Trim()).ToArray();
        Assert.Equal(Strings(matrix["releases"]).Select(release => Text(feature["implementation"]![release])),
            cells.Select(cell => cell == "I" ? "implemented" : cell));
    }
}
