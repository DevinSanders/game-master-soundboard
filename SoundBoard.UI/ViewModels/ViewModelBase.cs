using CommunityToolkit.Mvvm.ComponentModel;

namespace SoundBoard.UI.ViewModels;

/// <summary>
/// Base class for every view model. Inherits <c>ObservableObject</c> from
/// CommunityToolkit.Mvvm so derived types can declare bindable state with
/// <c>[ObservableProperty]</c> and commands with <c>[RelayCommand]</c>
/// rather than hand-rolling <c>INotifyPropertyChanged</c> plumbing.
/// </summary>
public class ViewModelBase : ObservableObject
{
}
