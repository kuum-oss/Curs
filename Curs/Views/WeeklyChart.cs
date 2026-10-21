using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using KanbanContracts;
namespace Curs.Views;
// Drawn from the API series, never a screenshot or a static illustration.
public sealed class WeeklyChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<DailyCompletionDto>?> DaysProperty=AvaloniaProperty.Register<WeeklyChart,IReadOnlyList<DailyCompletionDto>?>(nameof(Days));
    public IReadOnlyList<DailyCompletionDto>? Days{get=>GetValue(DaysProperty);set=>SetValue(DaysProperty,value);}
    static WeeklyChart()=>AffectsRender<WeeklyChart>(DaysProperty);
    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);var rows=Days;if(rows is null||rows.Count==0)return;
        var w=Bounds.Width;var h=Bounds.Height;if(w<70||h<60)return;
        var font=new Typeface("avares://Avalonia.Fonts.Inter/Assets#Inter");
        void Text(string value,double x,double y,double size,IBrush color,bool center=false)
        {var text=new FormattedText(value,CultureInfo.GetCultureInfo("uk-UA"),FlowDirection.LeftToRight,font,size,color);ctx.DrawText(text,new Point(center?x-text.Width/2:x,y));}
        var left=w<400?20d:30d;var right=8d;var top=24d;var bottom=h-30;var plot=bottom-top;var width=w-left-right;
        var max=Math.Max(3,rows.Max(d=>d.Count));var step=Math.Max(1,(int)Math.Ceiling(max/3d));max=step*3;
        var muted=Brush.Parse("#7B8AA4");
        for(var tick=0;tick<=3;tick++){var y=bottom-plot*tick/3;ctx.DrawLine(new Pen(Brush.Parse("#E8EDF5"),1),new(left,y),new(w-right,y));Text((tick*step).ToString(),0,y-7,10,muted);}
        var unit=width/7;var bar=Math.Max(6,unit*.67);
        for(var i=0;i<rows.Count;i++)
        {
            var row=rows[i];var x=left+unit*(i+.5);var barHeight=plot*row.Count/max;
            if(row.Count>0)ctx.DrawRectangle(Brush.Parse(i==4?"#0095A5":"#70C5CD"),null,new Rect(x-bar/2,bottom-barHeight,bar,barHeight),2,2);
            Text(row.Count.ToString(),x,bottom-barHeight-19,11,Brush.Parse("#334363"),true);
            Text(row.Date.ToString(w<400?"dd.MM":"d MMM",CultureInfo.GetCultureInfo("uk-UA")),x,bottom+9,w<400?9:10,muted,true);
        }
    }
}
