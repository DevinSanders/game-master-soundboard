using SoundBoard.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
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
/// Hosts one <see cref="ShortcutPage"/> in its own pop-out window. Loads
/// just that page's buttons; refreshes when anyone sends a
/// <see cref="ShortcutAddedMessage"/> targeting this page or when the
/// library is reloaded.
///
/// The main <see cref="ShortcutsViewModel"/> stays in the main window and
/// keeps showing its currently-selected page. The popped instance is a
/// separate view of the same page — the data is shared (via the DB), so
/// clicks on either keep the engine and outlines in sync.
/// </summary>
public partial class PoppedShortcutPageViewModel : ViewModelBase,
    IRecipient<ShortcutAddedMessage>, IRecipient<LibraryRefreshedMessage>,
    IRecipient<ShortcutsReorderedMessage>, IDisposable
{
    private readonly ISoundBoardDbContextFactory _dbFactory;
    private readonly IAudioPlaybackEngine _playbackEngine;
    private bool _disposed;

    public int PageId { get; }
    public string PageName { get; private set; }

    [ObservableProperty]
    private ObservableCollection<ShortcutButtonViewModel> _currentButtons = new();

    public PoppedShortcutPageViewModel(ISoundBoardDbContextFactory dbFactory,
                                       IAudioPlaybackEngine playbackEngine,
                                       ShortcutPage page)
    {
        _dbFactory = dbFactory;
        _playbackEngine = playbackEngine;
        PageId = page.Id;
        PageName = page.Name;

        WeakReferenceMessenger.Default.Register<ShortcutAddedMessage>(this);
        WeakReferenceMessenger.Default.Register<LibraryRefreshedMessage>(this);
        WeakReferenceMessenger.Default.Register<ShortcutsReorderedMessage>(this);

        ReloadButtons();
    }

    public void Receive(ShortcutAddedMessage message)
    {
        // Only refresh when the added shortcut targets THIS page.
        if (message.Value == PageId) ReloadButtons();
    }

    public void Receive(LibraryRefreshedMessage message) => ReloadButtons();

    public void Receive(ShortcutsReorderedMessage message)
    {
        // Reload only when the message targets THIS page — other popouts /
        // the main window send the same message for their own pages.
        if (message.Value == PageId) ReloadButtons();
    }

    private void ReloadButtons()
    {
        // Dispose outgoing VMs so they don't keep listening to engine events.
        foreach (var oldVm in CurrentButtons) oldVm.Dispose();

        using var db = _dbFactory.CreateDbContext();
        var page = db.ShortcutPages
            .AsNoTracking()
            .Include(p => p.Buttons)
                .ThenInclude(b => b.Track)
            .Include(p => p.Buttons)
                .ThenInclude(b => b.Preset)
                    .ThenInclude(pr => pr!.Tracks)
                        .ThenInclude(i => i.Track)
            .Include(p => p.Buttons)
                .ThenInclude(b => b.Playlist)
            .FirstOrDefault(p => p.Id == PageId);

        var fresh = new ObservableCollection<ShortcutButtonViewModel>();
        if (page != null)
        {
            PageName = page.Name;
            OnPropertyChanged(nameof(PageName));
            foreach (var btn in page.Buttons.OrderBy(b => b.Row).ThenBy(b => b.Id))
                fresh.Add(new ShortcutButtonViewModel(btn, _playbackEngine));
        }
        CurrentButtons = fresh;
    }

    public void SwapButtons(ShortcutButtonViewModel source, ShortcutButtonViewModel target)
    {
        var sIndex = CurrentButtons.IndexOf(source);
        var tIndex = CurrentButtons.IndexOf(target);
        if (sIndex != -1 && tIndex != -1 && sIndex != tIndex)
            CurrentButtons.Move(sIndex, tIndex);
    }

    public void PersistButtonOrder()
    {
        using var db = _dbFactory.CreateDbContext();
        var trackedButtons = db.ShortcutButtons
            .Where(b => b.ShortcutPageId == PageId)
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

        // Notify the main soundboard (and any other popouts of this page) so
        // they reload from disk and pick up the new order.
        WeakReferenceMessenger.Default.Send(new ShortcutsReorderedMessage(PageId));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        WeakReferenceMessenger.Default.UnregisterAll(this);
        foreach (var vm in CurrentButtons) vm.Dispose();
    }
}
