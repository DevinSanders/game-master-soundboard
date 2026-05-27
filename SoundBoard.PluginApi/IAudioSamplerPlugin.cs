namespace SoundBoard.PluginApi;

/// <summary>
/// A DSP plugin that the host can attach to one or more audio targets
/// (master bus, individual shortcuts, presets, or playlists). The plugin
/// itself is a factory — <see cref="CreateInstance"/> returns a fresh
/// <see cref="ISamplerInstance"/> with default settings each time. The
/// host creates one instance per attachment so multiple Reverbs with
/// different decay times can coexist without sample-history contamination
/// or parameter aliasing.
///
/// <para>Library tracks are intentionally not exposed as attachment
/// targets: the host preserves each Track row's audio as an unmodified source.
/// Effects ride on the things that <i>activate</i> a track (a shortcut,
/// a preset entry, or a playlist item), or on the master bus.</para>
/// </summary>
public interface IAudioSamplerPlugin : IPlugin
{
    /// <summary>Where the host is allowed to attach instances of this
    /// sampler. A flags enum; combine values for samplers that make sense
    /// in multiple places (e.g. a Reverb at any target, vs. a Ducking
    /// sampler that only makes sense at <see cref="SamplerAttachmentPoints.Master"/>).</summary>
    SamplerAttachmentPoints SupportedAttachments { get; }

    /// <summary>Spawn a new instance with default settings. Called once per
    /// attachment the host materialises (typically at app launch after
    /// reading the persisted chain from the database).</summary>
    ISamplerInstance CreateInstance();
}

/// <summary>
/// One configured DSP node owned by a single attachment. The host owns the
/// instance's lifetime: it's created at attach time, fed config via
/// <see cref="DeserializeConfig"/>, edited live via the UI returned from
/// <see cref="CreateControl"/>, persisted via <see cref="SerializeConfig"/>,
/// inserted into the audio chain by <see cref="CreateEffect"/>, and
/// disposed when the user removes the attachment.
///
/// <para><b>Threading.</b> <see cref="CreateEffect"/> is called from the
/// UI thread when audio starts; the returned <c>ISampleProvider.Read</c>
/// runs on the audio thread. Settings backing the UI control are read on
/// the audio thread and written from the UI thread when the user adjusts
/// a knob OR when the host pushes live config via
/// <see cref="DeserializeConfig"/>. Plugin authors are responsible for
/// keeping these reads/writes safe — common patterns are <c>volatile</c>
/// primitives, <c>Interlocked.Exchange</c> for struct configs, or a
/// lock-free single-writer queue for richer state. See
/// <see cref="DeserializeConfig"/> for the full contract.</para>
///
/// <para><b>Exception isolation.</b> The host wraps every plugin's
/// <c>Read</c> in a safety harness: a throw from <c>Read</c> doesn't
/// kill the audio thread — the buffer is silenced for that cycle, the
/// throw is logged once with stack, and after 3 consecutive throws the
/// instance is permanently bypassed for the rest of the session. Hangs,
/// however, will stall the mixer. Don't block in <c>Read</c>.</para>
/// </summary>
public interface ISamplerInstance : System.IDisposable
{
    /// <summary>Serialise this instance's current settings to a JSON
    /// string the host can persist. Round-trips through
    /// <see cref="DeserializeConfig"/>.</summary>
    string SerializeConfig();

    /// <summary>Restore settings from a previously-serialised JSON blob.
    /// Called once at attach time (after construction, before any audio
    /// flows through). Should tolerate empty or malformed input by
    /// falling back to defaults.
    ///
    /// <para><b>Thread-safety contract.</b> The host MAY also call this
    /// from the UI thread WHILE the audio thread is reading from this
    /// instance — specifically during live config push from the FX Chain
    /// editor (every ~100 ms while a slider is being dragged). Plugin
    /// authors must therefore ensure <c>DeserializeConfig</c> updates
    /// the instance's audio-visible state atomically:</para>
    /// <list type="bullet">
    /// <item>For scalar parameters (gain, threshold, …) — use
    ///   <c>Interlocked.Exchange</c> on an int/long bit-pattern of the
    ///   float, or mark backing fields <c>volatile</c>.</item>
    /// <item>For richer state (coefficient arrays, lookup tables, …) —
    ///   build the new state into a fresh object, then publish via
    ///   <c>Volatile.Write</c> / <c>Interlocked.Exchange</c> of a
    ///   reference. The audio thread reads the reference with
    ///   <c>Volatile.Read</c> and uses whichever object it got. Don't
    ///   mutate fields of an object the audio thread is currently
    ///   reading.</item>
    /// </list>
    /// <para>A <c>lock</c> on the audio-thread Read path is forbidden by
    /// contract (the host's <c>SafeSampleProvider</c> won't catch a
    /// priority inversion). If your config is too complex for atomic
    /// publishing, queue it for the audio thread to swap in at the top
    /// of <c>Read</c>.</para></summary>
    void DeserializeConfig(string json);

    /// <summary>Wrap <paramref name="source"/> with the plugin's DSP using
    /// the instance's current settings. Implementations must preserve the
    /// source's <see cref="NAudio.Wave.WaveFormat"/> — the host mixer is
    /// 48 kHz IEEE-float stereo by contract.</summary>
    NAudio.Wave.ISampleProvider CreateEffect(NAudio.Wave.ISampleProvider source);

    /// <summary>Return an Avalonia <c>Control</c> (or a ViewModel the host
    /// can resolve to one) bound to <i>this</i> instance's settings.
    /// Editing the control updates this instance live; the host's audio
    /// chain picks up the new values on the next buffer. Return
    /// <c>null</c> if the sampler has no configurable parameters.</summary>
    object? CreateControl();
}

/// <summary>Attachment points where the host can mount sampler instances.
/// Flag-combinable so a plugin can declare it works in several contexts.</summary>
[System.Flags]
public enum SamplerAttachmentPoints
{
    /// <summary>No attachment points — the sampler can't be mounted anywhere.
    /// Effectively disables the plugin; use a real flag instead.</summary>
    None     = 0,
    /// <summary>The post-combine master output. A Master instance persists
    /// for the app's lifetime and affects everything regardless of bus.</summary>
    Master   = 1,
    /// <summary>A soundboard shortcut that targets a single Track. Ephemeral:
    /// a fresh instance is created per activation and disposed when it stops.</summary>
    Shortcut = 2,
    /// <summary>A preset. Ephemeral per preset spawn — the chain wraps every
    /// track the preset plays.</summary>
    Preset   = 4,
    /// <summary>A playlist. Ephemeral per playlist-item spawn.</summary>
    Playlist = 8,
    /// <summary>An individual audio bus (Music, Ambient, SFX, or a custom
    /// bus the user has added). The plugin's instance persists for the
    /// app's lifetime, like Master — bus FX run AFTER the bus's internal
    /// mixing and BEFORE the bus signal feeds into the master combine,
    /// so sidechain plugins listening to one bus can apply gain to a
    /// different one without one signal contaminating the other.</summary>
    Bus      = 16,
    /// <summary>Every attachment point at once — convenience for a sampler
    /// that's valid everywhere (e.g. a generic gain or EQ).</summary>
    All      = Master | Shortcut | Preset | Playlist | Bus
}
