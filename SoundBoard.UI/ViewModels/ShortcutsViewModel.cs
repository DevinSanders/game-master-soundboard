using SoundBoard.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using SoundBoard.Core.Models;
using SoundBoard.UI.Messages;
using SoundBoard.UI.Services;
using System.Collections.Generic;
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

    /// <summary>Full set of pages in the library, ordered by OrderIndex.
    /// Used internally for lookups and as the source for
    /// <see cref="VisiblePages"/> / <see cref="HiddenPages"/>; the view
    /// binds to those filtered collections rather than this one.</summary>
    [ObservableProperty]
    private ObservableCollection<ShortcutPage> _pages = new();

    /// <summary>Pages currently shown in the tab strip — every page that
    /// has <c>IsHidden=false</c>, ordered by OrderIndex. Kept as a stable
    /// reference (mutated in place) so the TabStrip's SelectedItem
    /// binding survives a refresh.</summary>
    public ObservableCollection<ShortcutPage> VisiblePages { get; } = new();

    /// <summary>Pages the user has hidden — surfaced in the
    /// "Hidden tabs ▾" overflow popup so they can be shown again. Empty
    /// when no page is hidden; the overflow button binds visibility to
    /// <see cref="HasHiddenPages"/>.</summary>
    public ObservableCollection<ShortcutPage> HiddenPages { get; } = new();

    [ObservableProperty]
    private bool _hasHiddenPages;

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
        RefreshVisibleAndHidden();
        SelectedPage = VisiblePages.FirstOrDefault() ?? Pages.First();
    }

    private void ReloadCurrentPage()
    {
        var selectedPageId = SelectedPage?.Id;
        var pages = QueryPagesFromDb();
        Pages = new ObservableCollection<ShortcutPage>(pages);
        RefreshVisibleAndHidden();
        // Prefer the previously-selected page if it's still visible. If it
        // got hidden (or deleted), fall through to the first visible page.
        SelectedPage = VisiblePages.FirstOrDefault(p => p.Id == selectedPageId)
                     ?? VisiblePages.FirstOrDefault()
                     ?? Pages.FirstOrDefault();
    }

    /// <summary>Project <see cref="Pages"/> into the two view-bound
    /// collections (<see cref="VisiblePages"/> + <see cref="HiddenPages"/>)
    /// without reassigning either — the TabStrip's SelectedItem binding
    /// and any open popup ItemsControls are pinned to the existing
    /// references and would lose state on reassign.</summary>
    private void RefreshVisibleAndHidden()
    {
        VisiblePages.Clear();
        foreach (var p in Pages.Where(p => !p.IsHidden).OrderBy(p => p.OrderIndex))
            VisiblePages.Add(p);

        HiddenPages.Clear();
        foreach (var p in Pages.Where(p => p.IsHidden).OrderBy(p => p.OrderIndex))
            HiddenPages.Add(p);

        HasHiddenPages = HiddenPages.Count > 0;
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

    /// <summary>Mark a page as hidden — it disappears from the tab strip
    /// but stays in the database (preserves its buttons + position) and
    /// resurfaces in the "Hidden tabs ▾" overflow popup. Called from the
    /// code-behind when "Hide tab" is clicked. Refuses to hide the last
    /// visible tab; the user always needs somewhere to land.</summary>
    public void HidePageDirect(int pageId)
    {
        var visibleCount = Pages.Count(p => !p.IsHidden);
        if (visibleCount <= 1) return;
        if (_dbFactory.EditorSave<Core.Models.ShortcutPage>(pageId, p => p.IsHidden = true))
            WeakReferenceMessenger.Default.Send(new ShortcutPageChangedMessage());
        ReloadCurrentPage();
    }

    /// <summary>Re-show a hidden page. Called from the code-behind when
    /// the user clicks ▸ Show next to a row in the "Hidden tabs ▾" popup.</summary>
    public void ShowPageDirect(int pageId)
    {
        if (_dbFactory.EditorSave<Core.Models.ShortcutPage>(pageId, p => p.IsHidden = false))
            WeakReferenceMessenger.Default.Send(new ShortcutPageChangedMessage());
        ReloadCurrentPage();
        SelectedPage = VisiblePages.FirstOrDefault(p => p.Id == pageId) ?? SelectedPage;
    }

    /// <summary>Visual-only swap during a drag. The ghost reorder
    /// controller calls this many times per drag to keep the tab strip
    /// in sync with the dragged ghost; <see cref="ReorderPages"/> commits
    /// the final order to disk once the user releases. Mirrors the
    /// SwapButtons / PersistButtonOrder pair used by the grid.</summary>
    public void SwapPages(ShortcutPage source, ShortcutPage target)
    {
        var sIndex = VisiblePages.IndexOf(source);
        var tIndex = VisiblePages.IndexOf(target);
        if (sIndex != -1 && tIndex != -1 && sIndex != tIndex)
            VisiblePages.Move(sIndex, tIndex);
    }

    /// <summary>Persist a new tab order from the soundboard's drag-reorder
    /// gesture. <paramref name="orderedVisibleIds"/> lists the page ids in
    /// the order the user dragged them in the tab strip; we walk it and
    /// stamp OrderIndex on each page. Hidden pages retain their existing
    /// OrderIndex (the drag UI never showed them) — they keep their
    /// relative position among the hidden set, sorted after the visible
    /// run by the higher OrderIndex values we assign here.</summary>
    public void ReorderPages(IReadOnlyList<int> orderedVisibleIds)
    {
        if (orderedVisibleIds.Count == 0) return;
        using var db = _dbFactory.CreateDbContext();
        var tracked = db.ShortcutPages.ToList();
        for (int i = 0; i < orderedVisibleIds.Count; i++)
        {
            var page = tracked.FirstOrDefault(p => p.Id == orderedVisibleIds[i]);
            if (page != null) page.OrderIndex = i;
        }
        // Hidden pages get pushed past the visible run so they re-sort
        // cleanly if any of them comes back. Preserve their relative
        // order among themselves.
        int next = orderedVisibleIds.Count;
        foreach (var hidden in tracked.Where(p => p.IsHidden).OrderBy(p => p.OrderIndex))
            hidden.OrderIndex = next++;
        db.SaveChanges();
        ReloadCurrentPage();
        WeakReferenceMessenger.Default.Send(new ShortcutPageChangedMessage());
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
