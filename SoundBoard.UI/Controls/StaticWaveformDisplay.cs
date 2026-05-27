using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SoundBoard.Core.Audio;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SoundBoard.UI.Controls;

/// <summary>
/// Pre-renders the full RMS waveform of an audio file and displays it as
/// centered symmetric bars. Supports start/end point markers.
/// </summary>
public class StaticWaveformDisplay : Control
{
    // ── Styled Properties ─────────────────────────────────────

    public static readonly StyledProperty<string?> FilePathProperty =
        AvaloniaProperty.Register<StaticWaveformDisplay, string?>(nameof(FilePath));

    public static readonly StyledProperty<double> StartPointSecondsProperty =
        AvaloniaProperty.Register<StaticWaveformDisplay, double>(nameof(StartPointSeconds));

    public static readonly StyledProperty<double> EndPointSecondsProperty =
        AvaloniaProperty.Register<StaticWaveformDisplay, double>(nameof(EndPointSeconds));

    public static readonly StyledProperty<double> TotalDurationSecondsProperty =
        AvaloniaProperty.Register<StaticWaveformDisplay, double>(nameof(TotalDurationSeconds));

    // Default to the live theme's WaveformBrush so the waveform follows
    // the active palette out of the box. Callers can still override via
    // an explicit binding if they want a custom color per control instance.
    public static readonly StyledProperty<IBrush> WaveformBrushProperty =
        AvaloniaProperty.Register<StaticWaveformDisplay, IBrush>(nameof(WaveformBrush),
            ResolveThemeBrush("WaveformBrush", "#2563EB"));

    public static readonly StyledProperty<IBrush> StartMarkerBrushProperty =
        AvaloniaProperty.Register<StaticWaveformDisplay, IBrush>(nameof(StartMarkerBrush), Brushes.LimeGreen);

    public static readonly StyledProperty<IBrush> EndMarkerBrushProperty =
        AvaloniaProperty.Register<StaticWaveformDisplay, IBrush>(nameof(EndMarkerBrush), Brushes.OrangeRed);

    public string? FilePath
    {
        get => GetValue(FilePathProperty);
        set => SetValue(FilePathProperty, value);
    }

    public double StartPointSeconds
    {
        get => GetValue(StartPointSecondsProperty);
        set => SetValue(StartPointSecondsProperty, value);
    }

    public double EndPointSeconds
    {
        get => GetValue(EndPointSecondsProperty);
        set => SetValue(EndPointSecondsProperty, value);
    }

    public double TotalDurationSeconds
    {
        get => GetValue(TotalDurationSecondsProperty);
        set => SetValue(TotalDurationSecondsProperty, value);
    }

    public IBrush WaveformBrush
    {
        get => GetValue(WaveformBrushProperty);
        set => SetValue(WaveformBrushProperty, value);
    }

    public IBrush StartMarkerBrush
    {
        get => GetValue(StartMarkerBrushProperty);
        set => SetValue(StartMarkerBrushProperty, value);
    }

    public IBrush EndMarkerBrush
    {
        get => GetValue(EndMarkerBrushProperty);
        set => SetValue(EndMarkerBrushProperty, value);
    }

    // ── Internal State ────────────────────────────────────────

