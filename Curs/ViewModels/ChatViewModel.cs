using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Layout;
using Avalonia.Media;
using Curs.Services;
using KanbanContracts;

namespace Curs.ViewModels;

public sealed class TeamPersonViewModel : IDisposable
{
    public AvatarViewModel Avatar { get; }
    public UserDto User => Avatar.User;
    public string Name => User.Name;
    public bool CanChat { get; }
    public ActionCommand ChatCommand { get; }
    public TeamPersonViewModel(UserDto user,Guid self,Action<UserDto> open)
    { Avatar=new(user);CanChat=user.Id!=self;ChatCommand=new(()=>open(user),()=>CanChat); }
    public void Dispose()=>Avatar.Dispose();
}
public sealed class ChatThreadViewModel : IDisposable
{
    public ChatDto Chat { get; }
    public AvatarViewModel Avatar { get; }
    public string Name=>Chat.Peer.Name;
    public string Preview=>Chat.LastMessage??"Почніть розмову";
    public string Unread=>Chat.UnreadCount>0?Chat.UnreadCount.ToString():"";
    public bool HasUnread=>Chat.UnreadCount>0;
    public ActionCommand OpenCommand { get; }
    public ChatThreadViewModel(ChatDto chat,ChatViewModel owner){Chat=chat;Avatar=new(chat.Peer);OpenCommand=new(()=>_=owner.Open(chat));}
    public void Dispose()=>Avatar.Dispose();
}
public sealed class ChatMessageViewModel(ChatMessageDto message,Guid self)
{
    public ChatMessageDto Message { get; }=message;
    public string Text=>Message.Text;
    public string Time=>Message.CreatedAtUtc.ToLocalTime().ToString("dd.MM · HH:mm");
    public string Author=>Message.SenderId==self?"Ви":Message.SenderId is null?"Видалений користувач":"";
    public HorizontalAlignment Alignment=>Message.SenderId==self?HorizontalAlignment.Right:HorizontalAlignment.Left;
    public IBrush Background=>Brush.Parse(Message.SenderId==self?"#DDF3F1":"#F0F3F8");
}
public sealed class ChatViewModel : ViewModelBase,IDisposable
{
    private readonly ClientServices services;
    private CancellationTokenSource lifetime=new();
    private Guid? userId;
    private ChatDto? selected;
    private string draft="",error="";
    private Guid nonce=Guid.NewGuid();
    private long fetchedThrough;
    private bool busy,polling,active,hasOlder,disposed;
    private readonly System.Collections.Generic.Dictionary<Guid,(string Text,Guid Nonce)> drafts=[];
    public ObservableCollection<ChatThreadViewModel> Threads{get;}=[];
    public ObservableCollection<ChatMessageViewModel> Messages{get;}=[];
    public bool Active {get=>active;set{active=value;if(!value){NewLifetime();IsBusy=false;}}}
    public bool HasConversation=>selected is not null;
    public bool NoConversation=>!HasConversation;
    public bool NoChats=>Threads.Count==0;
    public bool NoMessages=>Messages.Count==0&&!IsBusy&&HasConversation;
    public bool IsBusy{get=>busy;private set{busy=value;Notify();Refresh();}}
    public string PeerName=>selected?.Peer.Name??"Особисті повідомлення";
    public string PeerEmail=>selected?.Peer.Email??"";
    public bool CanSend=>selected?.CanSend==true&&!IsBusy&&services.Session.User is not null;
    public bool PeerUnavailable=>selected?.CanSend==false;
    public bool HasOlder{get=>hasOlder;private set{hasOlder=value;Notify();}}
    public string Draft{get=>draft;set{draft=value;nonce=Guid.NewGuid();Notify();Refresh();}}
    public string Error{get=>error;private set{error=value;Notify();Notify(nameof(HasError));}}
    public bool HasError=>!string.IsNullOrWhiteSpace(Error);
    public ActionCommand SendCommand{get;}
    public ActionCommand OlderCommand{get;}
    public event Action? ScrollToEnd;
    public event Action? PrependingHistory;
    public ChatViewModel(ClientServices services)
    {
        this.services=services;userId=services.Session.User?.Id;
        SendCommand=new(()=>_=Send(),()=>CanSend&&!string.IsNullOrWhiteSpace(Draft)&&Draft.Length<=2000);
        OlderCommand=new(()=>_=Older(),()=>HasOlder&&!IsBusy);
        services.Session.Changed+=SessionChanged;
    }
    private void SessionChanged(){var next=services.Session.User?.Id;if(next!=userId){Clear();userId=next;}}
    private void Refresh(){Notify(nameof(CanSend));Notify(nameof(NoMessages));SendCommand.Refresh();OlderCommand.Refresh();}
    private void NotifySelection(){Notify(nameof(HasConversation));Notify(nameof(NoConversation));Notify(nameof(PeerName));Notify(nameof(PeerEmail));Notify(nameof(PeerUnavailable));Refresh();}
    private void SaveDraft(){if(selected is {} c)drafts[c.Id]=(draft,nonce);}
    private void RestoreDraft(){var d=selected is {} c?drafts.GetValueOrDefault(c.Id):default;draft=d.Text??"";nonce=d.Nonce==Guid.Empty?Guid.NewGuid():d.Nonce;Notify(nameof(Draft));}
    private void NewLifetime(){lifetime.Cancel();lifetime.Dispose();lifetime=new();}
    public async Task Show(UserDto? peer=null)
    {
        Active=true;
        await Run(async t=>
        {
            await ReloadList(t);
            if(peer is not null){var chat=await services.Chats.Start(peer.Id,t);t.ThrowIfCancellationRequested();await Select(chat,t);await ReloadList(t);}
            else if(selected is {} current)await Select(current,t);
        });
    }
    public Task Open(ChatDto chat)=>Run(t=>Select(chat,t));
    private async Task Select(ChatDto chat,CancellationToken token)
    {
        SaveDraft();selected=chat;RestoreDraft();Messages.Clear();fetchedThrough=0;HasOlder=false;NotifySelection();
        var page=await services.Chats.Messages(chat.Id,token);token.ThrowIfCancellationRequested();
        Merge(page.Items);fetchedThrough=page.Items.LastOrDefault()?.Id??0;HasOlder=page.HasMore;ScrollToEnd?.Invoke();await MarkRead(token);
    }
    private async Task ReloadList(CancellationToken token)
    {
        var rows=await services.Chats.List(token);token.ThrowIfCancellationRequested();
        if(rows.SequenceEqual(Threads.Select(t=>t.Chat)))return;
        foreach(var old in Threads)old.Dispose();Threads.Clear();
        foreach(var row in rows){var item=new ChatThreadViewModel(row,this);Threads.Add(item);_=item.Avatar.Load(services);}
        if(selected is {} current){selected=rows.SingleOrDefault(c=>c.Id==current.Id)??current;NotifySelection();}
        Notify(nameof(NoChats));
    }
    private void Merge(System.Collections.Generic.IReadOnlyList<ChatMessageDto> rows)
    {
        foreach(var row in rows.Where(r=>Messages.All(m=>m.Message.Id!=r.Id)).OrderBy(r=>r.Id))
        {
            var index=0;while(index<Messages.Count&&Messages[index].Message.Id<row.Id)index++;
            Messages.Insert(index,new(row,services.Session.User!.Id));
        }
        Notify(nameof(NoMessages));
    }
    public Task Send()=>Run(async token=>
    {
        if(selected is not {} chat||!chat.CanSend||string.IsNullOrWhiteSpace(Draft)||Draft.Length>2000)return;
        var text=draft;var id=nonce;
        var message=await services.Chats.Send(chat.Id,text,id,token);token.ThrowIfCancellationRequested();
        Merge([message]);draft="";nonce=Guid.NewGuid();drafts.Remove(chat.Id);Notify(nameof(Draft));ScrollToEnd?.Invoke();await ReloadList(token);
    });
    public Task Older()=>Run(async token=>
    {
        if(selected is not {} chat||Messages.Count==0||!HasOlder)return;
        var page=await services.Chats.Messages(chat.Id,token,before:Messages[0].Message.Id);token.ThrowIfCancellationRequested();
        PrependingHistory?.Invoke();Merge(page.Items);HasOlder=page.HasMore;
    });
    private async Task MarkRead(CancellationToken token)
    {
        if(active&&selected is {} chat&&fetchedThrough>0)await services.Chats.Read(chat.Id,fetchedThrough,token);
    }
    public async Task Poll()
    {
        if(!active||IsBusy||polling||services.Session.User is null)return;
        polling=true;var token=lifetime.Token;var observed=services.Session.AccessToken;var chat=selected?.Id;
        try
        {
            await ReloadList(token);
            if(chat is {} id)
            {
                var page=await services.Chats.Messages(id,token,after:fetchedThrough);
                token.ThrowIfCancellationRequested();if(!active||selected?.Id!=id||observed!=services.Session.AccessToken)return;
                Merge(page.Items);fetchedThrough=Math.Max(fetchedThrough,page.Items.LastOrDefault()?.Id??0);await MarkRead(token);
            }
            Error="";
        }
        catch(OperationCanceledException){}
        catch(Exception){if(!token.IsCancellationRequested)Error="Не вдалося оновити чат. Повторимо автоматично.";}
        finally{polling=false;}
    }
    private async Task Run(Func<CancellationToken,Task> action)
    {
        if(disposed||IsBusy||services.Session.User is null)return;
        NewLifetime();var source=lifetime;var token=source.Token;Error="";IsBusy=true;
        try{await action(token);}
        catch(OperationCanceledException){}
        catch(ApiClientException ex){if(!token.IsCancellationRequested)Error=ex.Message;}
        catch(HttpRequestException){if(!token.IsCancellationRequested)Error="Не вдалося підключитися. Текст збережено — спробуйте ще раз.";}
        catch(Exception){if(!token.IsCancellationRequested)Error="Не вдалося виконати дію. Спробуйте ще раз.";}
        finally{if(!disposed&&ReferenceEquals(source,lifetime))IsBusy=false;}
    }
    public void Clear()
    {
        NewLifetime();active=false;IsBusy=false;selected=null;draft="";drafts.Clear();Messages.Clear();
        foreach(var row in Threads)row.Dispose();Threads.Clear();HasOlder=false;Error="";Notify(nameof(Draft));Notify(nameof(NoChats));NotifySelection();
    }
    public void Dispose(){services.Session.Changed-=SessionChanged;Clear();disposed=true;lifetime.Cancel();lifetime.Dispose();}
}
