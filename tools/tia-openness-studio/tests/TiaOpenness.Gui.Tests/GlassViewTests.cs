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
public class GlassViewTests(WpfContext wpf)
{
    private static void Set(MainViewModel model, string name, object value) =>
        typeof(MainViewModel).GetProperty(name)!.SetValue(model, value);
    private static void Field(MainViewModel model, string name, object value) =>
        typeof(MainViewModel).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(model, value);
    private const string CompileLog = "09:50:02  Connected to native Openness\n09:50:03  Project bound · Conveyor_Line_04\n"
        + "09:52:48  Warning: Safety_Door_FB - Block not consistent — recompile required\n"
        + "09:52:48  Warning: Scale_Analog_FC - Unused temp variable #tmpRaw\n"
        + "09:52:48  Warning: HMI_Interface_DB - Optimized access mismatch\n"
        + "09:52:48  Warning: 0 error(s), 3 warning(s) in 1.4s\n"
        + "09:53:00  --- inspection of PLC_1 ---\n"
        + "09:53:00  NAMING-001 (3)\n09:53:00  DOC-001 (1)\n09:53:00  BUILD-001 (1)\n"
        + "09:53:00  5 finding(s) over 128 block(s).\n";

    [Fact]
    public void Results_show_recorded_outcomes_and_clear_back_to_not_run()
    {
        wpf.RunWithLanguage(AppLanguage.English, () =>
        {
            using var model = new MainViewModel();
            using var results = new GlassResults(model);
            Assert.Equal("—", results.Errors);
            Set(model, "Log", CompileLog);
            Assert.Equal("0", results.Errors);
            Assert.Equal("3", results.Warnings);
            Assert.Equal(3, results.Diagnostics.Count);
            Assert.Equal("Checked 128 blocks, found 5 issues.", results.InspectionSummary);
            Assert.Equal(4, results.Rules.Count);
            Assert.Contains("Naming · 3", results.Rules);
            Assert.Contains("Know-how · 0", results.Rules);
            model.ClearLog();
            Assert.Equal("—", results.Errors);
            Assert.Empty(results.Diagnostics);
            Assert.Equal("Not run", results.InspectionSummary);
        });
    }

