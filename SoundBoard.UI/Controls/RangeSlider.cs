using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using System;

namespace SoundBoard.UI.Controls;

/// <summary>
/// A custom control providing a range selection with two thumbs (Start and End).
/// </summary>
public class RangeSlider : TemplatedControl
{
    // ── Styled Properties ─────────────────────────────────────

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<RangeSlider, double>(nameof(Minimum), 0.0);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<RangeSlider, double>(nameof(Maximum), 100.0);

    public static readonly StyledProperty<double> StartValueProperty =
        AvaloniaProperty.Register<RangeSlider, double>(nameof(StartValue), 0.0, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> EndValueProperty =
        AvaloniaProperty.Register<RangeSlider, double>(nameof(EndValue), 100.0, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double StartValue { get => GetValue(StartValueProperty); set => SetValue(StartValueProperty, value); }
    public double EndValue { get => GetValue(EndValueProperty); set => SetValue(EndValueProperty, value); }

    // ── Internal State ────────────────────────────────────────

    private Thumb? _startThumb;
    private Thumb? _endThumb;
    private Canvas? _canvas;
    private Rectangle? _rangeHighlight;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        _startThumb = e.NameScope.Find<Thumb>("PART_StartThumb");
        _endThumb = e.NameScope.Find<Thumb>("PART_EndThumb");
        _canvas = e.NameScope.Find<Canvas>("PART_Canvas");
        _rangeHighlight = e.NameScope.Find<Rectangle>("PART_RangeHighlight");

        if (_canvas != null)
        {
            _canvas.PropertyChanged += (s, e) =>
            {
                if (e.Property == BoundsProperty) UpdateThumbPositions();
            };
        }

        if (_startThumb != null) _startThumb.DragDelta += OnStartThumbDragDelta;
        if (_endThumb != null) _endThumb.DragDelta += OnEndThumbDragDelta;
        
        UpdateThumbPositions();
    }

    private void OnStartThumbDragDelta(object? sender, VectorEventArgs e)
    {
        if (_canvas == null || Maximum <= Minimum) return;

        double deltaValue = (e.Vector.X / _canvas.Bounds.Width) * (Maximum - Minimum);
        double newValue = Math.Clamp(StartValue + deltaValue, Minimum, EndValue - 0.01);
        StartValue = newValue;
    }

    private void OnEndThumbDragDelta(object? sender, VectorEventArgs e)
    {
        if (_canvas == null || Maximum <= Minimum) return;

        double deltaValue = (e.Vector.X / _canvas.Bounds.Width) * (Maximum - Minimum);
        double newValue = Math.Clamp(EndValue + deltaValue, StartValue + 0.01, Maximum);
        EndValue = newValue;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StartValueProperty || change.Property == EndValueProperty || 
            change.Property == MinimumProperty || change.Property == MaximumProperty ||
            change.Property == BoundsProperty)
        {
            UpdateThumbPositions();
        }
    }

    private void UpdateThumbPositions()
    {
        if (_startThumb == null || _endThumb == null || _canvas == null || Bounds.Width <= 0) return;

        double range = Maximum - Minimum;
        if (range <= 0) return;

        double startPos = ((StartValue - Minimum) / range) * _canvas.Bounds.Width;
        double endPos = ((EndValue - Minimum) / range) * _canvas.Bounds.Width;

        Canvas.SetLeft(_startThumb, startPos - (_startThumb.Bounds.Width / 2));
        Canvas.SetLeft(_endThumb, endPos - (_endThumb.Bounds.Width / 2));

        if (_rangeHighlight != null)
        {
            Canvas.SetLeft(_rangeHighlight, startPos);
            _rangeHighlight.Width = Math.Max(0, endPos - startPos);
        }
    }
}
