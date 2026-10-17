using System;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Curs.ViewModels;

namespace Curs.Views;

public partial class ChatView : UserControl
{
    private readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromSeconds(2)};
    private ChatViewModel? model;
    private readonly ScrollViewer scroll;
    private double? historyExtent;
    public ChatView()
    {
        AvaloniaXamlLoader.Load(this);scroll=this.FindControl<ScrollViewer>("MessageScroll")!;
        timer.Tick+=async(_,_)=>{if(model is {} current)await current.Poll();};
        DataContextChanged+=(_,_)=>Bind();AttachedToVisualTree+=(_,_)=>{Bind();timer.Start();};DetachedFromVisualTree+=(_,_)=>{timer.Stop();Unbind();};
        scroll.ScrollChanged+=(_,e)=>{if(historyExtent is {} old&&e.ExtentDelta.Y>0){historyExtent=null;scroll.Offset=new Vector(0,scroll.Offset.Y+scroll.Extent.Height-old);}};
    }
    private void Bind(){Unbind();model=DataContext as ChatViewModel;if(model is null)return;model.ScrollToEnd+=Bottom;model.PrependingHistory+=BeforeHistory;model.Messages.CollectionChanged+=MessagesChanged;}
    private void Unbind(){if(model is null)return;model.ScrollToEnd-=Bottom;model.PrependingHistory-=BeforeHistory;model.Messages.CollectionChanged-=MessagesChanged;model=null;}
    private void BeforeHistory()=>historyExtent=scroll.Extent.Height;
    private void Bottom()=>Dispatcher.UIThread.Post(scroll.ScrollToEnd,DispatcherPriority.Loaded);
    private void MessagesChanged(object? sender,NotifyCollectionChangedEventArgs e)
    {if(historyExtent is null&&scroll.Extent.Height-scroll.Viewport.Height-scroll.Offset.Y<80)Bottom();}
}
