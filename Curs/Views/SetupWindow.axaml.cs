using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Curs.Views;

public partial class SetupWindow : Window
{
    public SetupWindow() => AvaloniaXamlLoader.Load(this);
    private async void OpenTerms(object? sender, RoutedEventArgs e)
    {
        try { await Launcher.LaunchUriAsync(new Uri("https://www.microsoft.com/en-us/useterms")); }
        catch (Exception) { /* The external link is optional; setup remains available. */ }
    }
}
