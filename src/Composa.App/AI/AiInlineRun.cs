using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Composa.App.AI;

/// <summary>Renders operation feedback in the run button without changing the surrounding layout.</summary>
internal sealed class AiInlineRun
{
    private enum Phase { Ready, Queue, Wait, Progress, Apply, Cancel, Done, Charge, Failed, Cancelled }
    private static readonly Icons.Icon Clock = new("M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0 -18Z M12 7v5l3 2");
    private static readonly Icons.Icon Check = new("M5 12l4 4L19 6");
    private const double Width = 100;
    private const double Height = 28;
    private const double Radius = 6;
    private const double VariantWidth = 24;
    private const double ColorPeriod = 420;
    private const double ColorTravelTime = 6600;
    private const double ColorOverscan = 16;
    private const double DoneTime = 900;
    private static readonly Geometry SplitFace = CreateFaceClip(split: true);
    private static readonly Geometry StandaloneFace = CreateFaceClip(split: false);

    private readonly Button run;
    private readonly Button more;
    private readonly MenuFlyout variantMenu;
    private readonly Grid face = new() { ClipToBounds = true };
    private readonly Canvas colors = new() { Opacity = 0, IsHitTestVisible = false };
    private readonly TranslateTransform colorOffset = new();
    private readonly Border progress = new()
    {
        Width = Width, Height = 2, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
        Background = new SolidColorBrush(Color.FromArgb(212, 255, 255, 255)), RenderTransformOrigin = RelativePoint.TopLeft
    };
    private readonly ScaleTransform progressScale = new(0, 1);
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly Action<TimeSpan> onFrame;
    private readonly Stopwatch clock = new();
    private readonly Action stop;
    private readonly Action ready;
    private readonly Action<int> launch;
    private readonly TextBlock caption = Ui.Label("", size: 11.5, weight: FontWeight.SemiBold);
    private StackPanel? label;
    private Control? outgoing;
    private TranslateTransform? enter;
    private Phase phase;
    private long entered;
    private long colorStarted;
    private double previousColorOpacity;
    private bool framePending;
    private double oldProgress, nextProgress;
    private long progressChanged;
    private AiOperationState? operation;
    private string? charge;
    private int seconds;
    private bool supportsVariants;
    private bool canStart;
    private double captionWidth;
    private int shownSecond = -1;
    internal static string RunLabel => Locale.Language == "ru" ? Locale.T("Generation start") : "Run";

    internal AiInlineRun(Button run, Action<int> launch, Action stop, Action ready)
    {
        this.run = run;
        this.launch = launch;
        this.stop = stop;
        this.ready = ready;
        run.Name = "AiRun";
        run.Classes.Remove("accent");
        run.Width = run.MinWidth = Width;
        run.Height = run.MinHeight = Height;
        run.Padding = default;
        run.BorderThickness = default;
        run.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        run.VerticalContentAlignment = VerticalAlignment.Stretch;
        run.Content = face;
        progress.RenderTransform = progressScale;
        progress.IsHitTestVisible = false;
        colors.Children.Add(new Border
        {
            Width = ColorPeriod + Width + 2 * ColorOverscan, Height = Height,
            Background = Spectrum(), RenderTransformOrigin = RelativePoint.TopLeft,
            RenderTransform = new TransformGroup { Children = { new SkewTransform(-24, 0), colorOffset } }
        });
        face.Children.Add(colors);
        face.Children.Add(progress);
        more = Ui.IconButton(Icons.ChevronDown, "Number of variants", () =>
        {
            if (Busy && phase != Phase.Cancel)
            {
                SetPhase(Phase.Cancel);
                stop();
            }
        }, 11);
        more.Name = "AiRunVariants";
        more.Classes.Remove("flat");
        more.Classes.Add("ai-panel-control");
        more.Width = VariantWidth;
        more.MinWidth = 0;
        more.Height = more.MinHeight = Height;
        more.Padding = default;
        more.CornerRadius = new CornerRadius(0, Radius, Radius, 0);
        more.HorizontalContentAlignment = HorizontalAlignment.Center;
        more.VerticalContentAlignment = VerticalAlignment.Center;
        variantMenu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (var count in new[] { 2, 3 })
        {
            var item = new MenuItem { Header = Locale.T(count == 2 ? "2 variants" : "3 variants"), Name = $"AiVariants{count}", Tag = count };
            item.Click += (_, _) => { if (phase == Phase.Ready && canStart) this.launch(count); };
            variantMenu.Items.Add(item);
        }
        View = Ui.Row(0, run, more);
        onFrame = _ => { framePending = false; Tick(); };
        timer.Tick += (_, _) => Tick();
        View.AttachedToVisualTree += (_, _) => ScheduleTick();
        View.DetachedFromVisualTree += (_, _) => timer.Stop();
        SetPhase(Phase.Ready, force: true);
    }

