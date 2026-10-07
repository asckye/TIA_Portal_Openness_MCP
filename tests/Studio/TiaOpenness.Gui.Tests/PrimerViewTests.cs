using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Gui.Controls;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Themes;
using TiaOpenness.Gui.ViewModels;
using Xunit;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public class PrimerViewTests(WpfContext wpf)
{
    private static void Set(object model, string name, object value) =>
        model.GetType().GetProperty(name)!.SetValue(model, value);
    private static void Field(object model, string name, object value) =>
        model.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(model, value);
    private static void RecordCompile(TiaOpenness.Gui.Services.WorkbenchActivity activity)
    {
        activity.Append("Connected to native Openness\nProject bound · Conveyor_Line_04");
        activity.AppendDiagnostic("Safety_Door_FB", "Block not consistent — recompile required", TiaOpenness.Gui.Services.WorkbenchActivity.Severity.Warning);
        activity.AppendDiagnostic("Scale_Analog_FC", "Unused temp variable #tmpRaw", TiaOpenness.Gui.Services.WorkbenchActivity.Severity.Warning);
        activity.AppendDiagnostic("HMI_Interface_DB", "Optimized access mismatch", TiaOpenness.Gui.Services.WorkbenchActivity.Severity.Warning);
        activity.AppendLocalized("Status.CompileResult", "Warning", 0, 3, "1.4");
        activity.AppendLocalized("Log.InspectionHeader", "PLC_1");
        activity.AppendRule("NAMING-001", 3);
        activity.AppendRule("DOC-001", 1);
        activity.AppendRule("BUILD-001", 1);
        activity.AppendLocalized("Status.InspectResult", 5, 128);
    }

    [Fact]
    public void Raw_messages_cannot_create_result_cards()
    {
        wpf.RunWithLanguage(AppLanguage.English, () =>
        {
            using var model = new MainViewModel();
            using var results = new ResultPresentation(model);
            model.Activity.Append(Loc.Current.T("Status.CompileResult", "Error", 99, 8, "1.0"));
            model.Activity.Append("Warning: PLC_1 - pretend diagnostic\nNAMING-001 (99)");
            Assert.Equal("—", results.Errors);
            Assert.Empty(results.Diagnostics);
            Assert.Empty(results.Rules);
            model.Activity.AppendLocalized("Status.CompileResult", "Error", 99, 8, "1.0"); WpfContext.Drain();
            Assert.Equal("99", results.Errors);
        });
    }

    [Fact]
    public void Results_show_recorded_outcomes_and_clear_back_to_not_run()
    {
        wpf.RunWithLanguage(AppLanguage.English, () =>
        {
            using var model = new MainViewModel();
            using var results = new ResultPresentation(model);
            Assert.Equal("—", results.Errors);
            RecordCompile(model.Activity); WpfContext.Drain();
            Assert.Equal("0", results.Errors);
            Assert.Equal("3", results.Warnings);
            Assert.Equal(3, results.Diagnostics.Count);
            Assert.Equal("Checked 128 blocks, found 5 issues.", results.InspectionSummary);
            Assert.Equal(4, results.Rules.Count);
            Assert.Contains("Naming · 3", results.Rules);
            Assert.Contains("Know-how · 0", results.Rules);
            model.Activity.ClearLog(); WpfContext.Drain();
            Assert.Equal("—", results.Errors);
            Assert.Empty(results.Diagnostics);
            Assert.Equal("Not run", results.InspectionSummary);
        });
    }

    internal void RenderEngineeringFixture(AppTheme theme, bool vci, AppLanguage language, bool log = false)
    {
        wpf.RunWithLanguage(language, () =>
        {
            var oldTheme = ThemeManager.Current.Theme;
            ThemeManager.Current.Theme = theme;
            var window = new MainWindow(new TiaOpenness.Gui.ViewModels.MainViewModel(), false);
            var model = (MainViewModel)window.DataContext;
            try
            {
                model.Session.ProjectPath = @"D:\Projects\Line04\Conveyor_Line_04.ap21";
                Set(model.Session, "ProjectName", "Conveyor_Line_04");
                Set(model.Session, "IsConnected", true);
                Field(model.Session, "_opennessVersion", "V21");
                Field(model.Engineering, "_suppressDeviceLoad", true);
                var device = new DeviceInfo { Id = "PLC_1", Name = "PLC_1 · Software", Category = "Plc" };
                model.Engineering.Devices.Add(device);
                model.Engineering.SelectedDevice = device;
                model.Engineering.OutputDirectory = @"D:\Exports";
                string[] names = ["Main", "Conveyor_Ctrl_FB", "Conveyor_Ctrl_IDB", "Scale_Analog_FC", "Safety_Door_FB", "HMI_Interface_DB", "Motor_Start_FB"];
                BlockKind[] kinds = [BlockKind.OB, BlockKind.FB, BlockKind.DB, BlockKind.FC, BlockKind.FB, BlockKind.DB, BlockKind.FB];
                int[] numbers = [1, 120, 121, 30, 200, 300, 110];
                string[] languages = ["LAD", "SCL", "DB", "SCL", "F-FBD", "DB", "FBD"];
                for (int i = 0; i < names.Length; i++) model.Engineering.Blocks.Add(new BlockRow(new BlockInfo
                {
                    Name = names[i], Kind = kinds[i], Number = numbers[i], ProgrammingLanguage = languages[i],
                    Path = "Program blocks" + (i == 0 ? "" : "/" + names[i]), IsConsistent = i != 4, IsKnowHowProtected = i == 4,
                }) { Selected = i is 0 or 1 or 3 or 5 });
                RecordCompile(model.Activity); WpfContext.Drain();
                if (vci)
                {
                    model.IsVcTab = true;
                    Set(model.VersionControl, "VcSupported", true);
                    var workspace = new WorkspaceInfo { Name = "Line04_Workspace", RootPath = @"D:\Workspaces\Line04", MappedObjectCount = 128 };
                    model.VersionControl.Workspaces.Add(workspace); model.VersionControl.SelectedWorkspace = workspace;
                    foreach (string name in new[] { names[1], names[4], names[5], names[3], names[0], names[6] }) model.VersionControl.VcStatusItems.Add(new MappedObjectInfo
                    { Name = name, FilePath = name + ".xml", FileFormat = "SimaticML", CompareState = name == "Main" || name == "Motor_Start_FB" ? VcCompareState.Equal : VcCompareState.Unequal });
                    Field(model.VersionControl,"_selectedVcItem",model.VersionControl.VcStatusItems[0]);
                    Set(model.VersionControl, "VcDiffCaption", "Conveyor_Ctrl_FB.xml");
                    foreach (string line in new[] { "@@ -212,7 +212,18 @@", "   <Member Name=\"Speed_SP\"", "-    Datatype=\"Int\" />", "+    Datatype=\"Real\">", "+    <Comment>Setpoint mm/min</Comment>", "+  </Member>", "+  <Member Name=\"Ramp_Up\"", "+    Datatype=\"Time\" />", "+  <Member Name=\"Enable\"", "+    Datatype=\"Bool\" />" }) model.VersionControl.VcDiffLines.Add(new DiffLine
                    { Text = line, Kind = line.StartsWith('+') ? DiffLineKind.Added : line.StartsWith('-') ? DiffLineKind.Removed : DiffLineKind.Context });
                    model.Activity.ClearLog(); WpfContext.Drain();
                    model.Activity.AppendLocalized("Status.VcMapApplied", 128, 0, 6, 0);
                    model.Activity.AppendLocalized("Status.VcSyncDry", 4, "ProjectToWorkspace", 124);
                }
                if (log) model.IsLogTab = true;
                window.Navigate(log ? "Log" : vci ? "VersionControl" : "Blocks");
                var root = (FrameworkElement)window.Content;
                window.Content = null;
                var host = new Border { Child = root, DataContext = model, Width = 1200, Height = 780 };
                NameScope.SetNameScope(host, NameScope.GetNameScope(window));
                host.Measure(new Size(1200, 780));host.Arrange(new Rect(0,0,1200,780));host.UpdateLayout();
                host.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                host.UpdateLayout();
                Assert.Equal(theme, ThemeManager.Current.Theme);
                string[] cards = log ? new[] { "LogCard" } : vci ? new[] { "WorkspaceCard", "VciStatusCard", "SyncCard", "MappedObjectsCard", "DiffCard" } : new[] { "BlocksHeaderCard", "BlocksCard", "CompileCard", "InspectionCard", "BlocksLogCard" };
                DesktopCapture.AssertCards(host, theme, cards.Where(name => name != "DiffCard").Select(name => (Border)ProjectPageTestSupport.Find(window, name)).ToArray());
                var bitmap = new RenderTargetBitmap(1200,780,96,96,PixelFormats.Pbgra32);bitmap.Render(host);
                string? output = Environment.GetEnvironmentVariable("TIA_PRIMER_SCREENSHOTS");
                if (!string.IsNullOrEmpty(output))
                {
                    Directory.CreateDirectory(output);
                    var encoder = new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(output, "studio-" + (log ? "log" : vci ? "vci" : "blocks") + (language == AppLanguage.Chinese ? "-zh-" : "-") + theme.ToString().ToLowerInvariant() + ".png"));encoder.Save(file);
                }
                Assert.Equal(1200, bitmap.PixelWidth);
                Assert.Equal(780, bitmap.PixelHeight);
            }
            finally { window.Close(); ThemeManager.Current.Theme = oldTheme; }
        });
    }
}
