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
        string log = "20:39:16  → " + Loc.Current["Shell.Environment"] + "\n20:39:27  → " + Loc.Current["Shell.Audit"] + "\n20:39:33  → " + Loc.Current["Shell.Calls"] + "\n20:40:14  → " + Loc.Current["Shell.Engineering"] + "\n";
        if (connected) log += "20:40:58  Connecting · D:\\Projects\\Line04\n20:41:00  Project bound · Conveyor_Line_04\n20:41:09  → " + Loc.Current["Tree.ProgramBlocks"] + "\n";
        if (results)
        {
            log += "20:42:48  Warning: Safety_Door_FB - Block not consistent — recompile required\n20:42:48  Warning: Scale_Analog_FC - Unused temp variable #tmpRaw\n20:42:48  Warning: HMI_Interface_DB - Optimized access mismatch\n";
            log += "20:42:48  " + Loc.Current.T("Status.CompileResult", "Warning", 0, 3, "1.4") + "\n20:43:00  " + Loc.Current.T("Log.InspectionHeader", "PLC_1") + "\n20:43:00  NAMING-001 (3)\n20:43:00  DOC-001 (1)\n20:43:00  BUILD-001 (1)\n20:43:00  " + Loc.Current.T("Status.InspectResult", 5, 9) + "\n";
        }
        if (workspace && connected) log += "20:44:00  " + Loc.Current.T("Status.VcMapApplied", 9, 0, 2, 0) + "\n20:44:01  " + Loc.Current.T("Status.VcSyncDry", 4, "ProjectToWorkspace", 5) + "\n";
        typeof(Services.WorkbenchActivity).GetProperty("Log")!.SetValue(model.Activity, log);
        return model;
    }
}
