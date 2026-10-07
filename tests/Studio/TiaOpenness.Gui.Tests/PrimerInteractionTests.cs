using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Themes;
using TiaOpenness.Gui.ViewModels;
using Xunit;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public class PrimerInteractionTests(WpfContext wpf)
{
    [Fact]
    public void Count_badges_hide_zero_and_pending_counts_use_the_warning_palette()
    {
        wpf.Run(() =>
        {
            var count = new Border { Style = (Style)Application.Current.FindResource("Primer.TabCountBadge"), Tag = 0 };
            var pending = new Border { Style = (Style)Application.Current.FindResource("Primer.PendingCountBadge"), Tag = 0, Child = new TextBlock() };
            var panel = new StackPanel(); panel.Children.Add(count); panel.Children.Add(pending);
            var window = new Window { Content = panel };
            WpfContext.Drain();
            Assert.Equal(Visibility.Collapsed, count.Visibility);
            Assert.Equal(Visibility.Collapsed, pending.Visibility);
            count.Tag = pending.Tag = 3; WpfContext.Drain();
            Assert.Equal(Visibility.Visible, count.Visibility);
            Assert.Equal(Visibility.Visible, pending.Visibility);
            var previous = ThemeManager.Current.Theme;
            try
            {
                foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
                {
                    ThemeManager.Current.Theme = theme; WpfContext.Drain();
                    Assert.Equal(theme == AppTheme.Light ? Color.FromRgb(255, 241, 229) : ((SolidColorBrush)Application.Current.FindResource("Primer.warnBg")).Color, ((SolidColorBrush)pending.Background).Color);
                    Assert.Equal(((SolidColorBrush)Application.Current.FindResource("Primer.warn")).Color, ((SolidColorBrush)pending.BorderBrush).Color);
                }
            }
            finally { window.Close(); ThemeManager.Current.Theme = previous; }
        });
    }

    private sealed class ProbeCommand(Action action) : ICommand
    {
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
    private static void Click(Button button) => typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, null);
    private static System.Collections.Generic.IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        {
            var child=VisualTreeHelper.GetChild(root,i);
            if(child is T match) yield return match;
            foreach(var nested in Find<T>(child)) yield return nested;
        }
    }
    [Fact]
    public void Preview_and_run_set_dry_run_before_executing_the_existing_command()
    {
        wpf.RunWithLanguage(AppLanguage.English, () =>
        {
            var window = new MainWindow(ProjectPageTestSupport.Model(), false);
            try
            {
                var model=(MainViewModel)window.DataContext;window.Navigate("VersionControl");
                var content=(FrameworkElement)window.Content;window.Content=null;
                var host=new Border { Child=content, DataContext=model };
                host.Measure(new Size(1200,780));host.Arrange(new Rect(0,0,1200,780));host.UpdateLayout();
                var preview=Find<Button>(host).Single(b=>Equals(b.Content,"Preview sync"));
                var run=Find<Button>(host).Single(b=>Equals(b.Content,"Run sync"));
                Assert.Same(model.VersionControl.VcPush,preview.Command);
                var direction=(RadioButton)ProjectPageTestSupport.Find(window, "ToWorkspace");
                direction.IsChecked=false;host.UpdateLayout();
                Assert.Same(model.VersionControl.VcPull,preview.Command);
                Assert.Same(model.VersionControl.VcPull,run.Command);
                bool? observed=null;
                preview.Command=new ProbeCommand(()=>observed=model.VersionControl.VcDryRun);
                model.VersionControl.VcDryRun=false;Click(preview);Assert.True(observed);
                run.Command=new ProbeCommand(()=>observed=model.VersionControl.VcDryRun);
                Click(run);Assert.False(observed);
            }
            finally {window.Close();}
        });
    }
    [Fact]
    public void Language_and_theme_menu_items_change_the_live_window_without_replacing_the_model()
    {
        wpf.RunWithLanguage(AppLanguage.English, () =>
        {
            var previous=ThemeManager.Current.Theme;
            var window=new MainWindow(new TiaOpenness.Gui.ViewModels.MainViewModel(), false);
            try
            {
                var model=window.DataContext;
                var content=(FrameworkElement)window.Content;window.Content=null;
                var host=new Border {Child=content,DataContext=model};
                host.Measure(new Size(1200,780));host.Arrange(new Rect(0,0,1200,780));host.UpdateLayout();
                UnifiedDesktopTests.ClickControl((RadioButton)((TiaOpenness.Gui.Views.SettingsView)window.FindName("SettingsContent")).FindName("Chinese"));
                Assert.True(Loc.Current.IsChinese);
                UnifiedDesktopTests.ClickControl((RadioButton)((TiaOpenness.Gui.Views.SettingsView)window.FindName("SettingsContent")).FindName("Dark"));
                Assert.Equal(AppTheme.Dark,ThemeManager.Current.Theme);
                Assert.Same(model,window.DataContext);
            }
            finally {window.Close();ThemeManager.Current.Theme=previous;}
        });
    }
}
