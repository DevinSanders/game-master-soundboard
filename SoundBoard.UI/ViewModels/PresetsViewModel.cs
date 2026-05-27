using SoundBoard.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using SoundBoard.Core.Models;
using SoundBoard.UI.Messages;
using SoundBoard.UI.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace SoundBoard.UI.ViewModels;

/// <summary>
/// Top-level Presets window — list of presets with create/rename/delete.
/// Opening a preset launches a <see cref="PresetEditorViewModel"/> window.
/// </summary>
public partial class PresetsViewModel : ViewModelBase, IRecipient<LibraryRefreshedMessage>, IRecipient<PresetItemsChangedMessage>
{
    private readonly ISoundBoardDbContextFactory _dbFactory;
    private readonly IWindowManagerService _windowManager;
    private readonly Func<PresetEditorViewModel> _editorFactory;
    private readonly IAudioPlaybackEngine _playbackEngine;
    private readonly ISamplerChainService _samplerChain;

    [ObservableProperty]
    private ObservableCollection<Preset> _presets = new();

    [ObservableProperty]
    private Preset? _selectedPreset;

    public PresetsViewModel(
        ISoundBoardDbContextFactory dbFactory,
        IWindowManagerService windowManager,
        Func<PresetEditorViewModel> editorFactory,
        IAudioPlaybackEngine playbackEngine,
        ISamplerChainService samplerChain)
    {
        _dbFactory = dbFactory;
        _windowManager = windowManager;
        _editorFactory = editorFactory;
        _playbackEngine = playbackEngine;
        _samplerChain = samplerChain;
        WeakReferenceMessenger.Default.Register<LibraryRefreshedMessage>(this);
        WeakReferenceMessenger.Default.Register<PresetItemsChangedMessage>(this);
        Reload();
    }

    public void Receive(LibraryRefreshedMessage message) => Reload();

    public void Receive(PresetItemsChangedMessage message) => Reload();

    public void Reload()
    {
        var prevSelectedId = SelectedPreset?.Id;

        using var db = _dbFactory.CreateDbContext();
        var data = db.Presets
            .AsNoTracking()
            .Include(p => p.Tracks)
                .ThenInclude(t => t.Track)
            .OrderBy(p => p.Name)
            .ToList();
        Presets = new ObservableCollection<Preset>(data);

        if (prevSelectedId.HasValue)
            SelectedPreset = Presets.FirstOrDefault(p => p.Id == prevSelectedId.Value);
    }

    [RelayCommand]
    private void CreatePreset()
    {
        using var db = _dbFactory.CreateDbContext();
        var preset = new Preset { Name = $"Preset {Presets.Count + 1}" };
        db.Presets.Add(preset);
        db.SaveChanges();
        Reload();
        SelectedPreset = Presets.FirstOrDefault(p => p.Id == preset.Id);
        OpenSelected();
    }

    [RelayCommand]
    private void DeletePreset(Preset? preset)
    {
        if (preset == null) return;
        using var db = _dbFactory.CreateDbContext();
        var tracked = db.Presets
            .Include(p => p.Tracks)
            .FirstOrDefault(p => p.Id == preset.Id);
        if (tracked == null) return;

        // Clean up everything that points at this preset before deleting it.
        // The nullable FKs on ShortcutButton.PresetId and PlaylistItem.PresetId
        // don't cascade at the SQLite level — EF's default ClientSetNull only
        // nulls the FK on entities currently tracked, which isn't enough here
        // because we haven't loaded those tables. SQLite would reject the
        // DELETE with "FOREIGN KEY constraint failed" otherwise.
        var presetId = tracked.Id;
        var orphanedButtons = db.ShortcutButtons.Where(b => b.PresetId == presetId).ToList();
        db.ShortcutButtons.RemoveRange(orphanedButtons);

        var orphanedPlaylistItems = db.PlaylistItems.Where(i => i.PresetId == presetId).ToList();
        db.PlaylistItems.RemoveRange(orphanedPlaylistItems);

        db.PresetTracks.RemoveRange(tracked.Tracks);
        db.Presets.Remove(tracked);
        db.SaveChanges();

        // Drop any FX Chain attachments owned by this preset. Done after
        // the Preset DELETE commits so we never end up with a chain
        // service holding live instances tied to a row that's about to
        // come back via rollback.
        _samplerChain.RemoveAttachmentsFor(Core.Models.SamplerOwnerType.Preset, presetId);

        Reload();
    }

    [RelayCommand]
    private void OpenSelected()
    {
        if (SelectedPreset == null) return;
        var editor = _editorFactory();
        editor.LoadPreset(SelectedPreset.Id);
        // Per-preset key so opening A then B doesn't swap the content of
        // a shared window and discard A's in-flight debounced writes.
        _windowManager.ShowWindow(editor, key: $"preset-editor-{SelectedPreset.Id}",
            title: $"Preset: {SelectedPreset.Name}", width: 1100, height: 700);
    }

    [RelayCommand]
    private void PlayPreset(Preset? preset)
    {
        if (preset == null) return;
        _playbackEngine.PlayPreset(preset);
    }

    [RelayCommand]
    private void StopPreset(Preset? preset)
    {
        if (preset == null) return;
        _playbackEngine.StopPreset(preset);
    }
}
