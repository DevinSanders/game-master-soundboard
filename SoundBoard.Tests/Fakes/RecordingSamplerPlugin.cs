using NAudio.Wave;
using SoundBoard.PluginApi;

namespace SoundBoard.Tests.Fakes;

/// <summary>
/// Test double for <see cref="IAudioSamplerPlugin"/>. Each
/// <see cref="CreateInstance"/> call returns a fresh
/// <see cref="RecordingSamplerInstance"/>; the test keeps a reference to
/// every spawned instance via <see cref="SpawnedInstances"/> so it can
/// assert on per-instance state (wet read counts, gain values, etc.).
/// </summary>
public sealed class RecordingSamplerPlugin : IAudioSamplerPlugin
{
    public RecordingSamplerPlugin(string id = "test.recording", float defaultGain = 2.0f)
    {
        Id = id;
        _defaultGain = defaultGain;
    }

    private readonly float _defaultGain;
    private readonly List<RecordingSamplerInstance> _spawned = new();

    public string Id { get; }
    public string Name => "Recording (test)";
    public string Description => "Doubles input and counts reads for testing.";
    public string Version => "1.0.0";
    public string Author => "tests";

    public SamplerAttachmentPoints SupportedAttachments => SamplerAttachmentPoints.All;

    /// <summary>Every instance handed out by this plugin, in spawn order.
    /// Tests use this to find the live wrapper(s) for an attachment.</summary>
    public IReadOnlyList<RecordingSamplerInstance> SpawnedInstances => _spawned;

    public ISamplerInstance CreateInstance()
    {
        var inst = new RecordingSamplerInstance(_defaultGain);
        _spawned.Add(inst);
        return inst;
    }

    public void Initialize(IPluginContext context) { }
    public void Shutdown() { }
}

/// <summary>
/// Test <see cref="ISamplerInstance"/> that applies a configurable gain
/// to its source. The wet wrapper increments
/// <see cref="WetReadCount"/> and accumulates <see cref="WetSamplesRead"/>
/// on every <c>Read</c> — the central probe for the "wet state stays
/// warm during bypass" regression test (Phase 1 #2).
///
/// <para>Config JSON is a single number — the gain value. Round-trip via
/// <see cref="SerializeConfig"/> / <see cref="DeserializeConfig"/> is the
/// same fast path the real <c>AttenuatorPlugin</c> uses, so
/// <c>PushLiveConfig</c> tests exercise a realistic-shaped payload.</para>
/// </summary>
public sealed class RecordingSamplerInstance : ISamplerInstance
{
    private float _gain;
    private int _wetReadCount;
    private int _wetSamplesRead;
    private bool _disposed;

    public RecordingSamplerInstance(float gain)
    {
        _gain = gain;
    }

    /// <summary>Current gain. Mutated by tests directly or via
    /// <see cref="DeserializeConfig"/>.</summary>
    public float Gain
    {
        get => _gain;
        set => _gain = value;
    }

    /// <summary>How many times <c>Read</c> was called on the wet provider.
    /// 0 means the wet was never pulled — the canonical signal of the
    /// bypass state-freeze bug.</summary>
    public int WetReadCount => _wetReadCount;

    /// <summary>How many samples have flowed through the wet provider.</summary>
    public int WetSamplesRead => _wetSamplesRead;

    public bool IsDisposed => _disposed;

    public string SerializeConfig() => _gain.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    public void DeserializeConfig(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        if (float.TryParse(json, System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out var g))
            _gain = g;
    }

    public ISampleProvider CreateEffect(ISampleProvider source) => new GainProvider(source, this);

    public object? CreateControl() => null;

    public void Dispose() => _disposed = true;

    private sealed class GainProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly RecordingSamplerInstance _owner;

        public GainProvider(ISampleProvider source, RecordingSamplerInstance owner)
        {
            _source = source;
            _owner = owner;
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            float g = _owner._gain;
            for (int i = 0; i < read; i++)
                buffer[offset + i] *= g;

            // Probe counters — sampled by tests to detect bypass-freeze.
            _owner._wetReadCount++;
            _owner._wetSamplesRead += read;
            return read;
        }
    }
}
