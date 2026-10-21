using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
namespace Curs.Views;
public partial class StatisticsView:UserControl
{
    public static readonly StyledProperty<bool> CompactProperty=AvaloniaProperty.Register<StatisticsView,bool>(nameof(Compact));
    public bool Compact{get=>GetValue(CompactProperty);set=>SetValue(CompactProperty,value);}
    public static readonly StyledProperty<int> MetricColumnsProperty=AvaloniaProperty.Register<StatisticsView,int>(nameof(MetricColumns),4);
    public static readonly StyledProperty<int> MetricRowsProperty=AvaloniaProperty.Register<StatisticsView,int>(nameof(MetricRows),1);
    public int MetricColumns=>GetValue(MetricColumnsProperty);
    public int MetricRows=>GetValue(MetricRowsProperty);
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if(change.Property==CompactProperty){SetValue(MetricColumnsProperty,Compact?2:4);SetValue(MetricRowsProperty,Compact?2:1);}
    }
    public StatisticsView()=>AvaloniaXamlLoader.Load(this);
}
