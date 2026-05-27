using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using System;

namespace SoundBoard.UI.Services;

/// <summary>
/// View-side glue that detects slider drag start/end and forwards the
/// burst to an <see cref="EditPersistence"/> instance. Subscribed once per
/// editor view at construction; pulls the live persistence ref via the
/// <paramref name="getPersistence"/> callback so a DataContext swap is
/// picked up automatically.
///
/// We listen at the root control with the Tunnel strategy and inspect the
/// event source: anything inside a <see cref="Slider"/> kicks off a burst
/// on PointerPressed and ends it on PointerCaptureLost. Other controls
/// (textboxes, toggles, buttons) are ignored — their setters debounce
/// naturally on the 300 ms timer.
/// </summary>
public static class SliderBurstBehavior
{
    public static void Attach(Control root, Func<EditPersistence?> getPersistence)
    {
        root.AddHandler(InputElement.PointerPressedEvent, (s, e) =>
        {
            if (IsInsideSlider(e.Source))
                getPersistence()?.BeginBurst();
        }, RoutingStrategies.Tunnel);

        // PointerCaptureLost is the reliable "drag ended" signal — fires
        // whether the user released the mouse, clicked outside, or the
        // window lost focus. Tunnel strategy so we see it even if the
        // Slider marks the event handled.
        root.AddHandler(InputElement.PointerCaptureLostEvent, (s, e) =>
        {
            if (IsInsideSlider(e.Source))
                getPersistence()?.EndBurst();
        }, RoutingStrategies.Tunnel);
    }

    private static bool IsInsideSlider(object? source)
    {
        if (source is not Visual v) return false;
        return v is Slider || v.FindAncestorOfType<Slider>() != null;
    }
}
