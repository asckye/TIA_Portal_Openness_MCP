using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using TiaMcp.Logic.V4.Inputs;

namespace TiaMcp.Logic.V4.Hmi
{
    [JsonConverter(typeof(HmiJsonConverterFactory))]
    public sealed class UnifiedThemeSpec : HmiObject
    {
        public AttributeMap<string> Palette { get; }
        public string? Name { get; }

        public UnifiedThemeSpec(IReadOnlyDictionary<string, string> palette, string? name = null)
        {
            Palette = new AttributeMap<string>(palette);
            Name = name;
            Validate();
        }

        internal override void Validate()
        {
            HmiRules.Palette(Palette);
        }
    }

    [JsonConverter(typeof(HmiJsonConverterFactory))]
    public sealed class UnifiedScreenSpec : HmiObject
    {
        public IReadOnlyList<UnifiedScreenItem> Items { get; }
        public UnifiedScreen? Screen { get; }

        public UnifiedScreenSpec(IReadOnlyList<UnifiedScreenItem> items, UnifiedScreen? screen = null)
        {
            Items = V4Validation.List(items);
            Screen = screen;
            Validate();
        }
    }

    [JsonConverter(typeof(HmiJsonConverterFactory))]
    public sealed class UnifiedScreen : HmiObject
    {
        public string Name { get; }
        public double? Width { get; }
        public double? Height { get; }
        public AttributeMap<Scalar>? Properties { get; }

        public UnifiedScreen(string name, double? width = null, double? height = null,
            IReadOnlyDictionary<string, Scalar>? properties = null)
        {
            Name = name;
            Width = width;
            Height = height;
            Properties = properties == null ? null : new AttributeMap<Scalar>(properties);
            Validate();
        }

        internal override void Validate()
        {
            V4Validation.Require(Name != null, "Screen name is required.");
            HmiRules.Finite(Width, Height);
            HmiRules.Properties(Properties);
            V4Validation.Require(Properties == null || !Properties.Keys.Any(k => k == "Name" || k == "Width" || k == "Height"),
                "Screen identity and size belong in the dedicated fields.");
        }
    }

    [JsonConverter(typeof(HmiJsonConverterFactory))]
    public abstract class UnifiedScreenItem : HmiObject
    {
        public abstract string Type { get; }
        public string Name { get; }
        public double? Left { get; }
        public double? Top { get; }
        public double? Width { get; }
        public double? Height { get; }
        public AttributeMap<Scalar>? Properties { get; }

        internal UnifiedScreenItem(string name, double? left, double? top, double? width, double? height,
            IReadOnlyDictionary<string, Scalar>? properties)
        {
            V4Validation.Text(name, "name");
            HmiRules.Finite(left, top, width, height);
            Name = name; Left = left; Top = top; Width = width; Height = height;
            Properties = properties == null ? null : new AttributeMap<Scalar>(properties);
        }
    }

    [JsonConverter(typeof(HmiJsonConverterFactory))]
    public sealed class UnifiedTextItem : UnifiedScreenItem
    {
        public override string Type => "Text";
        public string? Text { get; }
        public string? Culture { get; }
        public string? TextProperty { get; }
        public AttributeMap<Scalar>? Font { get; }
        public AttributeMap<Scalar>? Content { get; }
        public AttributeMap<Scalar>? Padding { get; }

        public UnifiedTextItem(string name, double? left = null, double? top = null, double? width = null,
            double? height = null, IReadOnlyDictionary<string, Scalar>? properties = null, string? text = null,
            string? culture = null, string? textProperty = null, IReadOnlyDictionary<string, Scalar>? font = null,
            IReadOnlyDictionary<string, Scalar>? content = null,
            IReadOnlyDictionary<string, Scalar>? padding = null)
            : base(name, left, top, width, height, properties)
        {
            Text = text;
            Culture = culture;
            TextProperty = textProperty;
            Font = font == null ? null : new AttributeMap<Scalar>(font);
            Content = content == null ? null : new AttributeMap<Scalar>(content);
            Padding = padding == null ? null : new AttributeMap<Scalar>(padding);
            Validate();
        }

        internal override void Validate()
        {
            HmiRules.UnifiedProperties(Type, Properties);
            HmiRules.TextRequest(Text, Culture, TextProperty);
            HmiRules.Properties(Font); HmiRules.Properties(Content); HmiRules.Properties(Padding);
        }
    }

    [JsonConverter(typeof(HmiJsonConverterFactory))]
    public sealed class UnifiedButtonItem : UnifiedScreenItem
    {
        public override string Type => "Button";
        public string? Text { get; }
        public string? Culture { get; }
        public string? TextProperty { get; }
        public AttributeMap<Scalar>? Font { get; }
        public AttributeMap<Scalar>? Content { get; }
        public AttributeMap<Scalar>? Padding { get; }

        public UnifiedButtonItem(string name, double? left = null, double? top = null, double? width = null,
            double? height = null, IReadOnlyDictionary<string, Scalar>? properties = null, string? text = null,
            string? culture = null, string? textProperty = null, IReadOnlyDictionary<string, Scalar>? font = null,
            IReadOnlyDictionary<string, Scalar>? content = null,
            IReadOnlyDictionary<string, Scalar>? padding = null)
            : base(name, left, top, width, height, properties)
        {
            Text = text;
            Culture = culture;
            TextProperty = textProperty;
            Font = font == null ? null : new AttributeMap<Scalar>(font);
            Content = content == null ? null : new AttributeMap<Scalar>(content);
            Padding = padding == null ? null : new AttributeMap<Scalar>(padding);
            Validate();
        }