    internal StackPanel View { get; }
    internal bool Busy => phase is Phase.Queue or Phase.Wait or Phase.Progress or Phase.Apply or Phase.Cancel;
    internal bool Holding => phase != Phase.Ready;

    internal void Configure(bool canStart, bool variants)
    {
        this.canStart = canStart;
        supportsVariants = variants;
        ConfigureButtons();
        ScheduleTick();
    }

    internal void SetVariantEstimates(PartnerPriceEstimate? estimate)
    {
        foreach (var item in variantMenu.Items.OfType<MenuItem>())
            ToolTip.SetTip(item, estimate == null ? null : $"{Locale.T("Estimated credits")} · {estimate.RoundedCredits((int)item.Tag!)}");
    }

    internal void Update(AiOperationState? value, string? directCharge)
    {
        if (ReferenceEquals(operation, value)) return;
        var previous = operation?.Status;
        operation = value;
        if (value == null) { clock.Reset(); SetPhase(Phase.Ready); ScheduleTick(); return; }
        if (value.Status is AiOperationStatus.Queued or AiOperationStatus.Running &&
            previous is not (AiOperationStatus.Queued or AiOperationStatus.Running)) clock.Restart();
        if (value.Status is AiOperationStatus.Completed or AiOperationStatus.Failed or AiOperationStatus.Cancelled)
        {
            clock.Stop();
            seconds = Math.Max((int)Math.Floor(clock.Elapsed.TotalSeconds), value.ElapsedSeconds ?? 0);
            charge = value.CreditsUsed?.ToString("0.########", Locale.Language == "ru" ? CultureInfo.GetCultureInfo("ru-RU") : CultureInfo.InvariantCulture)
                ?? directCharge;
        }
        var phaseForResult = value.Status switch
        {
            AiOperationStatus.Queued => Phase.Queue,
            AiOperationStatus.Completed => Phase.Done,
            AiOperationStatus.Failed => Phase.Failed,
            AiOperationStatus.Cancelled => Phase.Cancelled,
            _ when phase == Phase.Cancel => Phase.Cancel,
            _ when value.Stage?.Contains("Applying result", StringComparison.OrdinalIgnoreCase) == true => Phase.Apply,
            _ when value.Value.HasValue && value.Maximum > 0 => Phase.Progress,
            _ => Phase.Wait
        };
        if (phaseForResult == Phase.Progress)
        {
            oldProgress = progressScale.ScaleX;
            nextProgress = Math.Clamp((double)value.Value!.Value / value.Maximum!.Value, 0, 1);
            progressChanged = Stopwatch.GetTimestamp();
        }
        SetPhase(phaseForResult);
        UpdateCaption();
        ScheduleTick();
    }

    private void ConfigureButtons()
    {
        run.IsEnabled = phase != Phase.Ready || canStart;
        run.IsHitTestVisible = run.Focusable = phase == Phase.Ready;
        more.IsVisible = supportsVariants || Busy;
        more.IsEnabled = phase == Phase.Ready && canStart || Busy && phase != Phase.Cancel;
        run.CornerRadius = more.IsVisible ? new CornerRadius(Radius, 0, 0, Radius) : new CornerRadius(Radius);
        face.Clip = more.IsVisible ? SplitFace : StandaloneFace;
        var variants = phase == Phase.Ready;
        more.Flyout = variants ? variantMenu : null;
        more.Content = Icons.Create(Busy ? Icons.Close : Icons.ChevronDown, 11);
        var hint = variants ? "Number of variants" : "Cancel";
        Locale.Tip(more, hint);
        AutomationProperties.SetName(more, Locale.T(hint));
    }

