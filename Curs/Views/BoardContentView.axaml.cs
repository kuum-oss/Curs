using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Curs.Views;

[SuppressMessage("ReSharper", "PartialTypeWithSinglePart", Justification = "Avalonia generates a second class declaration during compilation.")]
public partial class BoardContentView : UserControl
{
    public BoardContentView() => AvaloniaXamlLoader.Load(this);
}
