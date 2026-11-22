using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Composa.App.Controls;
using Composa.App.Dialogs;
using Composa.Editing;
using Composa.IO;
using Composa.Model;

namespace Composa.App;

public sealed partial class MainWindow
{
    private enum LayoutInspector { None, Offer, Preview, Group, Child }
    private LayoutInspector optionsLayoutInspector;
    private bool separateLayoutPadding;
    private (EditorSession Session, AutoLayoutProposal Proposal)? layoutProposal;

    private LayoutInspector LayoutInspectorFor(EditorSession? target)
    {
        if (target == null || target.IsEditingMask || target.IsEditingChannel || target.Tool is not (Tool.Move or Tool.Text))
            return LayoutInspector.None;
        if (layoutProposal is { } pending && pending.Session == target && pending.Proposal.Revision == target.Revision
            && target.SelectedRoots().Select(layer => layer.Id).SequenceEqual(pending.Proposal.Selection)) return LayoutInspector.Preview;
        if (target.Document.SelectedLayerIds.Count == 1 && target.ActiveLayer?.AutoLayout != null) return LayoutInspector.Group;
        if (target.CanOfferAutoLayout) return LayoutInspector.Offer;
        if (target.ActiveLayer is { } child && !child.Clipped && target.CanSetLayoutSize(child) && target.AutoLayoutParent(child) != null)
            return LayoutInspector.Child;
        return LayoutInspector.None;
    }

    private async Task PrepareAutoLayout()
    {
        if (session is not { } target || IsDragging) return;
        target.FinishText();
        var proposal = target.ProposeAutoLayout(out var problem);
        if (proposal == null)
        {
            if (problem != null) ShowProblem(problem);
            return;
        }
        if (target.Tool != Tool.Move) SelectTool(Tool.Move);
        layoutProposal = (target, proposal);
        canvas.SetLayoutProposal(proposal);
        RebuildOptions();
        if (dock.Section("Properties").State.Visible) return;
        var keep = await Prompts.Confirm(this, "Auto Layout", LayoutPreviewMessage(proposal), "Apply");
        if (layoutProposal?.Proposal == proposal) FinishLayoutProposal(keep);
    }

    private static string LayoutPreviewMessage(AutoLayoutProposal proposal) => proposal.ChangesPositions
        ? "Review the proposed positions on the canvas."
        : "The original positions will be preserved.";

    private void FinishLayoutProposal(bool keep)
    {
        var pending = layoutProposal;
        layoutProposal = null;
        canvas.SetLayoutProposal(null);
        if (keep && pending is { } current && current.Session == session)
            current.Session.CreateAutoLayout(current.Proposal, Locale.T("Auto Layout"));
        RebuildOptions();
    }

    private void ClearStaleLayoutProposal()
    {
        if (layoutProposal != null && LayoutInspectorFor(session) != LayoutInspector.Preview)
        {
            layoutProposal = null;
            canvas.SetLayoutProposal(null);
        }
    }

    private static Button LayoutButton(string label, Action action, bool accent = false)
    {
        var button = Ui.TextButton(label, action, accent);
        button.MinWidth = 0;
        button.MinHeight = 30;
        button.CornerRadius = new CornerRadius(5);
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        return button;
    }

    private static NumericUpDown LayoutNumber(string name, double value, Action<double> set, double minimum = 0)
    {
        var field = Ui.Number(value, minimum, DocumentLimits.MaxSide, set, format: "0.##", width: double.NaN, scrub: false);
        field.Name = name;
        field.MinWidth = 0;
        field.Height = field.MinHeight = 30;
        field.FontSize = 12;
        field.Padding = new Thickness(7, 0);
        field.HorizontalAlignment = HorizontalAlignment.Stretch;
        field.VerticalContentAlignment = VerticalAlignment.Center;
        return field;
    }

    private void AddLayoutOffer(StackPanel body, EditorSession target)
    {
        if (optionsLayoutInspector == LayoutInspector.Preview && layoutProposal is { } pending)
        {
            body.Children.Add(CanvasLayoutEditor.Heading("Auto Layout Preview"));
            var proposal = pending.Proposal;
            var flow = proposal.Settings.Direction == LayoutDirection.Row ? Locale.T("Row") : Locale.T("Column");
            var details = Ui.Label($"{flow}  ·  {proposal.Settings.Gap:0.##} px", Palette.Secondary);
            body.Children.Add(details);
            body.Children.Add(new TextBlock { Text = Locale.T(LayoutPreviewMessage(proposal)), Foreground = Palette.Secondary, TextWrapping = TextWrapping.Wrap });
            var cancel = LayoutButton("Cancel", () => FinishLayoutProposal(false));
            cancel.Name = "CancelAutoLayout";
            var apply = LayoutButton("Apply", () => FinishLayoutProposal(true), accent: true);
            apply.Name = "ApplyAutoLayout";
            body.Children.Add(PropertyPair(cancel, apply));
        }
        else
        {
            body.Children.Add(Ui.Label(target.Document.SelectedLayerIds.Count == 1 ? target.ActiveLayer!.Name :
                $"{Locale.T("Selected Layers")} · {target.Document.SelectedLayerIds.Count}", weight: FontWeight.SemiBold));
            var button = LayoutButton("Auto Layout", () => _ = PrepareAutoLayout());
            button.Name = "CreateAutoLayout";
            body.Children.Add(button);
        }
    }

