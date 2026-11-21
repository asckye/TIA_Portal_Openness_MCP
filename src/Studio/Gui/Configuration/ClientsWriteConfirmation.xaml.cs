using System;
using System.Windows;
using System.Windows.Markup;
using TiaOpenness.Gui.Localization;

namespace TiaMcpConfigurator;

internal partial class ClientsWriteConfirmation : Window
{
    internal ClientsWriteConfirmation(int clientCount, string names, string files, string migration, bool remote)
    {
        InitializeComponent();
        Language = XmlLanguage.GetLanguage(Loc.Current.IsChinese ? "zh-CN" : "en-US");
        Title = Loc.Current.T("Config.ConfirmWrite", clientCount);
        Description.Text = Loc.Current["Config.ConfirmBody"] + " " + Loc.Current["Config.WritePrecautions"];
        TargetNames.Text = names;
        TargetFiles.Text = Loc.Current["Config.WriteClientsPrompt"] + files;
        if (remote) TargetFiles.Text += "\n\n" + Loc.Current["Config.RelayMode"];
        TargetFiles.Text += Loc.Current["Config.WriteClientsContinue"];
        Migration.Text = migration;
        Migration.Visibility = migration.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 80);
        Loaded += (_, _) => CancelWrite.Focus();
    }

    private void Cancel(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Confirm(object sender, RoutedEventArgs e) => DialogResult = true;
}
