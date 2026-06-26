using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using System;

namespace SoundBoard.UI.Controls;

/// <summary>
/// Single-line text label that scrolls horizontally when its content is
/// wider than the slot it's been arranged into, and renders as a normal
/// static label when the content fits. Used by the mixer cards so long
/// track / playlist names don't have to choose between an ellipsis and
/// overflowing into adjacent controls.
///
/// <para><b>Animation pattern.</b> Pause at the start so the user can
/// read the leading characters; scroll left at <see cref="ScrollSpeed"/>
/// px/sec; pause at the right edge so the trailing characters can be
/// read; scroll back to the start. Repeats forever. Stops automatically
/// when the control leaves the visual tree (timer + tick handler unwired
/// in <see cref="OnDetachedFromVisualTree"/>) so dismissed mixer cards
/// don't leak a dispatcher subscription.</para>
///
/// <para><b>Why not Avalonia.Animation.</b> Keyframe animations are
/// declarative and clean, but the scroll distance depends on the
/// difference between text desired width and the container's arranged
/// width — both runtime-measured. A single ArrangeOverride callback +
/// DispatcherTimer keeps the logic next to the measurement.</para>
/// </summary>
public sealed class MarqueeTextBlock : Border
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<MarqueeTextBlock, string?>(nameof(Text));

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Pixels per second once the scroll starts. Default 30 —
    /// slow enough to read, fast enough that a 30-character name finishes
    /// in ~10 seconds.</summary>
    public static readonly StyledProperty<double> ScrollSpeedProperty =
        AvaloniaProperty.Register<MarqueeTextBlock, double>(nameof(ScrollSpeed), 30.0);

    public double ScrollSpeed
    {
        get => GetValue(ScrollSpeedProperty);
        set => SetValue(ScrollSpeedProperty, value);
    }

    /// <summary>Hold time at each endpoint, in seconds. Default 2.0.</summary>
    public static readonly StyledProperty<double> PauseSecondsProperty =
        AvaloniaProperty.Register<MarqueeTextBlock, double>(nameof(PauseSeconds), 2.0);

    public double PauseSeconds
    {
        get => GetValue(PauseSecondsProperty);
        set => SetValue(PauseSecondsProperty, value);
    }

    // AddOwner each typography property so the host XAML can set them
    // directly on this control (FontWeight, FontSize, Foreground…) and
    // the binding flows through to the inner TextBlock that actually
    // renders the glyphs.
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextBlock.ForegroundProperty.AddOwner<MarqueeTextBlock>();
    public new IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public static readonly StyledProperty<double> FontSizeProperty =
        TextBlock.FontSizeProperty.AddOwner<MarqueeTextBlock>();
    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public static readonly StyledProperty<FontWeight> FontWeightProperty =
        TextBlock.FontWeightProperty.AddOwner<MarqueeTextBlock>();
    public FontWeight FontWeight
    {
        get => GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextBlock.FontFamilyProperty.AddOwner<MarqueeTextBlock>();
    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    private readonly TextBlock _text;
    private readonly TranslateTransform _transform;

    private DispatcherTimer? _timer;
    private DateTime _lastTick;
    private double _phaseTime;
    private double _phaseDuration;
    private double _overflow;
    private Phase _phase = Phase.PauseAtStart;

    private enum Phase { PauseAtStart, ScrollLeft, PauseAtEnd, ScrollRight }

    public MarqueeTextBlock()
    {
        ClipToBounds = true;
        _transform = new TranslateTransform();
        _text = new TextBlock
        {
            RenderTransform = _transform,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        Child = _text;

        // Forward this-control properties to the inner TextBlock that
        // does the actual rendering. Binding lives in code so the XAML
        // host doesn't have to know there's a wrapper at all.
        _text.Bind(TextBlock.TextProperty,       this.GetObservable(TextProperty));
        _text.Bind(TextBlock.ForegroundProperty, this.GetObservable(ForegroundProperty));
        _text.Bind(TextBlock.FontSizeProperty,   this.GetObservable(FontSizeProperty));
        _text.Bind(TextBlock.FontWeightProperty, this.GetObservable(FontWeightProperty));
        _text.Bind(TextBlock.FontFamilyProperty, this.GetObservable(FontFamilyProperty));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var arranged = base.ArrangeOverride(finalSize);

        // Re-measure the inner text against unbounded width so the
        // overflow comparison sees the full natural width — a TextBlock
        // arranged inside a clipping Border has already been clamped to
        // the container's width by base.ArrangeOverride and would always
        // report DesiredSize.Width == finalSize.Width.
        _text.Measure(new Size(double.PositiveInfinity, finalSize.Height));
        _overflow = _text.DesiredSize.Width - finalSize.Width;

        if (_overflow > 1.0)
        {
            EnsureTimerRunning();
        }
        else
        {
            // Fits — snap the transform back to zero and shut the timer
            // off so non-overflowing labels cost nothing per frame.
            _transform.X = 0;
            StopTimer();
        }
        return arranged;
    }

    private void EnsureTimerRunning()
    {
        if (_timer != null) return;
        _phase = Phase.PauseAtStart;
        _phaseTime = 0;
        _phaseDuration = PauseSeconds;
        _lastTick = DateTime.UtcNow;
        _transform.X = 0;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) }; // ~30 fps
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void StopTimer()
    {
        if (_timer == null) return;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer = null;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = (now - _lastTick).TotalSeconds;
        _lastTick = now;
        _phaseTime += dt;

        switch (_phase)
        {
            case Phase.PauseAtStart:
                if (_phaseTime >= _phaseDuration) AdvancePhase(Phase.ScrollLeft, _overflow / Math.Max(1, ScrollSpeed));
                break;

            case Phase.ScrollLeft:
                _transform.X = -_overflow * Math.Min(1, _phaseTime / _phaseDuration);
                if (_phaseTime >= _phaseDuration) AdvancePhase(Phase.PauseAtEnd, PauseSeconds);
                break;

            case Phase.PauseAtEnd:
                if (_phaseTime >= _phaseDuration) AdvancePhase(Phase.ScrollRight, _overflow / Math.Max(1, ScrollSpeed));
                break;

            case Phase.ScrollRight:
                _transform.X = -_overflow * (1 - Math.Min(1, _phaseTime / _phaseDuration));
                if (_phaseTime >= _phaseDuration) AdvancePhase(Phase.PauseAtStart, PauseSeconds);
                break;
        }
    }

    private void AdvancePhase(Phase next, double duration)
    {
        _phase = next;
        _phaseTime = 0;
        _phaseDuration = duration;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        StopTimer();
        base.OnDetachedFromVisualTree(e);
    }
}
