using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Composa.Model;

namespace Composa.App.Controls;

/// <summary>Edits the same canvas geometry in the new canvas dialog and in Properties.</summary>
internal sealed class CanvasLayoutEditor : StackPanel
{
    private readonly NumericUpDown[] safeFields;
    private readonly ToggleButton safeLink;
    private readonly TextBlock safeStatus;
    private readonly Dictionary<CanvasGridKind, (Button Button, Rectangle Outline, Icons.Icon Icon)> kinds = [];
    private readonly StackPanel trackDetails;
    private readonly Control squareDetails;
    private readonly NumericUpDown count, gap, margin, trackSize, trackOpacity, squareSize, squareOpacity, bleed;
    private readonly ComboBox sizing, alignment;
    private readonly TextBlock trackSizeLabel, computedSize, gridStatus;
    private readonly Control measuredSize, editableSize;
    private readonly Control alignmentRow;
    private readonly Button remove;
    private readonly Action<CanvasLayout> changed;
    private CanvasLayout layout = CanvasLayout.Empty;
    private CanvasGridKind? active;
    private int width, height;
    private bool syncing;

    public CanvasLayoutEditor(Action<CanvasLayout> changed)
    {
        this.changed = changed;
        Name = "CanvasLayoutEditor";
        Spacing = 13;

        safeLink = new ToggleButton { Name = "LinkSafeArea", Classes = { "layout-toggle" }, Content = Icons.Create(Icons.Link, 15) };
        SizeIconButton(safeLink);
        Locale.Tip(safeLink, "Link safe area sides");
        AutomationProperties.SetName(safeLink, Locale.T("Link safe area sides"));
        safeLink.Click += (_, _) =>
        {
            var link = safeLink.IsChecked == true;
            Change(layout with { LinkSafeArea = link, SafeArea = link ? new CanvasMargins(layout.SafeArea.Top) : layout.SafeArea });
        };
        var sides = new[] { "Top inset", "Right inset", "Bottom inset", "Left inset" };
        var captions = Locale.Language == "ru"
            ? new[] { "Top side", "Right side", "Bottom side", "Left side" }
            : new[] { "Top", "Right", "Bottom", "Left" };
        safeFields = sides.Select((side, i) => Number(side, 0, 0, DocumentLimits.MaxSide, v =>
        {
            if (syncing) return;
            var before = layout.SafeArea;
            var value = (int)v;
            var next = layout.LinkSafeArea ? new CanvasMargins(value) : i switch
            {
                0 => before with { Top = value },
                1 => before with { Right = value },
                2 => before with { Bottom = value },
                _ => before with { Left = value }
            };
            Change(layout with { SafeArea = next });
        }, blankIsZero: true)).ToArray();
        var safeRow = Row("*,*,*,*,Auto");
        for (var i = 0; i < safeFields.Length; i++)
        {
            var cell = Field(captions[i], safeFields[i], "px");
            safeFields[i].PlaceholderText = "0";
            ((TextBlock)cell.Children[0]).TextTrimming = TextTrimming.CharacterEllipsis;
            Put(safeRow, cell, i);
        }
        Put(safeRow, safeLink, 4);
        safeStatus = Ui.Label(Locale.T("Safe area exceeds canvas size"), Palette.Secondary, 11);
        safeStatus.TextWrapping = TextWrapping.Wrap;
        Children.Add(Ui.Column(9, Heading("Safe area"), safeRow, safeStatus));
        Children.Add(Ui.Separator(vertical: false));

        var typeRow = Row("*,*,*", 5);
        AddKind(CanvasGridKind.Columns, Icons.LayoutColumns, 0);
        AddKind(CanvasGridKind.Rows, Icons.LayoutRows, 1);
        AddKind(CanvasGridKind.Squares, Icons.LayoutSquares, 2);
        void AddKind(CanvasGridKind kind, Icons.Icon icon, int column)
        {
            var button = new Button { Name = "Layout" + kind, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center, Height = 30, MinHeight = 0,
                Padding = new Thickness(5), BorderThickness = default };
            var outline = new Rectangle { Stroke = Palette.Edge, StrokeThickness = 1, RadiusX = 5, RadiusY = 5,
                StrokeDashArray = [3, 2], Opacity = .6, IsHitTestVisible = false };
            AutomationProperties.SetName(button, Locale.T(kind.ToString()));
            Locale.Tip(button, kind.ToString());
            button.Click += (_, _) => Activate(kind);
            kinds.Add(kind, (button, outline, icon));
            Put(typeRow, new Panel { Children = { button, outline } }, column);
        }

        count = Number("LayoutCount", 0, 0, DocumentLimits.MaxLayoutTracks, v => SetTrack(track => track with { Count = (int)v }), blankIsZero: true);
        gap = Number("LayoutGap", 24, 0, DocumentLimits.MaxSide, v => SetTrack(track => track with { Gap = (int)v }));
        margin = Number("LayoutMargin", 64, 0, DocumentLimits.MaxSide, v => SetTrack(track => track with { Margin = (int)v }));
        trackOpacity = Number("LayoutOpacity", 100, 0, 100, v => SetTrack(track => track with { Opacity = (int)v }));
        trackSize = Number("LayoutTrackSize", 96, 1, DocumentLimits.MaxSide, v => SetTrack(track => track with { Size = (int)v }));
        trackSize.FormatString = "0.##";
        sizing = Ui.Combo(Enum.GetValues<GridSizing>(), GridSizing.Stretch, v => v.ToString(),
            v => SetTrack(track => track with { Sizing = v }), double.NaN);
        sizing.Name = "LayoutSizing";
        alignment = Ui.Combo(Enum.GetValues<GridAlignment>(), GridAlignment.Start, v => v.ToString(),
            v => SetTrack(track => track with { Alignment = v }), double.NaN);
        computedSize = Ui.Label("", Palette.Secondary, 12);
        measuredSize = UnitSurface(computedSize, "px");
        editableSize = UnitSurface(trackSize, "px");
        var sizeField = Field("Column width", new Panel { Children = { measuredSize, editableSize } });
        trackSizeLabel = (TextBlock)sizeField.Children[0];
        alignmentRow = Pair(new Border(), Field("Alignment", alignment));
        trackDetails = Ui.Column(10,
            Pair(Field("Count", count), Field("Gap", gap, "px")),
            Pair(Field("Outer margins", margin, "px"), Field("Sizing", sizing)),
            Pair(sizeField, Field("Opacity", trackOpacity, "%")),
            alignmentRow);
        squareSize = Number("LayoutSquareSize", 0, 0, DocumentLimits.MaxSide, v =>
        {
            if (!syncing) Change(layout with { SquareSize = v > 0 ? Math.Max(LayoutGrid.MinSpacing, (int)v) : 0 });
        }, blankIsZero: true);
        squareOpacity = Number("LayoutSquareOpacity", 100, 0, 100, v =>
        {
            if (!syncing) Change(layout with { SquareOpacity = (int)v });
        });
        squareDetails = Pair(Field("Square size", squareSize, "px"), Field("Opacity", squareOpacity, "%"));
        gridStatus = Ui.Label("", Palette.Secondary, 11);
        gridStatus.TextWrapping = TextWrapping.Wrap;
        remove = Ui.TextButton("Remove", Remove);
        remove.Name = "RemoveLayoutGrid";
        remove.Content = Ui.Row(5, Icons.Create(Icons.Trash, 13, Palette.Secondary), Ui.Label(Locale.T("Remove"), Palette.Secondary, 11));
        AutomationProperties.SetName(remove, Locale.T("Remove"));
        remove.Classes.Add("flat");
        remove.MinWidth = 0;
        remove.HorizontalAlignment = HorizontalAlignment.Right;
        Children.Add(Ui.Column(9, Heading("Layout Grid"), typeRow, trackDetails, squareDetails, gridStatus, remove));

        bleed = Number("LayoutBleed", 0, 0, CanvasLayout.MaxBleedMm, v =>
        {
            if (!syncing) Change(layout with { BleedMm = v });
        }, .5, "0.##", blankIsZero: true);
        bleed.PlaceholderText = "0";
        Locale.Tip(bleed, "Show the bleed guide outside the canvas edge.");
        Children.Add(Ui.Separator(vertical: false));
        Children.Add(Pair(Field("Bleed", bleed, "mm"), new Border()));
        Show(CanvasLayout.Empty, 1920, 1080);
    }

