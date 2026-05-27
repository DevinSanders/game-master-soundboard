using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using SoundBoard.UI.Controls;
using SoundBoard.UI.ViewModels;
using SoundBoard.PluginApi;

namespace SoundBoard.UI.Services;

/// <summary>
/// Spawns top-level chrome windows for view-models.
///
/// At most one window per view-model <see cref="Type"/> is kept open at any
/// time. Asking for a second one swaps the existing window's content + title
/// to the new view-model and activates it, so flows like "edit a different
/// preset" reuse the existing editor window instead of opening duplicates.
/// </summary>
public class WindowManagerService : IWindowManagerService
{
    // Keyed by string so each call can pick its own dedupe granularity.
    // Type-based callers use the type's full name; keyed callers (e.g. one
    // window per shortcut page) pass an explicit "page-42"-style key.
    private readonly Dictionary<string, AppWindow> _openWindows = new();

    public void ShowWindow(ViewModelBase viewModel, string title, int width = 800, int height = 600)
    {
        VerifyUiThread();
        ShowWindow((object)viewModel, title, (double)width, (double)height);
    }

    public void ShowWindow(ViewModelBase viewModel, string key, string title, int width, int height)
    {
        VerifyUiThread();
        ShowWindowInternal(viewModel, key, title, width, height);
    }

    public void ShowWindow(object content, string title, double? width = null, double? height = null)
    {
        VerifyUiThread();
        ShowWindowInternal(content, KeyFor(content), title, width ?? 800, height ?? 600);
    }

    /// <summary>The dictionary is mutated from <see cref="ShowWindowInternal"/>
    /// and from the <see cref="Avalonia.Controls.Window.Closed"/> handler.
    /// Both run on the UI thread today; this assert is defensive — a
    /// background-thread caller would silently corrupt the dictionary
    /// otherwise.</summary>
    private static void VerifyUiThread()
    {
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            throw new System.InvalidOperationException(
                "WindowManagerService must be called from the UI thread. " +
                "Marshal via Dispatcher.UIThread.Post or Dispatcher.UIThread.InvokeAsync.");
    }

    private void ShowWindowInternal(object content, string key, string title, double width, double height)
    {
        if (_openWindows.TryGetValue(key, out var existingWindow))
        {
            var oldContent = existingWindow.ShellContent;

            // Reference-equal: singleton-style VM reopened (Library,
            // Playlists, Presets, Main Mixer, etc.). Just focus the
            // window — no disposal anywhere.
            if (ReferenceEquals(oldContent, content))
            {
                existingWindow.Title = title;
                existingWindow.Activate();
                return;
            }

            // Same type, different instance: the caller created a fresh
            // VM for a window that's already showing one of the same
            // kind. This happens when a factory-resolved VM
            // (SamplerEditor, TrackEditor, BusMixer, UriBuilder, popped
            // shortcut page) gets re-launched on the same key. The
            // user gesture is "focus the existing editor"; the newly-
            // built VM is a duplicate. Dispose IT, not the live one.
            // Pre-fix the old VM was disposed and the new one swapped
            // in — destroying scroll position, transient UI state, and
            // in-flight live-edit pushes on the existing editor.
            if (oldContent != null && content != null
                && oldContent.GetType() == content.GetType())
            {
                existingWindow.Title = title;
                existingWindow.Activate();
                if (content is IDisposable newDisposable)
                {
                    try { newDisposable.Dispose(); }
                    catch (Exception ex) { SoundBoard.Core.Logging.Log.Warn("WindowManager", $"Disposing duplicate ShellContent threw: {ex.Message}"); }
                }
                return;
            }

            // Different type: genuine swap. The OLD content is about to
            // become unreachable from this window, so we must dispose
            // it if it owns resources — particularly any DispatcherTimer
            // started in its constructor. (This branch is reserved for
            // future cases where the same window key shows different
            // VM types over time — none today.)
            existingWindow.Title = title;
            existingWindow.ShellContent = content;
            existingWindow.Activate();
            if (oldContent is IDisposable oldDisposable)
            {
                try { oldDisposable.Dispose(); }
                catch (Exception ex) { SoundBoard.Core.Logging.Log.Warn("WindowManager", $"Disposing replaced ShellContent threw: {ex.Message}"); }
            }
            return;
        }

        var window = new AppWindow
        {
            Title = title,
            Width = width,
            Height = height,
            ShellContent = content,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };

        window.Closed += (s, e) =>
        {
            _openWindows.Remove(key);
            // Same disposal contract as the swap path: editor VMs hold
            // timers, message-bus subscriptions, and audio-thread-touching
            // editor instances that must be torn down deterministically
            // when the window closes. Pre-fix this was only ever done
            // (inconsistently) via individual view's Unloaded handlers.
            if (window.ShellContent is IDisposable d)
            {
                try { d.Dispose(); }
                catch (Exception ex) { SoundBoard.Core.Logging.Log.Warn("WindowManager", $"Disposing closed ShellContent threw: {ex.Message}"); }
            }
        };
        _openWindows[key] = window;

        window.Show();
    }

    private static string KeyFor(object content) => content.GetType().FullName ?? content.GetType().Name;

    public void CloseWindow(ViewModelBase viewModel)
    {
        var key = KeyFor(viewModel);
        if (_openWindows.TryGetValue(key, out var window))
        {
            window.Close();
        }
    }
}
