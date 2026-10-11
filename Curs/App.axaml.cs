using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using System;
using Curs.Services;
using Curs.ViewModels;

namespace Curs;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new ClientServices(new Uri(Environment.GetEnvironmentVariable("KANBAN_API_URL") ?? "http://localhost:5000/"),
                action => Dispatcher.UIThread.Post(action));
            desktop.MainWindow = new MainWindow { DataContext = new MainWindowViewModel(services) };
            desktop.Exit += (_, _) => services.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
