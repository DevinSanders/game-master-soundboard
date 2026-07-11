using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Globalization;

namespace SoundBoard.UI.Controls;

/// <summary>
/// A text control that draws its glyphs with a fill, an outline (stroke), and
/// an offset drop shadow — the legibility treatment a plain
/// <see cref="TextBlock"/> can't provide (it has no glyph stroke). Used for
/// shortcut-button labels so text stays readable over a full-size icon or a
/// custom button color without covering the icon with a scrim box.
///
/// <para>Set <see cref="Stroke"/> / <see cref="ShadowBrush"/> to
/// <c>Transparent</c> to render as plain text (the outline and shadow simply
/// don't show), so the same control covers both the "needs help" and the
/// plain-theme-text cases.</para>
///
/// <para><b>Vertical auto-scroll.</b> When the wrapped text is taller than the
/// slot it's arranged into, the control clips to its bounds and slowly scrolls
/// the text up then back down (pausing at each end) so the full label is
/// readable without an ellipsis. Labels that fit render static and cost
/// nothing per frame.</para>
/// </summary>
public class OutlinedTextBlock : Control
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<OutlinedTextBlock, string?>(nameof(Text));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<OutlinedTextBlock, IBrush?>(nameof(Foreground), Brushes.White);

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<OutlinedTextBlock, IBrush?>(nameof(Stroke), Brushes.Transparent);

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<OutlinedTextBlock, double>(nameof(StrokeThickness), 2.0);

    public static readonly StyledProperty<IBrush?> ShadowBrushProperty =
        AvaloniaProperty.Register<OutlinedTextBlock, IBrush?>(nameof(ShadowBrush), Brushes.Transparent);

    public static readonly StyledProperty<double> FontSizeProperty =
        TextBlock.FontSizeProperty.AddOwner<OutlinedTextBlock>();

    public static readonly StyledProperty<FontWeight> FontWeightProperty =
        TextBlock.FontWeightProperty.AddOwner<OutlinedTextBlock>();

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextBlock.FontFamilyProperty.AddOwner<OutlinedTextBlock>();

    public static readonly StyledProperty<TextAlignment> TextAlignmentProperty =
        AvaloniaProperty.Register<OutlinedTextBlock, TextAlignment>(nameof(TextAlignment), TextAlignment.Center);

    static OutlinedTextBlock()
    {
        AffectsMeasure<OutlinedTextBlock>(TextProperty, FontSizeProperty, FontWeightProperty,
            FontFamilyProperty, TextAlignmentProperty, StrokeThicknessProperty);
        AffectsRender<OutlinedTextBlock>(ForegroundProperty, StrokeProperty, ShadowBrushProperty);
    }

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double StrokeThickness { get => GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public IBrush? ShadowBrush { get => GetValue(ShadowBrushProperty); set => SetValue(ShadowBrushProperty, value); }
    public double FontSize { get => GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public FontWeight FontWeight { get => GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
    public FontFamily FontFamily { get => GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public TextAlignment TextAlignment { get => GetValue(TextAlignmentProperty); set => SetValue(TextAlignmentProperty, value); }

    // Shadow offset in device pixels — the hard drop-shadow displacement.
    private const double ShadowOffset = 2.5;

    // Vertical auto-scroll tuning.
    private const double ScrollSpeed = 22.0;   // px/sec once scrolling
    private const double PauseSeconds = 1.6;   // hold at each end

    private Geometry? _geometry;
    private Size _textSize;

    private DispatcherTimer? _timer;
    private DateTime _lastTick;
    private double _phaseTime;
    private double _phaseDuration;
    private double _overflow;   // px of text taller than the slot
    private double _scrollY;    // current vertical scroll offset [0, _overflow]
    private Phase _phase = Phase.PauseAtTop;

    private enum Phase { PauseAtTop, ScrollDown, PauseAtBottom, ScrollUp }

    public OutlinedTextBlock()
    {
        // Clip so scrolled text can't spill past the label's slot into
        // neighbouring buttons.
        ClipToBounds = true;
    }

    private void Build(double maxWidth)
    {
        var text = Text;
        if (string.IsNullOrEmpty(text))
        {
            _geometry = null;
            _textSize = default;
            return;
        }

        var ft = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyle.Normal, FontWeight),
            FontSize <= 0 ? 14 : FontSize,
            Foreground ?? Brushes.White)
        {
            TextAlignment = TextAlignment,
            // Never ellipsize: build the full, untrimmed multi-line geometry so
            // the whole label exists to scroll. Overflow is handled by clipping
            // + vertical auto-scroll, not by trimming.
            Trimming = TextTrimming.None,
        };
        // First lay out with the wrapping constraint to discover how wide the
        // text actually is, then pin MaxTextWidth to that used width. Alignment
        // (e.g. Center) then positions each line within the *actual* text box
        // rather than the full wrap width — otherwise short lines get centered
        // against the wider wrap bound and the whole label reads off-center
        // inside the control's tight bounds.
        if (!double.IsInfinity(maxWidth) && maxWidth > 0)
            ft.MaxTextWidth = maxWidth;
        ft.MaxTextWidth = ft.Width + 1;

        _textSize = new Size(ft.Width, ft.Height);
        _geometry = ft.BuildGeometry(new Point(0, 0));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Reserve room for the stroke (extends ±thickness/2) and the shadow
        // offset so neither is clipped by the control's own bounds.
        var pad = StrokeThickness;
        var maxWidth = double.IsInfinity(availableSize.Width)
            ? double.PositiveInfinity
            : System.Math.Max(0, availableSize.Width - (2 * pad + ShadowOffset));
        Build(maxWidth);
        if (_geometry == null) return default;
        var desiredH = _textSize.Height + 2 * pad + ShadowOffset;
        // Clamp to the slot so a tall (wrapped) label constrains itself and
        // scrolls internally instead of overflowing the button.
        if (!double.IsInfinity(availableSize.Height))
            desiredH = System.Math.Min(desiredH, availableSize.Height);
        return new Size(_textSize.Width + 2 * pad + ShadowOffset, desiredH);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var pad = StrokeThickness;
        var slotForText = finalSize.Height - (2 * pad + ShadowOffset);
        _overflow = System.Math.Max(0, _textSize.Height - slotForText);

        if (_overflow > 1.0)
        {
            EnsureTimerRunning();
        }
        else
        {
            _scrollY = 0;
            StopTimer();
        }
        return base.ArrangeOverride(finalSize);
    }

    public override void Render(DrawingContext context)
    {
        if (_geometry == null) return;

        var pad = StrokeThickness;
        var dy = -_scrollY;

        // Drop shadow — the same glyph geometry offset down-right.
        if (ShadowBrush is { } shadow && !ReferenceEquals(shadow, Brushes.Transparent))
        {
            using (context.PushTransform(Matrix.CreateTranslation(pad + ShadowOffset, pad + ShadowOffset + dy)))
                context.DrawGeometry(shadow, null, _geometry);
        }

        using (context.PushTransform(Matrix.CreateTranslation(pad, pad + dy)))
        {
            // Two passes so the outline sits OUTSIDE the letters: stroke the
            // glyph edge first (a centered pen), then paint the fill on top,
            // which covers the inner half of the stroke. A single fill+pen
            // pass would let the (centered) stroke eat into the fill and read
            // muddy at small sizes.
            if (Stroke is { } s && StrokeThickness > 0 && !ReferenceEquals(s, Brushes.Transparent))
            {
                var pen = new Pen(s, StrokeThickness, lineJoin: PenLineJoin.Round);
                context.DrawGeometry(null, pen, _geometry);
            }
            context.DrawGeometry(Foreground, null, _geometry);
        }
    }

    // ── Vertical scroll animation ────────────────────────────────────────

    private void EnsureTimerRunning()
    {
        if (_timer != null) return;
        _phase = Phase.PauseAtTop;
        _phaseTime = 0;
        _phaseDuration = PauseSeconds;
        _scrollY = 0;
        _lastTick = DateTime.UtcNow;
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
            case Phase.PauseAtTop:
                if (_phaseTime >= _phaseDuration) Advance(Phase.ScrollDown, _overflow / Math.Max(1, ScrollSpeed));
                break;
            case Phase.ScrollDown:
                _scrollY = _overflow * Math.Min(1, _phaseTime / _phaseDuration);
                if (_phaseTime >= _phaseDuration) Advance(Phase.PauseAtBottom, PauseSeconds);
                break;
            case Phase.PauseAtBottom:
                if (_phaseTime >= _phaseDuration) Advance(Phase.ScrollUp, _overflow / Math.Max(1, ScrollSpeed));
                break;
            case Phase.ScrollUp:
                _scrollY = _overflow * (1 - Math.Min(1, _phaseTime / _phaseDuration));
                if (_phaseTime >= _phaseDuration) Advance(Phase.PauseAtTop, PauseSeconds);
                break;
        }
        InvalidateVisual();
    }

    private void Advance(Phase next, double duration)
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
