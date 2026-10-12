using System;
using System.Windows.Data;
using System.Windows.Markup;

namespace TiaOpenness.Gui.Localization;

/// <summary>
/// XAML shorthand for a live-updating catalogue lookup: <c>{l:Tr Toolbar.Connect}</c>.
///
/// It hands back a binding rather than a string, which is the whole point - a plain string
/// would freeze the label in whatever language was active when the window was built.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }

    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    /// <summary>Wraps the looked-up text, e.g. <c>StringFormat="{}{0}:"</c>.</summary>
    public string? StringFormat { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = CreateBinding(Key, StringFormat);
        if (serviceProvider.GetService(typeof(IProvideValueTarget)) is IProvideValueTarget target
            && target.TargetProperty == System.Windows.Automation.AutomationProperties.NameProperty)
            binding = NameBinding(binding);
        return binding.ProvideValue(serviceProvider);
    }

    internal static Binding NameBinding(Binding label) => new(label.Path.Path)
    {
        Source = label.Source, Mode = BindingMode.OneWay, Converter = AccessName.Instance,
    };

    private sealed class AccessName : IValueConverter
    {
        internal static readonly AccessName Instance = new();
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => value is string text ? text.Replace("__", "\u0001").Replace("_", "").Replace("\u0001", "_") : "";
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>Exposed so the tests can exercise the same path parsing the UI relies on.</summary>
    public static Binding CreateBinding(string key, string? stringFormat = null) => new("[" + key + "]")
    {
        Source = Loc.Current,
        Mode = BindingMode.OneWay,
        StringFormat = stringFormat,
    };
}
