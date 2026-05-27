using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System;

namespace SoundBoard.UI.Services;

/// <summary>
/// App-wide mouse-wheel adjustment for every <see cref="Slider"/>. The
/// user expectation is "hover any slider, scroll wheel = nudge value" —
/// matching native desktop conventions for volume controls. Avalonia's
/// stock <see cref="Slider"/> doesn't handle wheel input, so this class
/// registers a single class-level handler that catches the event on any
/// Slider instance in any window.
///
/// <para><b>Step size.</b> 2% of the slider's range per wheel tick. That
/// works across every slider in the app — volume (range 0–2 → 4% per
/// tick), fade (0–10 → 0.2 s), start-delay (0–60 → 1.2 s), and an
/// unrelated plugin slider with any range gets sensible behaviour
/// automatically.</para>
///
/// <para><b>Why class handlers.</b> A class handler fires for every
/// instance of the target type without per-control wiring. Adding wheel
/// support via attached behaviour or per-view code-behind would require
/// touching every XAML file the app ships, plus every plugin's editor.
/// One class handler covers them all — host + plugins — for free.</para>
///
/// <para><b>ScrollViewer interaction.</b> The handler sets
/// <c>e.Handled = true</c>, so a slider inside a ScrollViewer captures
/// the wheel event when the cursor is over it and the parent scroller
/// doesn't see it. Move the cursor off the slider and wheel scrolls the
/// view as normal — the standard "wheel goes to the control under the
/// cursor" behaviour.</para>
/// </summary>
public static class SliderWheelInput
{
    /// <summary>Step expressed as a fraction of the slider's
    /// (Maximum - Minimum). 0.02 = 2% per wheel notch.</summary>
    private const double RangeFractionPerTick = 0.02;

    private static bool _registered;

    /// <summary>Register the class-level wheel handler. Idempotent — safe
    /// to call more than once. Call from
    /// <see cref="Avalonia.Application.OnFrameworkInitializationCompleted"/>
    /// once Avalonia's static event registry is initialised.</summary>
    public static void Register()
    {
        if (_registered) return;
        _registered = true;

        // Tunnel-only: catches the event on its way DOWN to the slider
        // before any child handler can mark it Handled. Adding Bubble too
        // would be redundant — by the time the event bubbled back up
        // we've already set e.Handled = true, so the bubble pass is a
        // no-op that still runs the range / step / clamp math for nothing.
        Slider.PointerWheelChangedEvent.AddClassHandler<Slider>(
            (slider, e) => OnWheel(slider, e),
            RoutingStrategies.Tunnel);
    }

    private static void OnWheel(Slider slider, PointerWheelEventArgs e)
    {
        if (slider == null) return;
        // Disabled sliders shouldn't respond — matches the rest of the
        // input subsystem (mouse clicks on a disabled control also no-op).
        if (!slider.IsEnabled) return;

        double range = slider.Maximum - slider.Minimum;
        if (range <= 0) return;

        // Avalonia's PointerWheelEventArgs.Delta is a Vector where Y is
        // the vertical wheel rotation. Positive Y = scrolled UP (toward
        // the screen). Both horizontal and vertical sliders honour the
        // same convention: wheel up = value up. A horizontal mouse wheel
        // (.Delta.X) on horizontal sliders could be wired here too, but
        // it's a rare gesture and the vertical wheel is what users
        // actually have — keep the simple path.
        double step = range * RangeFractionPerTick * e.Delta.Y;
        if (step == 0) return;

        double next = slider.Value + step;
        if (next < slider.Minimum) next = slider.Minimum;
        else if (next > slider.Maximum) next = slider.Maximum;
        slider.Value = next;

        // Consume the event so a ScrollViewer ancestor doesn't also scroll
        // the page when the user is adjusting the slider. The handler only
        // fires when the slider is in the hit-test path under the cursor,
        // so scrolling elsewhere on the page still works normally.
        e.Handled = true;
    }
}
