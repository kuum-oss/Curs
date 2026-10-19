using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Curs.Services;
using KanbanContracts;
namespace Curs.ViewModels;
public sealed class NotificationRow(NotificationDto row,NotificationViewModel owner)
{
    public NotificationDto Item {get;}=row;
    public string Title=>Item.Title;
    public string Context=>Item.Context;
    public string Time=>Item.CreatedAtUtc.ToLocalTime().ToString("dd.MM · HH:mm");
    public bool IsUnread=>!Item.IsRead;
    public bool CanOpen=>Item.CanOpen;
    public string Icon=>Item.Kind switch{NotificationKind.Assignment=>"♙",NotificationKind.Mention=>"@",NotificationKind.Deadline=>"◷",_=>"▤"};
    public IBrush Background=>Brush.Parse(IsUnread?"#EAF7F7":"#FFFFFF");
    public ActionCommand OpenCommand{get;}=new(()=>_=owner.Open(row.Id),()=>row.CanOpen&&!owner.IsBusy);
    public ActionCommand ReadCommand{get;}=new(()=>_=owner.Read(row.Id),()=>!row.IsRead&&!owner.IsBusy);
    public void Refresh(){OpenCommand.Refresh();ReadCommand.Refresh();}
}
public sealed class NotificationViewModel:ViewModelBase,IDisposable
{
    private readonly ClientServices services;
    private readonly Action<NotificationDto> navigate;
    private CancellationTokenSource lifetime=new();
    private Guid? user;
    private bool visible,busy,unread,more,disposed;
    private int count;
    private long latest;
    private string error="";
    public ObservableCollection<NotificationRow> Items{get;}=[];
    public bool Visible{get=>visible;private set{visible=value;Notify();}}
    public bool IsBusy{get=>busy;private set{busy=value;Notify();foreach(var i in Items)i.Refresh();AllCommand.Refresh();UnreadCommand.Refresh();ReadAllCommand.Refresh();MoreCommand.Refresh();}}
    public bool OnlyUnread=>unread;
    public bool HasUnread=>count>0;
    public string Badge=>count>99?"99+":count.ToString();
    public string UnreadLabel=>$"Непрочитані ({count})";
    public bool IsEmpty=>Items.Count==0&&!IsBusy;
    public bool HasMore=>more;
    public string Error{get=>error;private set{error=value;Notify();}}
    public ActionCommand ToggleCommand{get;}
    public ActionCommand CloseCommand{get;}
    public ActionCommand AllCommand{get;}
    public ActionCommand UnreadCommand{get;}
    public ActionCommand ReadAllCommand{get;}
    public ActionCommand MoreCommand{get;}
    public NotificationViewModel(ClientServices services,Action<NotificationDto> navigate)
    {
        this.services=services;this.navigate=navigate;user=services.Session.User?.Id;
        ToggleCommand=new(()=>{Visible=!Visible;if(Visible)_=Refresh();});CloseCommand=new(()=>Visible=false);
        AllCommand=new(()=>{unread=false;Notify(nameof(OnlyUnread));_=Refresh();},()=>!busy);
        UnreadCommand=new(()=>{unread=true;Notify(nameof(OnlyUnread));_=Refresh();},()=>!busy);
        ReadAllCommand=new(()=>_=Run(async t=>{await services.Notifications.ReadAll(latest,t);await Load(null,t);}),()=>!busy&&count>0&&latest>0);
        MoreCommand=new(()=>_=Run(t=>Load(Items.LastOrDefault()?.Item.Id,t)),()=>!busy&&more);
        services.Session.Changed+=SessionChanged;
    }
    private void SessionChanged(){var next=services.Session.User?.Id;if(user!=next){Clear();user=next;}}
    public Task Refresh()=>Run(t=>Load(null,t));
    private async Task Load(long? before,CancellationToken t)
    {
        var result=await services.Notifications.List(unread,before,t);t.ThrowIfCancellationRequested();
        if(before is null)Items.Clear();foreach(var row in result.Items)if(Items.All(i=>i.Item.Id!=row.Id))Items.Add(new(row,this));
        count=result.UnreadCount;latest=result.LatestId;more=result.HasMore;Changed();
    }
    public Task Open(long id)=>Run(async t=>
    {
        var target=await services.Notifications.Open(id,t);t.ThrowIfCancellationRequested();
        await Load(null,t);t.ThrowIfCancellationRequested();Visible=false;navigate(target);
    });
    public Task Read(long id)=>Run(async t=>{await services.Notifications.Read(id,t);await Load(null,t);});
    private async Task Run(Func<CancellationToken,Task> action)
    {
        if(disposed||busy||services.Session.User is null)return;
        var source=lifetime;var token=source.Token;IsBusy=true;Error="";
        try{await action(token);}
        catch(OperationCanceledException){}
        catch(ApiClientException ex){if(!token.IsCancellationRequested)Error=ex.Status==404?"Сповіщення вже недоступне. Оновіть список.":ex.Message;}
        catch(Exception){if(!token.IsCancellationRequested)Error="Не вдалося оновити сповіщення. Повторіть спробу.";}
        finally{if(ReferenceEquals(source,lifetime)){IsBusy=false;Notify(nameof(IsEmpty));}}
    }
    private void Changed(){foreach(var n in new[]{nameof(HasUnread),nameof(Badge),nameof(UnreadLabel),nameof(IsEmpty),nameof(HasMore)})Notify(n);ReadAllCommand.Refresh();MoreCommand.Refresh();}
    public void Clear(){lifetime.Cancel();lifetime.Dispose();lifetime=new();Items.Clear();count=0;latest=0;more=false;unread=false;Visible=false;IsBusy=false;Error="";Notify(nameof(OnlyUnread));Changed();}
    public void Dispose(){services.Session.Changed-=SessionChanged;Clear();disposed=true;lifetime.Cancel();lifetime.Dispose();}
}
public sealed partial class BoardWorkspaceViewModel
{
    public NotificationViewModel Notifications{get;private set;}=null!;
    private void NavigateNotification(NotificationDto row)=>Navigate(()=>_=OpenNotificationCore(row));
    private async Task OpenNotificationCore(NotificationDto row)
    {
        if(row.CardId is {} card&&row.BoardId is {} board)await OpenLinkedCard(board,card);
        else if(row.ChatId is {} chat){Content.Clear();selected=null;CloseDialogs();Page="chats";NotifyBoard();await Chat.ShowId(chat);}
    }
    public async Task OpenLinkedCard(Guid board,Guid card)
    {
        var session=services.Session.AccessToken;
        await OpenVisualCore(board);
        if(session==services.Session.AccessToken&&Content.BoardId==board)await Content.OpenCardAsync(card);
    }
}
public sealed partial class BoardContentViewModel
{
    private AssigneeOption? mention;
    public AssigneeOption? MentionPerson{get=>mention;set{mention=value;Notify();}}
    public void InsertMention(){if(!CanEdit||MentionPerson?.User is not {} person)return;CommentText+=(CommentText.Length>0&&!char.IsWhiteSpace(CommentText[^1])?" ":"")+"@"+person.Email+" ";MentionPerson=null;}
    public ActionCommand InsertMentionCommand{get;private set;}=null!;
}
