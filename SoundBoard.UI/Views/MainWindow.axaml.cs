using Avalonia.Controls;
using Avalonia.Input;

namespace SoundBoard.UI.Views;

/// <summary>The application's main window: the soundboard grid, navigation
/// sidebar, and Now Playing bar. Uses a custom (extended-client-area)
/// title bar — <see cref="OnTitleBarPointerPressed"/> forwards drags so
/// the window stays movable from that area.</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "Game Master Sound Board";
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        BeginMoveDrag(e);
    }
}
