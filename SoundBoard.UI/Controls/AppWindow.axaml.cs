using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace SoundBoard.UI.Controls;

/// <summary>
/// Window subclass with the same chrome the main window uses — extended
/// client area + custom drag-handle title bar. Secondary windows
/// (Library, Mixer, Settings, etc.) are spawned through this so the whole
/// app looks consistent on every platform.
/// </summary>
public partial class AppWindow : Window
{
    public static readonly StyledProperty<object?> ShellContentProperty =
        AvaloniaProperty.Register<AppWindow, object?>(nameof(ShellContent));

    /// <summary>Content rendered below the custom title bar.</summary>
    public object? ShellContent
    {
        get => GetValue(ShellContentProperty);
        set => SetValue(ShellContentProperty, value);
    }

    private ContentPresenter? _shellContentPresenter;

    public AppWindow()
    {
        InitializeComponent();
        _shellContentPresenter = this.FindControl<ContentPresenter>("PART_ShellContent");
        UpdateShellContent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ShellContentProperty) UpdateShellContent();
    }

    private void UpdateShellContent()
    {
        if (_shellContentPresenter != null)
            _shellContentPresenter.Content = ShellContent;
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e) => BeginMoveDrag(e);
}
