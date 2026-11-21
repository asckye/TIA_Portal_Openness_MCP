using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TiaOpenness.Gui.Controls;

/// <summary>Localized page-state content shared by the empty and error templates.</summary>
public sealed class WorkbenchState : Control
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(WorkbenchState), new PropertyMetadata(""));
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(WorkbenchState), new PropertyMetadata(""));
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }

    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint), typeof(string), typeof(WorkbenchState), new PropertyMetadata(""));
    public string Hint { get => (string)GetValue(HintProperty); set => SetValue(HintProperty, value); }

    public static readonly DependencyProperty DetailsProperty = DependencyProperty.Register(
        nameof(Details), typeof(string), typeof(WorkbenchState), new PropertyMetadata(""));
    public string Details { get => (string)GetValue(DetailsProperty); set => SetValue(DetailsProperty, value); }

    public static readonly DependencyProperty ActionLabelProperty = DependencyProperty.Register(
        nameof(ActionLabel), typeof(string), typeof(WorkbenchState), new PropertyMetadata(""));
    public string ActionLabel { get => (string)GetValue(ActionLabelProperty); set => SetValue(ActionLabelProperty, value); }

    public static readonly DependencyProperty ActionCommandProperty = DependencyProperty.Register(
        nameof(ActionCommand), typeof(ICommand), typeof(WorkbenchState), new PropertyMetadata(null));
    public ICommand? ActionCommand { get => (ICommand?)GetValue(ActionCommandProperty); set => SetValue(ActionCommandProperty, value); }

    public static readonly DependencyProperty ExportCommandProperty = DependencyProperty.Register(
        nameof(ExportCommand), typeof(ICommand), typeof(WorkbenchState), new PropertyMetadata(null));
    public ICommand? ExportCommand { get => (ICommand?)GetValue(ExportCommandProperty); set => SetValue(ExportCommandProperty, value); }
}