    public void Show(CanvasLayout value, int w, int h)
    {
        layout = value;
        width = w;
        height = h;
        if (active is not { } selected || !Enabled(selected))
            active = OnlyEnabled();
        Refresh();
    }

    private CanvasGridKind? OnlyEnabled() => kinds.Keys.Where(Enabled).Select(kind => (CanvasGridKind?)kind).FirstOrDefault();

    private bool Enabled(CanvasGridKind kind) => kind switch
    {
        CanvasGridKind.Columns => layout.Columns != null,
        CanvasGridKind.Rows => layout.Rows != null,
        _ => layout.SquareSize != null
    };

    private void Activate(CanvasGridKind kind)
    {
        active = kind;
        var extent = kind == CanvasGridKind.Columns ? width : height;
        var defaults = new CanvasTracks(Count: 0, Gap: Math.Min(24, extent / 16), Margin: Math.Min(64, extent / 8));
        Change(kind switch
        {
            CanvasGridKind.Columns => layout with { Columns = layout.Columns ?? defaults },
            CanvasGridKind.Rows => layout with { Rows = layout.Rows ?? defaults },
            _ => layout with { SquareSize = layout.SquareSize ?? 0 }
        });
    }

    private void Remove()
    {
        var kind = active;
        active = null;
        Change(kind switch
        {
            CanvasGridKind.Columns => layout with { Columns = null },
            CanvasGridKind.Rows => layout with { Rows = null },
            CanvasGridKind.Squares => layout with { SquareSize = null, SquareOpacity = 100 },
            _ => layout
        });
        if (kind is { } focus) kinds[focus].Button.Focus();
    }

