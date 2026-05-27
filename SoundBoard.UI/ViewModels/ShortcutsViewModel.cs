using SoundBoard.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using SoundBoard.Core.Models;
using SoundBoard.UI.Messages;
using SoundBoard.UI.Services;
using System.Collections.ObjectModel;
using System.Linq;

namespace SoundBoard.UI.ViewModels;

/// <summary>
/// Backs the soundboard view embedded in the main window — the paginated
/// grid of <see cref="ShortcutButton"/>s. Owns the pages, the buttons on
/// the currently-selected page, and the drag-drop bookkeeping that turns
/// tracks / presets / playlists dragged from other windows into new buttons.
/// Registered as singleton so other windows can target the currently-active
/// page when adding shortcuts.
/// </summary>
public partial class ShortcutsViewModel : ViewModelBase, IRecipient<ShortcutAddedMessage>, IRecipient<LibraryRefreshedMessage>, IRecipient<ShortcutsReorderedMessage>
{
    private readonly ISoundBoardDbContextFactory _dbFactory;
    private readonly IAudioPlaybackEngine _playbackEngine;
    private readonly IWindowManagerService _windowManager;
    private readonly ISamplerChainService _samplerChain;
    private readonly ISamplerLauncherService _samplerLauncher;
    private readonly IPluginService _pluginService;

    [ObservableProperty]
    private ObservableCollection<ShortcutPage> _pages = new();

    [ObservableProperty]
    private ShortcutPage? _selectedPage;

    [ObservableProperty]
    private ObservableCollection<ShortcutButtonViewModel> _currentButtons = new();

    public ShortcutsViewModel(ISoundBoardDbContextFactory dbFactory, IAudioPlaybackEngine playbackEngine, IWindowManagerService windowManager,
        ISamplerChainService samplerChain, ISamplerLauncherService samplerLauncher, IPluginService pluginService)
    {
        _dbFactory = dbFactory;
        _playbackEngine = playbackEngine;
        _windowManager = windowManager;
        _samplerChain = samplerChain;
        _samplerLauncher = samplerLauncher;
        _pluginService = pluginService;

        WeakReferenceMessenger.Default.Register<ShortcutAddedMessage>(this);
        WeakReferenceMessenger.Default.Register<LibraryRefreshedMessage>(this);
        WeakReferenceMessenger.Default.Register<ShortcutsReorderedMessage>(this);
        LoadPages();
    }

    public void Receive(ShortcutAddedMessage message)
    {
        ReloadCurrentPage();
    }

    public void Receive(LibraryRefreshedMessage message)
    {
        // A bulk import just landed — re-fetch every page (and its buttons)
        // and reset the visible selection so the user sees the new state.
        LoadPages();
    }

    public void Receive(ShortcutsReorderedMessage message)
    {
        // A popout (or our own future self) persisted a new order for a
        // page. Only reload when it's the page we're currently displaying.
        if (SelectedPage != null && message.Value == SelectedPage.Id)
            ReloadCurrentPage();
    }

    private System.Collections.Generic.List<ShortcutPage> QueryPagesFromDb()
    {
        using var db = _dbFactory.CreateDbContext();
        return db.ShortcutPages
            .AsNoTracking()
            .Include(p => p.Buttons)
                .ThenInclude(b => b.Track)
            .Include(p => p.Buttons)
                .ThenInclude(b => b.Preset)
                    .ThenInclude(pr => pr!.Tracks)
                        .ThenInclude(i => i.Track)
            .Include(p => p.Buttons)
                .ThenInclude(b => b.Playlist)
            .OrderBy(p => p.OrderIndex)
            .ToList();
    }

    private void LoadPages()
    {
        var pages = QueryPagesFromDb();

        if (pages.Count == 0)
        {
            using var db = _dbFactory.CreateDbContext();
            var defaultPage = new ShortcutPage { Name = "Main Board", OrderIndex = 0 };
            db.ShortcutPages.Add(defaultPage);
            db.SaveChanges();
            pages.Add(defaultPage);
        }

        Pages = new ObservableCollection<ShortcutPage>(pages);
        SelectedPage = Pages.First();
    }

