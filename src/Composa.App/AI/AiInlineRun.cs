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

/// <summary>Renders operation feedback in the run button. Only its two small overlay layers animate.</summary>
internal sealed class AiInlineRun
{
    private enum Phase { Ready, Queue, Wait, Progress, Apply, Cancel, Done, Charge, Failed, Cancelled }
    private static readonly Icons.Icon Clock = new("M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0 -18Z M12 7v5l3 2");
    private static readonly Icons.Icon Check = new("M5 12l4 4L19 6");
    private const double Width = 100;

    private readonly Button run;
    private readonly Button more;
    private readonly MenuFlyout variantMenu;
    private readonly Grid face = new() { ClipToBounds = true };
    private readonly Border progress = new() { Background = Gradient("#2B67B8", "#4598F1"),
        Width = Width, HorizontalAlignment = HorizontalAlignment.Left, RenderTransformOrigin = RelativePoint.TopLeft };
    private readonly Border shimmer = new() { Width = Width, HorizontalAlignment = HorizontalAlignment.Left,
        Background = Gradient("#00558BC9", "#668BCAFF", "#00558BC9") };
    private readonly ScaleTransform progressScale = new(0, 1);
    private readonly TranslateTransform sweep = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
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
    private double oldProgress, nextProgress;
    private long progressChanged;
    private AiOperationState? operation;
    private string? charge;
    private int seconds;
    private bool supportsVariants;
    private bool canStart;
    private int shownSecond = -1;

    internal AiInlineRun(Button run, Action<int> launch, Action stop, Action ready)
    {
        this.run = run;
        this.launch = launch;
        this.stop = stop;
        this.ready = ready;
        run.Name = "AiRun";
        run.Classes.Remove("accent");
        run.Width = run.MinWidth = Width;
        run.Height = run.MinHeight = 28;
        run.Padding = default;
        run.BorderThickness = default;
        run.CornerRadius = new CornerRadius(4, 0, 0, 4);
        run.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        run.VerticalContentAlignment = VerticalAlignment.Stretch;
        run.Content = face;
        progress.RenderTransform = progressScale;
        shimmer.RenderTransform = sweep;
        progress.IsHitTestVisible = shimmer.IsHitTestVisible = false;
        face.Children.Add(progress); face.Children.Add(shimmer);
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
        more.Width = 24;
        more.MinWidth = 0;
        more.Height = more.MinHeight = 28;
        more.Padding = default;
        more.CornerRadius = new CornerRadius(0, 4, 4, 0);
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
        timer.Tick += (_, _) => Tick();
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
        if (Holding && View.IsEffectivelyVisible) timer.Start();
    }

    internal void SetVariantEstimates(PartnerPriceEstimate? estimate)
    {
        foreach (var item in variantMenu.Items.OfType<MenuItem>())
            ToolTip.SetTip(item, estimate == null ? null : $"{estimate.RoundedCredits((int)item.Tag!)} · {Locale.T("Credits spent")}");
    }

    internal void Update(AiOperationState? value, string? directCharge)
    {
        if (ReferenceEquals(operation, value)) return;
        var previous = operation?.Status;
        operation = value;
        if (value == null) { clock.Reset(); SetPhase(Phase.Ready); return; }
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
        if (Holding && View.IsEffectivelyVisible) timer.Start();
    }

    private void ConfigureButtons()
    {
        run.IsEnabled = phase != Phase.Ready || canStart;
        run.IsHitTestVisible = run.Focusable = phase == Phase.Ready;
        more.IsVisible = supportsVariants || Busy;
        more.IsEnabled = phase == Phase.Ready && canStart || Busy && phase != Phase.Cancel;
        more.CornerRadius = more.IsVisible ? new CornerRadius(0, 4, 4, 0) : new CornerRadius(4);
        run.CornerRadius = more.IsVisible ? new CornerRadius(4, 0, 0, 4) : new CornerRadius(4);
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
        phase = next;
        run.Background = phase switch
        {
            Phase.Ready => Palette.Active,
            Phase.Wait or Phase.Progress => Brush("#315883"),
            Phase.Done or Phase.Charge => Brush("#30675C"),
            Phase.Failed => Brush("#713C36"),
            Phase.Cancelled => Brush("#454A53"),
            _ => Brush("#35485D")
        };
        caption.Foreground = phase == Phase.Ready ? Palette.ActionText : Brushes.White;
        caption.MaxWidth = phase == Phase.Charge ? 82 : Width;
        caption.TextTrimming = TextTrimming.CharacterEllipsis;
        Icons.Icon? glyph = phase switch
        {
            Phase.Ready => Icons.Run, Phase.Queue or Phase.Wait or Phase.Progress or Phase.Apply => Clock,
            Phase.Done => Check, Phase.Charge when operation?.CreditsUsed != null => Icons.Coin, _ => null
        };
        label = glyph is { } icon ? Ui.Row(4, Icons.Create(icon, 13, caption.Foreground), caption) : Ui.Row(0, caption);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.IsHitTestVisible = false;
        enter = new TranslateTransform(0, outgoing == null ? 0 : 3);
        label.RenderTransform = enter;
        label.Opacity = outgoing == null ? 1 : 0;
        face.Children.Add(label);
        progress.IsVisible = phase == Phase.Progress;
        shimmer.IsVisible = phase == Phase.Wait;
        if (phase == Phase.Queue) progressScale.ScaleX = 0;
        ConfigureButtons();
        UpdateCaption();
    }

