using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;
using TiaOpenness.Client;

namespace TiaOpenness.Gui;

/// <summary>Uses the engine's existing discovery, preflight and dispatch contracts.</summary>
public sealed class McpToolsWindow : Window
{
    private readonly BridgeClient client;
    private readonly TextBox name = new() { Text = "GetVersionControlWorkspaces", Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBox arguments = new() { Text = "{}", AcceptsReturn = true, Height = 130, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox result = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Button discover = new() { Content = "Find tools / 查找工具", Margin = new Thickness(0, 8, 8, 8) };
    private readonly Button run = new() { Content = "Run exact call / 执行调用", Margin = new Thickness(0, 8, 0, 8) };
    public McpToolsWindow(BridgeClient client)
    {
        this.client = client;
        Title = "TIA MCP tools"; Width = 880; Height = 700;
        var layout = new DockPanel { Margin = new Thickness(16) };
        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = "Tool name / 工具名称" }); top.Children.Add(name);
        top.Children.Add(new TextBlock { Text = "Arguments (JSON) / 参数" }); top.Children.Add(arguments);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(discover); buttons.Children.Add(run); top.Children.Add(buttons);
        DockPanel.SetDock(top, Dock.Top); layout.Children.Add(top); layout.Children.Add(result); Content = layout;
        discover.Click += async (_, _) => await Call(false);
        run.Click += async (_, _) => await Call(true);
    }
    private async Task Call(bool execute)
    {
        try
        {
            var tool = name.Text.Trim(); var p = JObject.Parse(arguments.Text);
            if (execute && MessageBox.Show(this, tool + "\n" + p + "\n\nThis call can change the project or files. / 本次调用可能修改工程或文件。",
                "Execute exact MCP call / 执行精确调用", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            discover.IsEnabled = run.IsEnabled = false;
            result.Text = (await client.InspectToolAsync(tool, p, execute)).ToString();
        }
        catch (Exception ex) { result.Text = ex.Message; }
        finally { discover.IsEnabled = run.IsEnabled = true; }
    }
}
