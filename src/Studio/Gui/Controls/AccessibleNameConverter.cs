using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using TiaMcpConfigurator;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.ViewModels;
using TiaOpenness.Gui.Services;

namespace TiaOpenness.Gui.Controls;

public sealed class AccessibleNameConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 0 || values[0] == DependencyProperty.UnsetValue) return "";
        return values[0] switch
        {
            CallRow row => Loc.Current.T("Access.Call", row.Time, row.Tool, row.Target, row.Result, row.Actor),
            ApprovalRow row => Loc.Current.T("Access.Approval", row.Client, row.Request.Project, row.Request.Plc,
                string.Join(" · ", row.Request.Operations.Select(op => op.Action + " " + op.Object + " " + op.Tool)), row.State),
            AuditRow row => Loc.Current.T("Access.Audit", row.Time, row.Type, row.Record.ToolObject, row.Result, row.Actor),
            BlockRow row => Loc.Current.T("Access.Block", row.Name, row.Kind, row.Number, row.Path),
            DeviceNode node => node.Label,
            BlockNode node => node.Name + " · " + node.Detail,
            ResultPresentation.LogRow row => Loc.Current.T("Access.Log", row.Time, Level(row.Level), row.Message),
            ClientProfile client => client.Name,
            TiaMcp.Versioning.TiaVersionDescriptor release => release.DisplayName,
            OperatorSession session => session.DisplayName,
            McpProjectClient.Receipt receipt => receipt.Label,
            WorkspaceInfo workspace => workspace.Name,
            DeviceInfo device => device.DisplayName ?? device.Name,
            MappedObjectInfo item => item.Name + " · " + item.FilePath,
            DiffLine line => line.Text,
            GenerationDifferenceRow row => row.Text,
            GenerationStepRow row => row.Id + " · " + row.Phase + " · " + row.Key + " · " + row.Tool,
            string text => text.Replace("_", ""),
            _ => "",
        };
    }

    internal static string Level(LogLevel level) => level switch
    {
        LogLevel.Warning => Loc.Current["Access.Warning"], LogLevel.Error => Loc.Current["Access.Error"],
        LogLevel.Debug => Loc.Current["Access.Debug"], _ => Loc.Current["Access.Info"],
    };

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
