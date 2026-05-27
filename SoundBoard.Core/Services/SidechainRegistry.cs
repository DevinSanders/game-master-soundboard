using SoundBoard.Core.Audio;
using SoundBoard.Core.Data;
using SoundBoard.Core.Logging;
using SoundBoard.Core.Models;
using SoundBoard.PluginApi;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SoundBoard.Core.Services;

/// <summary>
/// Host implementation of <see cref="ISidechainRegistry"/>. Exposes one
/// <see cref="BusSidechainSource"/> per audio bus so a plugin can
/// subscribe to that bus's post-FX signal as a detection trigger.
///
/// <para><b>Lifecycle.</b> Built once at startup after the bus mixers
/// have been pre-created (i.e. after
/// <see cref="ISamplerChainService.Initialize"/>). The registry holds a
/// reference to <see cref="MasterMixer"/> + <see cref="ISoundBoardDbContextFactory"/>
/// so it can re-read bus metadata when the source list refreshes.</para>
///
/// <para><b>Refresh.</b> The Settings → Buses page calls
/// <see cref="Refresh"/> after add / rename / delete operations; that
/// rebuilds the source list and fires <see cref="SourcesChanged"/> so
/// plugins listening for live updates can re-bind their UIs.</para>
/// </summary>
public sealed class SidechainRegistry : ISidechainRegistry
{
    private readonly MasterMixer _masterMixer;
    private readonly ISoundBoardDbContextFactory _dbFactory;
    private readonly object _lock = new();
    private List<BusSidechainSource> _sources = new();

    public event EventHandler? SourcesChanged;

    public SidechainRegistry(MasterMixer masterMixer, ISoundBoardDbContextFactory dbFactory)
    {
        _masterMixer = masterMixer;
        _dbFactory = dbFactory;
        Refresh();
    }

    public IReadOnlyList<ISidechainSource> GetSources()
    {
        lock (_lock) return _sources.ToArray();
    }

    public ISidechainSource? GetSourceById(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (_lock)
        {
            foreach (var s in _sources)
                if (s.Id == id) return s;
        }
        return null;
    }

    /// <summary>Re-read the Buses table and rebuild the source list.
    /// Called from <see cref="ViewModels.SettingsViewModel"/>'s
    /// add / rename / delete bus commands; idempotent so a UI that
    /// fires twice doesn't churn subscribers.</summary>
    public void Refresh()
    {
        List<Bus> buses;
        using (var db = _dbFactory.CreateDbContext())
            buses = db.Buses.OrderBy(b => b.Order).ThenBy(b => b.Id).ToList();

        bool changed;
        lock (_lock)
        {
            var newSources = new List<BusSidechainSource>(buses.Count);
            // Snapshot old (id, name) pairs BEFORE we mutate _sources so
            // the change-detection at the bottom can see name diffs too.
            // Pre-fix this method only compared ids in sequence — a rename
            // updated the source's DisplayName in place but didn't fire
            // SourcesChanged, so plugin source-pickers kept showing the
            // old name until something else invalidated them.
            var oldPairs = _sources.Select(s => (s.Id, s.DisplayName)).ToList();

            foreach (var bus in buses)
            {
                var mixer = _masterMixer.GetBus(bus.Id);
                if (mixer == null) continue;
                // Reuse an existing source if the bus id matches — same
                // BusMixer reference means subscribers stay valid across
                // a rename refresh.
                var existing = _sources.FirstOrDefault(s => s.BusId == bus.Id);
                if (existing != null)
                {
                    existing.UpdateDisplayName(bus.Name);
                    newSources.Add(existing);
                }
                else
                {
                    newSources.Add(new BusSidechainSource(bus.Id, bus.Name, mixer));
                }
            }

            var newPairs = newSources.Select(s => (s.Id, s.DisplayName)).ToList();
            // Detect any change: count, ids, OR display-name differs.
            // Name diff matters because plugin source-picker UIs key on
            // DisplayName for their dropdown labels.
            changed = !oldPairs.SequenceEqual(newPairs);
            _sources = newSources;
        }
        Log.Debug("Sidechain", $"Refresh: {buses.Count} bus(es) -> {_sources.Count} source(s); changed={changed}.");
        if (changed) SourcesChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// One <see cref="ISidechainSource"/> backed by a <see cref="BusMixer"/>.
/// <see cref="Subscribe"/> forwards directly to
/// <see cref="BusMixer.SubscribeSidechain"/>; the bus mixer fans out a
/// copy of every post-FX buffer to every subscriber on the audio thread.
/// </summary>
public sealed class BusSidechainSource : ISidechainSource
{
    /// <summary>The underlying bus id. Same value used everywhere in
    /// Core for bus routing (<see cref="Track.BusId"/>,
    /// <see cref="Preset.BusIdOverride"/>, etc.).</summary>
    public int BusId { get; }

    public string Id { get; }
    public string DisplayName { get; private set; }
    public int SampleRate { get; }
    public int Channels { get; }

    private readonly BusMixer _mixer;

    public BusSidechainSource(int busId, string name, BusMixer mixer)
    {
        BusId = busId;
        Id = $"bus:{busId}";
        DisplayName = name ?? "";
        _mixer = mixer;
        SampleRate = mixer.WaveFormat.SampleRate;
        Channels = mixer.WaveFormat.Channels;
    }

    internal void UpdateDisplayName(string newName)
    {
        // Plain field write — DisplayName is documented as "host may
        // mutate", and consumers re-read it inside SourcesChanged
        // handlers after marshalling to their dispatcher. No
        // INotifyPropertyChanged contract on the SDK type to avoid
        // forcing every plugin to dispatch UI updates.
        DisplayName = newName ?? "";
    }

    public IDisposable Subscribe(Action<float[], int> onSamples)
    {
        if (onSamples == null) throw new ArgumentNullException(nameof(onSamples));
        return _mixer.SubscribeSidechain(onSamples);
    }
}
