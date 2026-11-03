using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using System;
using System.IO;
using Curs.Services;
using Curs.Services.LocalSetup;
using Curs.ViewModels;
using Curs.Views;

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
            if (Program.VerifyStartup)
            {
                VerifyStartup(desktop);
                base.OnFrameworkInitializationCompleted();
                return;
            }
            try
            {
                var configured = StartupConfiguration.ExplicitApiAddress(Environment.GetEnvironmentVariable("KANBAN_API_URL"));
                var manifest = configured is null ? RuntimeManifest.Load() : null;
                if (manifest is null) ShowMain(desktop, configured ?? new Uri("http://127.0.0.1:5000/"));
                else ShowSetup(desktop, new SetupRuntimeAdapter(new LocalSetupCoordinator(manifest), manifest));
            }
            catch (SetupException e) { ShowSetup(desktop, null, e.Message); }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
            { ShowSetup(desktop, null, "Не вдалося прочитати налаштування запуску. Перевірте доступ до папки програми та локальних компонентів."); }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static MainWindow ShowMain(IClassicDesktopStyleApplicationLifetime desktop, Uri address, bool show = false)
    {
        var services = new ClientServices(address, action => Dispatcher.UIThread.Post(action),
            sessionStore: OperatingSystem.IsMacOS() ? new MacKeychainSessionStore(address.AbsoluteUri) : null);
        var viewModel = new MainWindowViewModel(services);
        var window = new MainWindow { DataContext = viewModel };
        desktop.MainWindow = window;
        window.Opened += (_, _) => _ = viewModel.InitializeAsync();
        window.Closed += (_, _) => { viewModel.Dispose(); services.Dispose(); };
        if (show) window.Show();
        return window;
    }

    private static void ShowSetup(IClassicDesktopStyleApplicationLifetime desktop, ISetupRuntime? runtime, string? error = null)
    {
        var window = new SetupWindow();
        var viewModel = new SetupViewModel(runtime, address =>
        {
            ShowMain(desktop, address, show: true);
            window.Close();
        }, action => Dispatcher.UIThread.Post(action), error);
        window.DataContext = viewModel;
        desktop.MainWindow = window;
        window.Opened += (_, _) => _ = viewModel.InitializeAsync();
        window.Closed += (_, _) => viewModel.Dispose();
    }

    // Read-only native package probe: no Docker, downloads, database, or Keychain access.
    private static void VerifyStartup(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var manifest = RuntimeManifest.Load() ?? throw new InvalidOperationException("Packaged runtime manifest missing.");
        manifest.Validate();
        var services = new ClientServices(new Uri("http://127.0.0.1:9/"));
        var main = new MainWindowViewModel(services);
        _ = new MainWindow { DataContext = main };
        var setup = new SetupViewModel(null, _ => { }, blockingError: "Перевірка інсталяційного пакета");
        var window = new SetupWindow { DataContext = setup };
        desktop.MainWindow = window;
        window.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            setup.Dispose(); main.Dispose(); services.Dispose();
            desktop.Shutdown(0);
        }, DispatcherPriority.Background);
    }
}