    private float[]? _rmsData;
    private bool _isLoading;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _ = LoadWaveformAsync();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == FilePathProperty)
        {
            _ = LoadWaveformAsync();
        }
        else if (change.Property == StartPointSecondsProperty || change.Property == EndPointSecondsProperty)
        {
            InvalidateVisual();
        }
    }

    /// <summary>
    /// Reads the audio file and computes per-bar RMS amplitudes for the full duration.
    /// </summary>
    private async Task LoadWaveformAsync()
    {
        _rmsData = null;
        _isLoading = true;
        InvalidateVisual();

        var path = FilePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _isLoading = false;
            InvalidateVisual();
            return;
        }

        try
        {
            // NVorbis on certain corrupt .ogg files spins inside ReadSamples
            // indefinitely. Task.Run + cancellation token can't unstick a
            // native-CPU-bound loop. The canonical pattern: run the scan on
            // a dedicated background Thread, hold the reader in a
            // volatile field so a watchdog Task can dispose it from
            // outside — dispose-while-reading makes the next decoder access
            // throw, which unwinds the scan thread cleanly. Mirror of
            // the watchdog pattern in TrackEditorViewModel.TrimSilence
            // (works around NVorbis hanging on malformed OGG files).
            var tcs = new TaskCompletionSource<(float[] rmsData, double totalSeconds)>();
            ISeekableSampleProvider? sharedReader = null;

            var scanThread = new Thread(() =>
            {
                try
                {
                    var localReader = AudioFileReaderCrossPlatform.Create(path);
                    System.Threading.Volatile.Write(ref sharedReader, localReader);
                    var result = ComputeRmsDataWith(localReader);
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                finally
                {
                    var local = System.Threading.Volatile.Read(ref sharedReader);
                    if (local is IDisposable d)
                    {
                        try { d.Dispose(); } catch { /* watchdog may have disposed already */ }
                    }
                }
            }) { IsBackground = true, Name = "WaveformScanner" };
            scanThread.Start();

            // 15s watchdog — generous for large files, short enough that a
            // hung scan doesn't leave the editor stuck on the "Loading…"
            // text forever.
            var timeout = Task.Delay(TimeSpan.FromSeconds(15));
            var winner = await Task.WhenAny(tcs.Task, timeout).ConfigureAwait(true);
            if (winner == timeout)
            {
                var stuck = System.Threading.Volatile.Read(ref sharedReader);
                if (stuck is IDisposable d)
                {
                    try { d.Dispose(); } catch { /* already torn down */ }
                }
                _rmsData = null;
            }
            else
            {
                var (rmsData, totalSeconds) = await tcs.Task.ConfigureAwait(true);
                _rmsData = rmsData;
                TotalDurationSeconds = totalSeconds;
            }
        }
        catch
        {
            _rmsData = null;
        }

        _isLoading = false;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => InvalidateVisual());
    }

    /// <summary>Pull a theme brush by key, falling back to a hex literal
    /// if Application.Current isn't available (designer / unit tests).
    /// Resolved at access time so the active theme's palette wins.</summary>
    private static IBrush ResolveThemeBrush(string key, string fallbackHex)
    {
        var app = Application.Current;
        if (app?.Resources is { } resources)
        {
            if (resources.TryGetResource(key, app.ActualThemeVariant, out var found) && found is IBrush brush)
                return brush;
        }
        return new SolidColorBrush(Color.Parse(fallbackHex));
    }

    /// <summary>Compute RMS bars from an already-open reader. Disposal is
    /// the caller's responsibility — see <see cref="LoadWaveformAsync"/>'s
    /// scan-thread finally for the watchdog-friendly path.</summary>
    private static (float[] rmsData, double totalSeconds) ComputeRmsDataWith(ISeekableSampleProvider reader)
    {
        const int barCount = 200; // Resolution of the waveform

        var totalSeconds = reader.TotalTime.TotalSeconds;
        var totalSamples = (long)(totalSeconds * reader.WaveFormat.SampleRate * reader.WaveFormat.Channels);
        var samplesPerBar = (int)(totalSamples / barCount);

        if (samplesPerBar < 1) samplesPerBar = 1;

        var rmsData = new float[barCount];
        var buffer = new float[samplesPerBar];

        for (int bar = 0; bar < barCount; bar++)
        {
            int read = reader.Read(buffer, 0, samplesPerBar);
            if (read == 0) break;

            double sumSquares = 0;
            for (int i = 0; i < read; i++)
            {
                sumSquares += buffer[i] * buffer[i];
            }
            rmsData[bar] = (float)Math.Sqrt(sumSquares / read);
        }

        // Normalize to 0..1
        float maxRms = 0;
        foreach (var v in rmsData)
        {
            if (v > maxRms) maxRms = v;
        }
        if (maxRms > 0)
        {
            for (int i = 0; i < rmsData.Length; i++)
            {
                rmsData[i] /= maxRms;
            }
        }

        return (rmsData, totalSeconds);
    }

    // ── Rendering ─────────────────────────────────────────────

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        // Background — pull from PanelBackground1 so it tracks the
        // active theme rather than a hardcoded near-black.
        var bg = ResolveThemeBrush("PanelBackground1", "#0D1117");
        context.DrawRectangle(bg, null,
            new Rect(0, 0, bounds.Width, bounds.Height), 6, 6);

        if (_isLoading)
        {
            // Draw loading text
            var text = new FormattedText("Loading waveform...", System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, Typeface.Default, 12, Brushes.Gray);
            context.DrawText(text, new Point(bounds.Width / 2 - text.Width / 2, bounds.Height / 2 - text.Height / 2));
            return;
        }

        if (_rmsData == null || _rmsData.Length == 0) return;

        var centerY = bounds.Height / 2.0;
        var barCount = _rmsData.Length;
        var barWidth = bounds.Width / barCount;
        var gap = Math.Max(0.5, barWidth * 0.15);
        var actualBarWidth = barWidth - gap;
        if (actualBarWidth < 1) actualBarWidth = 1;

        // Draw dimmed region outside start/end points
        if (TotalDurationSeconds > 0)
        {
            var dimBrush = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0));

            if (StartPointSeconds > 0)
            {
                var startX = (StartPointSeconds / TotalDurationSeconds) * bounds.Width;
                context.DrawRectangle(dimBrush, null, new Rect(0, 0, startX, bounds.Height));
            }

            if (EndPointSeconds > 0 && EndPointSeconds < TotalDurationSeconds)
            {
                var endX = (EndPointSeconds / TotalDurationSeconds) * bounds.Width;
                context.DrawRectangle(dimBrush, null, new Rect(endX, 0, bounds.Width - endX, bounds.Height));
            }
        }

        // Draw center line
        var linePen = new Pen(WaveformBrush, 0.5);
        context.DrawLine(linePen, new Point(0, centerY), new Point(bounds.Width, centerY));

        // Draw bars
        for (int i = 0; i < barCount; i++)
        {
            var amplitude = _rmsData[i];
            var x = i * barWidth + gap / 2.0;
            var barHalfHeight = amplitude * (centerY - 2);
            if (barHalfHeight < 0.5) barHalfHeight = 0.5;

            var topRect = new Rect(x, centerY - barHalfHeight, actualBarWidth, barHalfHeight);
            var bottomRect = new Rect(x, centerY, actualBarWidth, barHalfHeight);

            context.DrawRectangle(WaveformBrush, null, topRect, actualBarWidth / 4, actualBarWidth / 4);
            context.DrawRectangle(WaveformBrush, null, bottomRect, actualBarWidth / 4, actualBarWidth / 4);
        }

        // Draw start/end markers
        if (TotalDurationSeconds > 0)
        {
            if (StartPointSeconds > 0)
            {
                var startX = (StartPointSeconds / TotalDurationSeconds) * bounds.Width;
                var pen = new Pen(StartMarkerBrush, 2);
                context.DrawLine(pen, new Point(startX, 0), new Point(startX, bounds.Height));

                var label = new FormattedText("START", System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, Typeface.Default, 9, StartMarkerBrush);
                context.DrawText(label, new Point(startX + 3, 2));
            }

            if (EndPointSeconds > 0 && EndPointSeconds < TotalDurationSeconds)
            {
                var endX = (EndPointSeconds / TotalDurationSeconds) * bounds.Width;
                var pen = new Pen(EndMarkerBrush, 2);
                context.DrawLine(pen, new Point(endX, 0), new Point(endX, bounds.Height));

                var label = new FormattedText("END", System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, Typeface.Default, 9, EndMarkerBrush);
                context.DrawText(label, new Point(endX - label.Width - 3, 2));
            }
        }
    }
}
