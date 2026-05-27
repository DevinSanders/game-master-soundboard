using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;

namespace SoundBoard.UI.Controls;

/// <summary>
/// Helpers for card/row drag handlers that need to avoid hijacking pointer
/// gestures intended for interactive children (sliders, toggles, etc.).
/// </summary>
internal static class DragGuards
{
    /// <summary>True if <paramref name="source"/> is, or descends from, an
    /// interactive control nested inside <paramref name="dragRoot"/>. Walks
    /// the logical-parent chain from the event source up toward
    /// <paramref name="dragRoot"/> and stops when it gets there — the
    /// container itself isn't "interactive".</summary>
    public static bool IsInteractiveChild(object? source, object? dragRoot)
    {
        // Cases below cover all the descendants we care about by base type:
        // - Button covers ToggleButton, CheckBox, RadioButton, RepeatButton.
        // - Slider sits on top of an internal Thumb/Track; matching on
        //   Slider catches the whole control, and Thumb/Track are listed
        //   anyway in case the user pressed directly on the rendered thumb.
        var current = source as ILogical;
        while (current != null)
        {
            if (ReferenceEquals(current, dragRoot)) return false;

            switch (current)
            {
                case Slider:
                case Thumb:
                case Track:
                case Button:    // also catches ToggleButton, ToggleSwitch, CheckBox, RadioButton, RepeatButton
                case TextBox:
                case ComboBox:
                case ScrollBar:
                    return true;
            }

            current = current.LogicalParent;
        }
        return false;
    }
}