    [Theory]
    [InlineData(AppTheme.Light, false, AppLanguage.English)]
    [InlineData(AppTheme.Light, false, AppLanguage.Chinese)]
    [InlineData(AppTheme.Light, true, AppLanguage.Chinese)]
    [InlineData(AppTheme.Dark, false, AppLanguage.English)]
    [InlineData(AppTheme.Dark, false, AppLanguage.Chinese)]
    [InlineData(AppTheme.Dark, true, AppLanguage.English)]
    [InlineData(AppTheme.Dark, true, AppLanguage.Chinese)]
    public void Renders_reference_fixture_at_1200_by_780(AppTheme theme, bool vci, AppLanguage language)
    {
        wpf.RunWithLanguage(language, () =>
        {
            var oldTheme = ThemeManager.Current.Theme;
            ThemeManager.Current.Theme = theme;
            var window = new MainWindow();
            var model = (MainViewModel)window.DataContext;
            try
            {
                model.ProjectPath = @"D:\Projects\Line04\Conveyor_Line_04.ap21";
                Set(model, "ProjectName", "Conveyor_Line_04");
                Set(model, "IsConnected", true);
                Field(model, "_opennessVersion", "V21");
                Field(model, "_suppressDeviceLoad", true);
                var device = new DeviceInfo { Id = "PLC_1", Name = "PLC_1 · Software", Category = "Plc" };
                model.Devices.Add(device);
                model.SelectedDevice = device;
                model.OutputDirectory = @"D:\Exports";
                string[] names = ["Main", "Conveyor_Ctrl_FB", "Conveyor_Ctrl_IDB", "Scale_Analog_FC", "Safety_Door_FB", "HMI_Interface_DB", "Motor_Start_FB"];
                BlockKind[] kinds = [BlockKind.OB, BlockKind.FB, BlockKind.DB, BlockKind.FC, BlockKind.FB, BlockKind.DB, BlockKind.FB];
                int[] numbers = [1, 120, 121, 30, 200, 300, 110];
                string[] languages = ["LAD", "SCL", "DB", "SCL", "F-FBD", "DB", "FBD"];
                for (int i = 0; i < names.Length; i++) model.Blocks.Add(new BlockRow(new BlockInfo
                {
                    Name = names[i], Kind = kinds[i], Number = numbers[i], ProgrammingLanguage = languages[i],
                    Path = "Program blocks" + (i == 0 ? "" : "/" + names[i]), IsConsistent = i != 4, IsKnowHowProtected = i == 4,
                }) { Selected = i is 0 or 1 or 3 or 5 });
                string fixtureLog = CompileLog;
                if (language == AppLanguage.Chinese) fixtureLog = fixtureLog
                    .Replace("Connected to native Openness", "已连接原生 Openness")
                    .Replace("Project bound", "工程已绑定")
                    .Replace("Warning: 0 error(s), 3 warning(s) in 1.4s", Loc.Current.T("Status.CompileResult", "Warning", 0, 3, "1.4"))
                    .Replace("--- inspection of PLC_1 ---", Loc.Current.T("Log.InspectionHeader", "PLC_1"))
                    .Replace("5 finding(s) over 128 block(s).", Loc.Current.T("Status.InspectResult", 5, 128));
                Set(model, "Log", fixtureLog);
                if (vci)
                {
                    model.IsVcTab = true;
                    Set(model, "VcSupported", true);
                    var workspace = new WorkspaceInfo { Name = "Line04_Workspace", RootPath = @"D:\Workspaces\Line04", MappedObjectCount = 128 };
                    model.Workspaces.Add(workspace); model.SelectedWorkspace = workspace;
                    foreach (string name in new[] { names[1], names[4], names[5], names[3], names[0], names[6] }) model.VcStatusItems.Add(new MappedObjectInfo
                    { Name = name, FilePath = name + ".xml", FileFormat = "SimaticML", CompareState = name == "Main" || name == "Motor_Start_FB" ? VcCompareState.Equal : VcCompareState.Unequal });
                    Field(model,"_selectedVcItem",model.VcStatusItems[0]);
                    Set(model, "VcDiffCaption", "Conveyor_Ctrl_FB.xml");
                    foreach (string line in new[] { "@@ -212,7 +212,18 @@", "   <Member Name=\"Speed_SP\"", "-    Datatype=\"Int\" />", "+    Datatype=\"Real\">", "+    <Comment>Setpoint mm/min</Comment>", "+  </Member>", "+  <Member Name=\"Ramp_Up\"", "+    Datatype=\"Time\" />", "+  <Member Name=\"Enable\"", "+    Datatype=\"Bool\" />" }) model.VcDiffLines.Add(new DiffLine
                    { Text = line, Kind = line.StartsWith('+') ? DiffLineKind.Added : line.StartsWith('-') ? DiffLineKind.Removed : DiffLineKind.Context });
                    Set(model, "Log", "09:54:00  Mapped 128, already 0, unsupported 6, failed 0.\n09:54:01  Dry run: 4 would sync ProjectToWorkspace, 124 already equal. Clear Dry run to apply.\n");
                }
                var root = (FrameworkElement)window.Content;
                window.Content = null;
                var host = new Border { Child = root, DataContext = model, Width = 1200, Height = 780 };
                NameScope.SetNameScope(host, NameScope.GetNameScope(window));
                host.Measure(new Size(1200, 780));host.Arrange(new Rect(0,0,1200,780));host.UpdateLayout();
                host.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                host.UpdateLayout();
                Assert.Equal(theme, ThemeManager.Current.Theme);
                if (vci) Assert.True(((Border)window.FindName("SyncThumb")).ActualWidth > 80);
                var bitmap = new RenderTargetBitmap(1200,780,96,96,PixelFormats.Pbgra32);bitmap.Render(host);
                string? output = Environment.GetEnvironmentVariable("TIA_GLASS_SCREENSHOTS");
                if (!string.IsNullOrEmpty(output))
                {
                    Directory.CreateDirectory(output);
                    var encoder = new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(output, "studio-" + (vci ? "vci" : "blocks") + (language == AppLanguage.Chinese ? "-zh-" : "-") + theme.ToString().ToLowerInvariant() + ".png"));encoder.Save(file);
                }
                Assert.Equal(1200, bitmap.PixelWidth);
                Assert.Equal(780, bitmap.PixelHeight);
            }
            finally { window.Close(); ThemeManager.Current.Theme = oldTheme; }
        });
    }
}