    private void SetPhase(Phase next, bool force = false)
    {
        if (phase == next && !force) return;
        if (outgoing != null) face.Children.Remove(outgoing);
        outgoing = label;
        if (outgoing != null)
        {
            // Snapshot the previous row's text so timer ticks never modify a fading label.
            label!.Children.Remove(caption);
            label.Children.Add(Ui.Label(caption.Text ?? "", caption.Foreground, caption.FontSize, FontWeight.SemiBold));
        }
        entered = Stopwatch.GetTimestamp();
        if (ColorsMoving(next) && !ColorsMoving(phase) && colors.Opacity < .001)
            colorStarted = entered;
        previousColorOpacity = colors.Opacity;
        phase = next;
        run.Background = phase switch
        {
            Phase.Ready => Palette.Active,
            Phase.Wait or Phase.Progress => Brush("#293950"),
            Phase.Failed => Brush("#713C36"),
            Phase.Cancelled => Brush("#454A53"),
            _ => Brush("#35485D")
        };
        caption.Foreground = phase == Phase.Ready ? Palette.ActionText : Brushes.White;
        Icons.Icon? glyph = phase switch
        {
            Phase.Ready => Icons.Run, Phase.Queue or Phase.Wait or Phase.Progress or Phase.Apply => Clock,
            Phase.Done => Check, Phase.Charge when operation?.CreditsUsed != null => Icons.Coin, _ => null
        };
        label = glyph is { } icon ? Ui.Row(4, Icons.Create(icon, 13, caption.Foreground), caption) : Ui.Row(0, caption);
        captionWidth = Width - 8 - (glyph != null ? 17 : 0);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.IsHitTestVisible = false;
        enter = new TranslateTransform(0, outgoing == null ? 0 : 3);
        label.RenderTransform = enter;
        label.Opacity = outgoing == null ? 1 : 0;
        face.Children.Add(label);
        progress.IsVisible = phase == Phase.Progress;
        if (phase == Phase.Queue) progressScale.ScaleX = 0;
        ConfigureButtons();
        UpdateCaption();
    }