    private void SetTrack(Func<CanvasTracks, CanvasTracks> update)
    {
        if (syncing) return;
        if (active == CanvasGridKind.Columns && layout.Columns is { } columns) Change(layout with { Columns = update(columns) });
        if (active == CanvasGridKind.Rows && layout.Rows is { } rows) Change(layout with { Rows = update(rows) });
    }

    private void Change(CanvasLayout value)
    {
        var previous = layout;
        Show(value, width, height);
        if (value != previous) changed(value);
    }

    private void Refresh()
    {
        syncing = true;
        var safe = layout.SafeArea;
        int[] values = [safe.Top, safe.Right, safe.Bottom, safe.Left];
        for (var i = 0; i < values.Length; i++) safeFields[i].Value = values[i] == 0 ? null : values[i];
        safeLink.IsChecked = layout.LinkSafeArea;
        safeStatus.IsVisible = layout.SafeBounds(width, height).IsEmpty;

        foreach (var (kind, tile) in kinds)
        {
            var brush = active == kind ? Palette.Foreground : Enabled(kind) ? Palette.Accent : Palette.Secondary;
            if (tile.Button.Content is not Control existing || existing.Tag is not IBrush previous || previous != brush)
            {
                var glyph = Icons.Create(tile.Icon, 16, brush);
                glyph.Tag = brush;
                tile.Button.Content = glyph;
            }
            tile.Button.Background = active == kind ? Palette.Selected : Palette.Window;
            tile.Outline.IsVisible = active != kind;
        }
        remove.IsVisible = active != null;
        var track = active == CanvasGridKind.Columns ? layout.Columns : active == CanvasGridKind.Rows ? layout.Rows : null;
        trackDetails.IsVisible = track != null;
        squareDetails.IsVisible = active == CanvasGridKind.Squares;
        squareSize.Value = layout.SquareSize > 0 ? layout.SquareSize : null;
        squareOpacity.Value = layout.SquareOpacity;
        gridStatus.Text = "";
        if (track != null)
        {
            var columns = active == CanvasGridKind.Columns;
            var geometry = track.Geometry(columns ? width : height);
            var fixedSize = track.Sizing == GridSizing.Fixed;
            count.Value = track.Count > 0 ? track.Count : null;
            gap.Value = track.Gap;
            margin.Value = track.Margin;
            trackOpacity.Value = track.Opacity;
            sizing.SelectedIndex = (int)track.Sizing;
            alignment.SelectedIndex = (int)track.Alignment;
            alignmentRow.IsVisible = editableSize.IsVisible = fixedSize;
            measuredSize.IsVisible = !fixedSize;
            trackSizeLabel.Text = Locale.T(columns ? "Column width" : "Row height");
            trackSize.Value = track.Size;
            computedSize.Text = geometry is { } exact ? $"{exact.Size:0.##}" : "—";
            if (track.Count > 0 && geometry == null) gridStatus.Text = Locale.T("Grid exceeds available space");
        }
        gridStatus.IsVisible = !string.IsNullOrEmpty(gridStatus.Text);
        bleed.Value = layout.BleedMm > 0 ? (decimal)layout.BleedMm : null;
        syncing = false;
    }