    private void ReloadCurrentPage()
    {
        var selectedPageId = SelectedPage?.Id;
        var pages = QueryPagesFromDb();
        Pages = new ObservableCollection<ShortcutPage>(pages);
        SelectedPage = Pages.FirstOrDefault(p => p.Id == selectedPageId) ?? Pages.FirstOrDefault();
    }

    partial void OnSelectedPageChanged(ShortcutPage? value)
    {
        // Dispose the outgoing VMs so their event subscriptions on the
        // singleton playback engine drop. Without this, every page switch
        // would leak the old buttons (engine retains them via its
        // CollectionChanged delegate list).
        foreach (var oldVm in CurrentButtons) oldVm.Dispose();
        CurrentButtons.Clear();
        if (value != null)
        {
            // Ensure buttons have unique Row values for sorting if they were created with defaults
            var buttons = value.Buttons.ToList();
            bool needsSave = false;
            for (int i = 0; i < buttons.Count; i++)
            {
                if (buttons[i].Row == 0 && i > 0 && buttons.All(b => b.Row == 0))
                {
                    buttons[i].Row = i;
                    needsSave = true;
                }
            }
            if (needsSave) FixButtonOrders(value.Id);

            foreach (var btn in value.Buttons.OrderBy(b => b.Row).ThenBy(b => b.Column))
            {
                CurrentButtons.Add(new ShortcutButtonViewModel(btn, _playbackEngine));
            }
        }
    }

    private void FixButtonOrders(int pageId)
    {
        using var db = _dbFactory.CreateDbContext();
        var trackedPage = db.ShortcutPages.Include(p => p.Buttons).FirstOrDefault(p => p.Id == pageId);
        if (trackedPage != null)
        {
            for (int i = 0; i < trackedPage.Buttons.Count; i++)
            {
                trackedPage.Buttons[i].Row = i;
            }
            db.SaveChanges();
        }
    }

    /// <summary>Open the sampler chain editor for a shortcut. Where the
    /// editor lands depends on what the shortcut points at:
    ///
    /// <list type="bullet">
    ///   <item><description><b>Track</b> shortcut → opens the shortcut's
    ///     own editor. Tracks don't have a sampler chain (we preserve the
    ///     library audio source), so the shortcut is where per-target DSP
    ///     hangs.</description></item>
    ///   <item><description><b>Preset / Playlist</b> shortcut → opens the
    ///     target's editor. Those entities own their own chain; we don't
    ///     layer another set of samplers on top of the shortcut, so the
    ///     user has one place to manage effects per audio target.</description></item>
    /// </list></summary>
    public void OpenSamplerEditorFor(ShortcutButtonViewModel btn)
    {
        var model = btn.ButtonModel;

        // Preset target → open the preset's chain editor.
        if (model.PresetId.HasValue && model.Preset != null)
        {
            _samplerLauncher.Open(SoundBoard.Core.Models.SamplerOwnerType.Preset, model.PresetId.Value, model.Preset.Name ?? "");
            return;
        }

        // Playlist target → open the playlist's chain editor.
        if (model.PlaylistId.HasValue && model.Playlist != null)
        {
            _samplerLauncher.Open(SoundBoard.Core.Models.SamplerOwnerType.Playlist, model.PlaylistId.Value, model.Playlist.Name ?? "");
            return;
        }

        // Track target (or empty button) → open the shortcut's own chain.
        _samplerLauncher.Open(SoundBoard.Core.Models.SamplerOwnerType.Shortcut, btn.ModelId, btn.Label ?? "");
    }

    public void SwapButtons(ShortcutButtonViewModel source, ShortcutButtonViewModel target)
    {
        var sIndex = CurrentButtons.IndexOf(source);
        var tIndex = CurrentButtons.IndexOf(target);

        if (sIndex != -1 && tIndex != -1 && sIndex != tIndex)
        {
            // Swap in the UI collection immediately for visual feedback
            CurrentButtons.Move(sIndex, tIndex);
        }
    }

