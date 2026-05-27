using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System;
using System.Collections.Generic;

namespace SoundBoard.UI.Controls;

/// <summary>
/// A centered waveform bar visualizer — vertical bars emanate symmetrically
/// from a horizontal center line, similar to a SoundCloud-style waveform.
/// </summary>
public class AudioVisualizer : Control
{
    public static readonly StyledProperty<IBrush> BarBrushProperty =
        AvaloniaProperty.Register<AudioVisualizer, IBrush>(nameof(BarBrush), Brushes.LightGreen);

    public IBrush BarBrush
    {
        get => GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    // Bound to anything that exposes a sample stream — works for both
    // PlayingTrackViewModel and PlayingPresetViewModel via IAudioSamples.
    public static readonly StyledProperty<ViewModels.IAudioSamples?> SourceProperty =
        AvaloniaProperty.Register<AudioVisualizer, ViewModels.IAudioSamples?>(nameof(Source));

    public ViewModels.IAudioSamples? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SourceProperty)
        {
            if (change.OldValue is ViewModels.IAudioSamples oldSrc)
                oldSrc.AudioDataAvailable -= OnAudioDataAvailable;
            if (change.NewValue is ViewModels.IAudioSamples newSrc)
                newSrc.AudioDataAvailable += OnAudioDataAvailable;
        }
    }

    private void OnAudioDataAvailable(object? sender, float[] data)
    {
        PushAudioData(data);
    }

    // ── Audio Data Processing ─────────────────────────────────

    // _barHistory is mutated ONLY from the UI thread (inside the
    // Dispatcher.Post callback). The audio thread computes the per-buffer
    // amplitude scalar and posts that single float; the List itself never
    // touches the audio thread. Pre-fix this was directly mutated from
    // the audio-thread event handler — racing the UI's Render iteration.
    private readonly List<float> _barHistory = new();
    private const int BarCount = 48; // Number of bars to display

    public void PushAudioData(float[] buffer)
    {
        if (buffer.Length == 0) return;

        // Peak amplitude per chunk — visually closer to a SoundCloud-style
        // waveform than RMS, which under-represents short transients in
        // music. A small boost pushes typical music peaks (~0.6–0.8) near
        // the top of the bar; clamp so full-scale never overdraws.
        // Computed on the calling thread (audio) but only reads the buffer
        // — no shared state mutated here.
        float peak = 0f;
        for (int i = 0; i < buffer.Length; i++)
        {
            float a = buffer[i];
            if (a < 0) a = -a;
            if (a > peak) peak = a;
        }
        float amplitude = Math.Min(1.0f, peak * 1.3f);

        // Marshal the history mutation + invalidate to the UI thread so
        // _barHistory has a single writer (UI) and Render's iteration
        // can never race a concurrent Add/RemoveAt.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _barHistory.Add(amplitude);
            if (_barHistory.Count > BarCount)
                _barHistory.RemoveAt(0);
            InvalidateVisual();
        });
    }

    // ── Rendering ─────────────────────────────────────────────

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var centerY = bounds.Height / 2.0;
        var barWidth = bounds.Width / BarCount;
        var gap = Math.Max(1.0, barWidth * 0.2); // 20% gap between bars
        var actualBarWidth = barWidth - gap;

        if (actualBarWidth < 1) actualBarWidth = 1;

        // Draw center line (subtle)
        var linePen = new Pen(BarBrush, 0.5, lineCap: PenLineCap.Round);
        context.DrawLine(linePen, new Point(0, centerY), new Point(bounds.Width, centerY));

        // Draw bars from right (newest) to left (oldest)
        for (int i = 0; i < _barHistory.Count; i++)
        {
            var amplitude = _barHistory[i];

            // Position from left to right
            var x = i * barWidth + gap / 2.0;
            var barHalfHeight = amplitude * (centerY - 1); // Leave 1px margin

            if (barHalfHeight < 0.5) barHalfHeight = 0.5; // Minimum visible bar

            // Draw top bar (above center)
            var topRect = new Rect(x, centerY - barHalfHeight, actualBarWidth, barHalfHeight);
            // Draw bottom bar (below center, mirrored)
            var bottomRect = new Rect(x, centerY, actualBarWidth, barHalfHeight);

            context.DrawRectangle(BarBrush, null, topRect, actualBarWidth / 4, actualBarWidth / 4);
            context.DrawRectangle(BarBrush, null, bottomRect, actualBarWidth / 4, actualBarWidth / 4);
        }
    }
}
