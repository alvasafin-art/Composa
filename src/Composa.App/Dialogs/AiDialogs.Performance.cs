using Avalonia.Controls;
using Avalonia.Threading;
using Composa.App.Controls;
using Composa.Rendering;
using Composa.Vision;
using static Composa.App.Dialogs.StudioSettingsLayout;

namespace Composa.App.Dialogs;

public static partial class AiDialogs
{
    private static Control[] PerformanceSections(Settings settings, CanvasView? canvas, CheckBox editorGpu, CheckBox rasterGpu,
        CheckBox selectionGpu, OnnxModel[] models)
    {
        CheckBox[] options = [editorGpu, rasterGpu, selectionGpu];
        var supported = options.Where(option => option.IsEnabled).ToArray();
        var master = new ToggleSwitch { Name = "UseHardwareAcceleration", IsEnabled = supported.Length > 0,
            IsChecked = supported.Any(option => option.IsChecked == true) };
        Avalonia.Automation.AutomationProperties.SetName(master, Locale.T("Hardware acceleration"));
        var syncing = false;
        master.IsCheckedChanged += (_, _) =>
        {
            if (syncing) return;
            syncing = true;
            foreach (var option in supported) option.IsChecked = master.IsChecked == true;
            syncing = false;
        };
        foreach (var option in supported)
            option.IsCheckedChanged += (_, _) =>
            {
                if (syncing) return;
                syncing = true;
                master.IsChecked = supported.Any(item => item.IsChecked == true);
                syncing = false;
            };

        var adapter = Caption("");
        var deviceRows = Ui.Column(0, InsetRow(Columns("*,130", Caption("Module"), Caption("Available device")), Palette.SettingsSidebar));
        var skia = AddDevice("Canvas preview (Skia)", "CanvasPreviewDevice");
        var composite = AddDevice("Canvas compositing (OpenCL)", "CanvasCompositeDevice");
        var coverage = AddDevice("Brush and eraser coverage (OpenCL)", "BrushCoverageDevice");
        var blending = AddDevice("Brush and eraser blending (OpenCL)", "BrushBlendingDevice");
        var blur = AddDevice("Shadow and glow blur (OpenCL)", "LayerBlurDevice");
        var stroke = AddDevice("Layer strokes (OpenCL)", "LayerStrokeDevice");
        var rgb16 = AddDevice("Large RGB16 blur filters", "Rgb16BlurDevice");
        var sam = AddDevice("EfficientSAM S", "SelectionModelDevice");
        var matte = AddDevice("ViTMatte Small", "MattingModelDevice");

        TextBlock AddDevice(string module, string name)
        {
            var value = Caption("Not loaded");
            value.Name = name;
            deviceRows.Children.Add(InsetRow(Columns("*,130", Caption(module), value), vertical: 9));
            return value;
        }

        void RefreshDevices()
        {
            var openCl = GpuAcceleration.OpenCl;
            var editorEnabled = editorGpu.IsEnabled && settings.UseEditorGpu;
            var filtersEnabled = rasterGpu.IsEnabled && settings.UseRasterFilterGpu;
            var adapterText = (editorEnabled || filtersEnabled) && openCl.Device is { } name ? $"OpenCL · {name}" : "";
            if (adapter.Text != adapterText) adapter.Text = adapterText;
            adapter.IsVisible = adapterText.Length > 0;
            SetDevice(skia, editorEnabled ? canvas?.HardwareViewportAvailable switch { true => "GPU", false => "CPU", _ => null } : "CPU");
            SetOpenCl(composite, editorEnabled, openCl.Composite);
            SetOpenCl(coverage, editorEnabled, openCl.BrushCoverage);
            SetOpenCl(blending, editorEnabled, openCl.BrushBlending);
            SetOpenCl(blur, editorEnabled, openCl.LayerBlur);
            SetOpenCl(stroke, editorEnabled, openCl.LayerStroke);
            SetOpenCl(rgb16, filtersEnabled, openCl.Rgb16Blur);
            var selectionEnabled = selectionGpu.IsEnabled && settings.UseSelectionGpu;
            SetDevice(sam, selectionEnabled ? ModelRunner.DeviceFor(models[0]) ?? ModelRunner.DeviceFor(models[1]) : "CPU");
            SetDevice(matte, selectionEnabled ? ModelRunner.DeviceFor(models[2]) : "CPU");

            void SetOpenCl(TextBlock row, bool enabled, bool supported) =>
                SetDevice(row, !enabled ? "CPU" : !openCl.Probed ? null : supported ? "GPU" : "CPU");
        }

        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => RefreshDevices();
        deviceRows.AttachedToVisualTree += (_, _) =>
        {
            GpuAcceleration.ProbeOpenCl();
            RefreshDevices();
            timer.Start();
        };
        deviceRows.DetachedFromVisualTree += (_, _) => timer.Stop();
        return [Card(Ui.Column(8, Columns("*,Auto", Heading("Hardware acceleration", 14), master), editorGpu, rasterGpu, selectionGpu), 16),
            Ui.Column(8, Heading("Devices", 13), adapter, Card(deviceRows, 0))];
    }

    private static void SetDevice(TextBlock value, string? device)
    {
        var text = Locale.T(device ?? "Not loaded");
        if (value.Text != text) value.Text = text;
        value.Foreground = device == "CPU" ? Palette.SettingsWarningText : device == null ? Palette.Secondary : Palette.SettingsSuccessText;
    }
}
