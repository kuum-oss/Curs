using System.ComponentModel;
using System.Runtime.CompilerServices;
using Curs.Services;

namespace Curs.ViewModels;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class MainWindowViewModel(ClientServices services) : ViewModelBase
{
    public ClientServices Services { get; } = services;
    public string Title => "KanbanBoard";
    public string Status => "Основа клієнта підготовлена. Вхід і дошки з'являться в наступних фазах.";
}
