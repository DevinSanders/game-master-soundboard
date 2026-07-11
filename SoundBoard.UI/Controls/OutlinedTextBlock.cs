using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
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

    private Geometry? _geometry;
    private Size _textSize;

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
        return new Size(_textSize.Width + 2 * pad + ShadowOffset,
                        _textSize.Height + 2 * pad + ShadowOffset);
    }

    public override void Render(DrawingContext context)
    {
        if (_geometry == null) return;

        var pad = StrokeThickness;

        // Drop shadow — the same glyph geometry offset down-right.
        if (ShadowBrush is { } shadow && !ReferenceEquals(shadow, Brushes.Transparent))
        {
            using (context.PushTransform(Matrix.CreateTranslation(pad + ShadowOffset, pad + ShadowOffset)))
                context.DrawGeometry(shadow, null, _geometry);
        }

        using (context.PushTransform(Matrix.CreateTranslation(pad, pad)))
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
}