    private void UpdateCaption()
    {
        var passed = Math.Max(clock.Elapsed.TotalSeconds, operation?.ElapsedSeconds ?? 0);
        shownSecond = (int)passed;
        var time = $"{(int)(passed / 60)}:{(int)passed % 60:00}";
        var text = phase switch
        {
            Phase.Queue => Locale.T("In queue"),
            Phase.Wait => time,
            Phase.Progress => $"{time} · {Math.Round(nextProgress * 100)}%",
            Phase.Apply => Locale.T("Applying result"),
            Phase.Cancel => Locale.T("Cancelling…"),
            Phase.Done => $"{Locale.T("Generation done")} · {seconds} {Locale.T("Short seconds")}",
            Phase.Charge => charge ?? "",
            Phase.Failed => Locale.T("Generation error"),
            Phase.Cancelled => Locale.T("Generation cancelled"),
            _ => Locale.T("Generation start")
        };
        if (caption.Text != text) caption.Text = text;
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
        var age = Stopwatch.GetElapsedTime(entered).TotalMilliseconds;
        if (outgoing != null)
        {
            var blend = Math.Clamp(age / 165, 0, 1);
            outgoing.Opacity = 1 - blend;
            label!.Opacity = blend;
            enter!.Y = 3 * (1 - blend);
            if (blend >= 1) { face.Children.Remove(outgoing); outgoing = null; }
        }
        if (phase == Phase.Wait) sweep.X = age % 2400 / 2400 * Width * 2 - Width;
        else if (phase == Phase.Progress)
        {
            var fraction = Math.Clamp(Stopwatch.GetElapsedTime(progressChanged).TotalMilliseconds / 180, 0, 1);
            progressScale.ScaleX = oldProgress + (nextProgress - oldProgress) * fraction;
        }
        if (Busy && (int)Math.Max(clock.Elapsed.TotalSeconds, operation?.ElapsedSeconds ?? 0) != shownSecond)
            UpdateCaption();
        if (phase == Phase.Done && age >= 850) SetPhase(charge != null ? Phase.Charge : Phase.Ready);
        else if (phase == Phase.Charge && age >= 1000 || phase == Phase.Cancelled && age >= 1200 || phase == Phase.Failed && age >= 2100)
            SetPhase(Phase.Ready);
        if (phase == Phase.Ready && !readyNotified)
        {
            readyNotified = true;
            ready();
        }
        else if (phase != Phase.Ready) readyNotified = false;
        if (!View.IsEffectivelyVisible || phase == Phase.Ready && outgoing == null) timer.Stop();
        else
        {
            var animating = outgoing != null || phase == Phase.Wait ||
                phase == Phase.Progress && Stopwatch.GetElapsedTime(progressChanged).TotalMilliseconds < 180;
            timer.Interval = TimeSpan.FromMilliseconds(animating ? 33 : 100);
        }
    }

    private bool readyNotified = true;
    private static IBrush Brush(string value) => new SolidColorBrush(Color.Parse(value));
    private static LinearGradientBrush Gradient(params string[] colors)
    {
        var stops = new GradientStops();
        for (int i = 0; i < colors.Length; i++) stops.Add(new GradientStop(Color.Parse(colors[i]), (double)i / (colors.Length - 1)));
        return new LinearGradientBrush { StartPoint = RelativePoint.TopLeft, EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative), GradientStops = stops };
    }
}
