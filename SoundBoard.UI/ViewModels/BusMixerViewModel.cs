using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using SoundBoard.Core.Audio;
using SoundBoard.Core.Data;
using SoundBoard.Core.Logging;
using SoundBoard.Core.Models;
using SoundBoard.Core.Services;
using SoundBoard.UI.Messages;
using SoundBoard.UI.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace SoundBoard.UI.ViewModels;

/// <summary>
/// Backs the Bus Mixer window. One vertical strip per
/// <see cref="Bus"/> row, in <see cref="Bus.Order"/> order. Each strip
/// hosts the bus's name, a volume slider that two-way binds to
/// <see cref="BusMixer.Volume"/>, and an "🎛 FX Chain" button that
/// opens the per-bus FX editor through
/// <see cref="ISamplerLauncherService"/>.
///
/// <para>Loading is one-shot at construction — the strips don't observe
/// the Buses table for live changes because bus add / rename / delete
/// is gated behind the Settings → Buses management page (Phase C6),
/// and that page emits a <see cref="Messages.BusesChangedMessage"/>
/// which this VM listens for to reload.</para>
///
/// <para><b>Persistence.</b> Volume changes flow synchronously into the
/// live <see cref="BusMixer.Volume"/> (so the audio thread picks the new
/// gain up on the next buffer) AND scheduled via <see cref="EditPersistence"/>
/// so the value survives a restart. Same pattern Track / Preset editors
/// use.</para>
/// </summary>
public partial class BusMixerViewModel : ViewModelBase, IDisposable, IRecipient<BusesChangedMessage>
{
    private readonly MasterMixer _masterMixer;
    private readonly ISoundBoardDbContextFactory _dbFactory;
    private readonly ISamplerLauncherService _samplerLauncher;
    private readonly EditPersistence _persistence = new();

    /// <summary>One strip per bus, in <see cref="Bus.Order"/> order.</summary>
    public ObservableCollection<BusStripViewModel> Strips { get; } = new();

    public BusMixerViewModel(MasterMixer masterMixer, ISoundBoardDbContextFactory dbFactory,
                              ISamplerLauncherService samplerLauncher)
    {
        _masterMixer = masterMixer;
        _dbFactory = dbFactory;
        _samplerLauncher = samplerLauncher;

        Load();
        // Listen for Settings → Buses page mutations so the strip list
        // (and each strip's bound Bus name) updates without the user
        // having to close and reopen the Bus Mixer window.
        WeakReferenceMessenger.Default.Register(this);
    }

    /// <summary>Receive a <see cref="BusesChangedMessage"/> dispatched by
    /// the Settings → Buses page. Reload the strip list so renames and
    /// add/delete operations surface live.</summary>
    public void Receive(BusesChangedMessage message)
    {
        Load();
    }

    /// <summary>Re-read the Buses table and rebuild the strip list. Called
    /// at construction and from <see cref="ReloadCommand"/> when the user
    /// adds / removes buses from the Settings → Buses page.</summary>
    [RelayCommand]
    public void Reload() => Load();

    private void Load()
    {
        // Flush any pending volume saves from the previous bus list
        // BEFORE clearing the strips. Otherwise a debounced write from
        // a slider drag that immediately preceded a Reload would be
        // dropped silently when the new strip list replaces the old.
        try { _persistence.Flush(); }
        catch (Exception ex) { Log.Warn("BusMixerVM", $"Persistence.Flush during Reload threw: {ex.Message}"); }

        Strips.Clear();

        using var db = _dbFactory.CreateDbContext();
        var buses = db.Buses.OrderBy(b => b.Order).ThenBy(b => b.Id).ToList();
        foreach (var b in buses)
        {
            // EnsureBus is idempotent — if the bus mixer was already
            // created by SamplerChainService.Initialize at startup, we
            // just get the same instance back.
            var mixer = _masterMixer.EnsureBus(b.Id);
            Strips.Add(new BusStripViewModel(b, mixer, _samplerLauncher, _persistence, _dbFactory));
        }
        Log.Debug("BusMixerVM", $"Loaded {Strips.Count} bus strip(s).");
    }

    /// <summary>Flush any pending volume saves and tear down the
    /// <see cref="EditPersistence"/> timer. Called by
    /// <see cref="WindowManagerService"/> on Bus Mixer window close /
    /// swap-replace. BusMixerViewModel is factory-resolved per-open
    /// (Transient + <c>Func&lt;BusMixerViewModel&gt;</c>), so a fresh
    /// instance is created every time the user clicks "🎚 Bus Mixer".
    /// Without this Dispose the previous instance's timer (and its
    /// captured Strips + DB factory closures) would stay rooted via
    /// the dispatcher's timer list — every open would leak one VM.</summary>
    public void Dispose()
    {
        try { WeakReferenceMessenger.Default.UnregisterAll(this); }
        catch (Exception ex) { Log.Warn("BusMixerVM", $"Messenger.UnregisterAll threw: {ex.Message}"); }
        try { _persistence.Dispose(); }
        catch (Exception ex) { Log.Warn("BusMixerVM", $"Persistence.Dispose threw: {ex.Message}"); }
    }
}

/// <summary>
/// One bus's row in the Bus Mixer. Volume is bound through the live
/// <see cref="BusMixer.Volume"/> so the audio thread sees changes
/// immediately; the persistence layer debounces the DB write.
/// </summary>
public sealed class BusStripViewModel : ObservableObject
{
    private readonly Bus _bus;
    private readonly BusMixer _mixer;
    private readonly ISamplerLauncherService _samplerLauncher;
    private readonly EditPersistence _persistence;
    private readonly ISoundBoardDbContextFactory _dbFactory;

    public int BusId => _bus.Id;
    public string Name => _bus.Name;

    /// <summary>Linear gain. The slider exposes 0.0–2.0 (same range as
    /// every other volume slider in the app); the readout converter
    /// formats as "%".</summary>
    public double Volume
    {
        get => _mixer.Volume;
        set
        {
            var f = (float)value;
            if (System.Math.Abs(_mixer.Volume - f) < 1e-6f) return;
            _mixer.Volume = f;
            OnPropertyChanged();

            // Persist with a stable per-bus key so a slider drag collapses
            // to one DB write rather than dozens.
            _persistence.Schedule($"Bus.Volume.{_bus.Id}", () =>
            {
                using var db = _dbFactory.CreateDbContext();
                var row = db.Buses.Find(_bus.Id);
                if (row == null) return;
                row.Volume = f;
                db.SaveChanges();
            });
        }
    }

    /// <summary>Open the FX chain editor for this bus. The launcher dedups
    /// per-owner so clicking 🎛 on the same strip twice focuses the
    /// existing editor instead of opening a duplicate.</summary>
    public IRelayCommand OpenFxChainCommand { get; }

    public BusStripViewModel(Bus bus, BusMixer mixer, ISamplerLauncherService samplerLauncher,
                              EditPersistence persistence, ISoundBoardDbContextFactory dbFactory)
    {
        _bus = bus;
        _mixer = mixer;
        _samplerLauncher = samplerLauncher;
        _persistence = persistence;
        _dbFactory = dbFactory;

        OpenFxChainCommand = new RelayCommand(() =>
            _samplerLauncher.Open(SamplerOwnerType.Bus, _bus.Id, _bus.Name));
    }
}