        internal override void Validate()
        {
            HmiRules.UnifiedProperties(Type, Properties);
            HmiRules.TextRequest(Text, Culture, TextProperty);
            HmiRules.Properties(Font); HmiRules.Properties(Content); HmiRules.Properties(Padding);
        }
    }

    [JsonConverter(typeof(HmiJsonConverterFactory))]
    public sealed class UnifiedIoFieldItem : UnifiedScreenItem
    {
        public override string Type => "IOField";
        public string? Text { get; }
        public string? Culture { get; }
        public string? TextProperty { get; }
        public AttributeMap<Scalar>? Font { get; }
        public AttributeMap<Scalar>? Content { get; }
        public AttributeMap<Scalar>? Padding { get; }

        public UnifiedIoFieldItem(string name, double? left = null, double? top = null, double? width = null,
            double? height = null, IReadOnlyDictionary<string, Scalar>? properties = null, string? text = null,
            string? culture = null, string? textProperty = null, IReadOnlyDictionary<string, Scalar>? font = null,
            IReadOnlyDictionary<string, Scalar>? content = null,
            IReadOnlyDictionary<string, Scalar>? padding = null)
            : base(name, left, top, width, height, properties)
        {
            Text = text;
            Culture = culture;
            TextProperty = textProperty;
            Font = font == null ? null : new AttributeMap<Scalar>(font);
            Content = content == null ? null : new AttributeMap<Scalar>(content);
            Padding = padding == null ? null : new AttributeMap<Scalar>(padding);
            Validate();
        }

        internal override void Validate()
        {
            HmiRules.UnifiedProperties(Type, Properties);
            HmiRules.TextRequest(Text, Culture, TextProperty);
            HmiRules.Properties(Font); HmiRules.Properties(Content); HmiRules.Properties(Padding);
        }
    }

    [JsonConverter(typeof(HmiJsonConverterFactory))]
    public sealed class UnifiedRectangleItem : UnifiedScreenItem
    {
        public override string Type => "Rectangle";

        public UnifiedRectangleItem(string name, double? left = null, double? top = null, double? width = null,
            double? height = null, IReadOnlyDictionary<string, Scalar>? properties = null)
            : base(name, left, top, width, height, properties)
        {
            Validate();
        }

        internal override void Validate()
        {
            HmiRules.UnifiedProperties(Type, Properties);
        }
    }

    [JsonConverter(typeof(HmiJsonConverterFactory))]
    public sealed class UnifiedLayoutSpec : HmiObject
    {
        public IReadOnlyList<LayoutItem> Items { get; }
        public int? Columns { get; }
        public int? CellWidth { get; }
        public int? CellHeight { get; }
        public int? Grid { get; }
        public int? Left { get; }
        public int? Top { get; }
        public int? Gap { get; }
        public UnifiedScreen? Screen { get; }

        public UnifiedLayoutSpec(IReadOnlyList<LayoutItem> items, int? columns = null, int? cellWidth = null,
            int? cellHeight = null, int? grid = null, int? left = null, int? top = null, int? gap = null,
            UnifiedScreen? screen = null)
        {
            Items = V4Validation.List(items);
            Columns = columns;
            CellWidth = cellWidth;
            CellHeight = cellHeight;
            Grid = grid;
            Left = left;
            Top = top;
            Gap = gap;
            Screen = screen;
            Validate();
        }
    }

    [JsonConverter(typeof(HmiJsonConverterFactory))]
    public sealed class LayoutItem : HmiObject
    {
        public string Name { get; }
        public string? Type { get; }
        public int? Row { get; }
        public int? Col { get; }
        public int? RowSpan { get; }
        public int? ColSpan { get; }
        public string? Text { get; }
        public string? Culture { get; }
        public AttributeMap<Scalar>? Properties { get; }
        public AttributeMap<Scalar>? Font { get; }
        public AttributeMap<Scalar>? Content { get; }
        public AttributeMap<Scalar>? Padding { get; }

        public LayoutItem(string name, string? type = null, int? row = null, int? col = null, int? rowSpan = null,
            int? colSpan = null, string? text = null, string? culture = null,
            IReadOnlyDictionary<string, Scalar>? properties = null,
            IReadOnlyDictionary<string, Scalar>? font = null,
            IReadOnlyDictionary<string, Scalar>? content = null,
            IReadOnlyDictionary<string, Scalar>? padding = null)
        {
            Name = name;
            Type = type;
            Row = row;
            Col = col;
            RowSpan = rowSpan;
            ColSpan = colSpan;
            Text = text;
            Culture = culture;
            Properties = properties == null ? null : new AttributeMap<Scalar>(properties);
            Font = font == null ? null : new AttributeMap<Scalar>(font);
            Content = content == null ? null : new AttributeMap<Scalar>(content);
            Padding = padding == null ? null : new AttributeMap<Scalar>(padding);
            Validate();
        }

        internal override void Validate()
        {
            V4Validation.Text(Name, "name");
            V4Validation.Require(Type == null || HmiContracts.Unified.ContainsKey(Type), "Unknown Unified layout control.");
            HmiRules.UnifiedProperties(Type ?? "Rectangle", Properties);
            V4Validation.Require((Type ?? "Rectangle") != "Rectangle" || Text == null && Font == null && Content == null && Padding == null,
                "Rectangle has no text, font, content or padding.");
            HmiRules.TextRequest(Text, Culture, null);
            HmiRules.Properties(Font); HmiRules.Properties(Content); HmiRules.Properties(Padding);
        }
    }

}
