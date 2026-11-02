using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Markup;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui.Controls;

internal sealed partial class PortalProcessPicker : Window
{
    internal PortalProcessPicker(PortalProcessPickerViewModel model)
    {
        InitializeComponent();
        DataContext = model;
        Language = XmlLanguage.GetLanguage(Loc.Current.Language == AppLanguage.Chinese ? "zh-CN" : "en-US");
        MaxWidth = Math.Max(640, SystemParameters.WorkArea.Width - 48);
        MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 48);
        model.CloseRequested += OnCloseRequested;
        Loaded += (_, _) => CancelButton.Focus();
        Closed += (_, _) => model.CloseRequested -= OnCloseRequested;
    }

    private void OnCloseRequested(object? sender, EventArgs args) => Close();
    private void OnPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key is not (Key.Enter or Key.Escape)) return;
        args.Handled = true;
        ((PortalProcessPickerViewModel)DataContext).Cancel.Execute(null);
    }

    internal static int? Show(IReadOnlyList<PortalProcessInfo> processes)
    {
        using var model = new PortalProcessPickerViewModel(processes);
        var dialog = new PortalProcessPicker(model);
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive);
        if (owner?.IsVisible == true) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
        return model.ResultProcessId;
    }
}
