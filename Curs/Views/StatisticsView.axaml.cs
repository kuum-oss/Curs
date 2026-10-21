using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
namespace Curs.Views;
public partial class StatisticsView:UserControl
{
    public static readonly StyledProperty<bool> CompactProperty=AvaloniaProperty.Register<StatisticsView,bool>(nameof(Compact));
    public bool Compact{get=>GetValue(CompactProperty);set=>SetValue(CompactProperty,value);}
    public StatisticsView()=>AvaloniaXamlLoader.Load(this);
}
