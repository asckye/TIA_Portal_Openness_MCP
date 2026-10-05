using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TiaOpenness.Gui.Controls;
using TiaOpenness.Gui.Themes;
using TiaOpenness.Gui.Views;
using Xunit;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class LogPresentationTests(WpfContext wpf)
{
    [Theory]
    [InlineData("[INFO] 工程已加载", LogLevel.Info)]
    [InlineData("Information: project loaded", LogLevel.Info)]
    [InlineData("信息：工程已加载", LogLevel.Info)]
    [InlineData("[warn] 块需要编译", LogLevel.Warning)]
    [InlineData("Warning: Safety_Door_FB - Block not consistent", LogLevel.Warning)]
    [InlineData("警告：块需要编译", LogLevel.Warning)]
    [InlineData("  [Error] PLC_1: compile failed", LogLevel.Error)]
    [InlineData("error: import failed", LogLevel.Error)]
    [InlineData("错误:导入失败", LogLevel.Error)]
    [InlineData("FAILED Main: import failed", LogLevel.Error)]
    [InlineData("失败 Main:导入失败", LogLevel.Error)]
    [InlineData("[DEBUG] 读取列表", LogLevel.Debug)]
    [InlineData("调试：读取列表", LogLevel.Debug)]
    [InlineData("Project bound · Conveyor_Line_04", LogLevel.Default)]
    [InlineData("Exported D:\\Warning\\Error.xml", LogLevel.Default)]
    [InlineData("DebugBlock imported", LogLevel.Default)]
    [InlineData("Compiled · 0 errors, 0 warnings", LogLevel.Default)]
    public void Typed_severity_controls_colour_independently_of_message_language(string message, LogLevel level)
    {
        var row = new GlassResults.LogRow("20:45:00", message, level);
        Assert.Equal(level, row.Level);
        Assert.Equal(message, row.Message);
    }

    [Fact]
    public void Existing_log_rows_follow_light_dark_and_light_theme_tokens()
    {
        wpf.Run(() =>
        {
            using var trace = new BindingPathTests.BindingTrace();
            var previous = ThemeManager.Current.Theme;
            var model = ProjectPageTestSupport.Model();
            model.Activity.ClearLog();
            model.Activity.Append("[INFO] 工程已加载", Services.WorkbenchActivity.Severity.Info);
            model.Activity.Append("Warning: 块需要编译", Services.WorkbenchActivity.Severity.Warning);
            model.Activity.Append("错误:导入失败", Services.WorkbenchActivity.Severity.Error);
            model.Activity.Append("[DEBUG] 读取列表", Services.WorkbenchActivity.Severity.Debug);
            model.Activity.Append("Plain message");
            var window = new MainWindow(model, false);
            try
            {
                window.ShowConfiguration(false);
                window.Navigate("Log");
                var host = (FrameworkElement)window.Content;
                var view = (LogView)window.FindName("LogContent");
                foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark, AppTheme.Light })
                {
                    ThemeManager.Current.Theme = theme;
                    WorkbenchRenderFeaturePagesTests.Layout(host);
                    var messages = WorkbenchRenderFeaturePagesTests.Descendants<TextBlock>(view)
                        .Where(t => t.DataContext is GlassResults.LogRow row && t.Text == row.Message).ToArray();
                    Assert.Equal(5, messages.Length);
                    string[] tokens = ["Ui.Accent", "Ui.Orange", "Ui.Red", "Ui.SecondaryLabel", "Ui.LogText"];
                    for (int i = 0; i < messages.Length; i++)
                        Assert.Equal(((SolidColorBrush)Application.Current.FindResource(tokens[i])).Color,
                            ((SolidColorBrush)messages[i].Foreground).Color);
                }
            }
            finally { window.Close(); ThemeManager.Current.Theme = previous; }
            Assert.True(string.IsNullOrWhiteSpace(trace.Text), trace.Text);
        });
    }
}
