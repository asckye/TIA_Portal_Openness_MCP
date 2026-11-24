using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using TiaOpenness.Gui.ViewModels;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Shared;

namespace TiaMcpConfigurator;

internal sealed record AdapterAddress(string Name, string Description, NetworkInterfaceType Type, OperationalStatus Status, IPAddress Address)
{
    private bool Matches(string text) => Name.Contains(text, StringComparison.OrdinalIgnoreCase)
        || Description.Contains(text, StringComparison.OrdinalIgnoreCase);

    internal bool Recommended => Matches("Host-Only") || Matches("host only") || Name.EndsWith("VMnet1", StringComparison.OrdinalIgnoreCase)
        || Description.EndsWith("VMnet1", StringComparison.OrdinalIgnoreCase);
    internal bool Eligible => Status == OperationalStatus.Up && Address.AddressFamily == AddressFamily.InterNetwork
        && Type is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp)
        && !Matches("VPN") && !Matches("TAP-Windows") && !Matches("WireGuard") && !Matches("Wintun")
        && !IPAddress.IsLoopback(Address) && Address.GetAddressBytes() is not [169, 254, _, _] && RemoteProxyPolicy.BindAddressAllowed(Address);

    internal static IEnumerable<AdapterAddress> Scan() => Scan(NetworkInterface.GetAllNetworkInterfaces(),
        nic => nic.GetIPProperties().UnicastAddresses.Select(ip => ip.Address));

    internal static IEnumerable<AdapterAddress> Scan(IEnumerable<NetworkInterface> adapters, Func<NetworkInterface, IEnumerable<IPAddress>> readUnicast)
    {
        foreach (var nic in adapters)
        {
            AdapterAddress[] addresses;
            try
            {
                var status = nic.OperationalStatus;
                if (status != OperationalStatus.Up) continue;
                addresses = readUnicast(nic).Select(ip => new AdapterAddress(nic.Name, nic.Description, nic.NetworkInterfaceType, status, ip)).ToArray();
            }
            catch (Exception ex) when (ex is NetworkInformationException or SocketException or UnauthorizedAccessException)
            {
                // The adapter can disappear while Windows is reading its addresses.
                continue;
            }
            foreach (var address in addresses) yield return address;
        }
    }
}

public sealed partial class ConfigurationView
{
    internal Func<IEnumerable<AdapterAddress>> ReadAddresses { get; set; } = AdapterAddress.Scan;

    private void OnScanAddresses(object sender, RoutedEventArgs e) => Guard(ShowAddressMenu);

    private void ShowAddressMenu()
    {
        var menu = new ContextMenu { PlacementTarget = ScanAddresses, Placement = PlacementMode.Bottom };
        foreach (var adapter in ReadAddresses().Where(row => row.Eligible).OrderByDescending(row => row.Recommended).ThenBy(row => row.Name))
        {
            string label = adapter.Recommended ? Loc.Current["Config.Recommended"] : "LAN";
            var header = new TextBlock { Text = $"{adapter.Address}   {adapter.Name} · {label}" };
            var item = new MenuItem { Header = header, ToolTip = adapter.Description,
                IsChecked = ServerAddress.Text == adapter.Address.ToString() };
            item.Click += (_, _) => ServerAddress.Text = adapter.Address.ToString();
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = Loc.Current["Config.NoAdapters"], IsEnabled = false });
        menu.Items.Add(new Separator());
        var rescan = new MenuItem { Header = Loc.Current["Config.Rescan"] };
        rescan.Click += (_, _) => Guard(ShowAddressMenu);
        menu.Items.Add(rescan);
        ScanAddresses.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void UpdateStdioCommand()
    {
        if (Remote) return;
        try
        {
            string engine = ConfigCore.Engine(root, SelectedVersion);
            StdioCommand.Text = string.Join(" ", ConfigCore.LocalArguments(engine, SelectedVersion, TiaPath.Text).Prepend(engine).Select(ConfigCore.Quote));
            CopyStdio.ToolTip = StdioCommand.Text;
            CopyStdio.IsEnabled = true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            StdioCommand.Text = Loc.Current.T("Config.EngineNotFound", TiaMcp.Versioning.TiaVersionCatalog.Get(SelectedVersion).DisplayName);
            CopyStdio.ToolTip = ex.Message;
            CopyStdio.IsEnabled = false;
        }
    }

    private void CopyConnection(string value)
    {
        CopyClientText(value);
        Notify(ToastKind.Success, LocalizedText.Key("Config.ConnectionCopied"), "copy");
    }
}
