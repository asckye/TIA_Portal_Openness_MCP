using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.ViewModels;
using TiaOpenness.Gui.Views;

namespace TiaOpenness.Gui.Tests;

internal static class ProjectPageTestSupport
{
    internal static object Find(MainWindow window, string name)
    {
        if (window.FindName(name) is { } own) return own;
        foreach (string host in new[] { "OperationsContent", "BlocksContent", "EngineeringSideContent", "VersionControlContent", "LogContent" })
        {
            var view = (UserControl)window.FindName(host);
            if (view.FindName(name) is { } element) return element;
            if (view.FindName("Options") is EngineeringOptionsView options && options.FindName(name) is { } option) return option;
        }
        throw new InvalidOperationException("No page element: " + name);
    }

    internal static MainViewModel Model(bool connected = true, bool results = false, bool workspace = true)
    {
        var client = new FakeStudioClient
        {
            AttachedProject = new ProjectInfo { Name = "Conveyor_Line_04", Path = @"D:\Projects\Line04\Conveyor_Line_04.ap14" },
        };
        client.Blocks.Clear();
        string[] names = ["Main", "Conveyor_Ctrl_FB", "Conveyor_Ctrl_IDB", "Scale_Analog_FC", "Safety_Door_FB", "HMI_Interface_DB", "Motor_Start_FB", "Alarm_Handler_FC", "Recipe_DB"];
        BlockKind[] kinds = [BlockKind.OB, BlockKind.FB, BlockKind.DB, BlockKind.FC, BlockKind.FB, BlockKind.DB, BlockKind.FB, BlockKind.FC, BlockKind.DB];
        int[] numbers = [1, 120, 121, 30, 200, 300, 110, 40, 310];
        string[] languages = ["LAD", "SCL", "DB", "SCL", "F-FBD", "DB", "FBD", "SCL", "DB"];
        string[] groups = ["", "Conveyor", "Conveyor", "Utilities", "Safety", "HMI", "Drives", "Utilities", "HMI"];
        for (int i = 0; i < names.Length; i++) client.Blocks.Add(new BlockInfo
        {
            Name = names[i], Kind = kinds[i], Number = numbers[i], ProgrammingLanguage = languages[i],
            Path = "Program blocks/" + (groups[i].Length > 0 ? groups[i] + "/" : "") + names[i], IsConsistent = i != 4,
        });
        client.Workspaces.Clear();
        if (workspace) client.Workspaces.Add(new WorkspaceInfo { Name = "Line04_Workspace", RootPath = @"D:\Workspaces\Line04", MappedObjectCount = 9 });
        foreach (int i in new[] { 1, 4, 5, 3, 0, 6, 2, 7, 8 }) client.StatusItems.Add(new MappedObjectInfo
        {
            Name = names[i], FilePath = names[i] + ".xml", FileFormat = "SimaticML",
            CompareState = i is 1 or 4 or 5 or 3 ? VcCompareState.Unequal : VcCompareState.Equal,
        });
        var model = new MainViewModel(client, new FakeDialogService()) { SelectedReleaseKey = "14sp1" };
        if (connected)
        {
            model.Session.Connect.Execute(null);
            foreach (int i in new[] { 0, 1, 3 }) model.Engineering.Blocks[i].Selected = true;
            model.Engineering.OutputDirectory = @"D:\Exports";
            model.VersionControl.VcDiffLines.Clear();
            foreach (string line in workspace ? new[] { "@@ -212,7 +212,18 @@", "   <Member Name=\"Speed_SP\"", "-    Datatype=\"Int\" />", "+    Datatype=\"Real\">", "+    <Comment>Setpoint mm/min</Comment>", "+  </Member>", "+  <Member Name=\"Ramp_Up\"", "+    Datatype=\"Time\" />", "+  <Member Name=\"Enable\"", "+    Datatype=\"Bool\" />" } : [])
                model.VersionControl.VcDiffLines.Add(new DiffLine { Text = line, Kind = line.StartsWith('+') ? DiffLineKind.Added : line.StartsWith('-') ? DiffLineKind.Removed : DiffLineKind.Context });
        }
        foreach (var key in new[] { "Shell.Environment", "Shell.Audit", "Shell.Calls", "Shell.Engineering" })
            model.Activity.Append("→ " + Loc.Current[key]);
        if (connected) model.Activity.Append("Connecting · D:\\Projects\\Line04\nProject bound · Conveyor_Line_04");
        if (results)
        {
            model.Activity.AppendDiagnostic("Safety_Door_FB", "Block not consistent — recompile required", Services.WorkbenchActivity.Severity.Warning);
            model.Activity.AppendDiagnostic("Scale_Analog_FC", "Unused temp variable #tmpRaw", Services.WorkbenchActivity.Severity.Warning);
            model.Activity.AppendDiagnostic("HMI_Interface_DB", "Optimized access mismatch", Services.WorkbenchActivity.Severity.Warning);
            model.Activity.AppendLocalized("Status.CompileResult", "Warning", 0, 3, "1.4");
            model.Activity.AppendLocalized("Log.InspectionHeader", "PLC_1");
            model.Activity.AppendRule("NAMING-001", 3);
            model.Activity.AppendRule("DOC-001", 1);
            model.Activity.AppendRule("BUILD-001", 1);
            model.Activity.AppendLocalized("Status.InspectResult", 5, 9);
        }
        if (workspace && connected)
        {
            model.Activity.AppendLocalized("Status.VcMapApplied", 9, 0, 2, 0);
            model.Activity.AppendLocalized("Status.VcSyncDry", 4, "ProjectToWorkspace", 5);
        }
        return model;
    }
}
