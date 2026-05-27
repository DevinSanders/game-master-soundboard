using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace SoundBoard.UI.Controls;

/// <summary>
/// Renders a single RPG Awesome glyph. Set <see cref="IconName"/> to a class
/// name like <c>"ra-sword"</c>; the control resolves the codepoint and binds
/// the embedded font automatically. Inherits Foreground from its parent, so
/// theme brushes (e.g. <c>TextPrimary</c>) flow through naturally.
/// </summary>
public class RpgIcon : TextBlock
{
    public static readonly StyledProperty<string?> IconNameProperty =
        AvaloniaProperty.Register<RpgIcon, string?>(nameof(IconName));

    public string? IconName
    {
        get => GetValue(IconNameProperty);
        set => SetValue(IconNameProperty, value);
    }

    public RpgIcon()
    {
        // RPG Awesome family is registered in App.axaml resources.
        if (Application.Current?.TryGetResource("RPGAwesome", null, out var family) == true
            && family is FontFamily ff)
        {
            FontFamily = ff;
        }
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Center;
        TextAlignment = TextAlignment.Center;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IconNameProperty)
        {
            Text = RpgAwesomeIcons.GetGlyph(IconName);
        }
    }
}