    public void PersistButtonOrder()
    {
        if (SelectedPage == null) return;
        using var db = _dbFactory.CreateDbContext();

        var trackedButtons = db.ShortcutButtons
            .Where(b => b.ShortcutPageId == SelectedPage.Id)
            .ToList();

        for (int i = 0; i < CurrentButtons.Count; i++)
        {
            var vm = CurrentButtons[i];
            var model = trackedButtons.FirstOrDefault(b => b.Id == vm.ModelId);
            if (model != null)
            {
                model.Row = i;
                model.Column = 0;
            }
        }

        db.SaveChanges();

        // Notify any open popouts of this page so they reload from disk.
        WeakReferenceMessenger.Default.Send(new ShortcutsReorderedMessage(SelectedPage.Id));
    }

    // ── Page Management ───────────────────────────────────────

    [RelayCommand]
    private void AddPage()
    {
        using var db = _dbFactory.CreateDbContext();
        var page = new ShortcutPage
        {
            Name = $"Page {Pages.Count + 1}",
            OrderIndex = Pages.Count
        };
        db.ShortcutPages.Add(page);
        db.SaveChanges();

        ReloadCurrentPage();
        SelectedPage = Pages.LastOrDefault();
        WeakReferenceMessenger.Default.Send(new ShortcutPageChangedMessage());
    }

    /// <summary>
    /// Called from the code-behind after the rename dialog closes.
    /// </summary>
    public void RenamePageDirect(int pageId, string newName)
    {
        if (_dbFactory.EditorSave<Core.Models.ShortcutPage>(pageId, p => p.Name = newName))
            WeakReferenceMessenger.Default.Send(new ShortcutPageChangedMessage());
        ReloadCurrentPage();
    }

    /// <summary>
    /// Called from the code-behind when "Delete Page" is clicked.
    /// </summary>
    public void DeletePageDirect(int pageId)
    {
        if (Pages.Count <= 1) return; // Don't delete the last page

        using var db = _dbFactory.CreateDbContext();
        var tracked = db.ShortcutPages.Include(p => p.Buttons).FirstOrDefault(p => p.Id == pageId);
        if (tracked != null)
        {
            // Capture button ids before delete so we can drop any FX Chain
            // attachments tied to them (Shortcut-tier chains apply only to
            // Track-target shortcuts today, but cleaning up unconditionally
            // is correct and cheap).
            var buttonIds = tracked.Buttons.Select(b => b.Id).ToList();
            db.ShortcutButtons.RemoveRange(tracked.Buttons);
            db.ShortcutPages.Remove(tracked);
            db.SaveChanges();
            foreach (var id in buttonIds)
                _samplerChain.RemoveAttachmentsFor(Core.Models.SamplerOwnerType.Shortcut, id);
            WeakReferenceMessenger.Default.Send(new ShortcutPageChangedMessage());
        }
        ReloadCurrentPage();
    }

    // ── Button Management ─────────────────────────────────────

    /// <summary>
    /// Called from the code-behind when "Remove" is clicked on a button.
    /// </summary>
    public void RemoveButtonDirect(int buttonId)
    {
        using var db = _dbFactory.CreateDbContext();
        var tracked = db.ShortcutButtons.Find(buttonId);
        if (tracked != null)
        {
            db.ShortcutButtons.Remove(tracked);
            db.SaveChanges();
            _samplerChain.RemoveAttachmentsFor(Core.Models.SamplerOwnerType.Shortcut, buttonId);
        }
        ReloadCurrentPage();
    }

    /// <summary>
    /// Called from the code-behind after the rename dialog closes for a button.
    /// </summary>
    public void RenameButtonDirect(int buttonId, string newLabel)
    {
        _dbFactory.EditorSave<Core.Models.ShortcutButton>(buttonId, b => b.Label = newLabel);
        ReloadCurrentPage();
    }

    /// <summary>Set the RPG Awesome icon on a shortcut button; pass null to clear.</summary>
    public void SetButtonIconDirect(int buttonId, string? icon)
    {
        _dbFactory.EditorSave<Core.Models.ShortcutButton>(buttonId, b => b.Icon = icon);
        ReloadCurrentPage();
    }