    private void AddGroupLayoutProperties(StackPanel body, EditorSession target, Func<Layer?> current)
    {
        if (current()?.AutoLayout is not { } layout) return;
        var controls = new StackPanel { Spacing = 8 };
        body.Children.Add(controls);
        var syncing = false;
        void Change(Func<AutoLayout, AutoLayout> edit)
        {
            if (!syncing && current() is { AutoLayout: { } live } group) target.SetAutoLayout(group, edit(live));
        }
        var direction = Ui.Combo(Enum.GetValues<LayoutDirection>(), layout.Direction, value => value.ToString(),
            value => Change(old => old with { Direction = value }), double.NaN);
        direction.Name = "AutoLayoutDirection";
        var alignment = Ui.Combo(Enum.GetValues<LayoutAlignment>(), layout.Alignment, value => value switch
        {
            LayoutAlignment.Center => "Center", LayoutAlignment.End => "Align End", _ => "Align Start"
        }, value => Change(old => old with { Alignment = value }), double.NaN);
        alignment.Name = "AutoLayoutAlignment";
        controls.Children.Add(PropertyPair(CanvasLayoutEditor.Field("Direction", direction), CanvasLayoutEditor.Field("Alignment", alignment)));
        var gap = LayoutNumber("AutoLayoutGap", layout.Gap, value => Change(old => old with { Gap = value }));
        controls.Children.Add(CanvasLayoutEditor.Field("Between elements", gap, "px"));
        var inset = LayoutNumber("AutoLayoutPadding", layout.Padding.Top, value => Change(old => old with { Padding = new(value, value, value, value) }));
        var expanded = separateLayoutPadding || layout.Padding != new LayoutInsets(layout.Padding.Top, layout.Padding.Top, layout.Padding.Top, layout.Padding.Top);
        var detailRow = CanvasLayoutEditor.Row("*,*,*,*");
        var separate = new List<NumericUpDown>();
        var names = new[] { "Top side", "Right side", "Bottom side", "Left side" };
        for (var i = 0; i < names.Length; i++)
        {
            var side = i;
            var field = LayoutNumber("AutoLayoutPadding" + i, 0, value => Change(old => old with
            {
                Padding = side switch
                {
                    0 => old.Padding with { Top = value }, 1 => old.Padding with { Right = value },
                    2 => old.Padding with { Bottom = value }, _ => old.Padding with { Left = value }
                }
            }));
            separate.Add(field);
            CanvasLayoutEditor.Put(detailRow, CanvasLayoutEditor.Field(names[i], field), i);
        }
        var toggle = Ui.IconButton(Icons.MoreActions, "Separate padding", () =>
        {
            expanded = separateLayoutPadding = !expanded;
            detailRow.IsVisible = expanded;
            inset.IsEnabled = !expanded;
        });
        toggle.Width = toggle.Height = 30;
        toggle.MinWidth = toggle.MinHeight = 0;
        toggle.CornerRadius = new CornerRadius(5);
        toggle.VerticalAlignment = VerticalAlignment.Bottom;
        var paddingRow = CanvasLayoutEditor.Row("*,Auto");
        CanvasLayoutEditor.Put(paddingRow, CanvasLayoutEditor.Field("Inside group", inset, "px"), 0);
        CanvasLayoutEditor.Put(paddingRow, toggle, 1);
        controls.Children.Add(paddingRow);
        controls.Children.Add(detailRow);
        var disable = LayoutButton("Disable Auto Layout", () => { if (current() is { } group) target.DisableAutoLayout(group); });
        disable.Name = "DisableAutoLayout";
        controls.Children.Add(disable);
        var saveElement = LayoutButton("Save to Library…", () => _ = SaveLibrarySelection());
        saveElement.Name = "SaveLayoutToLibrary";
        controls.Children.Add(saveElement);
        void Refresh()
        {
            if (current() is not { AutoLayout: { } value } group) return;
            syncing = true;
            controls.IsEnabled = target.CanEditAutoLayout(group);
            direction.SelectedIndex = (int)value.Direction;
            alignment.SelectedIndex = (int)value.Alignment;
            gap.Value = (decimal)value.Gap;
            inset.Value = value.Padding == new LayoutInsets(value.Padding.Top, value.Padding.Top, value.Padding.Top, value.Padding.Top)
                ? (decimal)value.Padding.Top : null;
            detailRow.IsVisible = expanded;
            inset.IsEnabled = !expanded;
            var values = new[] { value.Padding.Top, value.Padding.Right, value.Padding.Bottom, value.Padding.Left };
            for (var i = 0; i < separate.Count; i++) separate[i].Value = (decimal)values[i];
            syncing = false;
        }
        refreshOptions += Refresh;
        Refresh();
    }