    internal static NumericUpDown Number(string name, double value, double min, double max, Action<double> change,
        double step = 1, string format = "0", bool live = false, bool blankIsZero = false)
    {
        var field = Ui.Number(value, min, max, change, step, format, double.NaN);
        field.Name = name.Replace(" ", "");
        AutomationProperties.SetName(field, Locale.T(name));
        field.MinWidth = 0;
        field.Height = field.MinHeight = 30;
        field.FontSize = 12;
        field.Padding = new Thickness(7, 0);
        field.HorizontalAlignment = HorizontalAlignment.Stretch;
        field.VerticalContentAlignment = VerticalAlignment.Center;
        if (blankIsZero)
            field.ValueChanged += (_, e) => { if (e.NewValue == null) change(0); };
        if (live)
        {
            field.TemplateApplied += (_, e) =>
            {
                if (e.NameScope.Find("PART_TextBox") is TextBox text)
                    text.TextChanged += (_, _) =>
                    {
                        if (text.IsFocused && decimal.TryParse(text.Text, out var number) && number >= field.Minimum && number <= field.Maximum)
                            change((double)number);
                    };
            };
        }
        return field;
    }

    internal static TextBlock Heading(string title) => Ui.Label(Locale.T(title), weight: FontWeight.SemiBold);
    internal static StackPanel Field(string label, Control field, string? unit = null, bool scrubLabel = false)
    {
        if (field is ComboBox choice)
        {
            choice.HorizontalAlignment = HorizontalAlignment.Stretch;
            choice.VerticalContentAlignment = VerticalAlignment.Center;
            choice.Height = choice.MinHeight = 30;
            choice.Padding = new Thickness(8, 0);
            choice.FontSize = 12;
        }
        var caption = Ui.Label(Locale.T(label), Palette.Secondary, 11);
        if (scrubLabel && field is NumericUpDown number) Ui.Scrub(caption, number);
        return Ui.Column(5, caption, unit == null ? field : UnitSurface(field, unit));
    }

    internal static Grid Dimensions(NumericUpDown width, NumericUpDown height, Button swap, bool scrubLabels = false)
    {
        SizeIconButton(swap);
        var row = Row("*,Auto,*", 7);
        Put(row, Field("Width", width, "px", scrubLabels), 0);
        Put(row, swap, 1);
        Put(row, Field("Height", height, "px", scrubLabels), 2);
        return row;
    }

    private static void SizeIconButton(Button button)
    {
        button.Width = button.Height = 30;
        button.MinWidth = button.MinHeight = 0;
        button.Padding = new Thickness(5);
        button.VerticalAlignment = VerticalAlignment.Bottom;
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
    }

    private static Border UnitSurface(Control field, string unit)
    {
        if (field is NumericUpDown number)
        {
            number.Background = Brushes.Transparent;
            number.BorderThickness = default;
        }
        else field.Margin = new Thickness(8, 0, 0, 0);
        var row = Row("*,Auto", 1);
        Put(row, field, 0);
        var suffix = Ui.Label(Locale.T(unit), Palette.Secondary, 10);
        suffix.Margin = new Thickness(0, 0, 7, 0);
        Put(row, suffix, 1);
        return new Border { Height = 30, CornerRadius = new CornerRadius(5), Background = Palette.Field, Child = row };
    }
    internal static Grid Row(string columns, double gap = 7) => new() { ColumnDefinitions = new ColumnDefinitions(columns), ColumnSpacing = gap };

    internal static void Put(Grid grid, Control field, int column)
    {
        Grid.SetColumn(field, column);
        grid.Children.Add(field);
    }

    internal static Grid Pair(Control first, Control second)
    {
        var row = Row("*,*");
        Put(row, first, 0);
        Put(row, second, 1);
        return row;
    }
}