    private void UpdateCaption()
    {
        var passed = Math.Max(clock.Elapsed.TotalSeconds, operation?.ElapsedSeconds ?? 0);
        shownSecond = (int)passed;
        var time = FormatTime((int)passed);
        var text = phase switch
        {
            Phase.Queue => Locale.T("Queued"),
            Phase.Wait => time,
            Phase.Progress => $"{time} · {Math.Round(nextProgress * 100)}%",
            Phase.Apply => Locale.T("Inserting"),
            Phase.Cancel => Locale.T("Stopping"),
            Phase.Done => $"{Locale.T("Done")} · {FormatTime(seconds)}",
            Phase.Charge => charge ?? "",
            Phase.Failed => Locale.T("Failed"),
            Phase.Cancelled => Locale.T("Canceled"),
            _ => RunLabel
        };
        if (caption.Text != text)
        {
            const double fontSize = 11.5;
            var typeface = new Typeface(caption.FontFamily, caption.FontStyle, caption.FontWeight);
            var measured = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, fontSize, caption.Foreground);
            caption.FontSize = fontSize * Math.Min(1, captionWidth / Math.Max(1, measured.Width));
            caption.Text = text;
        }
        var hint = phase switch
        {
            Phase.Wait => operation?.Stage == null ? Locale.T("Waiting for the model") : Locale.T(operation.Stage),
            Phase.Failed => operation?.Error ?? text,
            Phase.Charge when operation?.CreditsUsed != null => $"{Locale.T("Credits spent")}: {charge}",
            _ => text
        };
        if (!Equals(ToolTip.GetTip(run), hint)) ToolTip.SetTip(run, hint);
        if (AutomationProperties.GetName(run) != hint) AutomationProperties.SetName(run, hint);
    }

    private void Tick()
    {
        if (!View.IsEffectivelyVisible || TopLevel.GetTopLevel(View) == null) { timer.Stop(); return; }
        var age = Stopwatch.GetElapsedTime(entered).TotalMilliseconds;
        if (outgoing != null)
        {
            var blend = SmoothStep(age / 180);
            outgoing.Opacity = 1 - blend;
            label!.Opacity = blend;
            enter!.Y = 3 * (1 - blend);
            if (blend >= 1) { face.Children.Remove(outgoing); outgoing = null; }
        }
        if (ColorsMoving(phase))
            colorOffset.X = -ColorOverscan - Stopwatch.GetElapsedTime(colorStarted).TotalMilliseconds % ColorTravelTime * ColorPeriod / ColorTravelTime;
        colors.Opacity = phase switch
        {
            Phase.Wait or Phase.Progress => previousColorOpacity + (1 - previousColorOpacity) * SmoothStep(age / 260),
            Phase.Done => Math.Max(previousColorOpacity, SmoothStep(age / 145)) * (1 - SmoothStep((age - 190) / (DoneTime - 190))),
            _ => previousColorOpacity * (1 - SmoothStep(age / 220))
        };
        if (phase == Phase.Progress)
        {
            var fraction = SmoothStep(Stopwatch.GetElapsedTime(progressChanged).TotalMilliseconds / 240);
            progressScale.ScaleX = oldProgress + (nextProgress - oldProgress) * fraction;
        }
        if (Busy && (int)Math.Max(clock.Elapsed.TotalSeconds, operation?.ElapsedSeconds ?? 0) != shownSecond)
            UpdateCaption();
        if (phase == Phase.Done && age >= DoneTime) SetPhase(charge != null ? Phase.Charge : Phase.Ready);
        else if (phase == Phase.Charge && age >= 1000 || phase == Phase.Cancelled && age >= 1200 || phase == Phase.Failed && age >= 2100)
            SetPhase(Phase.Ready);
        if (phase == Phase.Ready && !readyNotified)
        {
            readyNotified = true;
            ready();
        }
        else if (phase != Phase.Ready) readyNotified = false;
        ScheduleTick();
    }

    private void ScheduleTick()
    {
        if (!View.IsEffectivelyVisible || TopLevel.GetTopLevel(View) is not { } root) { timer.Stop(); return; }
        if (outgoing != null || ColorsMoving(phase) || colors.Opacity > 0)
        {
            timer.Stop();
            if (framePending) return;
            framePending = true;
            root.RequestAnimationFrame(onFrame);
        }
        else if (Holding) timer.Start();
        else timer.Stop();
    }

    private static bool ColorsMoving(Phase current) => current is Phase.Wait or Phase.Progress or Phase.Done;

    private static double SmoothStep(double value)
    {
        var t = Math.Clamp(value, 0, 1);
        return t * t * (3 - 2 * t);
    }

    private bool readyNotified = true;
    private static string FormatTime(int seconds) => seconds < 3600 ? $"{seconds / 60}:{seconds % 60:00}" :
        $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}";
    private static IBrush Brush(string value) => new SolidColorBrush(Color.Parse(value));
    private static Geometry CreateFaceClip(bool split)
    {
        var rightRadius = split ? 0 : Radius;
        var geometry = new StreamGeometry();
        using var outline = geometry.Open();
        outline.BeginFigure(new Point(Radius, 0), isFilled: true);
        outline.LineTo(new Point(Width - rightRadius, 0));
        if (!split) Arc(Width, Radius);
        outline.LineTo(new Point(Width, Height - rightRadius));
        if (!split) Arc(Width - Radius, Height);
        outline.LineTo(new Point(Radius, Height));
        Arc(0, Height - Radius);
        outline.LineTo(new Point(0, Radius));
        Arc(Radius, 0);
        outline.EndFigure(isClosed: true);
        return geometry;

        void Arc(double x, double y) => outline.ArcTo(new Point(x, y), new Size(Radius, Radius), 0, false, SweepDirection.Clockwise);
    }

    private static LinearGradientBrush Spectrum()
    {
        string[] hues = ["#0879AB", "#1C67EC", "#5942ED", "#953ED2", "#CC328C", "#CA426B", "#6649CA", "#0879AB"];
        var stops = new GradientStops();
        for (var index = 0; index < hues.Length; index++)
            stops.Add(new GradientStop(Color.Parse(hues[index]), index / (double)(hues.Length - 1)));
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(ColorPeriod, 0, RelativeUnit.Absolute),
            SpreadMethod = GradientSpreadMethod.Repeat,
            GradientStops = stops
        };
    }
}