    private void AddChildLayoutProperties(StackPanel body, EditorSession target, Func<Layer?> current)
    {
        if (current() is not { } layer || target.AutoLayoutParent(layer)?.AutoLayout is not { } parentLayout) return;
        var controls = new StackPanel { Spacing = 8 };
        controls.Children.Add(CanvasLayoutEditor.Heading("Layout Size"));
        body.Children.Add(controls);
        var syncing = false;
        var settings = target.LayoutItemSettings(layer);
        void Change(Func<AutoLayoutItem, AutoLayoutItem> edit)
        {
            if (!syncing && current() is { } live) target.SetAutoLayoutItem(live, edit(target.LayoutItemSettings(live)));
        }
        var modes = new[] { LayoutSizeMode.Content, LayoutSizeMode.Fixed, LayoutSizeMode.Fill };
        var widthModes = parentLayout.Direction == LayoutDirection.Row ? modes[..2] : modes;
        var heightModes = parentLayout.Direction == LayoutDirection.Column ? modes[..2] : modes;
        static string ModeLabel(LayoutSizeMode mode) => mode switch
        {
            LayoutSizeMode.Fixed => "Fixed size", LayoutSizeMode.Fill => "Fill available space", _ => "Size to content"
        };
        var widthMode = Ui.Combo(widthModes, settings.WidthMode, ModeLabel, value => Change(old => old with { WidthMode = value }), double.NaN);
        var heightMode = Ui.Combo(heightModes, settings.HeightMode, ModeLabel, value => Change(old => old with { HeightMode = value }), double.NaN);
        widthMode.Name = "AutoLayoutWidthMode";
        heightMode.Name = "AutoLayoutHeightMode";
        var width = LayoutNumber("AutoLayoutWidth", settings.Width, value => Change(old => old with { Width = value }), minimum: 1);
        var height = LayoutNumber("AutoLayoutHeight", settings.Height, value => Change(old => old with { Height = value }), minimum: 1);
        void SizeRow(string axis, NumericUpDown dimension, string modeLabel, ComboBox mode)
        {
            var row = CanvasLayoutEditor.Row("80,*");
            CanvasLayoutEditor.Put(row, CanvasLayoutEditor.Field(axis, dimension, "px"), 0);
            CanvasLayoutEditor.Put(row, CanvasLayoutEditor.Field(modeLabel, mode), 1);
            controls.Children.Add(row);
        }
        SizeRow("Width", width, "Width mode", widthMode);
        SizeRow("Height", height, "Height mode", heightMode);
        ComboBox? fit = null;
        if (layer.SupportsLayoutImageFrame)
        {
            fit = Ui.Combo(Enum.GetValues<LayoutImageFit>(), settings.ImageFit, value => value == LayoutImageFit.Crop ? "Crop to frame" : "Fit in frame",
                value => Change(old => old with { ImageFit = value }), double.NaN);
            controls.Children.Add(CanvasLayoutEditor.Field("Frame fit", fit));
            if (target.CanReplaceLayoutImage(layer))
            {
                var replace = LayoutButton("Replace Image…", () => { if (current() is { } live) _ = ReplaceLayoutImage(target, live); });
                controls.Children.Add(replace);
            }
        }
        void Refresh()
        {
            if (current() is not { } live || target.AutoLayoutParent(live) is not { } group) return;
            syncing = true;
            var item = target.LayoutItemSettings(live);
            var bounds = EditorSession.LayoutBounds(live);
            controls.IsEnabled = target.CanEditAutoLayout(group);
            width.Value = (decimal)(item.WidthMode == LayoutSizeMode.Fixed ? item.Width : Math.Max(1, bounds.Width));
            height.Value = (decimal)(item.HeightMode == LayoutSizeMode.Fixed ? item.Height : Math.Max(1, bounds.Height));
            width.IsReadOnly = item.WidthMode != LayoutSizeMode.Fixed;
            height.IsReadOnly = item.HeightMode != LayoutSizeMode.Fixed;
            widthMode.SelectedIndex = Array.IndexOf(widthModes, item.WidthMode);
            heightMode.SelectedIndex = Array.IndexOf(heightModes, item.HeightMode);
            if (fit != null)
            {
                fit.IsEnabled = item.WidthMode != LayoutSizeMode.Content || item.HeightMode != LayoutSizeMode.Content;
                fit.SelectedIndex = (int)item.ImageFit;
            }
            syncing = false;
        }
        refreshOptions += Refresh;
        Refresh();
    }

    private async Task ReplaceLayoutImage(EditorSession target, Layer layer)
    {
        var revision = target.Revision;
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Locale.T("Replace Image…"), AllowMultiple = false, FileTypeFilter = [ImageType]
        });
        if (picked.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        SkiaSharp.SKBitmap? image = null;
        try
        {
            image = await Task.Run(() => ImageFiles.Load(path, preserveProfile: true));
            if (session == target && target.Revision == revision && target.ActiveLayer == layer && !IsDragging
                && target.ReplaceLayoutImage(layer, image)) image = null;
        }
        catch (Exception error) { ShowProblem(error.Message); }
        finally { image?.Dispose(); }
    }

}
