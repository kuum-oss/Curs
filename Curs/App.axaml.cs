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
            MacApplicationIcon.Apply();
            var address = new Uri(Environment.GetEnvironmentVariable("KANBAN_API_URL") ?? "http://127.0.0.1:5000/");
            var services = new ClientServices(address, action => Dispatcher.UIThread.Post(action),
                sessionStore: OperatingSystem.IsMacOS() ? new MacKeychainSessionStore(address.AbsoluteUri) : null);
            var viewModel = new MainWindowViewModel(services);
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            desktop.MainWindow.Opened += (_, _) => _ = viewModel.InitializeAsync();
            desktop.Exit += (_, _) => { viewModel.Dispose(); services.Dispose(); };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
