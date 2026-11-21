using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using TiaOpenness.Gui.Themes;

namespace TiaOpenness.Gui.Controls;

/// <summary>Paints the live workbench beneath an overlay, then applies the theme's scrim tint.</summary>
public sealed class WorkbenchScrim : Grid
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(FrameworkElement), typeof(WorkbenchScrim),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsArrange, OnAppearanceChanged));
    public static readonly DependencyProperty BlurRadiusProperty = DependencyProperty.Register(
        nameof(BlurRadius), typeof(double), typeof(WorkbenchScrim), new PropertyMetadata(5d, OnAppearanceChanged));

    public FrameworkElement? Source { get => (FrameworkElement?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public double BlurRadius { get => (double)GetValue(BlurRadiusProperty); set => SetValue(BlurRadiusProperty, value); }

    // A page-owned modal keeps its original focus scope, command routes and automation tree.
    // When it is already part of Source, leave its foreground surface clear.
    private Border? _surface;
    private readonly RectangleGeometry _bounds = new(), _cutout = new();
    internal Border? ForegroundSurface
    {
        get => _surface;
        set
        {
            _surface = value;
            Clip = value == null ? null : new CombinedGeometry(GeometryCombineMode.Exclude, _bounds, _cutout);
            TrackForeground();
        }
    }
    internal Rectangle Backdrop { get; } = new() { IsHitTestVisible = false };
    private readonly VisualBrush _brush = new() { AutoLayoutContent = false, ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };

    public WorkbenchScrim()
    {
        ClipToBounds = true;
        Background = Brushes.Transparent;
        Children.Add(Backdrop);
        var tint = new Border { IsHitTestVisible = false };
        tint.SetResourceReference(Border.BackgroundProperty, "Ant.colorBgMask");
        Children.Add(tint);
        var theme = ThemeManager.Current;
        PropertyChangedEventManager.AddHandler(theme, OnThemeChanged, nameof(theme.UseLowEffects));
        PropertyChangedEventManager.AddHandler(theme, OnThemeChanged, nameof(theme.EffectivelyHighContrast));
        UpdateAppearance();
    }

    private static void OnAppearanceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) => ((WorkbenchScrim)sender).UpdateAppearance();
    private void OnThemeChanged(object? sender, PropertyChangedEventArgs e) => UpdateAppearance();
    private void UpdateAppearance()
    {
        bool blur = Visibility == Visibility.Visible && Source != null && !ThemeManager.Current.UseLowEffects && !ThemeManager.Current.EffectivelyHighContrast;
        _brush.Visual = blur ? Source : null;
        Backdrop.Fill = blur ? _brush : null;
        Backdrop.Effect = blur ? new BlurEffect { Radius = BlurRadius, KernelType = KernelType.Gaussian } : null;
        Backdrop.Visibility = blur ? Visibility.Visible : Visibility.Collapsed;
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var size = base.ArrangeOverride(arrangeSize);
        if (Source != null && FindCommonVisualAncestor(Source) != null)
            _brush.Viewbox = new Rect(TranslatePoint(default, Source), size);
        return size;
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == VisibilityProperty)
        {
            UpdateAppearance();
            if (_surface != null) TrackForeground();
        }
    }

    private void TrackForeground()
    {
        LayoutUpdated -= OnLayoutUpdated;
        if (_surface != null && Visibility == Visibility.Visible) LayoutUpdated += OnLayoutUpdated;
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        // Page layout (including the foreground card) can finish after the shell scrim's own arrange pass.
        if (_surface is not { } surface || FindCommonVisualAncestor(surface) == null) return;
        var rect = surface.TransformToVisual(this).TransformBounds(new Rect(surface.RenderSize));
        if (_cutout.Rect != rect) _cutout.Rect = rect;
        if (_bounds.Rect.Size != RenderSize || _bounds.Rect.IsEmpty) _bounds.Rect = new Rect(RenderSize);
        double radius = surface.CornerRadius.TopLeft;
        if (_cutout.RadiusX != radius) _cutout.RadiusX = _cutout.RadiusY = radius;
    }
}