    /// <summary>Set or clear the shortcut's bus override. Only meaningful
    /// for Track-targeting shortcuts — for Preset / Playlist shortcuts the
    /// override is ignored at play time (Presets carry their own override
    /// field; Playlists never override per the design spec). The View's
    /// menu item is hidden for non-Track shortcuts via
    /// <see cref="ShortcutButtonViewModel.IsTrackTarget"/>, so this method
    /// won't see those calls in practice.</summary>
    public void SetButtonBusOverrideDirect(int buttonId, int? busId)
    {
        _dbFactory.EditorSave<Core.Models.ShortcutButton>(buttonId, b => b.BusIdOverride = busId);
        // No ReloadCurrentPage — bus override doesn't surface in any
        // visible button state. The next play picks the new bus up by
        // re-reading the row in BuildTrackProvider.
    }

    /// <summary>Snapshot of every bus, in <see cref="Core.Models.Bus.Order"/>
    /// order. Used by the bus-override dialog the View pops on right-click.</summary>
    public System.Collections.Generic.IReadOnlyList<Core.Models.Bus> ListBuses()
    {
        using var db = _dbFactory.CreateDbContext();
        return db.Buses.OrderBy(b => b.Order).ThenBy(b => b.Id).ToList();
    }

    /// <summary>Current bus override id for a button, or null = inherit.</summary>
    public int? GetButtonBusOverride(int buttonId)
    {
        using var db = _dbFactory.CreateDbContext();
        return db.ShortcutButtons.Where(b => b.Id == buttonId)
                                  .Select(b => b.BusIdOverride)
                                  .FirstOrDefault();
    }

    /// <summary>
    /// Called by ShortcutsView when a Track is dropped onto the grid via drag-and-drop.
    /// </summary>
    public void AddTrackToCurrentPage(Track track)
    {
        if (SelectedPage == null) return;
        using var db = _dbFactory.CreateDbContext();

        var btn = new ShortcutButton
        {
            ShortcutPageId = SelectedPage.Id,
            TrackId = track.Id,
            Label = track.Name,
            Row = CurrentButtons.Count // Add to end
        };
        db.ShortcutButtons.Add(btn);
        db.SaveChanges();

        ReloadCurrentPage();
    }

    /// <summary>Creates a soundboard button for the given preset on the currently
    /// selected page. Called from the Presets window.</summary>
    public void AddPresetToCurrentPage(Preset preset)
    {
        if (SelectedPage == null) return;
        using var db = _dbFactory.CreateDbContext();

        var btn = new ShortcutButton
        {
            ShortcutPageId = SelectedPage.Id,
            PresetId = preset.Id,
            Label = preset.Name,
            Row = CurrentButtons.Count
        };
        db.ShortcutButtons.Add(btn);
        db.SaveChanges();

        ReloadCurrentPage();
    }

    /// <summary>Creates a soundboard button for the given playlist on the
    /// currently selected page. Called from the Playlists window.</summary>
    public void AddPlaylistToCurrentPage(Playlist playlist)
    {
        if (SelectedPage == null) return;
        using var db = _dbFactory.CreateDbContext();

        var btn = new ShortcutButton
        {
            ShortcutPageId = SelectedPage.Id,
            PlaylistId = playlist.Id,
            Label = playlist.Name,
            Row = CurrentButtons.Count
        };
        db.ShortcutButtons.Add(btn);
        db.SaveChanges();

        ReloadCurrentPage();
    }

    /// <summary>Pop the currently-selected page out into its own window.
    /// Each page is keyed by id, so popping the same page twice just brings
    /// the existing window forward; different pages can be popped to their
    /// own separate windows simultaneously.</summary>
    [RelayCommand]
    private void PopOut()
    {
        if (SelectedPage == null) return;
        var popped = new PoppedShortcutPageViewModel(_dbFactory, _playbackEngine, SelectedPage);
        _windowManager.ShowWindow(popped, $"shortcut-page-{SelectedPage.Id}",
                                  $"SoundBoard - {SelectedPage.Name}", 1000, 800);
    }
}
