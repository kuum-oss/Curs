using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Curs.ViewModels;
namespace Curs.Views;
public partial class CalendarView:UserControl
{
    private CalendarTaskViewModel? source;
    private CalendarViewModel? owner;
    private IPointer? pointer;
    private TopLevel? root;
    private Point start;
    private Border? target;
    private bool dragged;
    public CalendarView(){AvaloniaXamlLoader.Load(this);DetachedFromVisualTree+=(_,_)=>Reset();DataContextChanged+=(_,_)=>Reset();PropertyChanged+=(_,e)=>{if(e.Property==IsVisibleProperty&&!IsVisible)Reset();};}
    private void DragPressed(object? sender,PointerPressedEventArgs e)
    {
        if(sender is not Control{DataContext:CalendarTaskViewModel task} grip||DataContext is not CalendarViewModel{CanEdit:true,Active:true} model||!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed)return;
        Reset();source=task;owner=model;start=e.GetPosition(this);pointer=e.Pointer;root=TopLevel.GetTopLevel(this);if(root is not null)root.KeyDown+=Escape;pointer.Capture(grip);e.Handled=true;
    }
    private Border? Destination(Point point)
    {
        var hit=this.InputHitTest(point) as Visual;return (hit as Border is {} b&&b.Tag is "CalendarDay" or "CalendarUndated")?b:hit?.GetVisualAncestors().OfType<Border>().FirstOrDefault(b=>b.Tag is "CalendarDay" or "CalendarUndated");
    }
    private void DragMoved(object? sender,PointerEventArgs e)
    {
        if(source is null||owner?.CanEdit!=true||owner.Active!=true)return;var point=e.GetPosition(this);if(!dragged&&Math.Pow(point.X-start.X,2)+Math.Pow(point.Y-start.Y,2)<36)return;dragged=true;
        target?.Classes.Remove("calendar-over");target=Destination(point);target?.Classes.Add("calendar-over");e.Handled=true;
    }
    private async void DragReleased(object? sender,PointerReleasedEventArgs e)
    {
        var task=source;var model=owner;var destination=dragged?Destination(e.GetPosition(this)):null;var move=dragged;
        DateOnly? date=destination?.DataContext is CalendarDayViewModel day?day.Date:null;Reset();e.Handled=true;
        if(task is null||model is not {CanEdit:true,Active:true})return;
        if(move&&destination is not null)await model.Move(task.Row,date);else if(!move)model.Open(task.Row);
    }
    private void Escape(object? sender,KeyEventArgs e){if(e.Key==Key.Escape){Reset();e.Handled=true;}}
    private void DragLost(object? sender,PointerCaptureLostEventArgs e)=>Reset();
    private void Reset(){var old=pointer;pointer=null;source=null;owner=null;dragged=false;target?.Classes.Remove("calendar-over");target=null;if(root is not null)root.KeyDown-=Escape;root=null;old?.Capture(null);}
}
