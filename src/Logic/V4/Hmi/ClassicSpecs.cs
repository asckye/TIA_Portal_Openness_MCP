using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace TiaMcp.Logic.V4.Hmi
{
    [JsonConverter(typeof(HmiJsonConverterFactory))]
    public sealed class ClassicScreenSpec : HmiObject
    {
        public IReadOnlyList<ClassicScreenItem> Items { get; }
        public ClassicScreen? Screen { get; }

        public ClassicScreenSpec(IReadOnlyList<ClassicScreenItem> items, ClassicScreen? screen = null)
        {
            Items = V4Validation.List(items);
            Screen = screen;
            Validate();
        }

        internal override void Validate()
        {
            HmiRules.ClassicScreen(this);
        }
    }

    [JsonConverter(typeof(HmiJsonConverterFactory))]
    public sealed class ClassicScreen : HmiObject
    {
        public string Name { get; }
        public int? Width { get; }
        public int? Height { get; }
        public string? BackColor { get; }
        public int? Number { get; }

        public ClassicScreen(string name, int? width = null, int? height = null, string? backColor = null,
            int? number = null)
        {
            Name = name;
            Width = width;
            Height = height;
            BackColor = backColor;
            Number = number;
            Validate();
        }

        internal override void Validate()
        {
            V4Validation.Require(Name != null, "Screen name is required.");
            V4Validation.Require((Width ?? 640) >= 320 && (Height ?? 480) >= 240, "Classic screen must be at least 320x240.");
            HmiRules.ClassicColor(BackColor);
        }
    }

    [JsonConverter(typeof(HmiJsonConverterFactory))]
    public abstract class ClassicScreenItem : HmiObject
    {
        public abstract string Type { get; }
        public string Name { get; }
        public int? Left { get; }
        public int? Top { get; }
        public int? Width { get; }
        public int? Height { get; }
        public string? BackColor { get; }
        public string? ProcessValueTag { get; }
        public IReadOnlyList<ClassicAction>? Actions { get; }

        internal ClassicScreenItem(string name, int? left, int? top, int? width, int? height,
            string? backColor, string? processValueTag, IReadOnlyList<ClassicAction>? actions)
        {
            V4Validation.Text(name, "name");
            V4Validation.Require((width ?? 120) > 0 && (height ?? 40) > 0 && (left ?? 0) >= 0 && (top ?? 0) >= 0,
                "Classic item size must be positive and position nonnegative.");
            HmiRules.ClassicColor(backColor);
            Name = name; Left = left; Top = top; Width = width; Height = height;
            BackColor = backColor; ProcessValueTag = processValueTag; Actions = HmiContracts.OptionalList(actions);
        }
    }

    [JsonConverter(typeof(HmiJsonConverterFactory))]
    public sealed class ClassicTextItem : ClassicScreenItem
    {
        public override string Type => "Text";
        public ClassicTextProperties? Properties { get; }
        public ClassicText? Text { get; }

        public ClassicTextItem(string name, int? left = null, int? top = null, int? width = null, int? height = null,
            string? backColor = null, string? processValueTag = null, IReadOnlyList<ClassicAction>? actions = null,
            ClassicTextProperties? properties = null, ClassicText? text = null)
            : base(name, left, top, width, height, backColor, processValueTag, actions)
        {
            Properties = properties;
            Text = text;
            Validate();
        }
        internal override void Validate() => HmiRules.ClassicColor(BackColor ?? Properties?.BackColor);
    }

    [JsonConverter(typeof(HmiJsonConverterFactory))]
    public sealed class ClassicButtonItem : ClassicScreenItem
    {
        public override string Type => "Button";
        public ClassicButtonProperties? Properties { get; }
        public ClassicText? Text { get; }

        public ClassicButtonItem(string name, int? left = null, int? top = null, int? width = null, int? height = null,
            string? backColor = null, string? processValueTag = null, IReadOnlyList<ClassicAction>? actions = null,
            ClassicButtonProperties? properties = null, ClassicText? text = null)
            : base(name, left, top, width, height, backColor, processValueTag, actions)
        {
            Properties = properties;
            Text = text;
            Validate();
        }
        internal override void Validate() => HmiRules.ClassicColor(BackColor ?? Properties?.BackColor);
    }

    [JsonConverter(typeof(HmiJsonConverterFactory))]
    public sealed class ClassicIoFieldItem : ClassicScreenItem
    {
        public override string Type => "IOField";
        public ClassicIoFieldProperties? Properties { get; }

        public ClassicIoFieldItem(string name, int? left = null, int? top = null, int? width = null,
            int? height = null, string? backColor = null, string? processValueTag = null,
            IReadOnlyList<ClassicAction>? actions = null, ClassicIoFieldProperties? properties = null)
            : base(name, left, top, width, height, backColor, processValueTag, actions)
        {
            Properties = properties;
            Validate();
        }
        internal override void Validate() => HmiRules.ClassicColor(BackColor ?? Properties?.BackColor);
    }

    [JsonConverter(typeof(HmiJsonConverterFactory))]
    public sealed class ClassicRectangleItem : ClassicScreenItem
    {
        public override string Type => "Rectangle";
        public ClassicShapeProperties? Properties { get; }

        public ClassicRectangleItem(string name, int? left = null, int? top = null, int? width = null,
            int? height = null, string? backColor = null, string? processValueTag = null,
            IReadOnlyList<ClassicAction>? actions = null, ClassicShapeProperties? properties = null)
            : base(name, left, top, width, height, backColor, processValueTag, actions)
        {
            Properties = properties;
            Validate();
        }
        internal override void Validate() => HmiRules.ClassicColor(BackColor ?? Properties?.BackColor);
    }

    [JsonConverter(typeof(HmiJsonConverterFactory))]
    public sealed class ClassicLampItem : ClassicScreenItem
    {
        public override string Type => "Lamp";
        public ClassicShapeProperties? Properties { get; }

        public ClassicLampItem(string name, int? left = null, int? top = null, int? width = null, int? height = null,
            string? backColor = null, string? processValueTag = null, IReadOnlyList<ClassicAction>? actions = null,
            ClassicShapeProperties? properties = null)
            : base(name, left, top, width, height, backColor, processValueTag, actions)
        {
            Properties = properties;
            Validate();
        }
        internal override void Validate() => HmiRules.ClassicColor(BackColor ?? Properties?.BackColor);
    }

    [JsonConverter(typeof(HmiJsonConverterFactory))]
    public sealed class ClassicShapeProperties : HmiObject
    {
        public string? BackColor { get; }
        public string? BorderColor { get; }
        public int? BorderWidth { get; }

        public ClassicShapeProperties(string? backColor = null, string? borderColor = null, int? borderWidth = null)
        {
            BackColor = backColor;
            BorderColor = borderColor;
            BorderWidth = borderWidth;
            Validate();
        }

        internal override void Validate()
        {
            HmiRules.ClassicColor(BorderColor);
        }
    }

    [JsonConverter(typeof(HmiJsonConverterFactory))]
    public sealed class ClassicTextProperties : HmiObject
    {
        public string? BackColor { get; }
        public string? BorderColor { get; }
        public int? BorderWidth { get; }
        public string? ForeColor { get; }
        public int? FontSize { get; }

        public ClassicTextProperties(string? backColor = null, string? borderColor = null, int? borderWidth = null,
            string? foreColor = null, int? fontSize = null)
        {
            BackColor = backColor;
            BorderColor = borderColor;
            BorderWidth = borderWidth;
            ForeColor = foreColor;
            FontSize = fontSize;
            Validate();
        }

        internal override void Validate()
        {
            HmiRules.ClassicColor(BorderColor);
            HmiRules.ClassicColor(ForeColor);
        }
    }

    [JsonConverter(typeof(HmiJsonConverterFactory))]
    public sealed class ClassicButtonProperties : HmiObject
    {
        public string? BackColor { get; }
        public string? BorderColor { get; }
        public int? BorderWidth { get; }
        public string? ForeColor { get; }
        public int? FontSize { get; }
        public int? TabIndex { get; }

        public ClassicButtonProperties(string? backColor = null, string? borderColor = null, int? borderWidth = null,
            string? foreColor = null, int? fontSize = null, int? tabIndex = null)
        {
            BackColor = backColor;
            BorderColor = borderColor;
            BorderWidth = borderWidth;
            ForeColor = foreColor;
            FontSize = fontSize;
            TabIndex = tabIndex;
            Validate();
        }

        internal override void Validate()
        {
            HmiRules.ClassicColor(BorderColor);
            HmiRules.ClassicColor(ForeColor);
        }
    }

    [JsonConverter(typeof(HmiJsonConverterFactory))]
    public sealed class ClassicIoFieldProperties : HmiObject
    {
        public string? BackColor { get; }
        public string? BorderColor { get; }
        public int? BorderWidth { get; }
        public string? ForeColor { get; }
        public int? FontSize { get; }
        public int? TabIndex { get; }
        public string? Mode { get; }

        public ClassicIoFieldProperties(string? backColor = null, string? borderColor = null, int? borderWidth = null,
            string? foreColor = null, int? fontSize = null, int? tabIndex = null, string? mode = null)
        {
            BackColor = backColor;
            BorderColor = borderColor;
            BorderWidth = borderWidth;
            ForeColor = foreColor;
            FontSize = fontSize;
            TabIndex = tabIndex;
            Mode = mode;
            Validate();
        }

        internal override void Validate()
        {
            HmiRules.ClassicColor(BorderColor);
            HmiRules.ClassicColor(ForeColor);
        }
    }

    [JsonConverter(typeof(HmiJsonConverterFactory))]
    public sealed class ClassicAction : HmiObject
    {
        public string Event { get; }
        public string ActionKind { get; }
        public string TargetTag { get; }

        public ClassicAction(string @event, string actionKind, string targetTag)
        {
            Event = @event;
            ActionKind = actionKind;
            TargetTag = targetTag;
            Validate();
        }

        internal override void Validate()
        {
            V4Validation.Text(Event, "event");
            V4Validation.Require(ActionKind == "SetBit" || ActionKind == "ResetBit", "Unknown Classic actionKind.");
            V4Validation.Text(TargetTag, "targetTag");
        }
    }

    [JsonConverter(typeof(HmiJsonConverterFactory))]
    public sealed class ClassicTagTableSpec : HmiObject
    {
        public string Name { get; }
        public IReadOnlyList<ClassicTag> Tags { get; }

        public ClassicTagTableSpec(string name, IReadOnlyList<ClassicTag> tags)
        {
            Name = name;
            Tags = V4Validation.List(tags);
            Validate();
        }

        internal override void Validate()
        {
            V4Validation.Text(Name, "name");
            V4Validation.Require(Tags.Count > 0, "At least one Classic tag is required.");
            HmiRules.UniqueNames(Tags.Select(t => t.Name));
        }
    }

    [JsonConverter(typeof(HmiJsonConverterFactory))]
    public sealed class ClassicTag : HmiObject
    {
        public string Name { get; }
        public string DataType { get; }
        public string? Length { get; }
        public string? Connection { get; }
        public string? ControllerTag { get; }

        public ClassicTag(string name, string dataType, string? length = null, string? connection = null,
            string? controllerTag = null)
        {
            Name = name;
            DataType = dataType;
            Length = length;
            Connection = connection;
            ControllerTag = controllerTag;
            Validate();
        }

        internal override void Validate()
        {
            V4Validation.Text(Name, "name");
            V4Validation.Require(DataType != null, "dataType is required.");
            V4Validation.Require(string.IsNullOrWhiteSpace(Connection) == string.IsNullOrWhiteSpace(ControllerTag),
                "Symbolic tags require both connection and controllerTag.");
        }
    }

    [JsonConverter(typeof(HmiJsonConverterFactory))]
    public sealed class ClassicPackageSpec : HmiObject
    {
        public string Name { get; }
        public ClassicScreenSpec ScreenDesign { get; }
        public ClassicTagTableSpec TagTable { get; }

        public ClassicPackageSpec(string name, ClassicScreenSpec screenDesign, ClassicTagTableSpec tagTable)
        {
            Name = name;
            ScreenDesign = screenDesign;
            TagTable = tagTable;
            Validate();
        }

        internal override void Validate()
        {
            V4Validation.Require(Name != null && ScreenDesign != null && TagTable != null, "Package fields are required.");
        }
    }

}
